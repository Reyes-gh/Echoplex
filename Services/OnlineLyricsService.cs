using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Echoplex.Models;

namespace Echoplex.Services;

/// <summary>Resultado de buscar una letra en internet.</summary>
/// <param name="Text">Letra (LRC con tiempos si la hay sincronizada, si no texto plano), o null.</param>
/// <param name="Error">Mensaje si no se pudo consultar (sin conexión, servidor caído…); no se guarda en caché.</param>
/// <param name="Partial">Alguna búsqueda no respondió: puede haber una letra mejor, así que no se da por definitiva.</param>
public sealed record OnlineLyrics(string? Text, bool Instrumental, string? Error, bool Partial = false)
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

    // «lyrics5»: las guardadas con la búsqueda de antes (que a veces cogía la letra de otra versión o una mal
    // sincronizada) se vuelven a buscar
    public static string CacheDir => Path.Combine(SettingsStore.DataDirectory, "lyrics5");

    private static string CacheKey(Song song) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(song.Path.ToLowerInvariant())))[..20];

    /// <summary>Búsquedas en curso por canción (clave de caché).</summary>
    private static readonly Dictionary<string, Task<OnlineLyrics>> Pending = new();

    /// <summary>
    /// La letra de la canción, de la caché o de LRCLIB. Si ya se está buscando (adelantada mientras sonaba la anterior,
    /// o pedida a la vez por el móvil), se espera a esa misma búsqueda en vez de repetirla.
    /// </summary>
    public static Task<OnlineLyrics> FindAsync(Song song, CancellationToken ct = default)
    {
        var key = CacheKey(song);
        Task<OnlineLyrics> task;
        lock (Pending)
        {
            if (!Pending.TryGetValue(key, out task!))
            {
                task = FindCoreAsync(song, key);
                Pending[key] = task;
                task.ContinueWith(_ => { lock (Pending) Pending.Remove(key); }, TaskScheduler.Default);
            }
        }
        return task.WaitAsync(ct);
    }

    /// <summary>
    /// Caché por canción: <c>.lrc</c> letra sincronizada (definitiva); <c>.txt</c> solo texto y <c>.none</c> nada o
    /// instrumental, que se vuelven a consultar pasada una semana por si alguien sube la versión sincronizada;
    /// <c>.part</c> lo encontrado cuando alguna búsqueda no respondió, que se enseña pero se vuelve a buscar la próxima vez.
    /// Las cachés antiguas guardaban también texto sin tiempos en <c>.lrc</c>: esas se vuelven a buscar.
    /// </summary>
    private static async Task<OnlineLyrics> FindCoreAsync(Song song, string key)
    {
        var ct = CancellationToken.None; // compartida: la cancela nadie (quien espera puede dejar de esperar)
        var syncedFile = Path.Combine(CacheDir, key + ".lrc");
        var plainFile = Path.Combine(CacheDir, key + ".txt");
        var noneFile = Path.Combine(CacheDir, key + ".none");
        var partialFile = Path.Combine(CacheDir, key + ".part");

        string? stalePlain = null; // letra ya guardada pero no definitiva: se usa si no aparece nada mejor
        bool staleIsPartial = false;
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
        if (File.Exists(partialFile))
        {
            // más reciente que un texto caducado
            stalePlain = await File.ReadAllTextAsync(partialFile, ct);
            staleIsPartial = true;
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
        if (result.Text == null && stalePlain != null) result = new(stalePlain, false, null, result.Partial || staleIsPartial);

        try
        {
            Directory.CreateDirectory(CacheDir);
            if (result.Partial)
            {
                if (result.Text != null) await File.WriteAllTextAsync(partialFile, result.Text, ct);
            }
            else
            {
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
                if (File.Exists(partialFile)) File.Delete(partialFile);
            }
        }
        catch
        {
            // sin caché: se volverá a preguntar la próxima vez
        }
        return result;
    }

    private static bool IsFresh(string file) => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < RetryNotFoundAfter;

    /// <summary>
    /// Hasta cuándo no se vuelve a preguntar a LRCLIB por esta canción porque la última vez no tenía la letra (o era
    /// instrumental); null si no está en esa espera.
    /// </summary>
    public static DateTime? NextLookup(Song song)
    {
        try
        {
            var none = Path.Combine(CacheDir, CacheKey(song) + ".none");
            if (!File.Exists(none) || !IsFresh(none)) return null;
            return File.GetLastWriteTime(none) + RetryNotFoundAfter;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>«Reintentar búsqueda»: olvida lo guardado de esta canción (también una letra que no era la suya) para preguntar ya.</summary>
    public static void Forget(Song song)
    {
        var key = CacheKey(song);
        foreach (var ext in new[] { ".none", ".txt", ".lrc", ".part" })
        {
            try
            {
                File.Delete(Path.Combine(CacheDir, key + ext));
            }
            catch
            {
                // se volverá a intentar
            }
        }
    }

    /// <summary>¿Tiene tiempos por línea ([mm:ss.xx])?</summary>
    private static bool IsSynced(string text) => SyncedLine().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*\[\d{1,3}:\d{2}", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex SyncedLine();

    /// <summary>
    /// Peticiones a LRCLIB a la vez como mucho. Medido con 6 canciones (7 peticiones cada una): una detrás de otra, 11,8 s
    /// de media por canción; 3 a la vez, 4,5 s y ningún 429 («demasiadas peticiones»), que sí salía con todas a la vez.
    /// </summary>
    private const int MaxParallel = 3;

    /// <summary>
    /// Reintentos cuando LRCLIB contesta «saturado» (503: su límite de cargas a la vez es para todos sus usuarios) o 429.
    /// Medido con 3 a la vez y un 45 % de respuestas 503: con 1 reintento se perdían 10 de 48 búsquedas; con 3, 1 de 48.
    /// </summary>
    private const int MaxRetries = 3;

    /// <summary>
    /// La mejor letra: se pregunta por la exacta y por canción + álbum + artista, canción + álbum, canción + artista,
    /// canción con las palabras del álbum y solo la canción, y se elige entre todo (<see cref="Best"/>). Si una búsqueda
    /// no responde (LRCLIB a veces está saturado), cuentan las demás, pero lo elegido no se da por definitivo.
    /// </summary>
    private static async Task<OnlineLyrics> LookupAsync(string title, string artist, string album, double? duration, CancellationToken ct)
    {
        album = album.Trim();
        var simple = Simplify(title);
        // cada búsqueda de LRCLIB devuelve solo una parte de las entradas (unas 20), y la buena puede estar en cualquiera:
        // se hacen todas y se elige entre todo lo que salga
        string Q(string t, string ar = "", string al = "") =>
            $"{Api}/search?track_name={Uri.EscapeDataString(t)}" + (ar.Length > 0 ? $"&artist_name={Uri.EscapeDataString(ar)}" : "")
            + (al.Length > 0 ? $"&album_name={Uri.EscapeDataString(al)}" : "");
        var urls = new List<string>();
        // la exacta, por artista, título, álbum y duración (LRCLIB admite ±2 s de diferencia)
        if (duration is { } d && artist.Length > 0)
            urls.Add($"{Api}/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}" +
                     $"&album_name={Uri.EscapeDataString(album)}&duration={Math.Round(d)}");
        if (album.Length > 0 && artist.Length > 0) urls.Add(Q(title, artist, album));
        if (album.Length > 0) urls.Add(Q(title, al: album));
        if (artist.Length > 0) urls.Add(Q(title, artist));
        if (album.Length > 0) urls.Add($"{Api}/search?q={Uri.EscapeDataString($"{simple} {album}")}"); // canción + palabras del álbum
        if (!string.Equals(simple, title, StringComparison.OrdinalIgnoreCase) && artist.Length > 0) urls.Add(Q(simple, artist));
        urls.Add(Q(title)); // solo la canción: hay letras subidas con el artista escrito de otra forma (en japonés…)

        using var gate = new SemaphoreSlim(MaxParallel);
        var results = await Task.WhenAll(urls.Select(async u =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return await FetchRecordsAsync(u, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
        int failed = results.Count(r => r == null);
        // todo falló: es la conexión (que lo diga, en vez de «no la tiene», y que no se guarde como tal)
        if (failed == results.Length) throw new HttpRequestException("LRCLIB no responde");
        // en el orden de las peticiones, no en el que llegan: con empates sale siempre la misma
        var pool = results.Where(r => r != null).SelectMany(r => r!).ToList();
        var best = Best(pool, title, album, duration) ?? OnlineLyrics.NotFound;
        return failed > 0 ? best with { Partial = true } : best;
    }

    /// <summary>
    /// Lo que devuelve una petición a LRCLIB (la exacta, una entrada o ninguna si no la tiene; una búsqueda, la lista), o
    /// null si no respondió. Saturado (503) o 429: se espera lo que pide su Retry-After (1 s en su código) y se vuelve a
    /// intentar, hasta <see cref="MaxRetries"/> veces; si pide esperar más de 2 s, se deja para la próxima vez.
    /// </summary>
    private static async Task<List<JsonElement>?> FetchRecordsAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await Http.Value.GetAsync(url, ct);
                if (resp.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    if (attempt >= MaxRetries || wait > TimeSpan.FromSeconds(2)) return null;
                    await Task.Delay(wait, ct);
                    continue;
                }
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new(); // la exacta: no la tiene
                if (!resp.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                return doc.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList(),
                    JsonValueKind.Object => new() { doc.RootElement.Clone() },
                    _ => new(),
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>Tolerancia de duración de LRCLIB (su /api/get busca entre duración − 2 y + 2: track_repository.rs).</summary>
    private const double DurationTolerance = 2;

    /// <summary>
    /// La mejor entre todas las encontradas. Solo cuentan las del mismo título (sin paréntesis ni coletilla tras « - »:
    /// «Burn My Dread -Last Battle-» es otra canción) y la misma duración, con la tolerancia de LRCLIB (sin duración no se
    /// sabe de qué versión es). Sincronizada siempre que haya alguna, y de ellas la que menos líneas imposibles tiene
    /// (<see cref="Impossible"/>); luego la del mismo álbum y luego la que llega más lejos en la canción (en LRCLIB está la
    /// letra de la versión corta subida con la duración de la larga, que se queda a medias). Si no, el texto sin tiempos.
    /// </summary>
    private static OnlineLyrics? Best(List<JsonElement> pool, string title, string album, double? duration)
    {
        var wanted = Norm(Simplify(title));
        var wantAlbum = Norm(album);
        var seen = new HashSet<long>();
        var candidates = pool
            .Where(r => !r.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || seen.Add(id.GetInt64()))
            .Where(r => r.TryGetProperty("trackName", out var tn) && tn.ValueKind == JsonValueKind.String && Norm(Simplify(tn.GetString() ?? "")) == wanted)
            .Select(r => (rec: r, diff: duration is not { } d ? 0
                : r.TryGetProperty("duration", out var rd) && rd.ValueKind == JsonValueKind.Number ? Math.Abs(rd.GetDouble() - d) : double.MaxValue))
            .Where(x => x.diff <= DurationTolerance)
            .ToList();
        var synced = candidates
            .Where(x => LastTime(x.rec) > 0)
            .OrderBy(x => Impossible(x.rec, duration))
            .ThenBy(x => SameAlbum(wantAlbum, x.rec) ? 0 : 1)
            .ThenByDescending(x => LastTime(x.rec))
            .ThenBy(x => x.diff)
            .Select(x => x.rec.GetProperty("syncedLyrics").GetString())
            .FirstOrDefault();
        if (synced != null) return new(synced, false, null);
        foreach (var x in candidates.OrderBy(x => SameAlbum(wantAlbum, x.rec) ? 0 : 1).ThenBy(x => x.diff))
            if (PlainText(x.rec) is { } text) return new(text, false, null);
        return candidates.Any(x => x.rec.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True) ? new(null, true, null) : null;
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

    /// <summary>¿La entrada es del mismo álbum (sin contar signos: «-Reincarnation: Persona 3-» = «-Reincarnation Persona 3-»)?</summary>
    private static bool SameAlbum(string wantAlbum, JsonElement rec)
    {
        if (wantAlbum.Length < 3) return false;
        var album = rec.TryGetProperty("albumName", out var a) && a.ValueKind == JsonValueKind.String ? Norm(a.GetString() ?? "") : "";
        return album.Length >= 3 && (album == wantAlbum || album.Contains(wantAlbum) || wantAlbum.Contains(album));
    }

    /// <summary>Segundo de la última línea con texto de una letra sincronizada (0 si no lo está).</summary>
    private static double LastTime(JsonElement rec) =>
        HasText(rec, "syncedLyrics") ? Events(rec.GetProperty("syncedLyrics").GetString()!).Where(e => e.Text.Length > 0).Select(e => e.Time).DefaultIfEmpty(0).Max() : 0;

    /// <summary>
    /// Lo más rápido que se canta, en letras por segundo. En las letras de LRCLIB de los raps más rápidos grabados
    /// («Godzilla» y «Majesty» de Eminem, Twista) las líneas rápidas piden 34-44 en todas sus subidas; 74 es la más rápida
    /// de una de las subidas de «Godzilla». Cantando normal no pasa de 15.
    /// </summary>
    private const double MaxLettersPerSecond = 74;

    /// <summary>
    /// Cuántas líneas de una letra sincronizada no se pueden cantar como están: después del final de la canción (con la
    /// tolerancia de la duración) o más deprisa que <see cref="MaxLettersPerSecond"/> hasta la siguiente (o hasta la marca
    /// vacía que la cierra). En LRCLIB hay letras de la versión larga subidas en la corta (se pasan del final), letras con
    /// la segunda mitad metida en 6 s (14 líneas a más de 74 letras/s, hasta 164) y líneas que duran 0 s; en las buenas, 0.
    /// Los coros entre paréntesis se cantan encima de otra línea: no cuentan como siguiente línea ni se miden.
    /// </summary>
    private static int Impossible(JsonElement rec, double? duration)
    {
        var events = Events(rec.GetProperty("syncedLyrics").GetString()!);
        int count = 0;
        for (int i = 0; i < events.Count; i++)
        {
            var (t, text) = events[i];
            if (text.Length == 0) continue;
            if (duration is { } d && t > d + DurationTolerance)
            {
                count++;
                continue;
            }
            if (IsBacking(text)) continue;
            double? next = null;
            for (int k = i + 1; k < events.Count && next == null; k++)
                if (events[k].Text.Length == 0 ? events[k].Time >= t : events[k].Time > t && !IsBacking(events[k].Text))
                    next = events[k].Time;
            if (next is { } n && Lead(text).Count(char.IsLetterOrDigit) > (n - t) * MaxLettersPerSecond) count++;
        }
        return count;
    }

    /// <summary>Las líneas de una letra sincronizada por orden de tiempo (una por marca: «[00:06.62][00:11.05] texto» son dos).</summary>
    private static List<(double Time, string Text)> Events(string lrc)
    {
        var events = new List<(double Time, string Text)>();
        foreach (System.Text.RegularExpressions.Match m in LrcLine().Matches(lrc))
        {
            var text = m.Groups[2].Value.Trim();
            foreach (System.Text.RegularExpressions.Match s in Stamp().Matches(m.Groups[1].Value))
                events.Add((int.Parse(s.Groups[1].Value) * 60 + double.Parse(s.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), text));
        }
        return events.OrderBy(e => e.Time).ToList(); // estable: a igual tiempo, en el orden del archivo
    }

    // [ \t], no \s: \s se saltaría el fin de línea y una marca vacía («[00:48.25]», que cierra la línea anterior) se
    // pegaría a la siguiente
    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*((?:\[\d{1,3}:\d{2}(?:\.\d+)?\][ \t]*)+)(.*)$", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex LrcLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(\d{1,3}):(\d{2}(?:\.\d+)?)\]")]
    private static partial System.Text.RegularExpressions.Regex Stamp();

    /// <summary>La línea sin lo que va entre paréntesis (los coros).</summary>
    private static string Lead(string text) => Parens().Replace(text, "");

    /// <summary>Solo coro: todo entre paréntesis.</summary>
    private static bool IsBacking(string text) => !Lead(text).Any(char.IsLetterOrDigit);

    [System.Text.RegularExpressions.GeneratedRegex(@"\([^)]*\)")]
    private static partial System.Text.RegularExpressions.Regex Parens();

    /// <summary>Título comparable: solo letras y números, en minúsculas.</summary>
    private static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool HasText(JsonElement rec, string field) =>
        rec.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());

    /// <summary>El texto sin tiempos de una entrada: el suyo o, si no lo trae, las líneas de la sincronizada.</summary>
    private static string? PlainText(JsonElement rec)
    {
        if (HasText(rec, "plainLyrics")) return rec.GetProperty("plainLyrics").GetString();
        if (!HasText(rec, "syncedLyrics")) return null;
        return string.Join("\n", LrcLine().Matches(rec.GetProperty("syncedLyrics").GetString()!).Select(m => m.Groups[2].Value.Trim()));
    }
}
