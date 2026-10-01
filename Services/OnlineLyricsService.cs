using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Echoplex.Models;

namespace Echoplex.Services;

/// <summary>Resultado de buscar una letra en internet.</summary>
/// <param name="Text">Letra (LRC con tiempos si la hay sincronizada, si no texto plano), o null.</param>
/// <param name="Error">Mensaje si no se pudo consultar (sin conexión, servidor caído…); no se guarda en caché.</param>
public sealed record OnlineLyrics(string? Text, bool Instrumental, string? Error)
{
    public static readonly OnlineLyrics NotFound = new(null, false, null);
}

/// <summary>
/// Letras de LRCLIB (lrclib.net): gratis, sin clave y con tiempos por línea. Solo se usa si el usuario lo activa.
/// Por canción solo se envían artista, título, álbum y duración. Lo encontrado (o que no hay nada) se guarda en
/// %LocalAppData%\Echoplex\lyrics para no volver a preguntar; nunca se escribe en la carpeta de música.
/// </summary>
public static partial class OnlineLyricsService
{
    private const string Api = "https://lrclib.net/api";
    private static readonly TimeSpan RetryNotFoundAfter = TimeSpan.FromDays(7);

    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // LRCLIB pide identificar la aplicación: nombre, versión y dirección del proyecto
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Echoplex/{UpdateService.CurrentVersion} (+https://github.com/{UpdateService.Repository})");
        return http;
    });

    public static string CacheDir => Path.Combine(SettingsStore.DataDirectory, "lyrics");

    private static string CacheKey(Song song) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(song.Path.ToLowerInvariant())))[..20];

    /// <summary>
    /// Caché por canción: <c>.lrc</c> letra sincronizada (definitiva); <c>.txt</c> solo texto y <c>.none</c> nada o
    /// instrumental, que se vuelven a consultar pasada una semana por si alguien sube la versión sincronizada.
    /// Las cachés antiguas guardaban también texto sin tiempos en <c>.lrc</c>: esas se vuelven a buscar.
    /// </summary>
    public static async Task<OnlineLyrics> FindAsync(Song song, CancellationToken ct = default)
    {
        var key = CacheKey(song);
        var syncedFile = Path.Combine(CacheDir, key + ".lrc");
        var plainFile = Path.Combine(CacheDir, key + ".txt");
        var noneFile = Path.Combine(CacheDir, key + ".none");

        string? stalePlain = null; // texto sin tiempos ya guardado: se usa si no aparece nada mejor
        if (File.Exists(syncedFile))
        {
            var text = await File.ReadAllTextAsync(syncedFile, ct);
            if (IsSynced(text)) return new(text, false, null);
            stalePlain = text;
        }
        if (File.Exists(plainFile))
        {
            var text = await File.ReadAllTextAsync(plainFile, ct);
            if (IsFresh(plainFile)) return new(text, false, null);
            stalePlain ??= text;
        }
        if (stalePlain == null && File.Exists(noneFile) && IsFresh(noneFile))
            return (await File.ReadAllTextAsync(noneFile, ct)).Trim() == "instrumental" ? new(null, true, null) : OnlineLyrics.NotFound;

        // sin etiquetas leídas (canción aún en la nube) el título y el artista son provisionales: se espera a tenerlas
        OnlineLyrics Fallback() => stalePlain != null ? new(stalePlain, false, null) : OnlineLyrics.NotFound;
        if (!song.MetadataLoaded) return Fallback();
        var title = song.MetaTitle.Trim();
        var artist = song.PrimaryArtist.Trim();
        if (title.Length == 0) return Fallback();
        double? duration = song.Duration?.TotalSeconds;

        OnlineLyrics result;
        try
        {
            result = await LookupAsync(title, artist, song.Album, duration, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return stalePlain != null ? new(stalePlain, false, null) : new(null, false, "No se pudo conectar con LRCLIB");
        }
        if (result.Text == null && stalePlain != null) result = new(stalePlain, false, null);

        try
        {
            Directory.CreateDirectory(CacheDir);
            if (result.Text != null && IsSynced(result.Text))
            {
                await File.WriteAllTextAsync(syncedFile, result.Text, ct);
                if (File.Exists(plainFile)) File.Delete(plainFile);
            }
            else if (result.Text != null)
            {
                await File.WriteAllTextAsync(plainFile, result.Text, ct);
                if (File.Exists(syncedFile)) File.Delete(syncedFile); // la caché antigua sin tiempos
            }
            else await File.WriteAllTextAsync(noneFile, result.Instrumental ? "instrumental" : "none", ct);
        }
        catch
        {
            // sin caché: se volverá a preguntar la próxima vez
        }
        return result;
    }

    private static bool IsFresh(string file) => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < RetryNotFoundAfter;

    /// <summary>¿Tiene tiempos por línea ([mm:ss.xx])?</summary>
    private static bool IsSynced(string text) => SyncedLine().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\[\d{1,3}:\d{2}", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex SyncedLine();

    /// <summary>
    /// La mejor letra: la coincidencia exacta si viene sincronizada; si no, una versión sincronizada de la búsqueda
    /// (mismo título y misma duración ±3 s); y solo si no hay ninguna, el texto sin tiempos (mejor el exacto).
    /// LRCLIB guarda varias entradas por canción y la exacta a veces es una antigua sin tiempos.
    /// </summary>
    private static async Task<OnlineLyrics> LookupAsync(string title, string artist, string album, double? duration, CancellationToken ct)
    {
        var exact = await ExactAsync(title, artist, album, duration, ct);
        if (exact?.Text is { } t && IsSynced(t)) return exact;
        var searched = await SearchAsync(title, artist, duration, ct);
        if (searched?.Text is { } s && IsSynced(s)) return searched;
        // segundo intento con el título sin añadidos («(Tree City Sessions)», «[Remastered]», «- Live»…): la búsqueda
        // de LRCLIB a veces no devuelve nada con ellos; la duración (±3 s) sigue evitando que se cuele otra versión
        var simple = Simplify(title);
        if (simple.Length > 0 && !string.Equals(simple, title, StringComparison.OrdinalIgnoreCase))
        {
            var again = await SearchAsync(simple, artist, duration, ct);
            if (again?.Text is { } a && IsSynced(a)) return again;
            searched ??= again;
        }
        return exact ?? searched ?? OnlineLyrics.NotFound;
    }

    /// <summary>Título sin lo que va entre paréntesis o corchetes ni una coletilla tras « - ».</summary>
    private static string Simplify(string title)
    {
        var s = Brackets().Replace(title, " ");
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) s = s[..dash];
        return Spaces().Replace(s, " ").Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial System.Text.RegularExpressions.Regex Brackets();

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex Spaces();

    /// <summary>Búsqueda exacta por artista, título, álbum y duración (LRCLIB admite ±2 s de diferencia).</summary>
    private static async Task<OnlineLyrics?> ExactAsync(string title, string artist, string album, double? duration, CancellationToken ct)
    {
        if (duration is not { } d || artist.Length == 0) return null;
        var url = $"{Api}/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}" +
                  $"&album_name={Uri.EscapeDataString(album)}&duration={Math.Round(d)}";
        using var resp = await Http.Value.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return FromRecord(doc.RootElement);
    }

    /// <summary>
    /// Búsqueda libre por título y artista: entre las entradas del mismo título (sin contar signos: «Pt.2 1/2» y
    /// «Pt. 2 1-2» son la misma) y la misma duración (±3 s; otra versión tendría otros tiempos), primero las
    /// sincronizadas y, entre ellas, la de duración más parecida.
    /// </summary>
    private static async Task<OnlineLyrics?> SearchAsync(string title, string artist, double? duration, CancellationToken ct)
    {
        var url = $"{Api}/search?track_name={Uri.EscapeDataString(title)}" + (artist.Length > 0 ? $"&artist_name={Uri.EscapeDataString(artist)}" : "");
        using var resp = await Http.Value.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var wanted = Norm(title);
        var candidates = doc.RootElement.EnumerateArray()
            .Where(r => SameTitle(wanted, r.TryGetProperty("trackName", out var tn) && tn.ValueKind == JsonValueKind.String ? Norm(tn.GetString() ?? "") : ""))
            .Select(r => (rec: r, dur: r.TryGetProperty("duration", out var dd) && dd.ValueKind == JsonValueKind.Number ? dd.GetDouble() : (double?)null))
            .Select(x => (x.rec, x.dur, diff: duration is { } d && x.dur is { } rd ? Math.Abs(rd - d) : 0))
            .Where(x => duration == null || x.diff <= 3)
            .OrderBy(x => HasText(x.rec, "syncedLyrics") ? 0 : HasText(x.rec, "plainLyrics") ? 1 : 2).ThenBy(x => x.diff)
            .ToList();
        foreach (var (rec, _, _) in candidates)
            if (FromRecord(rec) is { } found && (found.Text != null || found.Instrumental)) return found;
        return null;
    }

    /// <summary>Título comparable: solo letras y números, en minúsculas.</summary>
    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Mismo título, o uno contiene al otro (p. ej. con un «(Live)» o «Remastered» añadido).</summary>
    private static bool SameTitle(string wanted, string candidate) =>
        candidate.Length > 0 && (candidate == wanted || candidate.Contains(wanted) || wanted.Contains(candidate));

    private static bool HasText(JsonElement rec, string field) =>
        rec.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());

    private static OnlineLyrics? FromRecord(JsonElement rec)
    {
        if (HasText(rec, "syncedLyrics")) return new(rec.GetProperty("syncedLyrics").GetString(), false, null);
        if (HasText(rec, "plainLyrics")) return new(rec.GetProperty("plainLyrics").GetString(), false, null);
        if (rec.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True) return new(null, true, null);
        return null;
    }
}
