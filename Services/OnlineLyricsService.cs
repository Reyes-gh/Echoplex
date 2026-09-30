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
public static class OnlineLyricsService
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

    public static async Task<OnlineLyrics> FindAsync(Song song, CancellationToken ct = default)
    {
        var key = CacheKey(song);
        var found = Path.Combine(CacheDir, key + ".lrc");
        var none = Path.Combine(CacheDir, key + ".none");
        if (File.Exists(found)) return new(await File.ReadAllTextAsync(found, ct), false, null);
        if (File.Exists(none) && DateTime.UtcNow - File.GetLastWriteTimeUtc(none) < RetryNotFoundAfter)
            return (await File.ReadAllTextAsync(none, ct)).Trim() == "instrumental" ? new(null, true, null) : OnlineLyrics.NotFound;

        // sin etiquetas leídas (canción aún en la nube) el título y el artista son provisionales: se espera a tenerlas
        if (!song.MetadataLoaded) return OnlineLyrics.NotFound;
        var title = song.MetaTitle.Trim();
        var artist = song.PrimaryArtist.Trim();
        if (title.Length == 0) return OnlineLyrics.NotFound;
        double? duration = song.Duration?.TotalSeconds;

        OnlineLyrics result;
        try
        {
            result = await ExactAsync(title, artist, song.Album, duration, ct) ?? await SearchAsync(title, artist, duration, ct) ?? OnlineLyrics.NotFound;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(null, false, "No se pudo conectar con LRCLIB");
        }

        try
        {
            Directory.CreateDirectory(CacheDir);
            if (result.Text != null) await File.WriteAllTextAsync(found, result.Text, ct);
            else await File.WriteAllTextAsync(none, result.Instrumental ? "instrumental" : "none", ct);
        }
        catch
        {
            // sin caché: se volverá a preguntar la próxima vez
        }
        return result;
    }

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

    /// <summary>Búsqueda libre por título y artista: la versión sincronizada con la duración más parecida.</summary>
    private static async Task<OnlineLyrics?> SearchAsync(string title, string artist, double? duration, CancellationToken ct)
    {
        var url = $"{Api}/search?track_name={Uri.EscapeDataString(title)}" + (artist.Length > 0 ? $"&artist_name={Uri.EscapeDataString(artist)}" : "");
        using var resp = await Http.Value.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var candidates = doc.RootElement.EnumerateArray()
            .Select(r => (rec: r, dur: r.TryGetProperty("duration", out var dd) && dd.ValueKind == JsonValueKind.Number ? dd.GetDouble() : (double?)null))
            .Select(x => (x.rec, x.dur, diff: duration is { } d && x.dur is { } rd ? Math.Abs(rd - d) : 0))
            // con duración conocida, solo versiones de la misma longitud (±3 s): otra versión tendría otros tiempos
            .Where(x => duration == null || x.diff <= 3)
            .OrderBy(x => HasText(x.rec, "syncedLyrics") ? 0 : 1).ThenBy(x => x.diff)
            .ToList();
        foreach (var (rec, _, _) in candidates)
            if (FromRecord(rec) is { } found && (found.Text != null || found.Instrumental)) return found;
        return null;
    }

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
