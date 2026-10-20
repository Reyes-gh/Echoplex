using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Echoplex.Services;

/// <summary>
/// Un servidor HTTP/1.1 mínimo sobre TCP, para que el móvil hable con Echoplex. Sin dependencias ni permisos de
/// administrador (el de Windows, http.sys, los pide para escuchar en la red). Lo justo: GET, HEAD y POST de
/// formularios, conexiones que se reutilizan y respuestas que se escriben por trozos (canciones, con saltos por rangos).
/// </summary>
public sealed class HttpLite : IDisposable
{
    public sealed class Request
    {
        public string Method { get; init; } = "GET";
        public string Path { get; init; } = "/";
        public Dictionary<string, List<string>> Query { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IPAddress Remote { get; set; } = IPAddress.None;

        public string? this[string key] => Query.TryGetValue(key, out var l) && l.Count > 0 ? l[0] : null;
        public IReadOnlyList<string> All(string key) => Query.TryGetValue(key, out var l) ? l : Array.Empty<string>();
    }

    public sealed class Response
    {
        public int Status { get; set; } = 200;
        public string ContentType { get; set; } = "text/plain; charset=utf-8";
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public byte[]? Body { get; set; }
        /// <summary>Para lo largo (una canción): escribe el cuerpo; <see cref="Length"/> dice cuánto ocupará (null = no se sabe).</summary>
        public Func<Stream, CancellationToken, Task>? Writer { get; set; }
        public long? Length { get; set; }
        /// <summary>Se llama al terminar (o cortarse) la respuesta: para cerrar el archivo.</summary>
        public Action? Done { get; set; }

        public static Response Text(int status, string text) => new() { Status = status, Body = Encoding.UTF8.GetBytes(text) };
    }

    private const int MaxHeader = 32 * 1024, MaxBody = 1024 * 1024, MaxConnections = 48;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly Func<Request, Task<Response>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private int _connections;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public HttpLite(IPAddress address, int port, Func<Request, Task<Response>> handler)
    {
        _listener = new TcpListener(address, port);
        _handler = handler;
    }

    /// <summary>Empieza a escuchar (lanza si el puerto está ocupado).</summary>
    public void Start()
    {
        _listener.Start(64);
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                if (_cts.IsCancellationRequested) return;
                continue;
            }
            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await Serve(client); }
                catch { /* conexión cortada */ }
                finally
                {
                    Interlocked.Decrement(ref _connections);
                    client.Dispose();
                }
            });
        }
    }

    private async Task Serve(TcpClient client)
    {
        client.NoDelay = true;
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        using var stream = client.GetStream();
        var reader = new ByteReader(stream);
        while (!_cts.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            idle.CancelAfter(IdleTimeout);
            var head = await reader.ReadHeadAsync(MaxHeader, idle.Token);
            if (head == null) return; // el otro lado cerró
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 3) return;
            var target = first[1];
            int q = target.IndexOf('?');
            var request = new Request
            {
                Method = first[0].ToUpperInvariant(),
                Path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target),
                Remote = remote,
            };
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) request.Headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            // lo que llega por el Tailscale de Echoplex (el paquete fuera de casa) viene de este mismo PC: quién llama
            // de verdad lo dice X-Forwarded-For. Solo se cree si la conexión es local.
            if (IPAddress.IsLoopback(remote) && request.Headers.TryGetValue("X-Forwarded-For", out var xff)
                && IPAddress.TryParse(xff.Split(',')[^1].Trim(), out var forwarded))
                request.Remote = forwarded.IsIPv4MappedToIPv6 ? forwarded.MapToIPv4() : forwarded;
            if (q >= 0) ParseForm(target[(q + 1)..], request.Query);
            if (request.Headers.TryGetValue("Content-Length", out var lenText) && long.TryParse(lenText, out var bodyLen) && bodyLen > 0)
            {
                if (bodyLen > MaxBody) return;
                var body = await reader.ReadExactAsync((int)bodyLen, idle.Token);
                if (body == null) return;
                if (request.Headers.TryGetValue("Content-Type", out var ct) && ct.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                    ParseForm(Encoding.UTF8.GetString(body), request.Query);
            }

            Response response;
            try
            {
                response = await _handler(request);
            }
            catch (Exception ex)
            {
                response = Response.Text(500, "Error: " + ex.Message);
            }

            bool http10 = first[2].EndsWith("1.0", StringComparison.Ordinal);
            bool wantsClose = request.Headers.TryGetValue("Connection", out var conn) && conn.Equals("close", StringComparison.OrdinalIgnoreCase);
            long? length = response.Body?.LongLength ?? response.Length;
            bool keepAlive = !http10 && !wantsClose && length != null;
            try
            {
                await WriteAsync(stream, request, response, length, keepAlive);
            }
            finally
            {
                response.Done?.Invoke();
            }
            if (!keepAlive) return;
        }
    }

    private async Task WriteAsync(NetworkStream stream, Request request, Response response, long? length, bool keepAlive)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n");
        sb.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        if (length != null) sb.Append("Content-Length: ").Append(length.Value).Append("\r\n");
        foreach (var (k, v) in response.Headers) sb.Append(k).Append(": ").Append(v).Append("\r\n");
        sb.Append("Server: Echoplex\r\n");
        sb.Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headBytes, _cts.Token);
        if (request.Method == "HEAD") return;
        if (response.Body != null) await stream.WriteAsync(response.Body, _cts.Token);
        else if (response.Writer != null) await response.Writer(stream, _cts.Token);
        await stream.FlushAsync(_cts.Token);
    }

    /// <summary>a=1&amp;b=dos+palabras&amp;b=otra → la consulta (los nombres repetidos se acumulan).</summary>
    public static void ParseForm(string text, Dictionary<string, List<string>> into)
    {
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = Decode(eq >= 0 ? pair[..eq] : pair), value = eq >= 0 ? Decode(pair[(eq + 1)..]) : "";
            if (!into.TryGetValue(key, out var list)) into[key] = list = new List<string>();
            list.Add(value);
        }
    }

    private static string Decode(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch { return s; }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 206 => "Partial Content", 304 => "Not Modified", 400 => "Bad Request", 401 => "Unauthorized",
        403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 416 => "Range Not Satisfiable",
        429 => "Too Many Requests", 500 => "Internal Server Error", 503 => "Service Unavailable", _ => "OK",
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    /// <summary>Lee la cabecera (hasta la línea en blanco) y luego el cuerpo, sin perder lo que ya llegó de más.</summary>
    private sealed class ByteReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buffer = new byte[8192];
        private int _start, _end;

        public ByteReader(Stream stream) => _stream = stream;

        public async Task<string?> ReadHeadAsync(int max, CancellationToken ct)
        {
            var head = new List<byte>(512);
            while (true)
            {
                while (_start < _end)
                {
                    head.Add(_buffer[_start++]);
                    int n = head.Count;
                    if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
                        return Encoding.Latin1.GetString(head.ToArray(), 0, n - 4);
                    if (n > max) return null;
                }
                if (!await FillAsync(ct)) return null;
            }
        }

        public async Task<byte[]?> ReadExactAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            int done = 0;
            while (done < count)
            {
                if (_start == _end && !await FillAsync(ct)) return null;
                int n = Math.Min(count - done, _end - _start);
                Buffer.BlockCopy(_buffer, _start, result, done, n);
                _start += n;
                done += n;
            }
            return result;
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            _start = 0;
            try
            {
                _end = await _stream.ReadAsync(_buffer, ct);
            }
            catch
            {
                _end = 0;
            }
            return _end > 0;
        }
    }
}
