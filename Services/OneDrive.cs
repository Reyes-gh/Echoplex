using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Echoplex.Services;

/// <summary>
/// Carpetas compartidas de OneDrive personal («Cualquier persona con el enlace»), sin iniciar sesión: se usa el mismo
/// permiso de invitado que la web de OneDrive al abrir un enlace compartido. Ese permiso dura una semana y hay que
/// «canjear» con él cada enlace compartido una vez; luego cada canción se descarga de una dirección fija con ese permiso
/// en la cabecera (sin enlaces que caduquen a la hora).
/// </summary>
public static class OneDrive
{
    public const string Api = "https://my.microsoftpersonalcontent.com/_api/v2.0";
    private const string ContentHost = "my.microsoftpersonalcontent.com";
    private const string TokenUrl = "https://api-badgerp.svc.ms/v1.0/token";
    private const string AppId = "5cbed6ac-a083-4e14-b191-b4ba07653de2"; // la web de OneDrive

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Links = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Redeemed = new(StringComparer.Ordinal);
    private static string? _token;
    private static DateTime _expires;
    private static DateTime _obtained;
    private static bool _loaded;

    private static string StateFile => Path.Combine(OnlineLibrary.BaseDir, "onedrive.json");

    public static bool IsLink(Uri uri) =>
        uri.Host.Equals("1drv.ms", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals("onedrive.live.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.Equals(ContentHost, StringComparison.OrdinalIgnoreCase);

    public static bool IsLink(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) && IsLink(u);

    /// <summary>Las descargas de OneDrive (se les pone el permiso en la cabecera).</summary>
    public static bool NeedsAuthorization(Uri uri) => uri.Host.Equals(ContentHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Identificador del enlace compartido para la API («u!» + el enlace en base64url).</summary>
    public static string ShareId(string link) =>
        "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(link.Trim())).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Un enlace compartido configurado: se canjea con el permiso de invitado antes de descargar nada.</summary>
    public static void Register(string link)
    {
        lock (Links) Links.Add(link.Trim());
    }

    public static void Unregister(string link)
    {
        lock (Links) Links.Remove(link.Trim());
    }

    /// <summary>
    /// El permiso de invitado, con todos los enlaces compartidos configurados ya canjeados. Con
    /// <paramref name="renew"/> se pide uno nuevo (OneDrive rechazó el que había), salvo que otro se acabe de pedir.
    /// </summary>
    public static async Task<string> TokenAsync(bool renew, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_loaded) LoadState();
            bool fresh = DateTime.UtcNow - _obtained < TimeSpan.FromSeconds(15);
            if (_token == null || DateTime.UtcNow > _expires || (renew && !fresh))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { appId = AppId }), Encoding.UTF8, "application/json"),
                };
                using var resp = await RemoteFile.Client.SendAsync(req, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                _token = doc.RootElement.GetProperty("token").GetString();
                _expires = doc.RootElement.TryGetProperty("expiryTimeUtc", out var e) && e.TryGetDateTime(out var exp)
                    ? exp.ToUniversalTime() - TimeSpan.FromHours(1)
                    : DateTime.UtcNow + TimeSpan.FromDays(1);
                _obtained = DateTime.UtcNow;
                Redeemed.Clear();
                RemoteFile.Trace?.Invoke($"OneDrive: permiso de invitado nuevo en {clock.ElapsedMilliseconds} ms");
            }
            List<string> pending;
            lock (Links) pending = Links.Where(l => !Redeemed.Contains(l)).ToList();
            if (pending.Count > 0)
            {
                await Task.WhenAll(pending.Select(async link =>
                {
                    if (await RedeemAsync(link, _token!, ct).ConfigureAwait(false))
                        lock (Redeemed) Redeemed.Add(link);
                })).ConfigureAwait(false);
                SaveState();
            }
            return _token!;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> RedeemAsync(string link, string token, CancellationToken ct)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{Api}/shares/{ShareId(link)}/driveitem?$select=id");
            req.Headers.Authorization = new AuthenticationHeaderValue("Badger", token);
            req.Headers.Add("Prefer", "autoredeem");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await RemoteFile.Client.SendAsync(req, timeout.Token).ConfigureAwait(false);
            RemoteFile.Trace?.Invoke($"OneDrive: enlace compartido canjeado ({(int)resp.StatusCode}) en {clock.ElapsedMilliseconds} ms");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class State
    {
        public string? Token { get; set; }
        public DateTime Expires { get; set; }
        public List<string> Redeemed { get; set; } = new();
    }

    // el permiso se guarda (dura una semana): así la primera canción tras abrir Echoplex no espera a pedirlo y canjearlo
    private static void LoadState()
    {
        _loaded = true;
        try
        {
            if (!File.Exists(StateFile) || JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) is not { Token: { } t } s) return;
            if (DateTime.UtcNow > s.Expires) return;
            _token = t;
            _expires = s.Expires;
            foreach (var l in s.Redeemed) Redeemed.Add(l);
        }
        catch
        {
            // se pedirá uno nuevo
        }
    }

    private static void SaveState()
    {
        try
        {
            Directory.CreateDirectory(OnlineLibrary.BaseDir);
            List<string> redeemed;
            lock (Redeemed) redeemed = Redeemed.ToList();
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new State { Token = _token, Expires = _expires, Redeemed = redeemed }));
        }
        catch
        {
            // sin guardar: se pedirá otra vez al abrir
        }
    }
}

/// <summary>
/// Lo que pasa por todas las peticiones de las carpetas en línea: pone el permiso de invitado de OneDrive a las que lo
/// necesitan (y, si OneDrive lo rechaza porque caducó o el enlace no estaba canjeado, pide otro y repite una vez) y,
/// si el servidor pide calma (429/503, pasa al leer miles de canciones seguidas), espera lo que diga y repite.
/// </summary>
internal sealed class OnlineHttpHandler : DelegatingHandler
{
    private const int Retries = 4;

    public OnlineHttpHandler(HttpMessageHandler inner) : base(inner) { }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) =>
        SendAsync(request, ct).GetAwaiter().GetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var resp = await SendAuthorizedAsync(request, ct).ConfigureAwait(false);
        for (int attempt = 1; attempt <= Retries && request.Content == null && (int)resp.StatusCode is 429 or 503; attempt++)
        {
            var wait = resp.Headers.RetryAfter?.Delta
                       ?? (resp.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1 << attempt));
            wait = TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 0.5, 20));
            resp.Dispose();
            RemoteFile.Trace?.Invoke($"{request.RequestUri?.Host} pide esperar {wait.TotalSeconds:0.#} s (intento {attempt} de {Retries})");
            await Task.Delay(wait, ct).ConfigureAwait(false);
            resp = await SendAuthorizedAsync(Copy(request), ct).ConfigureAwait(false);
        }
        return resp;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri is not { } uri || !OneDrive.NeedsAuthorization(uri) || request.Headers.Authorization != null)
            return await base.SendAsync(request, ct).ConfigureAwait(false);

        request.Headers.Authorization = new AuthenticationHeaderValue("Badger", await OneDrive.TokenAsync(false, ct).ConfigureAwait(false));
        var resp = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (resp.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) || request.Content != null)
            return resp;

        resp.Dispose();
        var retry = Copy(request);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Badger", await OneDrive.TokenAsync(true, ct).ConfigureAwait(false));
        return await base.SendAsync(retry, ct).ConfigureAwait(false);
    }

    // una petición ya enviada no se puede volver a enviar: otra igual (sin el permiso, que se pone de nuevo)
    private static HttpRequestMessage Copy(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri) { Version = request.Version };
        foreach (var h in request.Headers.Where(h => h.Key != "Authorization"))
            copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return copy;
    }
}
