using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace Echoplex.Services.Subsonic;

/// <summary>Lo que el servidor necesita de Echoplex.</summary>
public sealed class SubsonicHost
{
    public required Dispatcher Ui { get; init; }
    public required LibraryService Library { get; init; }
    public required UserDataService UserData { get; init; }
    public required CoverService Covers { get; init; }
    public required Scrobbler Scrobbler { get; init; }
    /// <summary>La carátula de una canción como la enseña Echoplex (imagen de su carpeta, incrustada, la de otra del álbum…).</summary>
    public required Func<Models.Song, int, Task<System.Windows.Media.Imaging.BitmapSource?>> SongCover { get; init; }
    /// <summary>La portada de una carpeta como la enseña Echoplex (su imagen o el mosaico de sus discos).</summary>
    public required Func<string, int, Task<System.Windows.Media.Imaging.BitmapSource?>> FolderCover { get; init; }
    /// <summary>La portada de una playlist como en Echoplex (mosaico de sus álbumes).</summary>
    public required Func<Models.Playlist, int, Task<System.Windows.Media.Imaging.BitmapSource?>> PlaylistCover { get; init; }
    /// <summary>Canciones parecidas a una (la radio de Echoplex), sin repetir lo que acaba de sonar.</summary>
    public required Func<Models.Song, int, List<Models.Song>> Radio { get; init; }
    /// <summary>La letra de una canción como en Echoplex (archivo, .lrc o internet si está activado), con su desfase aplicado.</summary>
    public required Func<Models.Song, Task<List<LyricEntry>?>> Lyrics { get; init; }
    /// <summary>Lo que suena (o sonó por última vez) en Echoplex del PC: la canción, por dónde, lo que sigue y cuándo.</summary>
    public required Func<(Models.Song current, double position, IReadOnlyList<Models.Song> next, DateTime changed)?> PcQueue { get; init; }
}

/// <summary>
/// Echoplex como servidor Subsonic (API 1.16.1 y lo básico de OpenSubsonic): el móvil, con Tempus, Symfonium o cualquier
/// app compatible, ve la biblioteca, escucha en streaming, marca favoritas, usa las playlists y sus escuchas cuentan aquí.
/// Lo que toca datos de Echoplex se hace en el hilo de la interfaz; leer canciones y carátulas, fuera.
/// </summary>
public sealed partial class SubsonicServer : IDisposable
{
    private readonly SubsonicHost _host;
    private HttpLite? _http;
    private SubsonicIndex? _index;
    private bool _dirty = true;
    private DateTime _dirtySince = DateTime.MinValue;

    /// <summary>Usuario y contraseña que piden las apps.</summary>
    public string User { get; set; } = "echoplex";
    public string Password { get; set; } = "";

    /// <summary>Alguien se conectó: la app (parámetro c) y desde dónde. Llega desde otro hilo.</summary>
    public event Action<string, IPAddress>? Accessed;

    public bool IsRunning => _http != null;
    public int Port => _http?.Port ?? 0;

    private static string LogFile => Path.Combine(SettingsStore.DataDirectory, "movil.log");

    public SubsonicServer(SubsonicHost host) => _host = host;

    public void Start(IPAddress address, int port)
    {
        Stop();
        var http = new HttpLite(address, port, Handle);
        http.Start();
        _http = http;
        Log($"escuchando en {address}:{port}");
    }

    public void Stop()
    {
        if (_http == null) return;
        _http.Dispose();
        _http = null;
        Log("parado");
    }

    public void Dispose() => Stop();

    /// <summary>Cambió la biblioteca (o sus etiquetas): el índice se rehará en la próxima petición.</summary>
    public void Invalidate()
    {
        if (!_dirty) _dirtySince = DateTime.Now;
        _dirty = true;
    }

    /// <summary>El índice al día (en el hilo de la interfaz). Mientras se leen etiquetas, como mucho uno cada 5 s.</summary>
    private SubsonicIndex Index
    {
        get
        {
            if (_index == null || (_dirty && DateTime.Now - _index.Built > TimeSpan.FromSeconds(5)))
            {
                _index = new SubsonicIndex(_host.Library);
                _dirty = false;
            }
            return _index;
        }
    }

    // ======================================================================
    // Peticiones
    // ======================================================================

    private async Task<HttpLite.Response> Handle(HttpLite.Request req)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var path = req.Path.TrimEnd('/');
        if (!path.StartsWith("/rest/", StringComparison.OrdinalIgnoreCase))
            return HttpLite.Response.Text(404, "Echoplex: aquí se conectan las apps compatibles con Subsonic (Tempus, Symfonium…).");
        var method = path[6..];
        if (method.EndsWith(".view", StringComparison.OrdinalIgnoreCase)) method = method[..^5];
        string? format = req["f"], callback = req["callback"];

        if (Blocked(req.Remote))
            return Reply(SubsonicWriter.Error(40, "Demasiados intentos fallidos: espera unos minutos."), format, callback, 429);
        var authError = Authenticate(req);
        if (authError != null)
        {
            Log($"{req.Remote} {method}: {authError.Value.message}");
            return Reply(SubsonicWriter.Error(authError.Value.code, authError.Value.message), format, callback);
        }
        Accessed?.Invoke(req["c"] ?? "", req.Remote);

        HttpLite.Response response;
        try
        {
            response = method.ToLowerInvariant() switch
            {
                "stream" or "download" => await StreamAsync(req, method.Equals("download", StringComparison.OrdinalIgnoreCase)),
                "getcoverart" => await CoverAsync(req),
                "getlyrics" => await LyricsAsync(req, bySongId: false),
                "getlyricsbysongid" => await LyricsAsync(req, bySongId: true),
                _ => Reply(await _host.Ui.InvokeAsync(() => Api(method.ToLowerInvariant(), req), DispatcherPriority.Background), format, callback),
            };
        }
        catch (Exception ex)
        {
            Log($"{req.Remote} {method}: error {ex.Message}");
            response = Reply(SubsonicWriter.Error(0, ex.Message), format, callback);
        }
        if (clock.ElapsedMilliseconds > 300 || response.Status >= 400)
            Log($"{req.Remote} {req["c"]} {method}: {response.Status} en {clock.ElapsedMilliseconds} ms");
        return response;
    }

    private static HttpLite.Response Reply(SubsonicNode root, string? format, string? callback, int status = 200)
    {
        var (body, type) = SubsonicWriter.Write(root, format, callback);
        return new HttpLite.Response { Status = status, Body = body, ContentType = type };
    }

    // ---------- usuario y contraseña ----------

    private readonly ConcurrentDictionary<IPAddress, (int count, DateTime first)> _failures = new();

    private bool Blocked(IPAddress ip) =>
        _failures.TryGetValue(ip, out var f) && f.count >= 10 && DateTime.Now - f.first < TimeSpan.FromMinutes(10);

    /// <summary>u + p (en claro o «enc:» en hexadecimal) o u + t + s (t = md5(contraseña + s)). null si vale.</summary>
    private (int code, string message)? Authenticate(HttpLite.Request req)
    {
        var u = req["u"];
        if (string.IsNullOrEmpty(u)) return (10, "Falta el usuario (u)");
        bool ok = false;
        if (string.Equals(u, User, StringComparison.OrdinalIgnoreCase) && Password.Length > 0)
        {
            if (req["t"] is { } t && req["s"] is { } s)
            {
                var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(Password + s))).ToLowerInvariant();
                ok = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(t.ToLowerInvariant()));
            }
            else if (req["p"] is { } p)
            {
                if (p.StartsWith("enc:", StringComparison.Ordinal))
                {
                    try { p = Encoding.UTF8.GetString(Convert.FromHexString(p[4..])); } catch { p = ""; }
                }
                ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(p), Encoding.UTF8.GetBytes(Password));
            }
            else
            {
                return (10, "Falta la contraseña (p o t y s)");
            }
        }
        if (ok)
        {
            _failures.TryRemove(req.Remote, out _);
            return null;
        }
        _failures.AddOrUpdate(req.Remote, _ => (1, DateTime.Now),
            (_, f) => DateTime.Now - f.first > TimeSpan.FromMinutes(10) ? (1, DateTime.Now) : (f.count + 1, f.first));
        return (40, "Usuario o contraseña incorrectos");
    }

    // ======================================================================
    // Canciones (con saltos por rangos)
    // ======================================================================

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg", [".flac"] = "audio/flac", [".m4a"] = "audio/mp4", [".mp4"] = "audio/mp4", [".aac"] = "audio/aac",
        [".alac"] = "audio/mp4", [".ogg"] = "audio/ogg", [".oga"] = "audio/ogg", [".opus"] = "audio/ogg", [".wav"] = "audio/wav",
        [".wma"] = "audio/x-ms-wma", [".aiff"] = "audio/aiff", [".aif"] = "audio/aiff", [".ape"] = "audio/x-ape", [".wv"] = "audio/x-wavpack",
    };

    public static string ContentType(string path) => Mime.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    private async Task<HttpLite.Response> StreamAsync(HttpLite.Request req, bool download)
    {
        var song = await _host.Ui.InvokeAsync(() => Index.SongById(req["id"]), DispatcherPriority.Background);
        if (song == null) return HttpLite.Response.Text(404, "No existe esa canción");
        int kbps = TranscodeKbps(req, song, download);
        if (kbps > 0) return await TranscodeAsync(req, song, kbps);
        Stream stream;
        try
        {
            // las de carpetas en línea, a través de Echoplex (que ya las sabe leer de la nube)
            stream = await Task.Run(() => OnlineLibrary.IsOnline(song.Path)
                ? OnlineLibrary.OpenStream(song.Path)
                : (Stream)new FileStream(song.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous));
        }
        catch (Exception ex)
        {
            Log($"{req.Remote} stream {Path.GetFileName(song.Path)}: no se pudo abrir ({ex.Message})");
            return HttpLite.Response.Text(404, "No se pudo abrir la canción");
        }
        long length = stream.Length, from = 0, to = length - 1;
        int status = 200;
        var response = new HttpLite.Response { ContentType = ContentType(song.Path), Done = stream.Dispose };
        response.Headers["Accept-Ranges"] = "bytes";
        if (download) response.Headers["Content-Disposition"] = $"attachment; filename=\"{Uri.EscapeDataString(Path.GetFileName(song.Path))}\"";
        if (req.Headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = range[6..].Split(',')[0].Split('-');
            long b = length - 1;
            bool hasFrom = long.TryParse(parts[0], out var a);
            bool hasTo = parts.Length > 1 && long.TryParse(parts[1], out b);
            if (!hasTo) b = length - 1;
            if (!hasFrom && hasTo) { a = Math.Max(0, length - b); b = length - 1; } // «los últimos N bytes»
            if (a >= length || a > b)
            {
                stream.Dispose();
                var bad = HttpLite.Response.Text(416, "");
                bad.Headers["Content-Range"] = $"bytes */{length}";
                return bad;
            }
            from = a;
            to = Math.Min(b, length - 1);
            status = 206;
            response.Headers["Content-Range"] = $"bytes {from}-{to}/{length}";
        }
        response.Status = status;
        response.Length = to - from + 1;
        response.Writer = async (net, ct) =>
        {
            stream.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[64 * 1024];
            long left = to - from + 1;
            while (left > 0)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
                if (n <= 0) break;
                await net.WriteAsync(buffer.AsMemory(0, n), ct);
                left -= n;
            }
        };
        return response;
    }

    // ======================================================================
    // Carátulas (a su tamaño, guardadas un rato en memoria)
    // ======================================================================

    private readonly ConcurrentDictionary<string, byte[]> _covers = new();

    private async Task<HttpLite.Response> CoverAsync(HttpLite.Request req)
    {
        var id = req["id"] ?? "";
        int size = int.TryParse(req["size"], out var sz) ? Math.Clamp(sz, 32, 1500) : 0;
        var key = id + "|" + size;
        if (!_covers.TryGetValue(key, out var bytes))
        {
            // las mismas que en Echoplex (se buscan en el hilo de la interfaz; lo pesado ya va aparte)
            var image = await await _host.Ui.InvokeAsync(() => CoverImage(id, size > 0 ? size : 600), DispatcherPriority.Background);
            bytes = image == null ? Array.Empty<byte>() : await Task.Run(() => Jpeg(image));
            if (_covers.Count > 500) _covers.Clear();
            _covers[key] = bytes;
        }
        if (bytes.Length == 0)
        {
            Log($"{req.Remote} sin carátula para {id}");
            return HttpLite.Response.Text(404, "Sin carátula");
        }
        var response = new HttpLite.Response { Body = bytes, ContentType = "image/jpeg" };
        response.Headers["Cache-Control"] = "max-age=86400";
        return response;
    }

    /// <summary>La carátula de un id: canción, álbum (la de su primera canción o la de la carpeta del disco), artista, playlist o carpeta.</summary>
    private async Task<System.Windows.Media.Imaging.BitmapSource?> CoverImage(string id, int px)
    {
        var ix = Index;
        if (ix.SongById(id) is { } song) return await _host.SongCover(song, px);
        var album = ix.AlbumById(id) ?? ix.ArtistById(id)?.Albums.FirstOrDefault();
        if (album != null)
        {
            var first = album.Songs.FirstOrDefault();
            var img = first == null ? null : await _host.SongCover(first, px);
            // un disco doble («CD 1», «CD 2»…): la portada suele estar en la carpeta del álbum, encima
            if (img == null && (first == null || !string.Equals(first.Directory, album.Directory, StringComparison.OrdinalIgnoreCase)))
                img = await _host.FolderCover(album.Directory, px);
            return img;
        }
        if (id.StartsWith("pl-") && _host.UserData.Playlists.FirstOrDefault(p => id.StartsWith(SubsonicIndex.PlaylistId(p), StringComparison.Ordinal)) is { } pl)
            return await _host.PlaylistCover(pl, px);
        if (ix.DirById(id) is { } dir) return await _host.FolderCover(dir, px);
        return null;
    }

    private static byte[] Jpeg(System.Windows.Media.Imaging.BitmapSource image)
    {
        try
        {
            var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 88 };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    // ---------- registro (movil.log, sin contraseñas) ----------

    private static readonly object LogLock = new();

    public static void Log(string text)
    {
        try
        {
            lock (LogLock)
            {
                var file = LogFile;
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
            }
        }
        catch
        {
            // sin registro
        }
    }
}
