using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Echoplex.Services;

/// <summary>Cómo está el Tailscale de Echoplex.</summary>
public sealed record TailscaleNodeState(
    string State,           // Starting, NeedsLogin, NeedsMachineAuth, Running, Stopped, Error
    string? AuthUrl = null, // el enlace para iniciar sesión (con NeedsLogin)
    string? Ip = null,      // la IP de Tailscale de este PC (con Running)
    string? DnsName = null,
    string? User = null,
    string? Error = null);

/// <summary>
/// El «paquete fuera de casa»: Tailscale dentro de Echoplex, sin instalar la app de Tailscale. Es un programa aparte
/// (echoplex-tailscale.exe, hecho con tsnet de Tailscale) que no viene en el zip de Echoplex: se descarga desde
/// Ajustes → Móvil solo si se quiere, y Echoplex lo arranca y lo para como proceso hijo (si Echoplex se cierra o
/// muere, él también). Escucha en la red de Tailscale y pasa las peticiones del móvil al servidor de Echoplex.
/// </summary>
public sealed class TailscaleNode : IDisposable
{
    public const string ExeName = "echoplex-tailscale.exe";

    /// <summary>Donde queda el paquete descargado.</summary>
    public static string PackageDir => Path.Combine(SettingsStore.DataDirectory, "paquetes", "fuera-de-casa");

    /// <summary>Donde guarda la sesión de Tailscale (borrarla = empezar de cero).</summary>
    public static string StateDir => Path.Combine(SettingsStore.DataDirectory, "tailscale");

    /// <summary>El ejecutable: el descargado o, para pruebas, uno junto a Echoplex.exe (carpeta fuera-de-casa).</summary>
    public static string? ExePath
    {
        get
        {
            foreach (var dir in new[] { PackageDir, Path.Combine(AppContext.BaseDirectory, "fuera-de-casa") })
            {
                var p = Path.Combine(dir, ExeName);
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    public static bool Installed => ExePath != null;

    private const string VersionFile = "version.txt";

    /// <summary>
    /// El paquete descargado es de otra versión de Echoplex (se actualizó Echoplex): hay que bajar el suyo. El de pruebas
    /// junto a Echoplex.exe no cuenta.
    /// </summary>
    public static bool Outdated
    {
        get
        {
            if (!File.Exists(Path.Combine(PackageDir, ExeName))) return false;
            try { return File.ReadAllText(Path.Combine(PackageDir, VersionFile)).Trim() != UpdateService.CurrentVersion.ToString(); }
            catch { return true; }
        }
    }

    private Process? _process;
    private readonly object _lock = new();

    public TailscaleNodeState State { get; private set; } = new("Stopped");
    public event Action<TailscaleNodeState>? StateChanged;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Arranca el nodo (si ya estaba, lo reinicia): escucha en Tailscale en ese puerto y pasa a Echoplex.</summary>
    public void Start(int port, string hostname)
    {
        Stop();
        var exe = ExePath;
        if (exe == null)
        {
            Set(new("Error", Error: "Falta el paquete fuera de casa"));
            return;
        }
        Directory.CreateDirectory(StateDir);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-mode", "serve", "-dir", StateDir, "-hostname", hostname, "-port", port.ToString(), "-target", $"http://127.0.0.1:{port}" })
            psi.ArgumentList.Add(a);
        try
        {
            var p = Process.Start(psi) ?? throw new InvalidOperationException("no arrancó");
            lock (_lock) _process = p;
            Set(new("Starting"));
            p.OutputDataReceived += (_, e) => { if (e.Data != null) OnLine(e.Data); };
            p.ErrorDataReceived += (_, _) => { }; // el registro de tsnet no hace falta (solo con -v)
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                lock (_lock) { if (_process != p) return; _process = null; }
                if (State.State != "Stopped") Set(new("Error", Error: "El Tailscale de Echoplex se ha cerrado"));
            };
        }
        catch (Exception ex)
        {
            Set(new("Error", Error: ex.Message));
        }
    }

    private void OnLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            switch (S("type"))
            {
                case "state":
                    Set(new(S("state") ?? "Starting", S("authUrl"), S("ip"), S("dnsName"), S("user")));
                    break;
                case "error":
                    Set(State with { Error = S("message") });
                    break;
            }
        }
        catch (JsonException)
        {
            // una línea que no es nuestra
        }
    }

    private void Set(TailscaleNodeState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    private void Send(string command)
    {
        try { _process?.StandardInput.WriteLine(command); } catch { /* ya cerrado */ }
    }

    /// <summary>Pide otra vez el enlace de inicio de sesión.</summary>
    public void Login() => Send("login");

    /// <summary>Cierra la sesión de Tailscale de este PC (luego habrá que iniciarla otra vez).</summary>
    public void Logout() => Send("logout");

    public void Stop()
    {
        Process? p;
        lock (_lock) { p = _process; _process = null; }
        if (p == null) return;
        Set(new("Stopped"));
        try
        {
            p.StandardInput.WriteLine("quit");
            p.StandardInput.Close();
            if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
        }
        catch
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ya no está */ }
        }
        p.Dispose();
    }

    public void Dispose() => Stop();

    /// <summary>Olvida la sesión (para empezar de cero): hay que pararlo antes.</summary>
    public static void ForgetSession()
    {
        try { if (Directory.Exists(StateDir)) Directory.Delete(StateDir, recursive: true); } catch { /* en uso */ }
    }

    // ---------------------------------------------------------------- descarga del paquete

    /// <summary>
    /// Descarga el paquete de la release de esta versión de Echoplex (Echoplex-&lt;versión&gt;-fuera-de-casa-windows.zip),
    /// comprueba su huella SHA-256 con la que publica GitHub y lo deja en <see cref="PackageDir"/>.
    /// </summary>
    public static async Task<string?> DownloadAsync(IProgress<double>? progress = null, Action? beforeSwap = null, CancellationToken ct = default)
    {
        var v = UpdateService.CurrentVersion;
        var name = $"Echoplex-{v}-fuera-de-casa-windows.zip";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Echoplex/{v}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var resp = await http.GetAsync($"https://api.github.com/repos/{UpdateService.Repository}/releases/tags/v{v}", ct);
            if (!resp.IsSuccessStatusCode) return $"La versión {v} aún no está publicada en GitHub con el paquete.";
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            JsonElement? asset = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase)) { asset = a; break; }
            if (asset is not { } found) return $"La versión {v} no incluye {name}.";
            var url = found.GetProperty("browser_download_url").GetString()!;
            var digest = found.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
            long total = found.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

            var work = Path.Combine(SettingsStore.DataDirectory, "paquetes");
            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, name);
            await using (var src = await http.GetStreamAsync(url, ct))
            await using (var dst = File.Create(zip))
            {
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                await using var fs = File.OpenRead(zip);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hash.Equals(d[7..], StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(zip);
                    return "La descarga no coincide con la huella publicada en GitHub; no se ha instalado.";
                }
            }
            // se descomprime aparte y solo entonces se cambia por el de antes (que puede estar en marcha: se para justo antes)
            var fresh = PackageDir + ".nuevo";
            if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
            ZipFile.ExtractToDirectory(zip, fresh);
            File.Delete(zip);
            if (!File.Exists(Path.Combine(fresh, ExeName)))
            {
                Directory.Delete(fresh, recursive: true);
                return $"El paquete no contiene {ExeName}.";
            }
            File.WriteAllText(Path.Combine(fresh, VersionFile), v.ToString());
            beforeSwap?.Invoke();
            if (Directory.Exists(PackageDir)) Directory.Delete(PackageDir, recursive: true);
            Directory.Move(fresh, PackageDir);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return "No se pudo descargar: " + ex.Message;
        }
    }

    /// <summary>Quita el paquete descargado (y la sesión). Hay que pararlo antes.</summary>
    public static void RemovePackage()
    {
        try { if (Directory.Exists(PackageDir)) Directory.Delete(PackageDir, recursive: true); } catch { /* en uso */ }
        ForgetSession();
    }
}
