using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Echoplex.Services;

/// <summary>
/// Carátulas para Discord sin subir nada: se buscan en internet por artista y álbum (Deezer y, si no, iTunes; los dos
/// gratis y sin claves). Solo vale una coincidencia exacta del título (sin contar «Deluxe», «Remastered», «Explicit»…)
/// y del artista: mejor el logo que la carátula de otra edición. Lo encontrado (y lo que no) se guarda en un JSON local.
/// </summary>
internal sealed partial class OnlineCovers
{
    /// <summary>Lo no encontrado se vuelve a buscar pasado este tiempo (puede que lo publiquen).</summary>
    private static readonly TimeSpan RetryMissing = TimeSpan.FromDays(14);
    private static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(300);

    /// <summary>Las pruebas sustituyen la búsqueda en internet (artista, álbum, título → URL).</summary>
    private static Func<string, string, string, string?>? s_fakeLookup;

    private static readonly HttpClient Http = CreateClient();

    private readonly string _file;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, Entry>? _cache;
    private DateTime _lastRequest;

    public OnlineCovers(string dataDirectory) => _file = Path.Combine(dataDirectory, "discord-covers-online.json");

    /// <summary>Línea para el registro.</summary>
    public event Action<string>? Log;

    private sealed class Entry
    {
        [JsonPropertyName("artist")] public string Artist { get; set; } = "";
        [JsonPropertyName("album")] public string Album { get; set; } = "";
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("source")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Source { get; set; }
        [JsonPropertyName("checked")] public DateTime Checked { get; set; }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Echoplex/{UpdateService.CurrentVersion} (+https://github.com/{UpdateService.Repository})");
        return client;
    }

    /// <summary>
    /// URL de la carátula del álbum (o, en canciones sin álbum, la del sencillo por su título), o null si no hay una que
    /// coincida de verdad.
    /// </summary>
    public async Task<string?> FindAsync(string artist, string album, string title, CancellationToken ct)
    {
        artist = artist.Trim();
        album = album.Trim();
        title = title.Trim();
        if (artist.Length == 0 || (album.Length == 0 && title.Length == 0)) return null;
        var key = Normalize(artist) + "|" + (album.Length > 0 ? Normalize(album) : "#" + Normalize(title));
        lock (_lock)
        {
            _cache ??= Load();
            if (_cache.TryGetValue(key, out var hit) && (hit.Url != null || DateTime.UtcNow - hit.Checked < RetryMissing)) return hit.Url;
        }

        await _gate.WaitAsync(ct);
        try
        {
            lock (_lock)
                if (_cache!.TryGetValue(key, out var meanwhile) && (meanwhile.Url != null || DateTime.UtcNow - meanwhile.Checked < RetryMissing))
                    return meanwhile.Url;

            string? url = null, source = null;
            if (s_fakeLookup is { } fake)
            {
                url = fake(artist, album, title);
                source = "prueba";
            }
            else if (album.Length > 0)
            {
                url = await DeezerAlbumAsync(artist, album, ct);
                source = "Deezer";
                if (url == null)
                {
                    url = await ITunesAlbumAsync(artist, album, ct);
                    source = "iTunes";
                }
            }
            else
            {
                url = await DeezerTrackAsync(artist, title, ct);
                source = "Deezer";
            }

            var what = album.Length > 0 ? $"«{album}» de {artist}" : $"«{title}» de {artist}";
            Log?.Invoke(url != null ? $"Carátula de {what} encontrada en {source}" : $"No se encontró en internet la carátula de {what}");
            Remember(key, new Entry { Artist = artist, Album = album.Length > 0 ? album : title, Url = url, Source = url != null ? source : null, Checked = DateTime.UtcNow });
            return url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Error buscando la carátula en internet: {ex.Message}");
            return null; // sin conexión, etc.: no se guarda, se reintenta la próxima vez
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------- servicios ----------

    private async Task<string?> DeezerAlbumAsync(string artist, string album, CancellationToken ct)
    {
        var baseTitle = BaseTitle(album);
        // primero la búsqueda por campos; si no da nada que coincida, texto libre
        foreach (var query in new[] { $"artist:\"{artist}\" album:\"{baseTitle}\"", $"{artist} {baseTitle}" })
        {
            using var doc = await GetJsonAsync($"https://api.deezer.com/search/album?q={Uri.EscapeDataString(query)}&limit=15", ct);
            if (doc == null || !doc.RootElement.TryGetProperty("data", out var data)) continue;
            var url = Best(data.EnumerateArray().Select(e => (
                Str(e, "title"),
                e.TryGetProperty("artist", out var a) ? Str(a, "name") : null,
                Str(e, "cover_big"))), artist, album);
            if (url != null) return url;
        }
        return null;
    }

    private async Task<string?> DeezerTrackAsync(string artist, string title, CancellationToken ct)
    {
        // por canción solo funciona bien el texto libre (la búsqueda por campos «track:» no devuelve nada)
        var q = Uri.EscapeDataString($"{artist} {BaseTitle(title)}");
        using var doc = await GetJsonAsync($"https://api.deezer.com/search/track?q={q}&limit=15", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("data", out var data)) return null;
        return Best(data.EnumerateArray().Select(e => (
            Str(e, "title"),
            e.TryGetProperty("artist", out var a) ? Str(a, "name") : null,
            e.TryGetProperty("album", out var al) ? Str(al, "cover_big") : null)), artist, title);
    }

    private async Task<string?> ITunesAlbumAsync(string artist, string album, CancellationToken ct)
    {
        var term = Uri.EscapeDataString($"{artist} {BaseTitle(album)}");
        using var doc = await GetJsonAsync($"https://itunes.apple.com/search?term={term}&entity=album&limit=15", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("results", out var results)) return null;
        return Best(results.EnumerateArray().Select(e => (
            Str(e, "collectionName"),
            Str(e, "artistName"),
            Str(e, "artworkUrl100")?.Replace("100x100bb", "512x512bb"))), artist, album);
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        // de una en una y con un respiro: los dos servicios limitan las peticiones seguidas
        var wait = _lastRequest + MinGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        _lastRequest = DateTime.UtcNow;
        using var response = await Http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    // ---------- coincidencias ----------

    /// <summary>
    /// El mejor candidato que coincida de verdad: mismo título base y mismo artista. A igualdad, el que no lleve versiones
    /// especiales (instrumental, karaoke, en directo…) que no estuvieran en lo que se busca.
    /// </summary>
    internal static string? Best(IEnumerable<(string? title, string? artist, string? url)> candidates, string artist, string wanted)
    {
        var wantedBase = Normalize(BaseTitle(wanted));
        var wantedArtist = Normalize(artist);
        var wantedExtras = Extras(wanted);
        string? best = null;
        int bestScore = 0;
        foreach (var (title, candidateArtist, url) in candidates)
        {
            if (title == null || candidateArtist == null || url == null || url.Length > 256 || !url.StartsWith("https://", StringComparison.Ordinal)) continue;
            if (Normalize(BaseTitle(title)) != wantedBase) continue;
            var a = Normalize(candidateArtist);
            if (a != wantedArtist && !a.Contains(wantedArtist) && !wantedArtist.Contains(a)) continue;
            // versiones especiales que no se pidieron: otra carátula, se descartan
            if (Extras(title).Except(wantedExtras).Any()) continue;
            int score = (a == wantedArtist ? 2 : 1) + (Normalize(title) == Normalize(wanted) ? 2 : 0);
            if (score > bestScore)
            {
                best = url;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>Título sin lo que va entre paréntesis o corchetes ni lo de detrás de « - » (ediciones, versiones…).</summary>
    internal static string BaseTitle(string s)
    {
        s = Brackets().Replace(s, " ");
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) s = s[..dash];
        return s.Trim();
    }

    /// <summary>Palabras de versión especial que cambian la carátula (instrumental, karaoke, en directo, remix…).</summary>
    private static HashSet<string> Extras(string s)
    {
        var n = Normalize(s);
        return SpecialWords().Matches(n).Select(m => m.Value).ToHashSet();
    }

    /// <summary>Minúsculas, sin tildes ni signos, «&amp;» como «and» y espacios simples: para comparar títulos y artistas.</summary>
    internal static string Normalize(string s)
    {
        s = s.Replace("&", " and ").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\b(instrumental|instrumentals|karaoke|live|en vivo|en directo|acoustic|acustico|remix|remixes|remixed|commentary|8 bit|lullaby|tribute|cover|covers|sped up|slowed|reverb|piano)\b")]
    private static partial Regex SpecialWords();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---------- JSON local ----------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_file), Json) is { } map)
                return map;
        }
        catch
        {
            // roto: se empieza de cero
        }
        return new Dictionary<string, Entry>();
    }

    private void Remember(string key, Entry entry)
    {
        string json;
        lock (_lock)
        {
            _cache ??= Load();
            _cache[key] = entry;
            json = JsonSerializer.Serialize(_cache, Json);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, true);
        }
        catch
        {
            // sin guardar: se volverá a buscar
        }
    }
}
