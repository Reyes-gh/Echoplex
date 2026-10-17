using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Echoplex.Services;

/// <summary>
/// Un archivo en internet leído por trozos, como un servicio de streaming: lo leído se guarda en memoria y nunca en el disco.
/// Para reproducir se usa una sola descarga continua (Drive tarda 1–2 s en empezar a servir cada petición, así que
/// pedir trozo a trozo se come ese tiempo una y otra vez): suena en cuanto llega el primer trozo y la misma conexión
/// sigue trayendo el resto. Al saltar lejos, la descarga sigue desde allí. Para leer etiquetas se piden solo los trozos
/// que hagan falta.
/// </summary>
public sealed class RemoteFile : IDisposable
{
    private static readonly HttpClient Http = CreateClient();
    private const int TailBlocks = 2; // leer el final (etiquetas al final del archivo) no mueve la descarga continua
    private static readonly HashSet<string> TailFormats = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".m4a", ".mp4", ".aac", ".alac", ".wma", ".ogg", ".opus" };

    private readonly string _url;
    // a dónde se piden los trozos: la dirección final tras las redirecciones (algunos servidores mandan a otro en cada
    // petición; así solo se sigue la redirección una vez)
    private string _fetchUrl;
    private readonly int _block;
    private readonly bool _playback;
    private readonly Dictionary<long, byte[]> _blocks = new();
    // trozos sueltos que se están pidiendo ahora (quien los necesite espera a esa petición en vez de repetirla)
    private readonly Dictionary<long, Task> _inflight = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    // la descarga continua: desde qué trozo empezó, cuál es el siguiente que traerá y cómo pararla
    private CancellationTokenSource? _stream;
    private long _streamStart = -1;
    private long _streamNext = -1;

    // Ventana de descarga de la que aún no suena (la siguiente, preparada): solo se baja hasta tantos trozos por delante
    // de lo leído, no la canción entera. 0 = sin límite (la que suena).
    private int _aheadBlocks;
    private long _maxRead = -1;

    private RemoteFile(string url, string fetchUrl, long length, int blockSize, bool playback)
    {
        _url = url;
        _fetchUrl = fetchUrl;
        Length = length;
        _block = blockSize;
        _playback = playback;
    }

    public string Url => _url;
    public long Length { get; }
    /// <summary>Nombre para el registro (el del archivo en la carpeta en línea).</summary>
    public string Name { get; private set; } = "";

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new OnlineHttpHandler(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 12, // el primer listado, las carátulas y lo que suena, a la vez sin esperarse
        }))
        {
            // una descarga continua de una canción larga puede durar minutos: el límite lo pone cada lectura
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Echoplex/{UpdateService.CurrentVersion} (+https://github.com/{UpdateService.Repository})");
        return http;
    }

    /// <summary>Para diagnosticar: cada petición (trozo pedido y lo que tardó).</summary>
    public static Action<string>? Trace;

    /// <summary>Lo usa también la lectura de carpetas en línea (mismo cliente, misma identificación).</summary>
    public static HttpClient Client => Http;

    /// <summary>
    /// Abre el archivo: pide desde el principio y, en cuanto llegan <paramref name="initialBlocks"/> trozos, vuelve.
    /// Con <paramref name="stream"/>, esa misma petición sigue trayendo el resto en segundo plano.
    /// </summary>
    /// <param name="blockSize">Tamaño de cada trozo: grande para reproducir, pequeño para leer etiquetas.</param>
    public static RemoteFile Open(string url, int blockSize = 256 * 1024, CancellationToken ct = default, string name = "",
        int initialBlocks = 1, bool stream = false, bool playback = false, int aheadBlocks = 0)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int first = blockSize * Math.Max(1, initialBlocks);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = stream ? new RangeHeaderValue(0, null) : new RangeHeaderValue(0, first - 1);
        var resp = Send(req, ct);
        try
        {
            var body = resp.Content.ReadAsStream(ct);
            var start = ReadAll(body, first);
            long length;
            if (resp.StatusCode == HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange?.Length is { } total) length = total;
            else if (resp.Content.Headers.ContentLength is { } len && resp.StatusCode == HttpStatusCode.OK) length = len;
            else length = start.Length;

            var file = new RemoteFile(url, resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url, length, blockSize, playback) { Name = name, _aheadBlocks = aheadBlocks };
            file.Store(0, start);
            Trace?.Invoke($"{name}: abrir · primeros {start.Length / 1024} KB de {length / 1024} KB en {clock.ElapsedMilliseconds} ms"
                          + (resp.StatusCode == HttpStatusCode.PartialContent ? "" : " (el servidor no da trozos)") + (stream ? " · sigue bajando" : ""));
            if (stream && start.Length < length)
            {
                file.StartStream(resp, body, start.Length, clock);
                resp = null; // ahora es de la descarga continua
            }
            return file;
        }
        finally
        {
            resp?.Dispose();
        }
    }

    private static HttpResponseMessage Send(HttpRequestMessage req, CancellationToken ct)
    {
        var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (resp.StatusCode == HttpStatusCode.NotFound) throw new IOException("el archivo ya no está en la carpeta en línea");
            if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new IOException("no hay permiso para leer el archivo (¿la carpeta sigue compartida?)");
            if ((int)resp.StatusCode == 429) throw new IOException("el servidor pide esperar un poco (demasiadas descargas seguidas)");
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentType?.MediaType == "text/html")
                throw new IOException("el servidor devolvió una página en vez del archivo (¿cuota de descargas de Drive agotada?)");
            return resp;
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    private static byte[] ReadAll(Stream s, int max)
    {
        var buf = new byte[max];
        int got = 0;
        while (got < max)
        {
            int n = s.Read(buf, got, max - got);
            if (n <= 0) break;
            got += n;
        }
        if (got < max) Array.Resize(ref buf, got);
        return buf;
    }

    private void Store(long offset, byte[] data)
    {
        lock (_lock)
        {
            for (long pos = 0; pos < data.Length; pos += _block)
            {
                long index = (offset + pos) / _block;
                int len = (int)Math.Min(_block, data.Length - pos);
                if ((offset + pos) % _block != 0) continue; // solo trozos alineados
                if (len < _block && offset + pos + len < Length) continue; // trozo a medias (no el último): se volverá a pedir
                if (!_blocks.ContainsKey(index)) _blocks[index] = data.AsSpan((int)pos, len).ToArray();
            }
            Monitor.PulseAll(_lock); // quien esperaba un trozo mira si ya llegó
        }
    }

    private bool Has(long index)
    {
        lock (_lock) return _blocks.ContainsKey(index);
    }

    private long BlockCount => (Length + _block - 1) / _block;

    // ======================================================================
    // Descarga continua
    // ======================================================================

    /// <summary>Sigue leyendo la respuesta ya abierta (la del primer trozo) hasta el final, guardando trozo a trozo.</summary>
    private void StartStream(HttpResponseMessage resp, Stream body, long position, System.Diagnostics.Stopwatch clock)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_lock)
        {
            _stream?.Cancel();
            _stream = cts;
            _streamStart = position / _block;
            _streamNext = position / _block;
        }
        Task.Run(() => RunStream(resp, body, position, cts, clock));
    }

    /// <summary>Empieza (o mueve) la descarga continua para que cubra el trozo <paramref name="index"/> y lo que sigue.</summary>
    public void EnsureStreamFrom(long index)
    {
        long from;
        lock (_lock)
        {
            if (_stream is { IsCancellationRequested: false } && index >= _streamStart && index <= _streamNext + 16) return; // ya viene
            from = index;
            while (from < BlockCount && _blocks.ContainsKey(from)) from++;
            if (from >= BlockCount) return; // ya está todo
            _stream?.Cancel();
            _stream = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _streamStart = from;
            _streamNext = from;
        }
        var cts = _stream!;
        long position = from * _block;
        Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, _fetchUrl);
                req.Headers.Range = new RangeHeaderValue(position, null);
                var resp = Send(req, cts.Token);
                if (resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    // sin trozos: viene entero desde el principio
                    RunStream(resp, resp.Content.ReadAsStream(cts.Token), 0, cts, clock);
                    return;
                }
                Trace?.Invoke($"{Name}: sigue bajando desde {position / 1024} KB (respondió en {clock.ElapsedMilliseconds} ms)");
                RunStream(resp, resp.Content.ReadAsStream(cts.Token), position, cts, clock);
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) _fetchUrl = _url; // la dirección final pudo caducar: se vuelve a la original
                lock (_lock) Monitor.PulseAll(_lock);
                if (!cts.IsCancellationRequested) Trace?.Invoke($"{Name}: no se pudo seguir bajando ({ex.Message})");
            }
        });
    }

    private void RunStream(HttpResponseMessage resp, Stream body, long position, CancellationTokenSource cts, System.Diagnostics.Stopwatch clock)
    {
        long startedAt = position;
        using (resp)
        using (body)
        using (cts.Token.Register(() => { try { resp.Dispose(); } catch { } })) // parar = cortar la conexión
        {
            try
            {
                while (position < Length && !cts.IsCancellationRequested)
                {
                    // la que aún no suena: espera (sin cortar la conexión) a que la lectura avance
                    lock (_lock)
                    {
                        bool paused = false;
                        while (_aheadBlocks > 0 && position / _block > Math.Max(0, _maxRead) + _aheadBlocks && !cts.IsCancellationRequested)
                        {
                            if (!paused) Trace?.Invoke($"{Name}: preparada · pausa la descarga en {position / 1024} KB hasta que suene");
                            paused = true;
                            Monitor.Wait(_lock, 500);
                        }
                    }
                    if (cts.IsCancellationRequested) break;
                    int want = (int)Math.Min(_block - position % _block, Length - position);
                    var chunk = ReadAll(body, want);
                    if (chunk.Length == 0) break;
                    Store(position, chunk);
                    position += chunk.Length;
                    lock (_lock)
                        if (_stream == cts) _streamNext = position / _block;
                }
                if (!cts.IsCancellationRequested)
                    Trace?.Invoke($"{Name}: descarga continua terminada · {(position - startedAt) / 1024} KB en {clock.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) Trace?.Invoke($"{Name}: se cortó la descarga continua en {position / 1024} KB ({ex.Message})");
            }
            finally
            {
                lock (_lock)
                {
                    if (_stream == cts) _stream = null;
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    /// <summary>
    /// Otra canción pasó a sonar: se para la descarga continua y de esta solo se guarda el principio (el primer mega),
    /// para volver a ella al instante sin acumular canciones enteras en memoria.
    /// </summary>
    public void Release(int keepBlocks = 4)
    {
        lock (_lock)
        {
            _aheadBlocks = Math.Max(_aheadBlocks, RemoteFiles.PreparedAhead);
            _maxRead = -1;
            bool streaming = _stream != null;
            _stream?.Cancel();
            _stream = null;
            int before = _blocks.Count;
            foreach (var index in _blocks.Keys.Where(i => i >= keepBlocks && i < BlockCount - TailBlocks).ToList()) _blocks.Remove(index);
            if (streaming || before != _blocks.Count)
                Trace?.Invoke($"{Name}: ya no suena · descarga parada, se quedan {_blocks.Count * (long)_block / 1024} KB en memoria");
        }
    }

    // ======================================================================
    // Lectura
    // ======================================================================

    /// <summary>Lee de cualquier posición: espera a la descarga continua o, si no viene por ahí, pide el trozo.</summary>
    public int ReadAt(long position, byte[] buffer, int offset, int count)
    {
        if (position >= Length || count <= 0) return 0;
        count = (int)Math.Min(count, Length - position);
        int done = 0;
        while (done < count)
        {
            long pos = position + done;
            long index = pos / _block;
            var block = WaitFor(index);
            int inBlock = (int)(pos - index * _block);
            int n = Math.Min(count - done, block.Length - inBlock);
            if (n <= 0) break;
            Buffer.BlockCopy(block, inBlock, buffer, offset + done, n);
            done += n;
        }
        return done;
    }

    private byte[] WaitFor(long index)
    {
        lock (_lock)
            if (_blocks.TryGetValue(index, out var ready)) return ready;

        // para reproducir: lo trae la descarga continua (se empieza o se mueve aquí si hace falta); el final del archivo,
        // que a veces se mira para las etiquetas, se pide suelto para no mover la descarga
        bool tail = index >= BlockCount - TailBlocks;
        if (_playback && !tail)
        {
            lock (_lock)
            {
                if (index > _maxRead)
                {
                    _maxRead = index; // la ventana avanza con la lectura
                    Monitor.PulseAll(_lock);
                }
            }
            EnsureStreamFrom(index);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            lock (_lock)
            {
                while (!_blocks.ContainsKey(index))
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    if (_stream == null || waited.Elapsed > TimeSpan.FromSeconds(45)) break; // la descarga se cortó
                    Monitor.Wait(_lock, 250);
                }
                if (_blocks.TryGetValue(index, out var arrived))
                {
                    if (waited.ElapsedMilliseconds > 40) Trace?.Invoke($"{Name}: la lectura del byte {index * _block} esperó {waited.ElapsedMilliseconds} ms a la descarga");
                    return arrived;
                }
            }
        }
        Fetch(index, ahead: 4, why: tail ? "final del archivo" : "lectura");
        lock (_lock)
            if (_blocks.TryGetValue(index, out var fetched)) return fetched;
        throw new IOException("no se pudo leer el archivo de la carpeta en línea");
    }

    /// <summary>
    /// Pide el trozo y los siguientes que falten (hasta <paramref name="ahead"/>) en una sola petición. Si alguno ya se
    /// está pidiendo, espera a esa en vez de repetirla.
    /// </summary>
    private void Fetch(long index, int ahead, string why = "")
    {
        Task? running;
        TaskCompletionSource? mine = null;
        long last = index;
        lock (_lock)
        {
            if (_blocks.ContainsKey(index)) return;
            if (!_inflight.TryGetValue(index, out running))
            {
                while (last + 1 < BlockCount && last - index < ahead && !_blocks.ContainsKey(last + 1) && !_inflight.ContainsKey(last + 1)) last++;
                mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                for (long b = index; b <= last; b++) _inflight[b] = mine.Task;
            }
        }
        if (mine == null)
        {
            // espera a esa petición, o a que el trozo llegue antes por otro lado (la descarga continua)
            lock (_lock)
            {
                while (!_blocks.ContainsKey(index) && !running!.IsCompleted)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    Monitor.Wait(_lock, 100);
                }
                if (_blocks.ContainsKey(index)) return;
            }
            Fetch(index, ahead, why); // la otra petición falló: se intenta aquí
            return;
        }

        try
        {
            long from = index * _block, to = Math.Min(Length, (last + 1) * _block) - 1;
            Exception? error = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    using var req = new HttpRequestMessage(HttpMethod.Get, _fetchUrl);
                    req.Headers.Range = new RangeHeaderValue(from, to);
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, _cts.Token);
                    using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                    resp.EnsureSuccessStatusCode();
                    using var body = resp.Content.ReadAsStream(linked.Token);
                    if (resp.StatusCode == HttpStatusCode.PartialContent) Store(from, ReadAll(body, (int)(to - from + 1)));
                    else Store(0, ReadAll(body, (int)Math.Min(Length, int.MaxValue))); // sin trozos: viene entero
                    Trace?.Invoke($"{Name}: {why} {from}-{to} ({(to - from + 1) / 1024} KB) en {started.ElapsedMilliseconds} ms");
                    return;
                }
                catch (Exception ex) when (!_cts.IsCancellationRequested)
                {
                    error = ex;
                    _fetchUrl = _url; // la dirección final pudo caducar: se vuelve a la original
                    Thread.Sleep(400 * (attempt + 1));
                }
            }
            if (error != null) throw new IOException("se cortó la conexión con la carpeta en línea", error);
        }
        finally
        {
            lock (_lock)
                for (long b = index; b <= last; b++) _inflight.Remove(b);
            mine.SetResult();
        }
    }

    /// <summary>
    /// Pide ya el final del archivo, en paralelo: algunos formatos (MP3, M4A) lo miran antes de empezar a sonar, y así
    /// esa espera se solapa con la del principio en vez de sumarse.
    /// </summary>
    public void PrefetchTail()
    {
        // FLAC y WAV no lo miran: ahí sería una petición de más
        if (BlockCount <= TailBlocks + 1 || !TailFormats.Contains(System.IO.Path.GetExtension(Name))) return;
        Task.Run(() =>
        {
            try { Fetch(BlockCount - TailBlocks, ahead: TailBlocks, why: "final del archivo, por adelantado"); }
            catch { /* ya se pedirá si hace falta */ }
        });
    }

    /// <summary>Pasa a sonar: se baja ya entera, sin ventana.</summary>
    public void Unlimit()
    {
        lock (_lock)
        {
            if (_aheadBlocks == 0) return;
            _aheadBlocks = 0;
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_lock)
        {
            _blocks.Clear();
            Monitor.PulseAll(_lock);
        }
    }
}

/// <summary>Flujo de solo lectura sobre un <see cref="RemoteFile"/> (lo que necesita el decodificador).</summary>
public sealed class RemoteStream : Stream
{
    private readonly RemoteFile _file;
    private long _position;

    public RemoteStream(RemoteFile file) => _file = file;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _file.Length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _file.ReadAt(_position, buffer, offset, count);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => offset,
        };
        _position = Math.Clamp(target, 0, Length);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Para leer etiquetas con TagLib directamente de la nube (solo pide los trozos que mira).</summary>
public sealed class RemoteTagFile : TagLib.File.IFileAbstraction
{
    private readonly RemoteFile _file;

    public RemoteTagFile(string name, RemoteFile file)
    {
        Name = name;
        _file = file;
    }

    public string Name { get; }
    public Stream ReadStream => new RemoteStream(_file);
    public Stream WriteStream => throw new NotSupportedException();
    public void CloseStream(Stream stream) => stream.Dispose();
}

/// <summary>
/// Los archivos en línea abiertos para reproducir: el que suena, el siguiente (preparado de antemano o sin pausas entre
/// canciones) y algunos más. Al saltar hacia atrás (que reabre el archivo) no se vuelve a bajar nada; solo el que suena
/// sigue descargándose. Los demás se sueltan de memoria.
/// </summary>
public static class RemoteFiles
{
    private const int Keep = 4;
    private const int PlaybackBlock = 64 * 1024;

    /// <summary>Lo que se baja por delante de la que aún no suena: 32 trozos de 64 KB = 2 MB (para empezar y fundirse).</summary>
    public const int PreparedAhead = 32;
    private static readonly LinkedList<RemoteFile> Open = new();
    private static readonly object Lock = new();

    private static RemoteFile? Cached(string url)
    {
        lock (Lock)
        {
            var node = Open.First;
            while (node != null && node.Value.Url != url) node = node.Next;
            if (node == null) return null;
            Open.Remove(node);
            Open.AddFirst(node);
            return node.Value;
        }
    }

    private static void Remember(RemoteFile file)
    {
        lock (Lock)
        {
            Open.AddFirst(file);
            while (Open.Count > Keep)
            {
                var old = Open.Last!.Value;
                Open.RemoveLast();
                old.Dispose();
            }
        }
    }

    /// <summary>Para reproducir: el ya abierto (o preparado) o uno nuevo, con la descarga continua en marcha.</summary>
    public static RemoteFile Get(string url, string name = "")
    {
        // con ventana hasta que se diga que es la que suena (Focus): la siguiente preparada no se baja entera antes de tiempo
        if (Cached(url) is { } file)
        {
            file.EnsureStreamFrom(0);
            return file;
        }
        // trozos de 64 KB: suena en cuanto llegan los primeros 64 KB (la cabecera); el resto sigue por la misma conexión
        file = RemoteFile.Open(url, blockSize: PlaybackBlock, name: name, stream: true, playback: true, aheadBlocks: PreparedAhead);
        file.PrefetchTail();
        Remember(file);
        return file;
    }

    /// <summary>Esta es la que suena: las demás dejan de descargarse y se quedan solo con su principio.</summary>
    public static void Focus(string url)
    {
        List<RemoteFile> files;
        lock (Lock) files = Open.ToList();
        foreach (var f in files)
            if (f.Url != url) f.Release(keepBlocks: 4);
            else f.Unlimit();
    }
}
