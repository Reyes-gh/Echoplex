using System.Text.Json;
using System.Windows.Threading;
using Echoplex.Models;
using NAudio.Wave;

namespace Echoplex.Services;

/// <summary>
/// El BPM que calcula Echoplex escuchando el audio (<see cref="BpmEstimator"/>), para las canciones que no lo traen en
/// sus etiquetas (encendido de serie; se apaga en Ajustes). Las del PC se analizan en segundo plano, una a una, en un hilo
/// de prioridad mínima y con pausas; las de carpetas en línea, nada más empezar a sonar, con la copia que se baja a memoria
/// para reproducirlas (según llega, sin descargar nada aparte); y las que están solo en OneDrive, cuando suenan enteras,
/// con el mismo audio que se reproduce (sin decodificarlas otra vez). Lo calculado se guarda en bpm-local.json; si el
/// cálculo no es seguro (<see cref="BpmEstimate.IsConfident"/>) se enseña con un aviso. No toca los archivos.
/// </summary>
public sealed class LocalBpmService
{
    private sealed class Entry
    {
        public long Len { get; set; }
        public long Ticks { get; set; }
        public double Bpm { get; set; }   // 0 = no se pudo calcular
        public double Conf { get; set; }  // seguridad (0 a 1)
    }

    private readonly Dispatcher _ui;
    private readonly string _file = Path.Combine(SettingsStore.DataDirectory, "bpm-local.json");
    private readonly Dictionary<string, Entry> _cache;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private List<Song> _all = new();
    private int _index;
    private volatile bool _enabled;
    private bool _started;
    private int _unsaved;
    // la que suena y, si es de una carpeta en línea y se está calculando, ese cálculo (solo en el hilo de la interfaz)
    private Song? _playing;
    private Song? _listeningSong;
    private CancellationTokenSource? _listening;

    public LocalBpmService(Dispatcher ui)
    {
        _ui = ui;
        try { _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file)) ?? new(); }
        catch { _cache = new(); }
        _cache = new Dictionary<string, Entry>(_cache, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Lo calculado de esta canción, si es de este mismo archivo (si cambió, se vuelve a calcular).</summary>
    private Entry? Get(Song s)
    {
        lock (_lock)
            return _cache.TryGetValue(s.Path, out var e) && e.Len == s.Length && e.Ticks == s.LastWriteTicks ? e : null;
    }

    /// <summary>Lo que se enseña: el BPM redondeado (0 si no se pudo calcular) y si es seguro.</summary>
    private static (uint bpm, bool sure) Shown(Entry e) =>
        e.Bpm > 0 ? ((uint)Math.Round(e.Bpm), new BpmEstimate(e.Bpm, e.Conf).IsConfident) : (0, false);

    /// <summary>Encendido o apagado (Ajustes). Apagado no analiza nada y quita de las canciones lo calculado.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Apply(_all);
            if (value) _signal.Release();
            // la que suena: se calcula ya si hace falta, o se deja de calcular
            if (value && _playing != null) Started(_playing);
            else if (!value) StopListening();
        }
    }

    /// <summary>Pone a las canciones el BPM ya calculado (en el hilo de la interfaz).</summary>
    private void Apply(IEnumerable<Song> songs)
    {
        foreach (var s in songs)
        {
            var (bpm, sure) = _enabled && Get(s) is { } e ? Shown(e) : (0, false);
            s.LocalBpm = bpm;
            s.LocalBpmSure = sure;
        }
    }

    public void Start(List<Song> all, bool enabled)
    {
        lock (_lock) { _all = all; _index = 0; }
        _enabled = enabled;
        Apply(all);
        if (_started) { _signal.Release(); return; }
        _started = true;
        new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Calcular BPM" }.Start();
        System.Windows.Application.Current.Exit += (_, _) => Save();
    }

    /// <summary>Han llegado etiquetas nuevas: puede que ahora se sepa que alguna no trae el BPM.</summary>
    public void Wake()
    {
        lock (_lock) _index = 0;
        _signal.Release();
    }

    /// <summary>
    /// ¿Se analiza en segundo plano? Del PC (no de carpetas en línea ni solo en OneDrive: esas, al sonar), con las
    /// etiquetas ya leídas, sin BPM en ellas y sin calcular aún.
    /// </summary>
    private bool Needs(Song s) =>
        s.MetadataLoaded && s.TagBpm == 0 && !OnlineLibrary.IsOnline(s.Path) && !s.IsCloud && Get(s) == null;

    private void Loop()
    {
        while (true)
        {
            Song? song = null;
            if (_enabled)
                lock (_lock)
                    while (_index < _all.Count)
                    {
                        var c = _all[_index++];
                        if (Needs(c)) { song = c; break; }
                    }
            if (song == null)
            {
                Save();
                _signal.Wait();
                continue;
            }
            // ilegible: se apunta igual (sin BPM) para no volver a intentarlo en cada arranque
            Put(song, Analyze(song.Path) ?? new BpmEstimate(0, 0));
            Thread.Sleep(30);
        }
    }

    /// <summary>
    /// ¿Se mide el tempo de esta mientras suena? (desde el hilo de audio, al empezar cada canción) Solo con el cálculo
    /// encendido y si aún no está calculada: con el BPM ya sabido no se gasta nada en medirlo. Las de carpetas en línea, no:
    /// esas se calculan al empezar a sonar (<see cref="Started"/>).
    /// </summary>
    public bool Wants(string path)
    {
        if (!_enabled || OnlineLibrary.IsOnline(path)) return false;
        lock (_lock) return !_cache.ContainsKey(path);
    }

    /// <summary>
    /// Empezó a sonar una canción (desde el hilo de la interfaz). Si es de una carpeta en línea y no se sabe su BPM, se
    /// calcula ya con la copia que Echoplex baja a memoria para reproducirla, sin descargar nada aparte: se va decodificando
    /// según llega, de principio a fin, y con la canción entera sale el BPM. Mientras, su casilla enseña que se calcula.
    /// </summary>
    public void Started(Song song)
    {
        _playing = song;
        StopListening(); // la de antes ya no suena: su copia se suelta de memoria
        if (!_enabled || !OnlineLibrary.IsOnline(song.Path) || song.TagBpm > 0 || song.OnlineBpm > 0 || Get(song) != null) return;
        var cts = _listening = new CancellationTokenSource();
        _listeningSong = song;
        song.BpmCalculating = true;
        new Thread(() =>
        {
            var estimate = AnalyzeDownload(song.Path, cts.Token);
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                // la de ahora, por si entretanto se recargó la biblioteca (Remap)
                var target = _listening == cts ? _listeningSong! : song;
                if (_listening == cts)
                {
                    _listening = null;
                    _listeningSong = null;
                }
                if (estimate is { } e) Measured(target, e);
                // después de poner el BPM (que también va en segundo plano), para que la casilla no se quede un momento
                // vacía; y salvo que la misma canción haya vuelto a empezar y se esté calculando otra vez
                _ui.BeginInvoke(DispatcherPriority.Background, () => target.BpmCalculating = _listeningSong == target);
            });
        }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Calcular BPM en línea" }.Start();
    }

    /// <summary>Deja de calcular la que se estaba calculando (y quita su círculo al momento).</summary>
    private void StopListening()
    {
        _listening?.Cancel();
        _listening = null;
        if (_listeningSong != null) _listeningSong.BpmCalculating = false;
        _listeningSong = null;
    }

    /// <summary>
    /// Se recargó la biblioteca (canciones nuevas para las mismas rutas): la que suena y la que se calcula pasan a las
    /// nuevas, para que el círculo y el BPM salgan en la tabla de ahora.
    /// </summary>
    public void Remap(Func<string, Song?> find)
    {
        if (_playing != null && find(_playing.Path) is { } playing) _playing = playing;
        if (_listeningSong != null && find(_listeningSong.Path) is { } song && song != _listeningSong)
        {
            _listeningSong.BpmCalculating = false;
            _listeningSong = song;
            song.BpmCalculating = true;
        }
    }

    /// <summary>Decodifica la canción entera y calcula su tempo; null si no se puede leer o es muy corta.</summary>
    private static BpmEstimate? Analyze(string path)
    {
        try
        {
            using var reader = AudioOutput.OpenForAnalysis(path);
            return Measure(reader, default);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decodifica la copia en memoria de la que suena, de principio a fin según se baja, y calcula su tempo. Null si no se
    /// leyó entera (dejó de sonar, se apagó el cálculo o no se pudo leer); sin BPM (0) si es muy corta.
    /// </summary>
    private static BpmEstimate? AnalyzeDownload(string path, CancellationToken ct)
    {
        if (OnlineLibrary.OpenDownloaded(path, ct) is not { } stream) return null;
        try
        {
            using var reader = AudioOutput.OpenForAnalysis(stream);
            var estimate = Measure(reader, ct);
            return stream.GaveUp || ct.IsCancellationRequested ? null : estimate ?? new BpmEstimate(0, 0);
        }
        catch
        {
            return null;
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>Decodifica lo que queda del lector y calcula su tempo; null si es muy corto.</summary>
    private static BpmEstimate? Measure(WaveStream reader, CancellationToken ct)
    {
        var estimator = new BpmEstimator(reader.WaveFormat.SampleRate, reader.WaveFormat.Channels);
        var samples = reader.ToSampleProvider();
        var buf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels / 2];
        int n;
        while ((n = samples.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            estimator.Add(buf.AsSpan(0, n));
        }
        return estimator.Estimate();
    }

    /// <summary>Lo calculado mientras sonaba entera (desde el hilo de la interfaz). Las que traen BPM en sus etiquetas, no.</summary>
    public void Measured(Song song, BpmEstimate estimate)
    {
        if (song.TagBpm > 0 || Get(song) != null) return;
        Put(song, estimate);
    }

    private void Put(Song song, BpmEstimate estimate)
    {
        var entry = new Entry { Len = song.Length, Ticks = song.LastWriteTicks, Bpm = Math.Round(estimate.Bpm, 2), Conf = Math.Round(estimate.Confidence, 3) };
        lock (_lock)
        {
            _cache[song.Path] = entry;
            _unsaved++;
        }
        var (bpm, sure) = Shown(entry);
        if (_enabled && bpm > 0)
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                song.LocalBpm = bpm;
                song.LocalBpmSure = sure;
            });
        if (_unsaved >= 25) Save();
    }

    private void Save()
    {
        string json;
        lock (_lock)
        {
            if (_unsaved == 0) return;
            _unsaved = 0;
            json = JsonSerializer.Serialize(_cache);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            lock (_lock) _unsaved++; // se guardará la próxima vez
        }
    }
}
