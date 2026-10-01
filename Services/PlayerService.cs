using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;

namespace Echoplex.Services;

public enum RepeatMode { Off, All, One }

/// <summary>
/// Reproductor con cola propia, aleatorio, repetición y radio automática. Suena por WASAPI (modo
/// normal o bit a bit) y se integra con los controles multimedia de Windows.
/// </summary>
public sealed partial class PlayerService : ObservableObject, IDisposable
{
    private const double CountAfterSeconds = 30;

    private readonly AudioOutput _out;
    private readonly MediaControls _smtc = new();
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _errorTimer;
    private readonly DispatcherTimer _deviceTimer;
    private readonly Stopwatch _tickClock = Stopwatch.StartNew();
    private readonly Random _rng = new();
    private readonly List<Song> _userQueue = new();
    private List<Song> _context = new();
    private List<int> _order = new();
    private int _pos = -1;
    private int _loadVersion;
    private int _failures;
    private bool _needsLoad;
    private double _listenedThisLoad;
    private bool _countedThisLoad;
    private bool _forceShared;
    private string? _lastNotice;
    private bool _loading;
    /// <summary>Si la carga en curso debe empezar a sonar al terminar (play/pausa durante la carga).</summary>
    private bool _autoplayPending;
    /// <summary>Canción preparada en la salida para enlazar sin pausa, o null.</summary>
    private Song? _gaplessNext;
    /// <summary>Segundos antes del final en los que se abre la siguiente (margen para OneDrive o discos lentos).</summary>
    private const double PrepareAheadSeconds = 12;

    public PlayerService(Dispatcher ui)
    {
        _ui = ui;
        _out = new AudioOutput(ui);
        _out.Ended += OnEnded;
        _out.Advanced += OnGaplessAdvanced;
        _out.Failed += OnFailed;
        _out.PlayingChanged += playing =>
        {
            IsPlaying = playing;
            _smtc.SetStatus(playing ? MediaControls.StatusPlaying : MediaControls.StatusPaused);
        };
        // Se apagaron los cascos, etc.: quedamos en pausa hasta que Windows elija otra salida.
        _out.DeviceLost += () => IsPlaying = false;
        _out.DefaultDeviceChanged += () =>
        {
            _deviceTimer!.Stop();
            _deviceTimer.Start();
        };

        _smtc.ButtonPressed += button => _ui.BeginInvoke(() =>
        {
            switch (button)
            {
                case MediaControls.ButtonPlay: Play(); break;
                case MediaControls.ButtonPause: Pause(); break;
                case MediaControls.ButtonStop: Pause(); break;
                case MediaControls.ButtonNext: Next(); break;
                case MediaControls.ButtonPrevious: Previous(); break;
            }
        });

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (s, e) => Tick(), ui);
        _errorTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal,
            (s, e) => { ((DispatcherTimer)s!).Stop(); Error = null; }, ui);
        _errorTimer.Stop();
        _deviceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Normal,
            (s, e) => { ((DispatcherTimer)s!).Stop(); FollowDefaultDevice(); }, ui);
        _deviceTimer.Stop();
    }

    public Func<Song, string?>? CoverFileProvider { get; set; }

    /// <summary>Genera más canciones parecidas cuando la lista se acaba (radio automática).</summary>
    public Func<Song, IReadOnlyCollection<Song>, List<Song>>? RadioProvider { get; set; }

    public event Action<Song>? SongOpened;
    public event Action<string>? ContextStarted;
    public event Action<Song, double>? Listened;
    public event Action<Song>? PlayCounted;
    /// <summary>Avisos para el usuario (p. ej. por qué no se pudo usar la salida bit a bit).</summary>
    public event Action<string>? Notice;

    public ObservableCollection<Song> QueueNext { get; } = new();
    public ObservableCollection<Song> ContextNext { get; } = new();

    [ObservableProperty] private Song? _current;
    [ObservableProperty] private string? _contextId;
    [ObservableProperty] private string _contextName = "";
    [ObservableProperty] private bool _hasQueue;
    [ObservableProperty] private bool _autoRadio = true;
    [ObservableProperty] private bool _stopAfterCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph))]
    private bool _isPlaying;

    [ObservableProperty] private bool _isBuffering;
    [ObservableProperty] private string _loadingText = "Cargando…";
    [ObservableProperty] private double _position;
    [ObservableProperty] private double _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph))]
    private double _volume = 0.7;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph))]
    private bool _isMuted;

    [ObservableProperty] private bool _shuffle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatGlyph), nameof(IsRepeatOn), nameof(RepeatTip))]
    private RepeatMode _repeat;

    [ObservableProperty] private string? _error;

    /// <summary>Salida WASAPI exclusiva (bit a bit) activada por el usuario.</summary>
    [ObservableProperty] private bool _exclusiveMode;
    /// <summary>Dispositivo para la salida bit a bit (null = el predeterminado de Windows).</summary>
    [ObservableProperty] private string? _exclusiveDeviceId;
    /// <summary>Por dónde y cómo está sonando la canción actual.</summary>
    [ObservableProperty] private string _outputInfo = "Salida normal de Windows";

    public string PlayGlyph => IsPlaying ? "" : "";
    public string RepeatGlyph => Repeat == RepeatMode.One ? "" : "";
    public bool IsRepeatOn => Repeat != RepeatMode.Off;
    public string RepeatTip => Repeat switch
    {
        RepeatMode.Off => "Repetir: no (Ctrl+R)",
        RepeatMode.All => "Repetir: todo (Ctrl+R)",
        _ => "Repetir: una canción (Ctrl+R)",
    };
    public string VolumeGlyph => IsMuted || Volume <= 0.001 ? "" : Volume < 0.34 ? "" : Volume < 0.67 ? "" : "";

    partial void OnVolumeChanged(double value) => _out.SetVolume(value, IsMuted);
    partial void OnIsMutedChanged(bool value) => _out.SetVolume(Volume, value);
    partial void OnShuffleChanged(bool value)
    {
        if (_context.Count == 0 || _pos < 0) return;
        BuildOrder(_order[_pos]);
        UpdateUpNext();
    }
    partial void OnRepeatChanged(RepeatMode value) => UpdateUpNext();
    partial void OnStopAfterCurrentChanged(bool value) => CheckGapless();
    partial void OnExclusiveModeChanged(bool value) => Reroute();
    partial void OnExclusiveDeviceIdChanged(string? value)
    {
        if (ExclusiveMode) Reroute();
    }

    // ---------- API pública ----------

    /// <summary>Engancha los controles multimedia de Windows a la ventana principal.</summary>
    public void AttachWindow(IntPtr hwnd)
    {
        if (!_smtc.Attach(hwnd)) return;
        if (Current != null) _ = UpdateSystemControlsAsync(Current, _loadVersion);
    }

    public void PlayContext(string id, string name, IList<Song> songs, int startIndex)
    {
        if (songs.Count == 0) return;
        SetContext(id, name, songs, startIndex);
        _ = LoadAsync(_context[_order[_pos]], true);
        ContextStarted?.Invoke(id);
    }

    /// <summary>Deja una canción preparada (sin descargar ni reproducir) para retomar la sesión.</summary>
    public void Prepare(string id, string name, IList<Song> songs, int index)
    {
        if (songs.Count == 0) return;
        SetContext(id, name, songs, index);
        var song = _context[_order[_pos]];
        SetCurrent(song);
        Duration = song.Duration?.TotalSeconds ?? 0;
        _needsLoad = true;
        OutputInfo = ExclusiveMode ? "Bit a bit (se aplicará al reproducir)" : "Salida normal de Windows";
        UpdateUpNext();
        _ = UpdateSystemControlsAsync(song, _loadVersion);
    }

    public void Play()
    {
        if (Current == null) return;
        if (_loading)
        {
            _autoplayPending = true;
            return;
        }
        if (_needsLoad || !_out.IsLoaded)
        {
            _ = LoadAsync(Current, true, _needsLoad ? null : Position);
            return;
        }
        _ = PlayOutputAsync();
    }

    public void Pause()
    {
        if (_loading) _autoplayPending = false;
        _out.Pause();
    }

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause(); else Play();
    }

    public void Next() => Advance(false);

    public void Previous()
    {
        if (Position > 3 || _order.Count == 0) { Seek(0); return; }
        if (_pos > 0) _pos--;
        else if (Repeat == RepeatMode.All) _pos = _order.Count - 1;
        else { Seek(0); return; }
        _ = LoadAsync(_context[_order[_pos]], true);
    }

    /// <summary>Salta a ese punto y suena, esté en pausa o sin cargar todavía (clic en una línea de la letra).</summary>
    public void PlayFrom(double seconds)
    {
        if (Current == null) return;
        seconds = Math.Max(0, seconds);
        if (_loading || _needsLoad || !_out.IsLoaded)
        {
            // aún sin abrir (sesión retomada) o cargando: se abre y empieza ahí mismo
            _ = LoadAsync(Current, true, seconds);
            return;
        }
        Seek(seconds);
        if (!IsPlaying) _ = PlayOutputAsync();
    }

    public void Seek(double seconds)
    {
        if (_needsLoad) return;
        _out.Seek(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        Position = seconds;
    }

    public void CycleRepeat() => Repeat = (RepeatMode)(((int)Repeat + 1) % 3);

    public void Enqueue(IEnumerable<Song> songs)
    {
        var list = songs.ToList();
        if (list.Count == 0) return;
        if (Current == null)
        {
            PlayContext("queue", "Cola", list, 0);
            return;
        }
        _userQueue.AddRange(list);
        UpdateUpNext();
    }

    public void PlayNext(IEnumerable<Song> songs)
    {
        var list = songs.ToList();
        if (list.Count == 0) return;
        if (Current == null)
        {
            PlayContext("queue", "Cola", list, 0);
            return;
        }
        _userQueue.InsertRange(0, list);
        UpdateUpNext();
    }

    public void RemoveFromQueue(Song song)
    {
        if (_userQueue.Remove(song)) UpdateUpNext();
    }

    public void ClearQueue()
    {
        _userQueue.Clear();
        UpdateUpNext();
    }

    public void PlayFromUpNext(Song song)
    {
        int qi = _userQueue.IndexOf(song);
        if (qi >= 0)
        {
            _userQueue.RemoveRange(0, qi + 1);
            _ = LoadAsync(song, true);
            return;
        }
        for (int i = _pos + 1; i < _order.Count; i++)
        {
            if (_context[_order[i]] != song) continue;
            _pos = i;
            _ = LoadAsync(song, true);
            return;
        }
    }

    /// <summary>
    /// Tras recargar la biblioteca, cambia las canciones de la cola por las nuevas instancias
    /// (mismo archivo) para que la canción actual siga marcada en las listas.
    /// </summary>
    public void Remap(Func<string, Song?> find)
    {
        Song Map(Song s) => find(s.Path) ?? s;
        _context = _context.Select(Map).ToList();
        for (int i = 0; i < _userQueue.Count; i++) _userQueue[i] = Map(_userQueue[i]);
        if (Current != null)
        {
            var mapped = Map(Current);
            if (mapped != Current)
            {
                mapped.Duration ??= Current.Duration;
                Current.IsCurrent = false;
                mapped.IsCurrent = true;
                Current = mapped;
            }
        }
        UpdateUpNext();
    }

    public void RefreshSmtc()
    {
        if (Current != null) _ = UpdateSystemControlsAsync(Current, _loadVersion);
    }

    // ---------- internos ----------

    /// <summary>Vuelve a abrir la canción actual por la salida correspondiente, en el mismo punto.</summary>
    private void Reroute()
    {
        _lastNotice = null;
        if (Current == null || _needsLoad)
        {
            OutputInfo = ExclusiveMode ? "Bit a bit (se aplicará al reproducir)" : "Salida normal de Windows";
            return;
        }
        _ = LoadAsync(Current, IsPlaying, Position);
    }

    /// <summary>En modo normal, sigue a la salida predeterminada de Windows (p. ej. al conectar los cascos).</summary>
    private void FollowDefaultDevice()
    {
        if (Current == null || _needsLoad || _out.IsExclusive) return;
        _ = LoadAsync(Current, IsPlaying, Position);
    }

    private void NotifyOnce(string text)
    {
        if (text == _lastNotice) return;
        _lastNotice = text;
        Notice?.Invoke(text);
    }

    private void SetContext(string id, string name, IList<Song> songs, int startIndex)
    {
        ContextId = id;
        ContextName = name;
        _context = songs.ToList();
        if (startIndex < 0 || startIndex >= _context.Count)
            startIndex = Shuffle ? _rng.Next(_context.Count) : 0;
        BuildOrder(startIndex);
    }

    private void BuildOrder(int currentIndex)
    {
        _order = Enumerable.Range(0, _context.Count).ToList();
        if (Shuffle)
        {
            _order.RemoveAt(currentIndex);
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            _order.Insert(0, currentIndex);
            _pos = 0;
        }
        else
        {
            _pos = currentIndex;
        }
    }

    private void Advance(bool auto)
    {
        if (_userQueue.Count > 0)
        {
            var q = _userQueue[0];
            _userQueue.RemoveAt(0);
            _ = LoadAsync(q, true);
            return;
        }
        if (_order.Count == 0) return;

        if (_pos + 1 < _order.Count)
        {
            _pos++;
            _ = LoadAsync(_context[_order[_pos]], true);
            return;
        }

        // fin de la lista
        if (Repeat == RepeatMode.Off && AutoRadio && RadioProvider != null && Current != null)
        {
            var more = RadioProvider(Current, _context);
            if (more.Count > 0)
            {
                foreach (var s in more)
                {
                    _context.Add(s);
                    _order.Add(_context.Count - 1);
                }
                if (!ContextName.StartsWith("Radio", StringComparison.Ordinal)) ContextName = $"Radio · {ContextName}";
                _pos++;
                _ = LoadAsync(_context[_order[_pos]], true);
                return;
            }
        }

        // vuelve al principio; solo sigue sonando si "repetir todo" está activo
        if (Shuffle) BuildOrder(_rng.Next(_context.Count)); else _pos = 0;
        _ = LoadAsync(_context[_order[_pos]], Repeat == RepeatMode.All);
    }

    private void SetCurrent(Song song)
    {
        if (Current != null) Current.IsCurrent = false;
        Current = song;
        song.IsCurrent = true;
    }

    private async Task<string?> TryLoad(string path, string? deviceId, bool exclusive)
    {
        try
        {
            return await _out.LoadAsync(path, deviceId, exclusive);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private async Task LoadAsync(Song song, bool autoplay, double? resumeAt = null)
    {
        int ver = ++_loadVersion;
        _loading = true;
        _autoplayPending = autoplay;
        _needsLoad = false;
        _gaplessNext = null; // la carga cierra también la pista preparada
        _listenedThisLoad = 0;
        _countedThisLoad = false;
        SetCurrent(song);
        Error = null;
        Position = resumeAt ?? 0;
        Duration = song.Duration?.TotalSeconds ?? 0;
        LoadingText = song.IsCloud ? "Descargando de OneDrive…" : "Cargando…";
        IsBuffering = true;
        UpdateUpNext();

        bool wantExclusive = ExclusiveMode && !_forceShared;
        _forceShared = false;
        string? reason = null;
        if (wantExclusive)
        {
            reason = await TryLoad(song.Path, ExclusiveDeviceId, true);
            if (ver != _loadVersion) return;
            if (reason != null)
            {
                OutputInfo = "Salida normal · " + reason;
                NotifyOnce($"Bit a bit no disponible: {reason}. Suena en modo normal.");
            }
        }
        if (!wantExclusive || reason != null)
        {
            var failure = await TryLoad(song.Path, null, false);
            if (ver != _loadVersion) return;
            if (failure != null)
            {
                _loading = false;
                OnFailed(failure);
                return;
            }
            if (!wantExclusive) OutputInfo = $"Salida normal · {_out.FormatText} · {_out.DeviceName}";
        }
        else
        {
            OutputInfo = $"Bit a bit · {_out.FormatText} · {_out.DeviceName}";
        }

        IsBuffering = false;
        _failures = 0;
        if (_out.Duration > TimeSpan.Zero)
        {
            Duration = _out.Duration.TotalSeconds;
            song.Duration ??= _out.Duration;
        }
        _out.SetVolume(Volume, IsMuted);
        if (resumeAt is > 0) _out.Seek(TimeSpan.FromSeconds(resumeAt.Value));
        SongOpened?.Invoke(song);
        _loading = false;
        if (_autoplayPending) await PlayOutputAsync();
        else _smtc.SetStatus(MediaControls.StatusPaused);
        await UpdateSystemControlsAsync(song, ver);
    }

    /// <summary>Empieza a sonar; si el dispositivo exclusivo está ocupado, sigue en modo normal.</summary>
    private async Task PlayOutputAsync()
    {
        try
        {
            await _out.PlayAsync();
        }
        catch (Exception ex)
        {
            if (Current == null) return;
            if (_out.IsExclusive)
            {
                var pos = Position;
                OutputInfo = "Salida normal · no se pudo abrir el dispositivo en exclusivo";
                NotifyOnce($"No se pudo abrir el dispositivo en exclusivo ({ex.Message}). Suena en modo normal.");
                _forceShared = true;
                await LoadAsync(Current, true, pos);
            }
            else
            {
                OnFailed(ex.Message);
            }
        }
    }

    private async Task UpdateSystemControlsAsync(Song song, int ver)
    {
        if (!_smtc.IsAvailable) return;
        var cover = await Task.Run(() =>
        {
            try
            {
                var path = CoverFileProvider?.Invoke(song);
                return path != null ? File.ReadAllBytes(path) : null;
            }
            catch
            {
                return null;
            }
        });
        if (ver == _loadVersion && Current == song) _smtc.SetTrack(song.Title, song.Artist, song.Album, cover);
    }

    private void Tick()
    {
        double delta = _tickClock.Elapsed.TotalSeconds;
        _tickClock.Restart();
        if (Current == null || _needsLoad || !_out.IsLoaded) return;

        Position = _out.Position.TotalSeconds;
        if (_out.Duration > TimeSpan.Zero) Duration = _out.Duration.TotalSeconds;

        if (!IsPlaying || delta > 2) return;
        PrepareGapless();
        _listenedThisLoad += delta;
        Listened?.Invoke(Current, delta);
        if (!_countedThisLoad && (_listenedThisLoad >= CountAfterSeconds || (Duration > 0 && _listenedThisLoad >= Duration * 0.5)))
        {
            _countedThisLoad = true;
            PlayCounted?.Invoke(Current);
        }
    }

    // ---------- sin pausas entre canciones ----------

    /// <summary>
    /// La canción que sonaría al acabar la actual, sin tocar nada; null si no se sabe de antemano
    /// (parar al acabar, fin de la lista con radio o aleatorio, que se decide en ese momento).
    /// </summary>
    private Song? PeekNextAuto()
    {
        if (StopAfterCurrent || Current == null) return null;
        if (Repeat == RepeatMode.One) return Current;
        if (_userQueue.Count > 0) return _userQueue[0];
        if (_pos + 1 < _order.Count) return _context[_order[_pos + 1]];
        if (Repeat == RepeatMode.All && !Shuffle && _order.Count > 0) return _context[_order[0]];
        return null;
    }

    /// <summary>En los últimos segundos de la canción, deja abierta la siguiente en la salida.</summary>
    private void PrepareGapless()
    {
        if (_loading || _needsLoad || !_out.IsLoaded || Duration <= 0 || Duration - Position > PrepareAheadSeconds) return;
        var next = PeekNextAuto();
        if (next == null || next == _gaplessNext) return;
        _gaplessNext = next; // aunque no se pueda (otro formato), no se reintenta en cada tic
        int ver = _loadVersion;
        _ = Task.Run(async () =>
        {
            bool ok;
            try { ok = await _out.PrepareNextAsync(next.Path, next); } catch { ok = false; }
            // si mientras se abría cambió la canción o la siguiente, se descarta
            _ = _ui.BeginInvoke(() =>
            {
                if (ok && (ver != _loadVersion || _gaplessNext != next)) _out.CancelNext();
            });
        });
    }

    /// <summary>Si la siguiente ya no es la preparada (cola, aleatorio, repetición, parar al acabar…), se descarta.</summary>
    private void CheckGapless()
    {
        if (_gaplessNext == null || PeekNextAuto() == _gaplessNext) return;
        _gaplessNext = null;
        _out.CancelNext();
    }

    /// <summary>La salida enlazó sin pausa con la canción preparada: se avanza la cola como al pasar de canción.</summary>
    private void OnGaplessAdvanced(object tag)
    {
        if (tag is not Song song) return;
        _gaplessNext = null;
        if (!(Repeat == RepeatMode.One && song == Current))
        {
            if (_userQueue.Count > 0 && _userQueue[0] == song) _userQueue.RemoveAt(0);
            else if (_pos + 1 < _order.Count && _context[_order[_pos + 1]] == song) _pos++;
            else if (Repeat == RepeatMode.All && _order.Count > 0 && _context[_order[0]] == song) _pos = 0;
        }
        int ver = ++_loadVersion;
        _listenedThisLoad = 0;
        _countedThisLoad = false;
        _failures = 0;
        SetCurrent(song);
        Position = 0;
        if (_out.Duration > TimeSpan.Zero)
        {
            Duration = _out.Duration.TotalSeconds;
            song.Duration ??= _out.Duration;
        }
        else Duration = song.Duration?.TotalSeconds ?? 0;
        UpdateUpNext();
        SongOpened?.Invoke(song);
        _ = UpdateSystemControlsAsync(song, ver);
    }

    private void OnEnded()
    {
        if (StopAfterCurrent)
        {
            StopAfterCurrent = false;
            Seek(0);
            Pause();
            return;
        }
        if (Repeat == RepeatMode.One)
        {
            Seek(0);
            Play();
            return;
        }
        Advance(true);
    }

    private void OnFailed(string message)
    {
        IsBuffering = false;
        Log($"{Current?.Path}: {message}");
        Error = $"No se pudo reproducir «{Current?.Title}»";
        _errorTimer.Stop();
        _errorTimer.Start();
        if (++_failures < 5)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            t.Tick += (s, e) => { t.Stop(); Advance(true); };
            t.Start();
        }
    }

    private void UpdateUpNext()
    {
        Sync(QueueNext, _userQueue);
        HasQueue = QueueNext.Count > 0;

        var context = new List<Song>(50);
        for (int i = _pos + 1; i < _order.Count && context.Count < 50; i++)
            context.Add(_context[_order[i]]);
        if (Repeat == RepeatMode.All && !Shuffle)
            for (int i = 0; i < _pos && context.Count < 50; i++)
                context.Add(_context[_order[i]]);
        Sync(ContextNext, context);
        CheckGapless();
    }

    /// <summary>
    /// Deja la lista visible igual que <paramref name="items"/> tocando solo lo que cambia: al pasar de canción
    /// se quita la primera y, si acaso, se añade al final (antes se vaciaba y rellenaba entera: con una cola de
    /// miles de canciones, miles de avisos a la interfaz). Si cambia de otra forma, se rehace como siempre.
    /// </summary>
    private static void Sync(ObservableCollection<Song> target, IList<Song> items)
    {
        int n = target.Count;
        bool Same(int from, int count, int itemsFrom)
        {
            for (int k = 0; k < count; k++)
                if (!ReferenceEquals(target[from + k], items[itemsFrom + k])) return false;
            return true;
        }
        if (n == items.Count && Same(0, n, 0)) return;
        // avance: la lista nueva empieza por la vieja sin su primera
        if (n > 0 && items.Count >= n - 1 && Same(1, n - 1, 0))
        {
            target.RemoveAt(0);
            for (int k = n - 1; k < items.Count; k++) target.Add(items[k]);
            return;
        }
        // añadido al final: la nueva empieza por toda la vieja
        if (items.Count > n && Same(0, n, 0))
        {
            for (int k = n; k < items.Count; k++) target.Add(items[k]);
            return;
        }
        target.Clear();
        foreach (var s in items) target.Add(s);
    }

    private static void Log(string text)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.AppendAllText(Path.Combine(SettingsStore.DataDirectory, "player.log"), $"[{DateTime.Now:u}] {text}\n");
        }
        catch
        {
            // sin log
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _smtc.Dispose();
        _out.Dispose();
    }
}
