using System.Runtime.InteropServices;

namespace Echoplex.Services;

/// <summary>
/// Ecualizador gráfico de 15 bandas (de 25 Hz a 16 kHz) más preamplificador.
/// Los ajustes se sustituyen de golpe (una instantánea inmutable) y el audio pasa a ellos en unos milisegundos, sin
/// transiciones que se oigan. Solo actúa en la salida normal; en bit a bit no se toca la señal.
/// </summary>
public static class Equalizer
{
    public static readonly double[] Frequencies = { 25, 40, 63, 100, 160, 250, 400, 630, 1000, 1600, 2500, 4000, 6300, 10000, 16000 };
    public const double MaxGain = 12;

    /// <summary>Lo que se aplica ahora mismo.</summary>
    public sealed class Snapshot
    {
        public Snapshot(bool enabled, double[] gains, double preamp, bool autoHeadroom)
        {
            Enabled = enabled;
            Gains = gains;
            Preamp = preamp;
            AutoHeadroom = autoHeadroom;
            IsActive = enabled && (Math.Abs(preamp) > 0.001 || gains.Any(g => Math.Abs(g) > 0.001));
        }

        public bool Enabled { get; }
        public double[] Gains { get; }
        public double Preamp { get; }
        /// <summary>Bajar el volumen lo justo para que la curva nunca pase de 0 dB (sin saturar ni limitar).</summary>
        public bool AutoHeadroom { get; }
        /// <summary>Encendido y con algo que hacer (todo a 0 dB no toca ni una muestra).</summary>
        public bool IsActive { get; }
    }

    private static volatile Snapshot _current = new(false, new double[Frequencies.Length], 0, false);

    public static Snapshot Current => _current;

    public static void Set(bool enabled, IReadOnlyList<double> gains, double preamp, bool autoHeadroom)
    {
        var g = new double[Frequencies.Length];
        for (int i = 0; i < g.Length && i < gains.Count; i++) g[i] = Math.Clamp(gains[i], -MaxGain, MaxGain);
        _current = new Snapshot(enabled, g, Math.Clamp(preamp, -MaxGain, MaxGain), autoHeadroom);
    }
}

/// <summary>
/// Los filtros que hacen sonar lo que marcan los deslizadores.
///
/// Cada banda es un filtro de pico, pero cada uno también empuja un poco a sus vecinas: con las ganancias tal cual, la
/// suma se pasa (todo a +6 sonaba a +9). Así que se calcula cuánto tiene que subir de verdad cada filtro para que la
/// suma dé la curva pedida: se mide cuánto se pisan las bandas (la «matriz de interacción») y se resuelve por mínimos
/// cuadrados, dos veces (la forma de cada filtro cambia algo con su ganancia). Es el método del ecualizador gráfico
/// preciso de Liski y Välimäki (Universidad Aalto). Las bandas son de una octava, con su ancho corregido para que no se
/// estrechen cerca del límite agudo del archivo (la transformación bilineal las comprime: la de 16 kHz se quedaba en un
/// tercio). El cálculo es diminuto y solo se hace al cambiar un ajuste, nunca por muestra.
/// </summary>
public sealed class EqDesign
{
    private const double BandwidthOctaves = 1.0;
    private const double ProbeGain = 12;  // ganancia de prueba para medir cuánto se pisan las bandas
    private const double MaxFilterGain = 24;
    private const double HeadroomSafety = 0.3;

    private EqDesign(double sampleRate, int[] bands, double[] filterGains, double[][] sections, double preampDb, double headroomDb)
    {
        SampleRate = sampleRate;
        Bands = bands;
        FilterGains = filterGains;
        Sections = sections;
        PreampDb = preampDb;
        HeadroomDb = headroomDb;
        OutputGain = Math.Pow(10, (preampDb + headroomDb) / 20);
    }

    public double SampleRate { get; }
    /// <summary>Bandas que caben en la frecuencia del archivo (índices en <see cref="Equalizer.Frequencies"/>).</summary>
    public int[] Bands { get; }
    /// <summary>Lo que sube de verdad cada filtro, en dB (no lo que marca su deslizador).</summary>
    public double[] FilterGains { get; }
    /// <summary>Coeficientes de cada filtro: b0, b1, b2, a1, a2 (a0 = 1).</summary>
    public double[][] Sections { get; }
    public double PreampDb { get; }
    /// <summary>Lo que baja el margen automático (0 o negativo).</summary>
    public double HeadroomDb { get; }
    /// <summary>Ganancia final: preamplificador más margen automático.</summary>
    public double OutputGain { get; }

    public static EqDesign Create(Equalizer.Snapshot eq, double sampleRate)
    {
        var bands = Enumerable.Range(0, Equalizer.Frequencies.Length)
            .Where(i => Equalizer.Frequencies[i] < sampleRate * 0.45).ToArray(); // lo que cabe en el archivo
        double[] gains = bands.Select(i => eq.Gains[i]).ToArray();
        double[] filterGains = gains.Any(g => Math.Abs(g) > 0.001) ? Solve(bands, gains, sampleRate) : new double[bands.Length];
        var sections = bands.Select((b, k) => Peaking(Equalizer.Frequencies[b], filterGains[k], sampleRate)).ToArray();

        double headroom = 0;
        if (eq.AutoHeadroom)
        {
            // el pico queda 0,3 dB por debajo del techo del limitador: con el margen puesto, el limitador no llega a actuar
            double peak = Curve(sections, sampleRate, CurveFrequencies(sampleRate, 256)).Max() + eq.Preamp + HeadroomSafety;
            if (peak > 0) headroom = -peak;
        }
        return new EqDesign(sampleRate, bands, filterGains, sections, eq.Preamp, headroom);
    }

    /// <summary>La respuesta de los filtros (sin preamplificador ni margen), en dB, en cada frecuencia.</summary>
    public double[] Response(IReadOnlyList<double> frequencies) => Curve(Sections, SampleRate, frequencies);

    /// <summary>Frecuencias repartidas en escala logarítmica de 20 Hz hasta 20 kHz (o lo que quepa en el archivo).</summary>
    public static double[] CurveFrequencies(double sampleRate, int count)
    {
        double lo = 20, hi = Math.Min(20000, sampleRate * 0.49);
        return Enumerable.Range(0, count).Select(i => lo * Math.Pow(hi / lo, i / (count - 1.0))).ToArray();
    }

    private static double[] Curve(double[][] sections, double fs, IReadOnlyList<double> frequencies)
    {
        var db = new double[frequencies.Count];
        for (int i = 0; i < db.Length; i++)
            foreach (var s in sections) db[i] += SectionDb(s, frequencies[i], fs);
        return db;
    }

    /// <summary>
    /// Ganancia de cada filtro para que la cascada dé la curva pedida en los centros de las bandas y a medio camino entre
    /// ellas (allí, la media de sus dos vecinas: la línea que se dibuja en Ajustes).
    /// </summary>
    private static double[] Solve(int[] bands, double[] gains, double fs)
    {
        int n = bands.Length;
        var freqs = bands.Select(b => Equalizer.Frequencies[b]).ToArray();
        var points = new List<double>();
        var target = new List<double>();
        for (int k = 0; k < n; k++)
        {
            points.Add(freqs[k]);
            target.Add(gains[k]);
            if (k + 1 < n)
            {
                points.Add(Math.Sqrt(freqs[k] * freqs[k + 1]));
                target.Add((gains[k] + gains[k + 1]) / 2);
            }
        }

        var probe = Enumerable.Repeat(ProbeGain, n).ToArray();
        var g = gains;
        for (int pass = 0; pass < 2; pass++)
        {
            // matriz de interacción: cuántos dB pone cada filtro en cada punto, por cada dB de su ganancia
            var b = new double[points.Count, n];
            for (int k = 0; k < n; k++)
            {
                double p = Math.Abs(probe[k]) < 0.1 ? ProbeGain : probe[k];
                var s = Peaking(freqs[k], p, fs);
                for (int r = 0; r < points.Count; r++) b[r, k] = SectionDb(s, points[r], fs) / p;
            }
            g = LeastSquares(b, target.ToArray());
            probe = g; // segunda pasada: con la forma que tendrá cada filtro de verdad
        }
        return g.Select(x => Math.Clamp(x, -MaxFilterGain, MaxFilterGain)).ToArray();
    }

    /// <summary>Mínimos cuadrados por las ecuaciones normales (AᵀA·x = Aᵀb), con un pelín de regularización.</summary>
    private static double[] LeastSquares(double[,] a, double[] y)
    {
        int rows = a.GetLength(0), n = a.GetLength(1);
        var m = new double[n, n + 1];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int r = 0; r < rows; r++) s += a[r, i] * a[r, j];
                m[i, j] = s + (i == j ? 1e-9 : 0);
            }
            double t = 0;
            for (int r = 0; r < rows; r++) t += a[r, i] * y[r];
            m[i, n] = t;
        }
        // Gauss con pivote parcial
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
            if (piv != c)
                for (int j = 0; j <= n; j++) (m[c, j], m[piv, j]) = (m[piv, j], m[c, j]);
            for (int r = c + 1; r < n; r++)
            {
                double f = m[r, c] / m[c, c];
                for (int j = c; j <= n; j++) m[r, j] -= f * m[c, j];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = m[r, n];
            for (int j = r + 1; j < n; j++) s -= m[r, j] * x[j];
            x[r] = s / m[r, r];
        }
        return x;
    }

    /// <summary>
    /// Filtro de pico (recetario de RBJ) con el ancho dado en octavas y corregido para la transformación bilineal: así
    /// mide lo mismo a 100 Hz que a 16 kHz.
    /// </summary>
    private static double[] Peaking(double f0, double gainDb, double fs)
    {
        double a = Math.Pow(10, gainDb / 40);
        double w0 = 2 * Math.PI * f0 / fs;
        double sin = Math.Sin(w0), cos = Math.Cos(w0);
        double alpha = sin * Math.Sinh(Math.Log(2) / 2 * BandwidthOctaves * w0 / sin);
        double a0 = 1 + alpha / a;
        return new[] { (1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0 };
    }

    private static double SectionDb(double[] s, double f, double fs)
    {
        double w = 2 * Math.PI * f / fs;
        double c1 = Math.Cos(w), s1 = Math.Sin(w), c2 = Math.Cos(2 * w), s2 = Math.Sin(2 * w);
        double nr = s[0] + s[1] * c1 + s[2] * c2, ni = -(s[1] * s1 + s[2] * s2);
        double dr = 1 + s[3] * c1 + s[4] * c2, di = -(s[3] * s1 + s[4] * s2);
        return 10 * Math.Log10((nr * nr + ni * ni) / (dr * dr + di * di));
    }
}

/// <summary>
/// Aplica el ecualizador a audio en coma flotante de 32 bits (lo que se entrega a Windows en la salida normal), en el sitio.
/// Filtros en cascada por canal en doble precisión. Al cambiar un ajuste, el sonido pasa del diseño anterior al nuevo en
/// 10 ms (se oye al momento, sin el chasquido de cambiar los coeficientes de golpe). Un limitador de seguridad baja el
/// volumen solo en los picos que pasarían de 0 dB (en vez de deformar la onda) y lo recupera poco a poco.
/// </summary>
public sealed class EqualizerProcessor
{
    private const double FadeSeconds = 0.010;
    private const double Ceiling = 0.989;          // −0,1 dBFS
    private const double ReleaseSeconds = 0.150;   // lo que tarda el limitador en devolver el volumen

    private readonly int _channels;
    private readonly double _sampleRate;
    private readonly int _fadeFrames;
    private readonly double _release;

    private sealed class Chain
    {
        public readonly double[][] Sections;
        public readonly double Gain;
        public readonly double[] State;
        public Chain(EqDesign? design, int channels)
        {
            Sections = design?.Sections ?? Array.Empty<double[]>();
            Gain = design?.OutputGain ?? 1;
            State = new double[Sections.Length * channels * 2];
        }
        public bool IsIdentity => Sections.Length == 0 && Math.Abs(Gain - 1) < 1e-9;
    }

    private Chain _current;
    private Chain? _previous;     // la que se va apagando durante el cambio
    private int _fade;            // tramas que quedan de cambio
    private Equalizer.Snapshot? _applied;
    private double _limit = 1;    // ganancia del limitador ahora mismo
    // normalización de volumen de la canción que suena (lineal); al cambiar, va hacia la nueva en unos 5 ms
    private double _gain = 1, _gainTarget = 1;
    private readonly double _gainStep;

    public EqualizerProcessor(int sampleRate, int channels)
    {
        _channels = Math.Max(1, channels);
        _sampleRate = sampleRate;
        _fadeFrames = Math.Max(1, (int)(sampleRate * FadeSeconds));
        _release = 1 - Math.Exp(-1 / (sampleRate * ReleaseSeconds));
        _gainStep = 1 - Math.Exp(-1 / (sampleRate * 0.005));
        _current = new Chain(null, _channels);
    }

    /// <summary>
    /// Normalización de la canción, en dB. <paramref name="immediate"/>: al cargarla (empieza ya con su volumen); si no
    /// (enlace sin pausa, cambio de modo), pasa en unos milisegundos.
    /// </summary>
    public void SetGain(double db, bool immediate)
    {
        _gainTarget = Math.Pow(10, db / 20);
        if (immediate) _gain = _gainTarget;
    }

    /// <summary>Lector nuevo (salto atrás): los filtros empiezan de cero.</summary>
    public void Reset()
    {
        Array.Clear(_current.State);
        _previous = null;
        _fade = 0;
        _limit = 1;
    }

    public void Process(byte[] buffer, int offset, int count)
    {
        var eq = Equalizer.Current;
        // un cambio nuevo espera a que acabe el anterior (10 ms como mucho): así nunca se corta un fundido a medias
        if (!ReferenceEquals(eq, _applied) && _fade == 0) Switch(eq);
        // ecualizador apagado (o todo a 0) y sin normalización: la señal pasa intacta
        if (_fade == 0 && _current.IsIdentity && _limit >= 1 && _gain == 1 && _gainTarget == 1) return;

        var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count - count % 4));
        int frames = samples.Length / _channels;
        for (int f = 0; f < frames; f++)
        {
            int at = f * _channels;
            double mix = 0;
            if (_fade > 0) mix = (double)_fade / _fadeFrames; // peso de la cadena anterior, de 1 a 0
            double peak = 0;
            if (_gain != _gainTarget)
            {
                _gain += (_gainTarget - _gain) * _gainStep;
                if (Math.Abs(_gain - _gainTarget) < 1e-5) _gain = _gainTarget;
            }
            for (int ch = 0; ch < _channels; ch++)
            {
                double x = samples[at + ch] * _gain;
                double y = Run(_current, x, ch);
                if (_previous != null) y = y + (Run(_previous, x, ch) - y) * mix;
                samples[at + ch] = (float)y;
                peak = Math.Max(peak, Math.Abs(y));
            }
            if (_fade > 0 && --_fade == 0) _previous = null;

            // limitador: baja al momento lo que haga falta para no pasar del techo y vuelve despacio
            double need = peak * _limit > Ceiling ? Ceiling / peak : 1;
            _limit = need < _limit ? need : _limit + (1 - _limit) * _release;
            if (_limit < 0.99999)
                for (int ch = 0; ch < _channels; ch++) samples[at + ch] = (float)(samples[at + ch] * _limit);
            else _limit = 1;
        }
        Flush(_current.State);
        if (_previous != null) Flush(_previous.State);
    }

    private double Run(Chain c, double x, int ch)
    {
        x *= c.Gain;
        var st = c.State;
        int z = ch * c.Sections.Length * 2;
        foreach (var s in c.Sections)
        {
            double y = s[0] * x + st[z];
            st[z] = s[1] * x - s[3] * y + st[z + 1];
            st[z + 1] = s[2] * x - s[4] * y;
            x = y;
            z += 2;
        }
        return x;
    }

    private void Switch(Equalizer.Snapshot eq)
    {
        _applied = eq;
        var next = new Chain(eq.IsActive ? EqDesign.Create(eq, _sampleRate) : null, _channels);
        // la memoria de los filtros pasa al diseño nuevo (mismas bandas): arranca casi donde iba el anterior
        if (next.State.Length == _current.State.Length) Array.Copy(_current.State, next.State, next.State.Length);
        if (_current.IsIdentity && next.IsIdentity) return;
        _previous = _current;
        _current = next;
        _fade = _fadeFrames;
    }

    // estados diminutos (silencio): a cero, que los números desnormalizados son lentísimos
    private static void Flush(double[] state)
    {
        for (int i = 0; i < state.Length; i++)
            if (Math.Abs(state[i]) < 1e-25) state[i] = 0;
    }
}
