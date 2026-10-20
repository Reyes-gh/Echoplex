using System.Text.Json;
using Echoplex.Models;
using NAudio.Wave;

namespace Echoplex.Services;

/// <summary>
/// Sonoridad de una canción como la mide la norma EBU R128 (la de ReplayGain 2.0): filtro de ponderación K, bloques
/// de 400 ms que se solapan un 75 %, y dos puertas (absoluta a −70 LUFS y relativa a 10 LU por debajo) para que los
/// silencios y los pasajes muy flojos no cuenten. También el pico de muestra.
/// </summary>
public sealed class LoudnessMeter
{
    private readonly int _channels;
    private readonly int _subBlock;               // 100 ms en muestras por canal
    private readonly double[] _pre = new double[5], _rlb = new double[5];
    private readonly double[] _state;             // 2 filtros × 2 estados por canal
    private double _sum;                          // energía del bloque de 100 ms en curso
    private int _count;
    private readonly List<double> _subBlocks = new();
    private double _peak;

    public LoudnessMeter(int sampleRate, int channels)
    {
        _channels = Math.Max(1, channels);
        _subBlock = Math.Max(1, sampleRate / 10);
        _state = new double[_channels * 4];
        // coeficientes de la norma recalculados para esta frecuencia de muestreo (como libebur128)
        double k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate), q = 0.7071752369554196;
        double vh = Math.Pow(10, 3.999843853973347 / 20), vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1 + k / q + k * k;
        _pre[0] = (vh + vb * k / q + k * k) / a0;
        _pre[1] = 2 * (k * k - vh) / a0;
        _pre[2] = (vh - vb * k / q + k * k) / a0;
        _pre[3] = 2 * (k * k - 1) / a0;
        _pre[4] = (1 - k / q + k * k) / a0;
        k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
        q = 0.5003270373238773;
        a0 = 1 + k / q + k * k;
        _rlb[0] = 1;
        _rlb[1] = -2;
        _rlb[2] = 1;
        _rlb[3] = 2 * (k * k - 1) / a0;
        _rlb[4] = (1 - k / q + k * k) / a0;
    }

    /// <summary>Muestras intercaladas en coma flotante (−1 a 1).</summary>
    public void Add(ReadOnlySpan<float> samples)
    {
        int frames = samples.Length / _channels;
        for (int f = 0; f < frames; f++)
        {
            for (int ch = 0; ch < _channels; ch++)
            {
                double x = samples[f * _channels + ch];
                double ax = Math.Abs(x);
                if (ax > _peak) _peak = ax;
                int z = ch * 4;
                double y = _pre[0] * x + _state[z];
                _state[z] = _pre[1] * x - _pre[3] * y + _state[z + 1];
                _state[z + 1] = _pre[2] * x - _pre[4] * y;
                double w = y + _state[z + 2];
                _state[z + 2] = -2 * y - _rlb[3] * w + _state[z + 3];
                _state[z + 3] = y - _rlb[4] * w;
                _sum += w * w;
            }
            if (++_count == _subBlock)
            {
                _subBlocks.Add(_sum / _subBlock);
                _sum = 0;
                _count = 0;
            }
        }
    }

    public double Peak => _peak;
    public double Seconds => _subBlocks.Count / 10.0;

    /// <summary>Sonoridad integrada en LUFS (NaN si no hay bastante audio o es silencio).</summary>
    public double Integrated()
    {
        // bloques de 400 ms con solape del 75 %: cuatro de 100 ms seguidos
        var blocks = new List<double>();
        for (int i = 3; i < _subBlocks.Count; i++)
            blocks.Add((_subBlocks[i] + _subBlocks[i - 1] + _subBlocks[i - 2] + _subBlocks[i - 3]) / 4);
        static double Lufs(double energy) => -0.691 + 10 * Math.Log10(energy);
        var gated = blocks.Where(e => e > 0 && Lufs(e) > -70).ToList();
        if (gated.Count == 0) return double.NaN;
        double relative = Lufs(gated.Average()) - 10;
        var final = gated.Where(e => Lufs(e) > relative).ToList();
        return final.Count == 0 ? double.NaN : Lufs(final.Average());
    }
}

/// <summary>Lo medido de cada canción (sonoridad y pico), guardado en loudness.json para no volver a medirla.</summary>
public static class LoudnessStore
{
    public sealed class Entry
    {
        public long Len { get; set; }
        public long Ticks { get; set; }
        public double Lufs { get; set; }
        public double Peak { get; set; }
        public double Seconds { get; set; }
    }

    private static readonly object Lock = new();
    private static Dictionary<string, Entry>? _map;
    private static bool _dirty;

    private static string FilePath => Path.Combine(SettingsStore.DataDirectory, "loudness.json");

    private static Dictionary<string, Entry> Map
    {
        get
        {
            if (_map != null) return _map;
            try
            {
                _map = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch
            {
                _map = new();
            }
            _map = new Dictionary<string, Entry>(_map, StringComparer.OrdinalIgnoreCase);
            return _map;
        }
    }

    public static Entry? Get(Song s)
    {
        lock (Lock)
            return Map.TryGetValue(s.Path, out var e) && e.Len == s.Length && e.Ticks == s.LastWriteTicks ? e : null;
    }

    public static void Put(Song s, double lufs, double peak, double seconds)
    {
        if (double.IsNaN(lufs)) return;
        lock (Lock)
        {
            Map[s.Path] = new Entry { Len = s.Length, Ticks = s.LastWriteTicks, Lufs = Math.Round(lufs, 2), Peak = Math.Round(peak, 4), Seconds = Math.Round(seconds, 1) };
            _dirty = true;
        }
    }

    public static void Save()
    {
        Dictionary<string, Entry> copy;
        lock (Lock)
        {
            if (!_dirty || _map == null) return;
            copy = new Dictionary<string, Entry>(_map);
            _dirty = false;
        }
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(copy));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            lock (Lock) _dirty = true; // se intentará en el próximo guardado
        }
    }
}

/// <summary>
/// Normalización de volumen: cuánto subir o bajar cada canción para que todas suenen igual de fuertes (−18 LUFS, la
/// referencia de ReplayGain 2.0). Primero lo que digan sus etiquetas ReplayGain; si no tienen, lo que midió Echoplex
/// (al sonar entera una vez, o con «Medir la biblioteca»). Nunca sube tanto que el pico pase de 0 dB.
/// </summary>
public static class Normalization
{
    public const string Off = "off", Track = "track", Album = "album", Smart = "smart";
    private const double Reference = -18;

    /// <param name="albumMode">Por álbum: las canciones de un disco conservan sus diferencias (una intro flojita sigue siéndolo).</param>
    /// <param name="albumSongs">Las del mismo álbum, para calcular su sonoridad conjunta si no hay etiqueta de álbum.</param>
    public static double GainDb(Song song, bool albumMode, IReadOnlyList<Song> albumSongs)
    {
        double? gain = null, peak = null;
        if (albumMode && song.AlbumGain is { } ag)
        {
            gain = ag;
            peak = song.AlbumPeak ?? song.TrackPeak;
        }
        else if (!albumMode && song.TrackGain is { } tg)
        {
            gain = tg;
            peak = song.TrackPeak;
        }
        else if (LoudnessStore.Get(song) is { } m)
        {
            if (albumMode && AlbumLoudness(albumSongs) is { } album)
            {
                gain = Reference - album.lufs;
                peak = album.peak;
            }
            else
            {
                gain = Reference - m.Lufs;
                peak = m.Peak;
            }
        }
        else if (song.TrackGain is { } fallback) // por álbum sin etiqueta de álbum ni medidas: la de la pista
        {
            gain = fallback;
            peak = song.TrackPeak;
        }
        if (gain is not { } g) return 0;
        // que el pico no pase de 0 dB (un poco por debajo): ni saturar ni que tenga que actuar el limitador
        if (peak is > 0 and var p) g = Math.Min(g, -20 * Math.Log10(p) - 0.3);
        return Math.Clamp(g, -24, 18);
    }

    /// <summary>
    /// Los valores ReplayGain «en bruto» (dB respecto a −18 LUFS y picos lineales), de las etiquetas o, si no las hay, de lo
    /// que midió Echoplex: los que se mandan al móvil para que normalice él. null lo que no se sabe.
    /// </summary>
    public static (double? trackGain, double? trackPeak, double? albumGain, double? albumPeak) Raw(Song song, IReadOnlyList<Song> albumSongs)
    {
        double? tg = song.TrackGain, tp = song.TrackPeak, ag = song.AlbumGain, ap = song.AlbumPeak;
        if (tg == null && LoudnessStore.Get(song) is { } m)
        {
            tg = Reference - m.Lufs;
            tp = m.Peak;
        }
        if (ag == null && albumSongs.Count > 0 && AlbumLoudness(albumSongs) is { } a)
        {
            ag = Reference - a.lufs;
            ap = a.peak;
        }
        return (tg, tp, ag, ap);
    }

    /// <summary>Sonoridad conjunta de un álbum (media de energía por duración) si están medidas todas sus canciones.</summary>
    private static (double lufs, double peak)? AlbumLoudness(IReadOnlyList<Song> songs)
    {
        double energy = 0, seconds = 0, peak = 0;
        foreach (var s in songs)
        {
            if (LoudnessStore.Get(s) is not { } m) return null;
            energy += Math.Pow(10, m.Lufs / 10) * m.Seconds;
            seconds += m.Seconds;
            peak = Math.Max(peak, m.Peak);
        }
        return seconds > 0 ? (10 * Math.Log10(energy / seconds), peak) : null;
    }
}

/// <summary>
/// «Medir la biblioteca»: decodifica las canciones del PC que no tienen etiquetas ReplayGain ni medida, una a una, en un
/// hilo de prioridad mínima y con pausas (para no quitarle CPU a nada). Las de carpetas en línea y las que están solo
/// en OneDrive no se tocan: se miden cuando suenan.
/// </summary>
public sealed class LoudnessAnalyzer
{
    private CancellationTokenSource? _cts;
    public bool IsRunning => _cts != null;

    /// <summary>Por dónde va (hechas, total), desde el hilo de fondo.</summary>
    public event Action<int, int>? Progress;
    public event Action<int>? Finished;

    public static bool NeedsMeasure(Song s) =>
        !OnlineLibrary.IsOnline(s.Path) && !s.IsCloud && s.TrackGain == null && LoudnessStore.Get(s) == null;

    public void Start(IReadOnlyList<Song> songs)
    {
        if (_cts != null) return;
        var todo = songs.Where(NeedsMeasure).ToList();
        var cts = _cts = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            int done = 0;
            foreach (var s in todo)
            {
                if (cts.IsCancellationRequested) break;
                Measure(s, cts.Token);
                Progress?.Invoke(++done, todo.Count);
                if (done % 20 == 0) LoudnessStore.Save();
                Thread.Sleep(30);
            }
            LoudnessStore.Save();
            _cts = null;
            Finished?.Invoke(done);
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Medir volumen" };
        thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    /// <summary>Decodifica la canción entera y guarda su sonoridad. En silencio si falla.</summary>
    public static void Measure(Song s, CancellationToken ct = default)
    {
        try
        {
            using var reader = AudioOutput.OpenForAnalysis(s.Path);
            var meter = new LoudnessMeter(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
            var sample = reader.ToSampleProvider();
            var buf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 2];
            int n;
            while ((n = sample.Read(buf, 0, buf.Length)) > 0)
            {
                if (ct.IsCancellationRequested) return;
                meter.Add(buf.AsSpan(0, n));
            }
            LoudnessStore.Put(s, meter.Integrated(), meter.Peak, meter.Seconds);
        }
        catch
        {
            // ilegible: se queda sin medir
        }
    }
}
