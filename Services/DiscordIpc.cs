using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Echoplex.Services;

/// <summary>
/// Conexión con la app de Discord del PC por su tubería local (\\.\pipe\discord-ipc-0 … 9): el protocolo de
/// Rich Presence sin librerías. Cada mensaje es opcode + longitud (int32 little endian) + JSON en UTF-8.
/// </summary>
internal sealed class DiscordIpc : IDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2, OpPing = 3, OpPong = 4;
    private const int MaxFrame = 1 << 20;

    /// <summary>Nombre de las tuberías de Discord (no readonly: las pruebas lo cambian para no tocar el Discord de verdad).</summary>
    private static string s_pipePrefix = "discord-ipc-";

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _readCts;

    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>Nombre del usuario de Discord (del saludo inicial).</summary>
    public string? UserName { get; private set; }

    /// <summary>Discord rechazó un comando (el mensaje de error que devuelve).</summary>
    public event Action<string>? CommandError;

    /// <summary>Se cortó la conexión sin pedirlo (Discord se cerró o la cerró él).</summary>
    public event Action? Disconnected;

    /// <summary>Conecta y se presenta con el ID de la aplicación. Devuelve null si todo fue bien o el motivo si no.</summary>
    public async Task<string?> ConnectAsync(string clientId, CancellationToken ct)
    {
        Close();
        string? failure = null;
        foreach (var name in PipeNames())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(500, ct);
                _pipe = pipe;
                await WriteAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = clientId }, ct);
                var (op, _, body) = await ReadAsync(pipe, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
                if (op == OpFrame && Str(body, "evt") == "READY")
                {
                    UserName = Str(body, "data", "user", "global_name") ?? Str(body, "data", "user", "username");
                    StartReading(pipe);
                    return null;
                }
                // p. ej. «Invalid Client ID»: con otra tubería sería lo mismo
                Close();
                return Str(body, "message") ?? Str(body, "data", "message") ?? $"respuesta inesperada de Discord ({op})";
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (_pipe == pipe)
                {
                    // conectó pero el saludo falló
                    Close();
                    failure = ex is TimeoutException ? "Discord no respondió" : ex.Message;
                }
                else
                {
                    pipe.Dispose(); // esa tubería no aceptó la conexión: la siguiente
                }
            }
        }
        return failure ?? "Discord no está abierto";
    }

    /// <summary>Manda un comando (SET_ACTIVITY…). La respuesta llega por el hilo de lectura; los errores, por <see cref="CommandError"/>.</summary>
    public Task SendAsync(JsonObject command, CancellationToken ct) => WriteAsync(OpFrame, command, ct);

    /// <summary>Cierra la conexión a propósito (sin avisar por <see cref="Disconnected"/>).</summary>
    public void Close()
    {
        NamedPipeClientStream? pipe;
        lock (_gate)
        {
            _readCts?.Cancel();
            _readCts = null;
            pipe = _pipe;
            _pipe = null;
            UserName = null;
        }
        try
        {
            pipe?.Dispose();
        }
        catch
        {
            // ya cerrada
        }
    }

    public void Dispose() => Close();

    /// <summary>Las tuberías de Discord que existen, por orden (sin conectarse para mirarlo); si no se pueden listar, las diez posibles.</summary>
    private static IEnumerable<string> PipeNames()
    {
        HashSet<string>? existing = null;
        try
        {
            existing = Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()
                .Where(n => n.StartsWith(s_pipePrefix, StringComparison.Ordinal)).ToHashSet();
        }
        catch
        {
            // sin lista: se prueban todas
        }
        for (int i = 0; i < 10; i++)
        {
            var name = s_pipePrefix + i;
            if (existing == null || existing.Contains(name)) yield return name;
        }
    }

    private void StartReading(NamedPipeClientStream pipe)
    {
        var cts = _readCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var (op, raw, body) = await ReadAsync(pipe, cts.Token);
                    switch (op)
                    {
                        case OpPing:
                            await WriteRawAsync(pipe, OpPong, raw, cts.Token);
                            break;
                        case OpClose:
                            throw new EndOfStreamException(Str(body, "message"));
                        case OpFrame when Str(body, "evt") == "ERROR":
                            CommandError?.Invoke(Str(body, "data", "message") ?? "error sin detalle");
                            break;
                    }
                }
            }
            catch
            {
                // tubería rota, cerrada o mensaje ilegible: se da por perdida
            }
            if (cts.IsCancellationRequested) return; // la cerramos nosotros
            if (_pipe == pipe) Close();
            Disconnected?.Invoke();
        });
    }

    private async Task WriteAsync(int op, JsonNode payload, CancellationToken ct)
    {
        var pipe = _pipe ?? throw new IOException("Sin conexión con Discord");
        await WriteRawAsync(pipe, op, JsonSerializer.SerializeToUtf8Bytes(payload), ct);
    }

    private async Task WriteRawAsync(Stream pipe, int op, byte[] json, CancellationToken ct)
    {
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await _writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(frame, ct);
            await pipe.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<(int op, byte[] raw, JsonObject? body)> ReadAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        int op = BinaryPrimitives.ReadInt32LittleEndian(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MaxFrame) throw new InvalidDataException($"Mensaje de Discord de {length} bytes");
        var raw = new byte[length];
        await pipe.ReadExactlyAsync(raw, ct);
        JsonObject? body = null;
        try
        {
            body = JsonNode.Parse(raw) as JsonObject;
        }
        catch (JsonException)
        {
            // sin JSON (o roto): solo cuenta el opcode
        }
        return (op, raw, body);
    }

    /// <summary>Texto en esa ruta del JSON, o null si no está o no es texto.</summary>
    private static string? Str(JsonNode? node, params string[] path)
    {
        foreach (var key in path) node = (node as JsonObject)?[key];
        return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
