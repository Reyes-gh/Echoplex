using NAudio.Wave;

namespace Echoplex.Services;

/// <summary>
/// Ecualizador gráfico de 15 bandas (2/3 de octava, de 25 Hz a 16 kHz) más preamplificador.
/// Los ajustes se sustituyen de golpe (una instantánea inmutable): el audio usa los nuevos en el siguiente bloque que
/// procesa, sin transiciones. Solo actúa en la salida normal; en bit a bit no se toca la señal.
/// </summary>
public static class Equalizer
{
    public static readonly double[] Frequencies = { 25, 40, 63, 100, 160, 250, 400, 630, 1000, 1600, 2500, 4000, 6300, 10000, 16000 };
    public const double MaxGain = 12;

    /// <summary>Lo que se aplica ahora mismo.</summary>
    public sealed class Snapshot
    {
        public Snapshot(bool enabled, double[] gains, double preamp)
        {
            Enabled = enabled;
            Gains = gains;
            Preamp = preamp;
            IsActive = enabled && (Math.Abs(preamp) > 0.001 || gains.Any(g => Math.Abs(g) > 0.001));
        }

        public bool Enabled { get; }
        public double[] Gains { get; }
        public double Preamp { get; }
        /// <summary>Encendido y con algo que hacer (todo a 0 dB no toca ni una muestra).</summary>
        public bool IsActive { get; }
    }

    private static volatile Snapshot _current = new(false, new double[Frequencies.Length], 0);

    public static Snapshot Current => _current;

    public static void Set(bool enabled, IReadOnlyList<double> gains, double preamp)
    {
        var g = new double[Frequencies.Length];
        for (int i = 0; i < g.Length && i < gains.Count; i++) g[i] = Math.Clamp(gains[i], -MaxGain, MaxGain);
        _current = new Snapshot(enabled, g, Math.Clamp(preamp, -MaxGain, MaxGain));
    }
}

/// <summary>
/// Aplica el ecualizador a un bloque de PCM (16/24/32 bits enteros o 32 bits en coma flotante), en el sitio.
/// Filtros de pico (RBJ) en cascada por canal, en doble precisión; un limitador suave por encima de −1 dB evita
/// que las subidas saturen a lo bruto.
/// </summary>
public sealed class EqualizerProcessor
{
    private const double Q = 2.15; // ancho de 2/3 de octava
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
    private readonly int _channels;
    private readonly int _bytes;
    private readonly bool _float;
    private readonly double _sampleRate;
    private readonly bool _supported;
    private Equalizer.Snapshot? _applied;
    // coeficientes de las bandas que actúan (b0, b1, b2, a1, a2) y su estado por canal (z1, z2)
    private double[] _coef = Array.Empty<double>();
    private int[] _bands = Array.Empty<int>();
    private readonly double[] _state;
    private double _pre = 1;
    private bool _wasActive;

    public EqualizerProcessor(WaveFormat format)
    {
        _channels = Math.Max(1, format.Channels);
        _bytes = format.BitsPerSample / 8;
        _sampleRate = format.SampleRate;
        _float = format.Encoding == WaveFormatEncoding.IeeeFloat
                 || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
        _supported = _float ? _bytes == 4 : _bytes is 2 or 3 or 4;
        _state = new double[Equalizer.Frequencies.Length * _channels * 2];
    }

    /// <summary>Lector nuevo (salto atrás o canción siguiente): los filtros empiezan de cero.</summary>
    public void Reset() => Array.Clear(_state);

    public void Process(byte[] buffer, int offset, int count)
    {
        if (!_supported) return;
        var eq = Equalizer.Current;
        if (!eq.IsActive)
        {
            if (_wasActive) Reset();
            _wasActive = false;
            return;
        }
        if (!_wasActive) Reset();
        _wasActive = true;
        if (!ReferenceEquals(eq, _applied)) Prepare(eq);

        int frame = _bytes * _channels;
        int frames = count / frame;
        int nb = _bands.Length;
        for (int f = 0; f < frames; f++)
        {
            int pos = offset + f * frame;
            for (int ch = 0; ch < _channels; ch++, pos += _bytes)
            {
                double x = Read(buffer, pos) * _pre;
                int s = ch * Equalizer.Frequencies.Length * 2;
                for (int b = 0; b < nb; b++)
                {
                    int c = b * 5, z = s + _bands[b] * 2;
                    double y = _coef[c] * x + _state[z];
                    _state[z] = _coef[c + 1] * x - _coef[c + 3] * y + _state[z + 1];
                    _state[z + 1] = _coef[c + 2] * x - _coef[c + 4] * y;
                    x = y;
                }
                Write(buffer, pos, SoftLimit(x));
            }
        }
        // estados diminutos (silencio): a cero, que los números desnormalizados son lentísimos
        for (int i = 0; i < _state.Length; i++)
            if (Math.Abs(_state[i]) < 1e-20) _state[i] = 0;
    }

    private void Prepare(Equalizer.Snapshot eq)
    {
        _applied = eq;
        _pre = Math.Pow(10, eq.Preamp / 20);
        var bands = new List<int>();
        var coef = new List<double>();
        for (int i = 0; i < Equalizer.Frequencies.Length; i++)
        {
            double gain = eq.Gains[i], f = Equalizer.Frequencies[i];
            if (Math.Abs(gain) < 0.001 || f >= _sampleRate * 0.45) continue; // sin cambio, o por encima de lo que cabe en la frecuencia del archivo
            double a = Math.Pow(10, gain / 40);
            double w0 = 2 * Math.PI * f / _sampleRate;
            double alpha = Math.Sin(w0) / (2 * Q);
            double cos = Math.Cos(w0);
            double a0 = 1 + alpha / a;
            bands.Add(i);
            coef.Add((1 + alpha * a) / a0);
            coef.Add(-2 * cos / a0);
            coef.Add((1 - alpha * a) / a0);
            coef.Add(-2 * cos / a0);
            coef.Add((1 - alpha / a) / a0);
        }
        _bands = bands.ToArray();
        _coef = coef.ToArray();
    }

    /// <summary>Transparente hasta −1 dB; por encima, una curva suave que nunca pasa de 0 dBFS.</summary>
    private static double SoftLimit(double x)
    {
        const double knee = 0.89;
        double ax = Math.Abs(x);
        if (ax <= knee) return x;
        double y = knee + (1 - knee) * Math.Tanh((ax - knee) / (1 - knee));
        return x < 0 ? -y : y;
    }

    private double Read(byte[] b, int p)
    {
        if (_float) return BitConverter.ToSingle(b, p);
        return _bytes switch
        {
            2 => (short)(b[p] | b[p + 1] << 8) / 32768.0,
            3 => ((b[p] | b[p + 1] << 8 | b[p + 2] << 16) << 8 >> 8) / 8388608.0,
            _ => BitConverter.ToInt32(b, p) / 2147483648.0,
        };
    }

    private void Write(byte[] b, int p, double v)
    {
        if (_float)
        {
            BitConverter.TryWriteBytes(b.AsSpan(p, 4), (float)v);
            return;
        }
        switch (_bytes)
        {
            case 2:
            {
                int s = (int)Math.Round(Math.Clamp(v * 32768.0, -32768, 32767));
                b[p] = (byte)s;
                b[p + 1] = (byte)(s >> 8);
                break;
            }
            case 3:
            {
                int s = (int)Math.Round(Math.Clamp(v * 8388608.0, -8388608, 8388607));
                b[p] = (byte)s;
                b[p + 1] = (byte)(s >> 8);
                b[p + 2] = (byte)(s >> 16);
                break;
            }
            default:
            {
                long s = (long)Math.Round(Math.Clamp(v * 2147483648.0, -2147483648.0, 2147483647.0));
                BitConverter.TryWriteBytes(b.AsSpan(p, 4), (int)s);
                break;
            }
        }
    }
}
