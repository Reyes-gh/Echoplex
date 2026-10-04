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
///   3. si hay programa de subida, una copia a 512 px que se le pasa como a foo_discord_rich: la ruta por la
///      entrada estándar y la URL de vuelta por la salida.
/// </summary>
internal sealed partial class DiscordCovers
{
    private const int MaxSide = 512;
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
        if (Known(key) is { } known) return known;
        if (FoobarUrl(album) is { } fromFoobar)
        {
            Remember(key, fromFoobar, album);
            return fromFoobar;
        }

        string? command;
        lock (_lock)
        {
            command = _command;
            if (command == null || _failed.Contains(key)) return null;
        }
        await _uploadGate.WaitAsync(ct);
        try
        {
            if (Known(key) is { } meanwhile) return meanwhile; // la subió otra canción del mismo álbum mientras esperaba
            return await UploadCoverAsync(bytes, key, album, command, ct);
        }
        finally
        {
            _uploadGate.Release();
        }
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

    /// <summary>Prepara la copia, la sube y guarda su URL (null si falla). Con _uploadGate cogido.</summary>
    private async Task<string?> UploadCoverAsync(byte[] bytes, string key, string album, string command, CancellationToken ct)
    {
        Uploading?.Invoke(string.IsNullOrWhiteSpace(album) ? "Subiendo la carátula…" : $"Subiendo la carátula de «{album.Trim()}»…");
        var file = await Task.Run(() => SaveJpeg(bytes, key, Path.Combine(_dataDir, "discord")), ct);
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
        Remember(key, url, album);
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
    }

    private static readonly JsonSerializerOptions CacheJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // tildes legibles
    };

    private string? Known(string key)
    {
        lock (_lock)
        {
            _urls ??= LoadCache();
            return _urls.GetValueOrDefault(key)?.Url;
        }
    }

    /// <summary>Guarda (o sustituye) la URL de esta imagen en el JSON local.</summary>
    private void Remember(string key, string url, string? album)
    {
        string json;
        lock (_lock)
        {
            _urls ??= LoadCache();
            _urls[key] = new CoverEntry { Url = url, Album = string.IsNullOrWhiteSpace(album) ? null : album.Trim() };
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
                    map[p.Name] = new CoverEntry { Url = u, Album = p.Value.TryGetProperty("album", out var a) ? a.GetString() : null };
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
                if (url != null && IsImageUrl(url)) urls[album.Trim()] = url;
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

    /// <summary>Copia en JPEG de como mucho 512 px (Discord la enseña pequeña; así sube rápido y en un formato que siempre abre).</summary>
    private static string? SaveJpeg(byte[] bytes, string key, string dir)
    {
        try
        {
            var header = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            int w = header.PixelWidth, h = header.PixelHeight;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.StreamSource = new MemoryStream(bytes);
            if (w >= h && w > MaxSide) img.DecodePixelWidth = MaxSide;
            else if (h > w && h > MaxSide) img.DecodePixelHeight = MaxSide;
            img.EndInit();
            img.Freeze();
            BitmapSource src = img.Format == PixelFormats.Bgr24 || img.Format == PixelFormats.Bgr32
                ? img
                : new FormatConvertedBitmap(img, PixelFormats.Bgr24, null, 0);
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(src));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"echoplex_{key[..16]}.jpg");
            using (var fs = File.Create(file)) encoder.Save(fs);
            return file;
        }
        catch
        {
            return null;
        }
    }

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
