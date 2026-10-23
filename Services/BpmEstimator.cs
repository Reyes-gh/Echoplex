namespace Echoplex.Services;

/// <summary>Tempo calculado del audio: pulsaciones por minuto y lo seguro que es (0 a 1).</summary>
public readonly record struct BpmEstimate(double Bpm, double Confidence)
{
    /// <summary>
    /// Seguridad mínima para darlo por bueno. Medido con 261 canciones de BPM conocido (157 de Deezer, 104 de sus
    /// etiquetas): mostrando las de seguridad ≥ 0,2 (unas dos de cada tres) acierta el 91-94 % (el 75-85 % sin contar
    /// las que salen al doble o a la mitad, que tampoco Deezer acierta siempre); con todas, el 82 %.
    /// </summary>
    public const double MinConfidence = 0.2;

    public bool IsConfident => Confidence >= MinConfidence;
}

/// <summary>
/// Calcula el tempo de una canción escuchando su audio, sin internet. Se le pasan las muestras de la canción entera
/// (con el principio de 30 s acierta bastante menos). El método es el clásico de autocorrelación de la envolvente de
/// golpes:
/// <list type="number">
/// <item>mono y a ~11 kHz (con filtro antes de reducir);</item>
/// <item>espectro cada 23 ms (1024 muestras), en 40 bandas mel de 30 a 5000 Hz, en escala logarítmica;</item>
/// <item>envolvente de golpes: lo que sube cada banda de una trama a la siguiente, de media;</item>
/// <item>autocorrelación de la envolvente entre 40 y 300 BPM, con preferencia por los tempos cercanos a 120 (una
/// octava de anchura, como librosa); el pico ganador, afinado con una parábola.</item>
/// </list>
/// La seguridad es cuánto gana ese pico al mejor rival que no sea el mismo pulso al doble, a la mitad, al cuádruple o a la
/// cuarta parte (esos dicen lo mismo): con un rival a 2/3 o 4/3 casi igual de fuerte, el tempo es dudoso.
/// </summary>
public sealed class BpmEstimator
{
    private const double TargetRate = 11025;
    private const int Nfft = 1024, Hop = 256, Mels = 40;
    private const double MinBpm = 40, MaxBpm = 300, PriorBpm = 120, PriorOctaves = 1;

    private readonly int _channels;
    private readonly int _decimation;
    private readonly double _rate;        // frecuencia tras reducir
    private readonly float[] _fir;        // filtro paso bajo antes de reducir
    private readonly float[] _history;    // últimas muestras mono a la frecuencia original (circular, duplicado para leer seguido)
    private int _historyPos;
    private int _phase;                   // muestras mono desde la última que se quedó al reducir
    private readonly float[] _samples = new float[Nfft]; // últimas muestras reducidas (circular)
    private long _reduced;                // muestras reducidas en total
    private readonly float[] _window = new float[Nfft];
    private readonly (int from, float[] weights)[] _bands = new (int, float[])[Mels];
    private readonly float[] _re = new float[Nfft], _im = new float[Nfft];
    private readonly float[] _cos = new float[Nfft / 2], _sin = new float[Nfft / 2];
    private readonly int[] _bitrev = new int[Nfft];
    // bandas de la trama actual y de la anterior (se reutilizan: se llama desde el hilo de audio)
    private double[] _mel = new double[Mels], _previous = new double[Mels];
    private bool _hasPrevious;
    private readonly List<float> _flux = new();
    private static readonly double[] SamePulse = { 1, 2, 0.5, 4, 0.25 };

    public BpmEstimator(int sampleRate, int channels)
    {
        _channels = Math.Max(1, channels);
        _decimation = Math.Max(1, (int)Math.Round(sampleRate / TargetRate));
        _rate = (double)sampleRate / _decimation;

        // paso bajo (seno cardinal con ventana de Blackman) por debajo de la mitad de la frecuencia reducida
        int taps = _decimation == 1 ? 1 : 16 * _decimation + 1;
        _fir = new float[taps];
        double cutoff = 0.45 / _decimation, sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - (taps - 1) / 2.0;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double w = taps == 1 ? 1 : 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            _fir[i] = (float)(sinc * w);
            sum += _fir[i];
        }
        for (int i = 0; i < taps; i++) _fir[i] = (float)(_fir[i] / sum);
        _history = new float[2 * taps];

        for (int i = 0; i < Nfft; i++) _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Nfft - 1)));
        for (int i = 0; i < Nfft / 2; i++)
        {
            _cos[i] = (float)Math.Cos(-2 * Math.PI * i / Nfft);
            _sin[i] = (float)Math.Sin(-2 * Math.PI * i / Nfft);
        }
        int bits = (int)Math.Log2(Nfft);
        for (int i = 0; i < Nfft; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b);
            _bitrev[i] = r;
        }

        // bandas mel triangulares de 30 a 5000 Hz sobre las frecuencias del espectro
        static double ToMel(double hz) => 2595 * Math.Log10(1 + hz / 700);
        static double ToHz(double mel) => 700 * (Math.Pow(10, mel / 2595) - 1);
        var points = new double[Mels + 2];
        for (int i = 0; i < points.Length; i++) points[i] = ToHz(ToMel(30) + (ToMel(5000) - ToMel(30)) * i / (Mels + 1));
        for (int m = 0; m < Mels; m++)
        {
            double lo = points[m], center = points[m + 1], hi = points[m + 2];
            var weights = new List<float>();
            int from = -1;
            for (int k = 0; k <= Nfft / 2; k++)
            {
                double f = k * _rate / Nfft;
                double w = Math.Max(0, Math.Min((f - lo) / (center - lo), (hi - f) / (hi - center)));
                if (w <= 0) continue;
                if (from < 0) from = k;
                while (from + weights.Count < k) weights.Add(0);
                weights.Add((float)w);
            }
            _bands[m] = (Math.Max(from, 0), weights.ToArray());
        }
    }

    /// <summary>Segundos de audio recibidos.</summary>
    public double Seconds => _reduced / _rate;

    /// <summary>Muestras intercaladas en coma flotante (−1 a 1), en orden y sin saltos.</summary>
    public void Add(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        int taps = _fir.Length;
        for (int f = 0; f < frames; f++)
        {
            float mono = 0;
            for (int ch = 0; ch < _channels; ch++) mono += interleaved[f * _channels + ch];
            mono /= _channels;
            _history[_historyPos] = mono;
            _history[_historyPos + taps] = mono;
            _historyPos = (_historyPos + 1) % taps;
            if (++_phase < _decimation) continue;
            _phase = 0;
            // la muestra reducida: el filtro sobre las últimas «taps» (la más antigua en _historyPos)
            float y = 0;
            for (int i = 0; i < taps; i++) y += _fir[i] * _history[_historyPos + i];
            _samples[_reduced % Nfft] = y;
            _reduced++;
            if (_reduced >= Nfft && (_reduced - Nfft) % Hop == 0) Frame();
        }
    }

    /// <summary>Una trama de 1024 muestras reducidas: su espectro en bandas mel y lo que ha subido desde la anterior.</summary>
    private void Frame()
    {
        long start = _reduced - Nfft;
        for (int i = 0; i < Nfft; i++)
        {
            int j = _bitrev[i];
            _re[j] = _samples[(start + i) % Nfft] * _window[i];
            _im[j] = 0;
        }
        for (int size = 2; size <= Nfft; size <<= 1)
        {
            int half = size >> 1, step = Nfft / size;
            for (int s = 0; s < Nfft; s += size)
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step], wi = _sin[k * step];
                    int a = s + k, b = a + half;
                    float tr = wr * _re[b] - wi * _im[b], ti = wr * _im[b] + wi * _re[b];
                    _re[b] = _re[a] - tr;
                    _im[b] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
        }
        for (int m = 0; m < Mels; m++)
        {
            var (from, weights) = _bands[m];
            double e = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                int k = from + i;
                e += weights[i] * Math.Sqrt((double)_re[k] * _re[k] + (double)_im[k] * _im[k]);
            }
            _mel[m] = Math.Log(1 + 100 * e);
        }
        if (_hasPrevious)
        {
            double rise = 0;
            for (int m = 0; m < Mels; m++) rise += Math.Max(0, _mel[m] - _previous[m]);
            _flux.Add((float)(rise / Mels));
        }
        (_previous, _mel) = (_mel, _previous);
        _hasPrevious = true;
    }

    /// <summary>El tempo de lo recibido, o null si no hay bastante (menos de 10 s) o es silencio.</summary>
    public BpmEstimate? Estimate()
    {
        double fps = _rate / Hop;
        int n = _flux.Count;
        if (n < fps * 10) return null;
        double mean = 0;
        foreach (var v in _flux) mean += v;
        mean /= n;
        var env = new double[n];
        for (int i = 0; i < n; i++) env[i] = _flux[i] - mean;

        int lmin = (int)(fps * 60 / MaxBpm), lmax = (int)(fps * 60 / MinBpm) + 1;
        if (lmin < 2 || lmax + 1 >= n) return null;
        var ac = new double[lmax + 2];
        for (int lag = lmin - 1; lag <= lmax + 1; lag++)
        {
            double s = 0;
            for (int t = 0; t + lag < n; t++) s += env[t] * env[t + lag];
            ac[lag] = s / n;
        }
        int count = lmax - lmin + 1;
        var score = new double[count];
        var bpm = new double[count];
        int best = 0;
        for (int i = 0; i < count; i++)
        {
            bpm[i] = 60 * fps / (lmin + i);
            double octaves = Math.Log2(bpm[i] / PriorBpm) / PriorOctaves;
            score[i] = ac[lmin + i] * Math.Exp(-0.5 * octaves * octaves);
            if (score[i] > score[best]) best = i;
        }
        if (score[best] <= 0) return null;
        int lag0 = lmin + best;
        double a = ac[lag0 - 1], b = ac[lag0], c = ac[lag0 + 1], den = a - 2 * b + c;
        double tempo = 60 * fps / (lag0 + (den != 0 ? 0.5 * (a - c) / den : 0));

        // el mejor pico rival que no sea el mismo pulso a otra octava
        double rival = 0;
        for (int i = 1; i < count - 1; i++)
        {
            if (!(score[i] > score[i - 1] && score[i] >= score[i + 1])) continue;
            bool related = false;
            foreach (var k in SamePulse)
                if (Math.Abs(bpm[i] - tempo * k) <= 0.06 * tempo * k) related = true;
            if (!related) rival = Math.Max(rival, score[i]);
        }
        return new BpmEstimate(tempo, 1 - rival / score[best]);
    }
}
