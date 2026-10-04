using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Echoplex.Models;

namespace Echoplex.Services;

/// <summary>Lo que suena, copiado en el hilo de la interfaz para pasarlo a Discord.</summary>
public sealed record NowPlaying(Song Song, string Title, string Artist, string Album, bool Playing, double Position, double Duration);

/// <summary>
/// Rich Presence de Discord: «Escuchando a …» con la carátula, la canción, el artista, el álbum y la barra de
/// progreso. La conexión, las subidas de carátulas y los límites de Discord van en segundo plano; la interfaz
/// solo llama a <see cref="Update"/> cuando cambia algo del reproductor.
/// </summary>
public sealed partial class DiscordPresence : IDisposable
{
    /// <summary>Lo que sale en la lista de miembros tras «Escuchando a»: el artista, la canción o Echoplex.</summary>
    public const string ShowArtist = "artist", ShowTitle = "title", ShowApp = "app";

    /// <summary>Logo para fondos oscuros (Discord suele estar en oscuro): sin carátula, en grande; con ella, en la esquina.</summary>
    private const string LogoUrl = "https://raw.githubusercontent.com/Reyes-gh/echoplex/main/Assets/echoplex-logo-light.png";
    /// <summary>Icono pequeño en la esquina de la carátula: sonando o en pausa (se puede quitar en Ajustes).</summary>
    private const string PlayingIconUrl = "https://raw.githubusercontent.com/Reyes-gh/echoplex/main/Assets/discord-playing.png";
    private const string PausedIconUrl = "https://raw.githubusercontent.com/Reyes-gh/echoplex/main/Assets/discord-paused.png";
    /// <summary>Las pruebas fijan si los iconos están en internet (null = se comprueba de verdad).</summary>
    private static bool? s_iconsAvailable;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private const int MaxText = 128;

    // no readonly: las pruebas los cambian
    /// <summary>Aplicación «Echoplex» del portal de desarrolladores de Discord: su nombre es el de la tarjeta.</summary>
    private static string s_applicationId = "1556108452797878272";
    /// <summary>En pausa, el estado se mantiene este tiempo y luego se quita.</summary>
    private static TimeSpan s_pauseKeep = TimeSpan.FromMinutes(1);
    /// <summary>Sin Discord abierto, cada cuánto se vuelve a probar (solo mientras haya algo que enseñar).</summary>
    private static TimeSpan s_retryDelay = TimeSpan.FromSeconds(20);
    /// <summary>
    /// Al cambiar de canción se espera la carátula como mucho esto antes de mandar el estado: si se manda primero con el
    /// logo y enseguida con la carátula, Discord a menudo se queda con el primero.
    /// </summary>
    private static TimeSpan s_coverWait = TimeSpan.FromSeconds(5);
    /// <summary>Tras varios cambios seguidos, el último se vuelve a mandar una vez pasado esto, por si Discord se saltó alguno.</summary>
    private static TimeSpan s_confirmAfter = TimeSpan.FromSeconds(16);

    // Discord admite 5 cambios de estado cada 20 s: se agrupan los que lleguen seguidos y se manda el último
    private const int BurstMax = 5;
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(1);
    /// <summary>Tras un cambio del reproductor se espera esto por si llega otro (pasar de canción son varios seguidos).</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

    private readonly DiscordIpc _ipc = new();
    private readonly DiscordCovers _covers;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<DateTime> _sends = new();
    private readonly string _dataDir;
    private readonly string _logFile;
    private Task? _loop;

    // ---- lo que se quiere enseñar (bajo _lock) ----
    private bool _enabled;
    private string _show = ShowArtist;
    private bool _playState = true;
    /// <summary>
    /// Los iconos de sonando/pausa se han encontrado en internet: si no están (aún sin subir a GitHub, sin conexión),
    /// Discord pondría una interrogación en la esquina, así que se omiten hasta que aparezcan.
    /// </summary>
    private bool _iconsOk;
    private DateTime _iconsNextCheck;
    private NowPlaying? _np;
    /// <summary>Marcas de la barra de progreso (ms Unix); solo cambian al saltar, no con el avance normal.</summary>
    private long _startMs, _endMs;
    private DateTime? _pausedAt;
    private DateTime _changedAt;
    /// <summary>La última canción que llegó a sonar: una preparada al abrir Echoplex y sin tocar no se anuncia.</summary>
    private Song? _playedSong;
    private Song? _coverSong;
    private string? _coverUrl;
    /// <summary>Desde cuándo se busca (o sube) la carátula de la canción actual; null si ya se sabe.</summary>
    private DateTime? _coverPendingSince;
    private string? _uploading;
    /// <summary>Discord rechazó un estado: se manda sin lo más nuevo (texto de la lista de miembros, botón).</summary>
    private bool _plain;
    /// <summary>Discord dijo que iba demasiado rápido: el bucle lo vuelve a intentar más tarde.</summary>
    private bool _retryRequested;

    // ---- lo enviado (solo en el bucle) ----
    private string? _sentJson;
    private DateTime _nextConnect;
    /// <summary>Cuándo volver a mandar el último estado (tras varios seguidos, o si Discord pidió calma).</summary>
    private DateTime? _confirmAt;
    private string _status = "";

    /// <param name="coverBytes">Bytes de la portada de una canción (la imagen de su carpeta o la incrustada).</param>
    /// <param name="dataDirectory">Dónde guardar la caché de carátulas y el registro (por defecto, %LocalAppData%\Echoplex).</param>
    public DiscordPresence(Func<Song, byte[]?> coverBytes, string? dataDirectory = null)
    {
        _dataDir = dataDirectory ?? SettingsStore.DataDirectory;
        _logFile = Path.Combine(_dataDir, "discord.log");
        _covers = new DiscordCovers(coverBytes, _dataDir);
        _covers.Log += Log;
        _covers.Uploading += text =>
        {
            lock (_lock) _uploading = text;
            RefreshStatus();
        };
        _ipc.Disconnected += () =>
        {
            Log("Discord cerró la conexión");
            RefreshStatus();
            Signal();
        };
        _ipc.CommandError += message =>
        {
            Log($"Discord rechazó el estado: {message}");
            if (message.Contains("rate", StringComparison.OrdinalIgnoreCase) || message.Contains("limit", StringComparison.OrdinalIgnoreCase))
            {
                // demasiado rápido: no es culpa del contenido, se vuelve a mandar en un rato
                lock (_lock) _retryRequested = true;
                Signal();
                return;
            }
            bool retry;
            lock (_lock)
            {
                retry = !_plain;
                _plain = true;
            }
            if (retry) Signal(); // se reintenta con lo básico
            else SetStatus($"Discord rechazó el estado: {message}");
        };
    }

    /// <summary>Texto para Ajustes (conectado, sin Discord, subiendo…). Llega desde segundo plano.</summary>
    public event Action<string>? StatusChanged;

    public string Status
    {
        get
        {
            lock (_lock) return _status;
        }
    }

    public bool Enabled
    {
        get
        {
            lock (_lock) return _enabled;
        }
        set
        {
            lock (_lock)
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value && _np is { Playing: true } np && _coverSong != np.Song) StartCover(np);
            }
            _loop ??= Task.Run(() => RunAsync(_cts.Token));
            RefreshStatus();
            Signal();
        }
    }

    /// <summary><see cref="ShowArtist"/>, <see cref="ShowTitle"/> o <see cref="ShowApp"/>.</summary>
    public string Show
    {
        get
        {
            lock (_lock) return _show;
        }
        set
        {
            lock (_lock) _show = value is ShowTitle or ShowApp ? value : ShowArtist;
            Signal();
        }
    }

    /// <summary>Icono de sonando / en pausa en la esquina de la carátula (sin él se ve la carátula entera).</summary>
    public bool ShowPlayState
    {
        get
        {
            lock (_lock) return _playState;
        }
        set
        {
            lock (_lock) _playState = value;
            Signal();
        }
    }

    /// <summary>Programa de subida de carátulas (como el de foo_discord_rich); vacío = sin subidas.</summary>
    public string? UploadCommand
    {
        get => _covers.Command;
        set
        {
            _covers.Command = value;
            lock (_lock)
            {
                // con programa nuevo, la canción actual vuelve a intentarlo
                if (_coverUrl == null) _coverSong = null;
                if (_enabled && _np is { Playing: true } np && _coverSong != np.Song) StartCover(np);
            }
        }
    }

    /// <summary>Lo que suena ahora (null = nada). Desde el hilo de la interfaz, tantas veces como cambie; aquí se filtra.</summary>
    public void Update(NowPlaying? np)
    {
        lock (_lock)
        {
            var old = _np;
            _np = np;
            if (np == null)
            {
                if (old == null) return;
                _pausedAt = null;
            }
            else
            {
                bool newSong = old?.Song != np.Song;
                bool changed = newSong || old!.Playing != np.Playing
                    || old.Title != np.Title || old.Artist != np.Artist || old.Album != np.Album;
                if (np.Playing)
                {
                    _playedSong = np.Song;
                    _pausedAt = null;
                    if (_enabled && _coverSong != np.Song) StartCover(np);
                    // barra de progreso: se recoloca si salta (búsqueda, otra canción, reanudar) o cambia la duración
                    long start = NowMs() - (long)(np.Position * 1000);
                    long end = np.Duration > 0 ? start + (long)(np.Duration * 1000) : 0;
                    if (changed || Math.Abs(start - _startMs) > 2000 || (end == 0) != (_endMs == 0)
                        || Math.Abs((end - start) - (_endMs - _startMs)) > 1000)
                    {
                        _startMs = start;
                        _endMs = end;
                        changed = true;
                    }
                }
                else if (changed)
                {
                    _pausedAt = DateTime.UtcNow;
                }
                if (!changed) return;
            }
            _changedAt = DateTime.UtcNow;
        }
        Signal();
    }

    /// <summary>Hay programa de subida: se puede forzar que se vuelvan a subir las carátulas.</summary>
    public bool CanUpload => _covers.Command != null;

    /// <summary>
    /// Vuelve a subir las carátulas de estas canciones (cambiaste la imagen y Discord seguía con la vieja). Si suena una
    /// de ellas, su estado cambia ya a la carátula nueva.
    /// </summary>
    public async Task<(int uploaded, int failed)> RefreshCoversAsync(IReadOnlyList<Song> songs)
    {
        var result = await _covers.RefreshAsync(songs, _cts.Token);
        lock (_lock)
        {
            if (_np is { } np && songs.Contains(np.Song))
            {
                _coverSong = null;
                if (_enabled && np.Playing) StartCover(np);
            }
        }
        Signal();
        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _ipc.Dispose(); // al cerrar la tubería, Discord quita el estado
    }

    // ======================================================================
    // Bucle en segundo plano
    // ======================================================================

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = Timeout.InfiniteTimeSpan;
            try
            {
                if (DateTime.UtcNow >= _iconsNextCheck && Enabled) await CheckIconsAsync(ct);
                var (activity, recheck, hold) = Desired();
                if (activity != null) wait = Shorter(wait, _iconsNextCheck - DateTime.UtcNow); // por si los iconos aparecen
                if (recheck is { } at) wait = Shorter(wait, at - DateTime.UtcNow);
                bool retry;
                lock (_lock)
                {
                    retry = _retryRequested;
                    _retryRequested = false;
                }
                if (retry) _confirmAt = DateTime.UtcNow + s_confirmAfter; // Discord pidió calma: se repite en un rato
                if (!hold)
                {
                    var json = activity?.ToJsonString();
                    if (!_ipc.IsConnected)
                    {
                        _sentJson = null; // tras reconectar hay que volver a mandarlo
                        _confirmAt = null;
                    }

                    if (json != _sentJson && !_ipc.IsConnected && json != null)
                    {
                        if (DateTime.UtcNow >= _nextConnect) await ConnectAsync(ct);
                        if (!_ipc.IsConnected) wait = Shorter(wait, _nextConnect - DateTime.UtcNow);
                    }
                    // tras varios cambios seguidos, el último se repite una vez: Discord a veces se salta alguno
                    bool again = false;
                    if (_confirmAt is { } c && _ipc.IsConnected)
                    {
                        if (DateTime.UtcNow >= c) again = true;
                        else wait = Shorter(wait, c - DateTime.UtcNow);
                    }
                    if ((json != _sentJson || again) && _ipc.IsConnected)
                    {
                        var delay = SendDelay();
                        if (delay > TimeSpan.Zero)
                        {
                            wait = Shorter(wait, delay);
                        }
                        else
                        {
                            bool burst = _sends.Count > 0 && DateTime.UtcNow - _sends.Last() < BurstWindow;
                            await _ipc.SendAsync(SetActivity(activity), ct);
                            _sends.Enqueue(DateTime.UtcNow);
                            _sentJson = json;
                            LogSend(activity, again);
                            // la repetición no programa otra; un cambio seguido de otro sí
                            _confirmAt = again ? null : burst ? DateTime.UtcNow + s_confirmAfter : null;
                            if (_confirmAt is { } next) wait = Shorter(wait, next - DateTime.UtcNow);
                        }
                    }
                    // desactivado y ya sin nada en Discord: no hace falta seguir conectado (al cerrar, Discord lo quita igualmente)
                    if (json == null && _sentJson == null && _ipc.IsConnected && !Enabled)
                    {
                        _confirmAt = null;
                        _ipc.Close();
                    }
                }
                RefreshStatus();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Error con Discord: {ex.Message}");
                _ipc.Close();
                wait = TimeSpan.FromSeconds(5);
            }

            try
            {
                await _signal.WaitAsync(wait, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s_applicationId))
        {
            _nextConnect = DateTime.MaxValue;
            return;
        }
        var error = await _ipc.ConnectAsync(s_applicationId, ct);
        if (error == null)
        {
            Log($"Conectado a Discord{(_ipc.UserName is { } u ? $" ({u})" : "")}");
            return;
        }
        _nextConnect = DateTime.UtcNow + s_retryDelay;
        if (error != _lastConnectError) Log($"Sin conexión con Discord: {error}");
        _lastConnectError = error;
    }

    private string? _lastConnectError;

    /// <summary>
    /// ¿Están los iconos de sonando/pausa en internet? Si no, se vuelve a mirar cada 10 minutos (aparecen solos en cuanto
    /// se suben); si sí, cada pocas horas.
    /// </summary>
    private async Task CheckIconsAsync(CancellationToken ct)
    {
        bool ok;
        if (s_iconsAvailable is { } fixedValue) ok = fixedValue;
        else
        {
            ok = true;
            foreach (var url in new[] { PlayingIconUrl, PausedIconUrl })
            {
                try
                {
                    using var response = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
                    ok &= response.IsSuccessStatusCode;
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    ok = false;
                }
            }
        }
        _iconsNextCheck = DateTime.UtcNow + (ok ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(10));
        bool changed;
        lock (_lock)
        {
            changed = _iconsOk != ok;
            _iconsOk = ok;
        }
        if (changed) Log(ok ? "Iconos de sonando/pausa disponibles" : "Los iconos de sonando/pausa aún no están en internet: se omiten");
    }

    /// <summary>Una línea por estado mandado (para ver en discord.log qué recibió Discord y cuándo).</summary>
    private void LogSend(JsonObject? activity, bool again)
    {
        string what;
        if (activity == null) what = "quitado";
        else
        {
            static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var image = Text(activity["assets"]?["large_image"]);
            what = $"{Text(activity["details"])} — {Text(activity["state"]) ?? "?"}"
                + (activity["timestamps"] == null ? " · en pausa" : "")
                + (image == LogoUrl ? " · logo" : " · carátula");
        }
        Log(again ? $"Estado (repetido por si acaso): {what}" : $"Estado: {what}");
    }

    /// <summary>
    /// Cuánto esperar antes de mandar (cero = ya): que el reproductor se asiente (al pasar de canción hay un instante
    /// sin sonar que no debe quitar el estado) y no pasarse del límite de Discord.
    /// </summary>
    private TimeSpan SendDelay()
    {
        var now = DateTime.UtcNow;
        DateTime changedAt;
        lock (_lock) changedAt = _changedAt;
        var delay = changedAt + Settle - now;
        while (_sends.Count > 0 && now - _sends.Peek() > BurstWindow) _sends.Dequeue();
        if (_sends.Count >= BurstMax) delay = Longer(delay, _sends.Peek() + BurstWindow - now); // 5 cada 20 s
        if (_sends.Count > 0) delay = Longer(delay, _sends.Last() + MinGap - now); // y al menos 1 s entre uno y otro
        return delay;
    }

    /// <summary>
    /// El estado que debería verse ahora (null = ninguno), cuándo volver a mirarlo aunque no cambie nada y si hay que
    /// esperar antes de mandarlo (la carátula de la canción nueva está a punto de llegar).
    /// </summary>
    private (JsonObject? activity, DateTime? recheck, bool hold) Desired()
    {
        lock (_lock)
        {
            if (!_enabled || string.IsNullOrEmpty(s_applicationId) || _np is not { } np || np.Song != _playedSong) return (null, null, false);
            DateTime? recheck = null;
            if (!np.Playing)
            {
                var until = (_pausedAt ?? DateTime.UtcNow) + s_pauseKeep;
                if (DateTime.UtcNow >= until) return (null, null, false);
                recheck = until;
            }
            // canción nueva cuya carátula aún se busca o se sube: mejor un solo cambio con todo que logo y luego carátula
            else if (_coverSong == np.Song && _coverUrl == null && _coverPendingSince is { } since && DateTime.UtcNow < since + s_coverWait)
            {
                return (null, since + s_coverWait, true);
            }
            return (Build(np), recheck, false);
        }
    }

    private JsonObject Build(NowPlaying np)
    {
        string title = Clean(np.Title) ?? Clean(np.Song.FileTitle) ?? "Sin título";
        string? artist = Clean(np.Artist), album = Clean(np.Album);
        var activity = new JsonObject { ["type"] = 2 }; // «Escuchando a»
        if (!_plain)
        {
            // qué va tras «Escuchando a» en la lista de miembros: 0 = Echoplex, 1 = state (artista), 2 = details (canción)
            int display = _show == ShowApp ? 0 : _show == ShowTitle || artist == null ? 2 : 1;
            activity["status_display_type"] = display;
        }
        activity["details"] = Fit(title);
        if (artist != null) activity["state"] = Fit(artist);
        if (np.Playing)
        {
            var timestamps = new JsonObject { ["start"] = _startMs };
            if (_endMs > _startMs) timestamps["end"] = _endMs;
            activity["timestamps"] = timestamps;
        }

        var assets = new JsonObject();
        bool hasCover = _coverSong == np.Song && _coverUrl != null;
        assets["large_image"] = hasCover ? _coverUrl : LogoUrl;
        if (album != null) assets["large_text"] = Fit(album);
        if (_playState && _iconsOk)
        {
            assets["small_image"] = np.Playing ? PlayingIconUrl : PausedIconUrl;
            assets["small_text"] = np.Playing ? "Reproduciendo" : "En pausa";
        }
        activity["assets"] = assets;

        if (!_plain)
        {
            var query = artist != null ? $"{artist} {title}" : title;
            activity["buttons"] = new JsonArray(new JsonObject
            {
                ["label"] = "Buscar en YouTube",
                ["url"] = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query),
            });
        }
        return activity;
    }

    private static JsonObject SetActivity(JsonObject? activity)
    {
        var args = new JsonObject { ["pid"] = Environment.ProcessId }; // Discord lo quita solo si Echoplex se cierra
        if (activity != null) args["activity"] = activity;
        return new JsonObject { ["cmd"] = "SET_ACTIVITY", ["args"] = args, ["nonce"] = Guid.NewGuid().ToString() };
    }

    /// <summary>Busca la URL de la carátula de esta canción (ya subida, de foobar2000 o subiéndola). Bajo _lock.</summary>
    private void StartCover(NowPlaying np)
    {
        var song = np.Song;
        var album = np.Album;
        _coverSong = song;
        _coverUrl = null;
        _coverPendingSince = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            string? url = null;
            try
            {
                url = await _covers.ResolveAsync(song, album, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"Carátula: {ex.Message}");
            }
            lock (_lock)
            {
                if (_coverSong != song) return; // ya suena otra
                _coverUrl = url;
                _coverPendingSince = null;
            }
            Signal();
        });
    }

    // ======================================================================
    // Estado para Ajustes y registro
    // ======================================================================

    private void RefreshStatus()
    {
        string text;
        lock (_lock)
        {
            text = !_enabled ? "Desactivado"
                : string.IsNullOrEmpty(s_applicationId) ? "Falta el ID de la aplicación de Discord"
                : _uploading ?? (_ipc.IsConnected
                    ? $"Conectado a Discord{(_ipc.UserName is { } u ? $" como {u}" : "")}"
                    : _np != null && _np.Song == _playedSong ? "Discord no está abierto: se vuelve a probar cada poco" : "Se conectará con Discord al reproducir");
        }
        SetStatus(text);
    }

    private void SetStatus(string text)
    {
        lock (_lock)
        {
            if (text == _status) return;
            _status = text;
        }
        StatusChanged?.Invoke(text);
    }

    private void Log(string text)
    {
        lock (_logFile) // llega desde varios hilos
        {
            try
            {
                Directory.CreateDirectory(_dataDir);
                var info = new FileInfo(_logFile);
                if (info.Exists && info.Length > 256 * 1024) File.Move(_logFile, _logFile + ".old", true);
                File.AppendAllText(_logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n");
            }
            catch
            {
                // sin registro
            }
        }
    }

    // ======================================================================
    // Utilidades
    // ======================================================================

    private void Signal()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // ya estaba avisado
        }
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Spaces().Replace(s.Trim(), " ");
    }

    /// <summary>Discord pide textos de 2 a 128 caracteres.</summary>
    private static string Fit(string s)
    {
        if (s.Length > MaxText) return s[..(MaxText - 1)].TrimEnd() + "…";
        return s.Length < 2 ? s + "⠀" : s; // relleno invisible para títulos de una letra
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static TimeSpan Shorter(TimeSpan a, TimeSpan b)
    {
        if (b < TimeSpan.Zero) b = TimeSpan.Zero;
        return a == Timeout.InfiniteTimeSpan || b < a ? b : a;
    }

    private static TimeSpan Longer(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
