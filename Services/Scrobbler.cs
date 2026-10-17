using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Echoplex.Models;

namespace Echoplex.Services;

/// <summary>
/// Scrobbling: apunta en Last.fm y/o ListenBrainz cada canción escuchada (la mitad o 4 minutos, lo que llegue antes, como
/// piden los dos). Las escuchas se guardan primero en scrobbles.json y se envían desde ahí, así no se pierde ninguna sin
/// conexión o con el servicio caído: se reintenta cada vez más espaciado (de 1 a 30 minutos). Apagado de serie: solo
/// envía algo con una cuenta conectada.
/// </summary>
public sealed class Scrobbler : IDisposable
{
    /// <summary>Una escucha: lo que se envía (sin rutas ni nada del PC).</summary>
    public sealed record Listen(string Artist, string Track, string Album, string AlbumArtist, int Duration, uint TrackNumber, long PlayedAt);

    private sealed class Pending
    {
        public List<Listen> LastFm { get; set; } = new();
        public List<Listen> ListenBrainz { get; set; } = new();
    }

    private enum Sent { Ok, Rejected, Retry, AuthLost }

    public const string LastFmName = "lastfm", ListenBrainzName = "listenbrainz";
    private const string LastFmApi = "https://ws.audioscrobbler.com/2.0/";
    private const string ListenBrainzApi = "https://api.listenbrainz.org/1/";
    private const int MaxQueued = 10000;

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object _lock = new();
    private readonly Pending _pending;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private DateTime _retryAt = DateTime.MinValue;
    private int _failures;

    public string? LastFmKey { get; set; }
    public string? LastFmSecret { get; set; }
    public string? LastFmSession { get; set; }
    public string? ListenBrainzToken { get; set; }

    public bool LastFmOn => !string.IsNullOrEmpty(LastFmKey) && !string.IsNullOrEmpty(LastFmSecret) && !string.IsNullOrEmpty(LastFmSession);
    public bool ListenBrainzOn => !string.IsNullOrEmpty(ListenBrainzToken);

    /// <summary>Cambió lo pendiente o el último error (para Ajustes). Llega desde otro hilo.</summary>
    public event Action? Changed;
    /// <summary>El servicio ya no acepta la sesión o el token (se revocó el permiso): hay que volver a conectar.</summary>
    public event Action<string>? AuthLost;

    /// <summary>Por qué no se pudo enviar lo último, o null si todo fue bien.</summary>
    public string? LastError { get; private set; }

    private static string FilePath => Path.Combine(SettingsStore.DataDirectory, "scrobbles.json");

    public Scrobbler()
    {
        _pending = Load();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Escuchas pendientes de enviar (de la cuenta que más tenga).</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock) return Math.Max(LastFmOn ? _pending.LastFm.Count : 0, ListenBrainzOn ? _pending.ListenBrainz.Count : 0);
        }
    }

    /// <summary>Lo que se envía de una canción, o null si no se puede (sin artista o sin título en las etiquetas).</summary>
    public static Listen? ListenOf(Song song, DateTimeOffset startedAt)
    {
        string artist = song.Artist.Trim();
        string track = song.MetaTitle.Trim();
        if (artist.Length == 0 || artist == "Artista desconocido" || track.Length == 0) return null;
        string albumArtist = song.AlbumArtist.Trim();
        return new Listen(artist, track, song.Album.Trim(), albumArtist == artist ? "" : albumArtist,
            (int)Math.Round(song.Duration?.TotalSeconds ?? 0), song.TrackNumber, startedAt.ToUnixTimeSeconds());
    }

    /// <summary>Una canción se escuchó lo bastante: a la cola de cada cuenta conectada, y a enviar.</summary>
    public void Scrobble(Listen listen)
    {
        lock (_lock)
        {
            if (LastFmOn) Add(_pending.LastFm, listen);
            if (ListenBrainzOn) Add(_pending.ListenBrainz, listen);
        }
        Save();
        Changed?.Invoke();
        _wake.Release();
    }

    private static void Add(List<Listen> queue, Listen listen)
    {
        if (queue.Count > 0 && queue[^1] == listen) return;
        queue.Add(listen);
        if (queue.Count > MaxQueued) queue.RemoveRange(0, queue.Count - MaxQueued);
    }

    /// <summary>«Escuchando ahora» en el perfil (no cuenta como escucha; si falla, da igual).</summary>
    public void NowPlaying(Listen listen)
    {
        if (LastFmOn)
        {
            var p = LastFmParams("track.updateNowPlaying");
            p["artist"] = listen.Artist;
            p["track"] = listen.Track;
            if (listen.Album.Length > 0) p["album"] = listen.Album;
            if (listen.AlbumArtist.Length > 0) p["albumArtist"] = listen.AlbumArtist;
            if (listen.Duration > 0) p["duration"] = listen.Duration.ToString();
            _ = Task.Run(async () => { try { await LastFmCallAsync(p, LastFmSecret!, post: true); } catch { } });
        }
        if (ListenBrainzOn)
        {
            var body = new JsonObject { ["listen_type"] = "playing_now", ["payload"] = new JsonArray(ListenBrainzListen(listen, withTime: false)) };
            _ = Task.Run(async () => { try { await ListenBrainzPostAsync(body); } catch { } });
        }
    }

    /// <summary>Al desconectar una cuenta, lo suyo pendiente se descarta.</summary>
    public void Forget(string service)
    {
        lock (_lock)
        {
            (service == LastFmName ? _pending.LastFm : _pending.ListenBrainz).Clear();
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Reintentar ya (al conectar una cuenta, o con «Enviar ahora»).</summary>
    public void Flush()
    {
        _retryAt = DateTime.MinValue;
        _wake.Release();
    }

    private async Task LoopAsync()
    {
        _wake.Release(); // lo que quedó pendiente de la otra vez
        while (!_cts.IsCancellationRequested)
        {
            var wait = _retryAt > DateTime.UtcNow ? _retryAt - DateTime.UtcNow : TimeSpan.FromMinutes(10);
            try
            {
                await _wake.WaitAsync(wait, _cts.Token);
            }
            catch
            {
                return;
            }
            while (_wake.CurrentCount > 0) _wake.Wait(0);
            if (DateTime.UtcNow < _retryAt) continue;
            bool ok;
            try
            {
                ok = await FlushAsync();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ok = false;
            }
            if (ok)
            {
                _failures = 0;
                _retryAt = DateTime.MinValue;
            }
            else
            {
                _failures++;
                _retryAt = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Min(30, Math.Pow(2, _failures - 1)));
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Envía lo pendiente de cada cuenta, en tandas. false si algo hay que reintentarlo más tarde.</summary>
    private async Task<bool> FlushAsync()
    {
        bool ok = true;
        if (LastFmOn) ok &= await FlushQueueAsync(_pending.LastFm, 50, SendLastFmAsync, LastFmName);
        if (ListenBrainzOn) ok &= await FlushQueueAsync(_pending.ListenBrainz, 100, SendListenBrainzAsync, ListenBrainzName);
        if (ok) LastError = null;
        return ok;
    }

    private async Task<bool> FlushQueueAsync(List<Listen> queue, int batchSize, Func<List<Listen>, Task<(Sent result, string? error)>> send, string service)
    {
        while (true)
        {
            List<Listen> batch;
            lock (_lock)
            {
                if (queue.Count == 0) return true;
                batch = queue.Take(batchSize).ToList();
            }
            (Sent result, string? error) r;
            try
            {
                r = await send(batch);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                r = (Sent.Retry, "sin conexión");
            }
            switch (r.result)
            {
                case Sent.Ok:
                case Sent.Rejected: // datos que el servicio no quiere (no se van a arreglar reintentando)
                    lock (_lock) queue.RemoveRange(0, Math.Min(batch.Count, queue.Count));
                    Save();
                    Changed?.Invoke();
                    break;
                case Sent.AuthLost:
                    LastError = r.error;
                    AuthLost?.Invoke(service);
                    return true;
                default:
                    LastError = r.error;
                    return false;
            }
        }
    }

    // ---------- Last.fm ----------

    private SortedDictionary<string, string> LastFmParams(string method) =>
        new(StringComparer.Ordinal) { ["method"] = method, ["api_key"] = LastFmKey ?? "", ["sk"] = LastFmSession ?? "" };

    private async Task<(Sent, string?)> SendLastFmAsync(List<Listen> batch)
    {
        var p = LastFmParams("track.scrobble");
        for (int i = 0; i < batch.Count; i++)
        {
            var l = batch[i];
            p[$"artist[{i}]"] = l.Artist;
            p[$"track[{i}]"] = l.Track;
            p[$"timestamp[{i}]"] = l.PlayedAt.ToString();
            if (l.Album.Length > 0) p[$"album[{i}]"] = l.Album;
            if (l.AlbumArtist.Length > 0) p[$"albumArtist[{i}]"] = l.AlbumArtist;
            if (l.Duration > 0) p[$"duration[{i}]"] = l.Duration.ToString();
            if (l.TrackNumber > 0) p[$"trackNumber[{i}]"] = l.TrackNumber.ToString();
        }
        var (status, json) = await LastFmCallAsync(p, LastFmSecret!, post: true);
        int code = json?["error"]?.GetValue<int>() ?? 0;
        string? message = json?["message"]?.GetValue<string>();
        if (code == 0 && status == HttpStatusCode.OK) return (Sent.Ok, null);
        return code switch
        {
            9 or 4 or 10 or 14 or 26 => (Sent.AuthLost, "Last.fm ya no acepta la conexión: vuelve a conectar."),
            11 or 16 or 29 or 8 => (Sent.Retry, "Last.fm no responde ahora mismo"),
            > 0 => (Sent.Rejected, message),
            _ => ((int)status >= 500 ? Sent.Retry : Sent.Rejected, $"Last.fm: error {(int)status}"),
        };
    }

    /// <summary>Firma de Last.fm: los parámetros ordenados (nombre y valor seguidos, sin format), el secreto y MD5.</summary>
    public static string LastFmSign(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in parameters.Where(kv => kv.Key is not ("format" or "callback")).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(k).Append(v);
        sb.Append(secret);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static async Task<(HttpStatusCode status, JsonNode? json)> LastFmCallAsync(IDictionary<string, string> p, string secret, bool post, CancellationToken ct = default)
    {
        var all = new Dictionary<string, string>(p);
        if (all.TryGetValue("sk", out var sk) && string.IsNullOrEmpty(sk)) all.Remove("sk");
        all["api_sig"] = LastFmSign(all, secret);
        all["format"] = "json";
        using var response = post
            ? await Http.PostAsync(LastFmApi, new FormUrlEncodedContent(all), ct)
            : await Http.GetAsync(LastFmApi + "?" + string.Join("&", all.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch { }
        return (response.StatusCode, json);
    }

    /// <summary>Paso 1 de conectar Last.fm: un permiso pendiente que el usuario aprueba en el navegador.</summary>
    public static async Task<(string? token, string? error)> LastFmTokenAsync(string key, string secret)
    {
        try
        {
            var (_, json) = await LastFmCallAsync(new Dictionary<string, string> { ["method"] = "auth.getToken", ["api_key"] = key }, secret, post: false);
            if (json?["token"]?.GetValue<string>() is { Length: > 0 } token) return (token, null);
            return (null, LastFmError(json));
        }
        catch
        {
            return (null, "No se pudo conectar con Last.fm.");
        }
    }

    public static string LastFmAuthUrl(string key, string token) =>
        $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(key)}&token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// Paso 2: la sesión, cuando ya se dio permiso en el navegador. Mientras no, devuelve pending = true (error 14 de
    /// Last.fm), y se vuelve a preguntar.
    /// </summary>
    public static async Task<(string? session, string? user, bool pending, string? error)> LastFmSessionAsync(string key, string secret, string token)
    {
        try
        {
            var (_, json) = await LastFmCallAsync(new Dictionary<string, string> { ["method"] = "auth.getSession", ["api_key"] = key, ["token"] = token }, secret, post: false);
            if (json?["session"] is JsonObject s && s["key"]?.GetValue<string>() is { Length: > 0 } sk)
                return (sk, s["name"]?.GetValue<string>(), false, null);
            int code = json?["error"]?.GetValue<int>() ?? 0;
            return (null, null, code == 14, LastFmError(json));
        }
        catch
        {
            return (null, null, true, null); // sin red un momento: se vuelve a probar
        }
    }

    private static string LastFmError(JsonNode? json) => (json?["error"]?.GetValue<int>() ?? 0) switch
    {
        10 or 26 => "Last.fm no reconoce esa API key.",
        13 => "El «Shared secret» no corresponde a esa API key.",
        15 => "El permiso caducó: vuelve a pulsar Conectar.",
        0 => "Last.fm no responde ahora mismo.",
        _ => json?["message"]?.GetValue<string>() ?? "Last.fm no aceptó la conexión.",
    };

    // ---------- ListenBrainz ----------

    private static JsonObject ListenBrainzListen(Listen l, bool withTime)
    {
        var info = new JsonObject
        {
            ["media_player"] = "Echoplex",
            ["submission_client"] = "Echoplex",
            ["submission_client_version"] = UpdateService.CurrentVersion.ToString(),
        };
        if (l.Duration > 0) info["duration_ms"] = l.Duration * 1000;
        if (l.TrackNumber > 0) info["tracknumber"] = (int)l.TrackNumber;
        if (l.AlbumArtist.Length > 0) info["release_artist_name"] = l.AlbumArtist;
        var meta = new JsonObject { ["artist_name"] = l.Artist, ["track_name"] = l.Track };
        if (l.Album.Length > 0) meta["release_name"] = l.Album;
        meta["additional_info"] = info;
        var listen = new JsonObject();
        if (withTime) listen["listened_at"] = l.PlayedAt;
        listen["track_metadata"] = meta;
        return listen;
    }

    private async Task<(Sent, string?)> SendListenBrainzAsync(List<Listen> batch)
    {
        var body = new JsonObject
        {
            ["listen_type"] = batch.Count == 1 ? "single" : "import",
            ["payload"] = new JsonArray(batch.Select(l => (JsonNode)ListenBrainzListen(l, withTime: true)).ToArray()),
        };
        var status = await ListenBrainzPostAsync(body);
        return (int)status switch
        {
            200 => (Sent.Ok, null),
            401 => (Sent.AuthLost, "ListenBrainz ya no acepta el token: vuelve a conectar."),
            429 or >= 500 => (Sent.Retry, "ListenBrainz no responde ahora mismo"),
            _ => (Sent.Rejected, $"ListenBrainz: error {(int)status}"),
        };
    }

    private async Task<HttpStatusCode> ListenBrainzPostAsync(JsonObject body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ListenBrainzApi + "submit-listens")
        {
            Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Token " + ListenBrainzToken);
        using var response = await Http.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>Comprueba un token de ListenBrainz: el nombre de usuario si vale.</summary>
    public static async Task<(string? user, string? error)> ListenBrainzValidateAsync(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ListenBrainzApi + "validate-token");
            request.Headers.TryAddWithoutValidation("Authorization", "Token " + token);
            using var response = await Http.SendAsync(request);
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            if (json?["valid"]?.GetValue<bool>() == true) return (json["user_name"]?.GetValue<string>() ?? "", null);
            return (null, "ListenBrainz no reconoce ese token.");
        }
        catch
        {
            return (null, "No se pudo conectar con ListenBrainz.");
        }
    }

    // ---------- guardar ----------

    private Pending Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Pending>(File.ReadAllText(FilePath)) ?? new Pending();
        }
        catch
        {
            // ilegible: se empieza de cero
        }
        return new Pending();
    }

    private void Save()
    {
        try
        {
            string text;
            lock (_lock) text = JsonSerializer.Serialize(_pending, Json);
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // sin guardar: se quedan en memoria y se envían igual
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Echoplex/{UpdateService.CurrentVersion}");
        return client;
    }

    public void Dispose()
    {
        _cts.Cancel();
    }
}
