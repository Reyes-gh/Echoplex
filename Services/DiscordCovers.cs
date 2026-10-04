using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Echoplex.Models;

namespace Echoplex.Services;

/// <summary>
/// URL de la carátula para Discord, que solo enseña imágenes que estén en internet. En este orden:
///   1. las que ya se subieron, reconocidas por el contenido de la imagen (cada portada distinta se sube una vez,
///      aunque dos álbumes se llamen igual o cada canción suelta traiga la suya),
///   2. las que subió foo_discord_rich (su JSON álbum → URL), si se usa en este PC,
///   3. si hay programa de subida, la imagen original tal cual (sin reducir ni recomprimir), que se le pasa como a
///      foo_discord_rich: la ruta por la entrada estándar y la URL de vuelta por la salida.
/// </summary>
internal sealed partial class DiscordCovers
{
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FoobarRecheck = TimeSpan.FromSeconds(30);

    private readonly string _dataDir;
    private readonly string _cacheFile;
    private readonly Func<Song, byte[]?> _coverBytes;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    /// <summary>Imágenes cuya subida falló: no se reintentan hasta que cambie el programa (o se reinicie Echoplex).</summary>
    private readonly HashSet<string> _failed = new();
    private Dictionary<string, CoverEntry>? _urls;
    private string? _command;
    private Dictionary<string, string>? _foobar;
    private string? _foobarFile;
    private DateTime _foobarStamp, _foobarChecked;

    public DiscordCovers(Func<Song, byte[]?> coverBytes, string dataDirectory)
    {
        _coverBytes = coverBytes;
        _dataDir = dataDirectory;
        _cacheFile = Path.Combine(dataDirectory, "discord-covers.json");
    }

    /// <summary>Línea para el registro (subidas, errores).</summary>
    public event Action<string>? Log;

    /// <summary>Empieza (texto) o acaba (null) una subida.</summary>
    public event Action<string?>? Uploading;

    /// <summary>Programa que sube una imagen y escribe su URL; vacío = no se sube nada.</summary>
    public string? Command
    {
        get
        {
            lock (_lock) return _command;
        }
        set
        {
            lock (_lock)
            {
                value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (value == _command) return;
                _command = value;
                _failed.Clear();
                _foobarChecked = default; // con otro programa puede cambiar dónde está foobar2000
            }
        }
    }

    public async Task<string?> ResolveAsync(Song song, string album, CancellationToken ct)
    {
        if (await CoverOf(song, ct) is not { } bytes) return null;
        var key = KeyOf(bytes);
        var entry = Entry(key);
        if (entry is { Reduced: false } && DiscordShows(entry.Url)) return entry.Url;
        if (entry == null && FoobarUrl(album) is { } fromFoobar)
        {
            Remember(key, fromFoobar, album, original: false);
            return fromFoobar;
        }

        // sin subir aún, subida reducida por una versión anterior o en un formato que Discord no enseña: se sube la
        // original (si no se puede, vale la que hay, si Discord la enseña)
        var fallback = entry != null && DiscordShows(entry.Url) ? entry.Url : null;
        string? command;
        lock (_lock)
        {
            command = _command;
            if (command == null || _failed.Contains(key)) return fallback;
        }
        await _uploadGate.WaitAsync(ct);
        try
        {
            // la subió otra canción del mismo álbum mientras esperaba
            if (Entry(key) is { Reduced: false } meanwhile && DiscordShows(meanwhile.Url)) return meanwhile.Url;
            return await UploadCoverAsync(bytes, key, album, command, ct) ?? fallback;
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    /// <summary>¿La enseña Discord? Las BMP y TIFF (algunas que subió foo_discord_rich) salen como una interrogación.</summary>
    internal static bool DiscordShows(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() is not (".bmp" or ".tif" or ".tiff");
    }

    /// <summary>Canciones que se miran como mucho al volver a subir, y carátulas distintas que se suben.</summary>
    private const int MaxRefreshSongs = 400, MaxRefreshCovers = 25;

    /// <summary>
    /// Vuelve a subir las carátulas de estas canciones aunque ya estuvieran subidas o las tuviera foo_discord_rich
    /// (cambiaste la imagen y Discord sigue enseñando la de antes). Cada imagen distinta se sube una sola vez.
    /// </summary>
    public async Task<(int uploaded, int failed)> RefreshAsync(IReadOnlyList<Song> songs, CancellationToken ct)
    {
        string? command;
        lock (_lock) command = _command;
        if (command == null) return (0, 0);
        var seen = new HashSet<string>();
        int uploaded = 0, failed = 0;
        foreach (var song in songs.Take(MaxRefreshSongs))
        {
            if (await CoverOf(song, ct) is not { } bytes) continue;
            var key = KeyOf(bytes);
            if (!seen.Add(key)) continue;
            if (seen.Count > MaxRefreshCovers) break;
            await _uploadGate.WaitAsync(ct);
            try
            {
                if (await UploadCoverAsync(bytes, key, song.Album, command, ct) != null)
                {
                    lock (_lock) _failed.Remove(key);
                    uploaded++;
                }
                else failed++;
            }
            finally
            {
                _uploadGate.Release();
            }
        }
        return (uploaded, failed);
    }

    private async Task<byte[]?> CoverOf(Song song, CancellationToken ct)
    {
        var bytes = await Task.Run(() =>
        {
            try
            {
                return _coverBytes(song);
            }
            catch
            {
                return null;
            }
        }, ct);
        return bytes is { Length: > 0 } ? bytes : null;
    }

    /// <summary>Huella del contenido de la imagen: la misma portada da la misma clave aunque venga de otra canción.</summary>
    private static string KeyOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes), 0, 16).ToLowerInvariant();

    /// <summary>Deja la imagen en un archivo, la sube y guarda su URL (null si falla). Con _uploadGate cogido.</summary>
    private async Task<string?> UploadCoverAsync(byte[] bytes, string key, string album, string command, CancellationToken ct)
    {
        Uploading?.Invoke(string.IsNullOrWhiteSpace(album) ? "Subiendo la carátula…" : $"Subiendo la carátula de «{album.Trim()}»…");
        var file = await Task.Run(() => SaveOriginal(bytes, key, Path.Combine(_dataDir, "discord")), ct);
        string? url = null;
        try
        {
            if (file == null) Log?.Invoke("No se pudo preparar la imagen para subirla");
            else url = await UploadAsync(command, file, ct);
        }
        finally
        {
            if (file != null) TryDelete(file);
            Uploading?.Invoke(null);
        }
        if (url == null)
        {
            lock (_lock) _failed.Add(key);
            return null;
        }
        Remember(key, url, album, original: true);
        return url;
    }

    // ---------- URL ya conocidas ----------

    /// <summary>Una carátula subida: su URL y, para que el JSON se entienda al abrirlo, de qué álbum es.</summary>
    private sealed class CoverEntry
    {
        [JsonPropertyName("album")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Album { get; set; }

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        /// <summary>Subida por Echoplex con la imagen original (desde la 1.1.14).</summary>
        [JsonPropertyName("original")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Original { get; set; }

        /// <summary>
        /// La subió reducida (JPEG de 512 px) una versión anterior: se reconoce por el nombre que le daba Echoplex,
        /// echoplex_&lt;huella&gt;….jpg. Se vuelve a subir la original la próxima vez que suene.
        /// </summary>
        [JsonIgnore]
        public bool Reduced => !Original && OldReducedUpload().IsMatch(Url);
    }

    [GeneratedRegex(@"/echoplex_[0-9a-f]{16}[^/]*\.jpg$", RegexOptions.IgnoreCase)]
    private static partial Regex OldReducedUpload();

    private static readonly JsonSerializerOptions CacheJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // tildes legibles
    };

    private CoverEntry? Entry(string key)
    {
        lock (_lock)
        {
            _urls ??= LoadCache();
            return _urls.GetValueOrDefault(key);
        }
    }

    /// <summary>Guarda (o sustituye) la URL de esta imagen en el JSON local.</summary>
    private void Remember(string key, string url, string? album, bool original)
    {
        string json;
        lock (_lock)
        {
            _urls ??= LoadCache();
            _urls[key] = new CoverEntry { Url = url, Album = string.IsNullOrWhiteSpace(album) ? null : album.Trim(), Original = original };
            json = JsonSerializer.Serialize(_urls, CacheJson);
        }
        try
        {
            Directory.CreateDirectory(_dataDir);
            var tmp = _cacheFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _cacheFile, true);
        }
        catch
        {
            // sin guardar: se volverá a buscar la próxima vez
        }
    }

    /// <summary>Lee el JSON local; acepta también el formato de la primera 1.1.12 (huella → URL, sin álbum).</summary>
    private Dictionary<string, CoverEntry> LoadCache()
    {
        var map = new Dictionary<string, CoverEntry>();
        try
        {
            if (!File.Exists(_cacheFile)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(_cacheFile));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String) map[p.Name] = new CoverEntry { Url = p.Value.GetString()! };
                else if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } u)
                    map[p.Name] = new CoverEntry
                    {
                        Url = u,
                        Album = p.Value.TryGetProperty("album", out var a) ? a.GetString() : null,
                        Original = p.Value.TryGetProperty("original", out var o) && o.ValueKind == JsonValueKind.True,
                    };
            }
        }
        catch
        {
            // caché rota: se empieza de cero
        }
        return map;
    }

    // ---------- foo_discord_rich ----------

    /// <summary>La URL que subió foo_discord_rich para ese álbum (su JSON se relee si cambia: foobar2000 puede seguir subiendo).</summary>
    private string? FoobarUrl(string album)
    {
        if (string.IsNullOrWhiteSpace(album)) return null;
        lock (_lock)
        {
            if (DateTime.UtcNow - _foobarChecked > FoobarRecheck)
            {
                _foobarChecked = DateTime.UtcNow;
                ReloadFoobar();
            }
            return _foobar?.GetValueOrDefault(album.Trim());
        }
    }

    private void ReloadFoobar()
    {
        try
        {
            var file = FindFoobarFile(_command);
            if (file == null)
            {
                _foobar = null;
                _foobarFile = null;
                return;
            }
            var stamp = File.GetLastWriteTimeUtc(file);
            if (file == _foobarFile && stamp == _foobarStamp) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(file)) ?? new();
            var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (album, url) in raw)
                if (url != null && IsImageUrl(url) && DiscordShows(url)) urls[album.Trim()] = url;
            _foobar = urls;
            _foobarFile = file;
            _foobarStamp = stamp;
            Log?.Invoke($"Carátulas ya subidas con foo_discord_rich: {urls.Count} ({file})");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"No se pudo leer el JSON de foo_discord_rich: {ex.Message}");
        }
    }

    /// <summary>
    /// art_urls*.json de foo_discord_rich: en foobar2000 instalado está en %AppData%; en uno portable se busca junto a
    /// las rutas del programa de subida (suele estar en la carpeta de foobar2000, junto a foobar2000.exe).
    /// </summary>
    private static string? FindFoobarFile(string? command)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new List<string>
        {
            Path.Combine(appData, "foobar2000-v2", "foo_discord_rich", "images"),
            Path.Combine(appData, "foobar2000", "foo_discord_rich", "images"),
        };
        if (command != null)
        {
            foreach (Match m in AbsolutePath().Matches(command))
            {
                var dir = Path.GetDirectoryName(m.Value);
                for (int up = 0; up < 3 && dir != null; up++, dir = Path.GetDirectoryName(dir))
                {
                    dirs.Add(Path.Combine(dir, "profile", "foo_discord_rich", "images"));
                    dirs.Add(Path.Combine(dir, "foo_discord_rich", "images"));
                }
            }
        }
        return dirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "art_urls*.json"))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^""]+?\.\w{1,4}(?=""|\s|$)")]
    private static partial Regex AbsolutePath();

    // ---------- subida ----------

    /// <summary>
    /// La imagen tal cual (los mismos bytes: ni se reduce ni se recomprime), con la extensión de su formato. Solo si no
    /// es un formato que Discord enseñe (JPEG, PNG, GIF o WebP; p. ej. un .bmp o .tif de carpeta) se pasa a PNG, que
    /// no pierde nada, con su tamaño original.
    /// </summary>
    private static string? SaveOriginal(byte[] bytes, string key, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var ext = WebImageExtension(bytes);
            var file = Path.Combine(dir, $"echoplex_{key[..16]}{ext ?? ".png"}");
            if (ext != null)
            {
                File.WriteAllBytes(file, bytes);
                return file;
            }
            var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource src = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Bgr24
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            using (var fs = File.Create(file)) encoder.Save(fs);
            return file;
        }
        catch
        {
            return null;
        }
    }

    private static readonly byte[] JpegSignature = { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Extensión de las imágenes que Discord enseña, por sus primeros bytes; null si es otro formato.</summary>
    internal static string? WebImageExtension(ReadOnlySpan<byte> b) =>
        b.StartsWith(JpegSignature) ? ".jpg"
        : b.StartsWith(PngSignature) ? ".png"
        : b.StartsWith("GIF8"u8) ? ".gif"
        : b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? ".webp"
        : null;

    private async Task<string?> UploadAsync(string command, string file, CancellationToken ct)
    {
        var (exe, args) = SplitCommand(command);
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var clock = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"No se pudo ejecutar «{exe}»: {ex.Message}");
            return null;
        }
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            // como foo_discord_rich: la ruta de la imagen por la entrada estándar (UTF-8 sin BOM)
            await process.StandardInput.BaseStream.WriteAsync(new UTF8Encoding(false).GetBytes(file), ct);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // el programa no lee la entrada: allá él
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(UploadTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ya había acabado
            }
            if (ct.IsCancellationRequested) throw;
            Log?.Invoke("La subida tardó más de 2 minutos y se canceló");
            return null;
        }

        string stdout = await output, stderr = await errors;
        var url = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(IsImageUrl);
        if (process.ExitCode != 0 || url == null)
        {
            Log?.Invoke($"La subida falló (código {process.ExitCode}): {Excerpt(stdout + " " + stderr)}");
            return null;
        }
        Log?.Invoke($"Carátula subida en {clock.Elapsed.TotalSeconds:0.0} s: {url}");
        return url;
    }

    /// <summary>
    /// Programa y argumentos de una línea de órdenes: «"C:\ruta con espacios\x.exe" args», «C:\Python\python.exe "script.py"»
    /// o «programa args».
    /// </summary>
    internal static (string exe, string args) SplitCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0) return (command[1..end], command[(end + 1)..].Trim());
        }
        var m = ExecutablePrefix().Match(command);
        if (m.Success) return (m.Groups[1].Value, command[m.Length..].Trim());
        int space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }

    [GeneratedRegex(@"^(.+?\.(?:exe|bat|cmd|com))(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutablePrefix();

    internal static bool IsImageUrl(string s) =>
        s.Length is > 10 and <= 256 && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>Un trozo corto de la salida para el registro, sin nada que parezca una clave.</summary>
    private static string Excerpt(string text)
    {
        text = Secrets().Replace(text.ReplaceLineEndings(" ").Trim(), "***");
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    [GeneratedRegex(@"github_pat_\w{10,}|gh[pousr]_[A-Za-z0-9]{10,}|(?i:bearer|token)\s+[\w\-\.]{16,}")]
    private static partial Regex Secrets();

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch
        {
            // se queda en %LocalAppData%\Echoplex\discord; se sobrescribe la próxima vez
        }
    }
}
