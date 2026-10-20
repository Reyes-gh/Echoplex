using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Echoplex.Services;

/// <summary>
/// Tailscale instalado en este PC (una VPN entre tus dispositivos): si está, el móvil llega a Echoplex desde fuera de casa
/// por su dirección de Tailscale. Solo se mira (su adaptador de red y «tailscale status»); no se cambia nada.
/// </summary>
public static class Tailscale
{
    /// <param name="Name">El nombre del PC en Tailscale (MagicDNS), p. ej. «mi-pc.tail1234.ts.net»; null si no lo usa.</param>
    /// <param name="Ip">Su dirección de Tailscale (100.x.y.z).</param>
    /// <param name="Running">Conectado ahora mismo.</param>
    public sealed record Info(string? Name, IPAddress? Ip, bool Running);

    /// <summary>Las direcciones de Tailscale están en 100.64.0.0/10 (rango reservado para esto, CG-NAT).</summary>
    public static bool IsTailscaleAddress(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    /// <summary>Lo que se sabe de Tailscale en este PC, o null si no está. Puede tardar (pregunta a su programa): fuera del hilo de la interfaz.</summary>
    public static Info? Detect()
    {
        IPAddress? ip = null;
        bool adapterUp = false;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                var addr = n.GetIPProperties().UnicastAddresses.Select(u => u.Address).FirstOrDefault(IsTailscaleAddress);
                bool isTailscale = n.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) || addr != null;
                if (!isTailscale) continue;
                ip ??= addr;
                adapterUp |= n.OperationalStatus == OperationalStatus.Up && addr != null;
            }
        }
        catch
        {
            // sin red
        }

        var fromCli = QueryCli();
        if (fromCli != null) return fromCli with { Ip = fromCli.Ip ?? ip, Running = fromCli.Running || adapterUp };
        return ip == null ? null : new Info(null, ip, adapterUp);
    }

    private static Info? QueryCli()
    {
        var exe = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
        }.FirstOrDefault(File.Exists);
        if (exe == null) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "status --json")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            return Parse(output.Result);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lee la salida de «tailscale status --json» (lo justo: el nombre, la dirección y si está conectado).</summary>
    public static Info? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool running = root.TryGetProperty("BackendState", out var state) && state.GetString() == "Running";
            if (!root.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object) return new Info(null, null, running);
            string? name = self.TryGetProperty("DNSName", out var dns) ? dns.GetString()?.TrimEnd('.') : null;
            IPAddress? ip = null;
            if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
                foreach (var e in ips.EnumerateArray())
                    if (IPAddress.TryParse(e.GetString(), out var a) && a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; }
            return new Info(string.IsNullOrWhiteSpace(name) ? null : name, ip, running);
        }
        catch
        {
            return null;
        }
    }
}
