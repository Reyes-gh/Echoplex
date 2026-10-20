using System.Diagnostics;
using System.Text.RegularExpressions;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>
/// Fuera de casa con el Tailscale de Echoplex (Ajustes → Móvil): se descarga el paquete, se inicia sesión en el
/// navegador y listo; sin instalar la app de Tailscale. Arranca y se para con el acceso desde el móvil.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly TailscaleNode _ts = new();
    private TailscaleNodeState _tsState = new("Stopped");
    private bool _tsBusy;
    private string? _tsMessage;
    private bool _tsOpenLogin;
    private int _tsPort;

    private void InitTailscale()
    {
        _ts.StateChanged += s => _ui.BeginInvoke(() =>
        {
            _tsState = s;
            // al pulsar «Iniciar sesión», el enlace se abre en el navegador en cuanto llega
            if (_tsOpenLogin && s.State == "NeedsLogin" && s.AuthUrl != null)
            {
                _tsOpenLogin = false;
                OpenUrl(s.AuthUrl);
            }
            if (s.State == "Running") _tsMessage = null;
            TsChanged();
            RefreshRemoteAddresses();
        });
        System.Windows.Application.Current.Exit += (_, _) => _ts.Dispose();
        // Echoplex se actualizó y el paquete es de la versión de antes: se baja el de esta, sin prisa y sin preguntar
        // (ya se había elegido usarlo). Si aún no está publicado o no hay red, se sigue con el que hay.
        if (TailscaleNode.Outdated) _ = RenewTailscaleAsync();
    }

    private async Task RenewTailscaleAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        bool wasRunning = false;
        var error = await TailscaleNode.DownloadAsync(beforeSwap: () => _ui.Invoke(() => { wasRunning = _ts.IsRunning; _ts.Stop(); }));
        if (error != null) return;
        _ui.Invoke(() =>
        {
            if (wasRunning) _tsPort = 0; // que ApplyTailscale lo arranque otra vez, ya con el nuevo
            ApplyTailscale();
        });
    }

    /// <summary>Lo arranca o lo para según los ajustes (lo llama ApplyRemote).</summary>
    private void ApplyTailscale()
    {
        if (Settings.RemoteEnabled && Settings.RemoteTailscale && TailscaleNode.Installed)
        {
            if (!_ts.IsRunning || _tsPort != Settings.RemotePort)
            {
                _tsPort = Settings.RemotePort;
                _ts.Start(_tsPort, TsHostname);
            }
        }
        else _ts.Stop();
        TsChanged();
    }

    /// <summary>El nombre de este PC en Tailscale: echoplex-&lt;nombre del equipo&gt;.</summary>
    private static string TsHostname =>
        "echoplex-" + Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-');

    private void TsChanged()
    {
        foreach (var p in new[] { nameof(RemoteTsInstalled), nameof(RemoteTsText), nameof(RemoteTsCanDownload), nameof(RemoteTsCanLogin), nameof(RemoteTsConnected) })
            OnPropertyChanged(p);
    }

    public bool RemoteTsInstalled => TailscaleNode.Installed && !_tsBusy;
    public bool RemoteTsCanDownload => !TailscaleNode.Installed && !_tsBusy;
    public bool RemoteTsConnected => _tsState.State == "Running" && _ts.IsRunning;
    public bool RemoteTsCanLogin => TailscaleNode.Installed && !_tsBusy && !RemoteTsConnected && _tsState.State != "Starting";

    /// <summary>Qué pasa ahora con el fuera de casa, en una frase.</summary>
    public string RemoteTsText
    {
        get
        {
            if (_tsMessage != null) return _tsMessage;
            if (!TailscaleNode.Installed)
                return "Para escuchar fuera de casa, descarga el paquete fuera de casa (Tailscale dentro de Echoplex, gratis): crea una red privada entre tu PC y tu móvil, sin abrir nada en el router ni instalar nada más. En el móvil, la app Echoplex lo lleva dentro.";
            if (!Settings.RemoteEnabled) return "Activa el acceso desde el móvil para usarlo.";
            return _tsState.State switch
            {
                "Running" => $"Conectado a Tailscale{(_tsState.User != null ? $" como {_tsState.User}" : "")}{(_tsState.DnsName != null ? $" ({_tsState.DnsName})" : "")}. El móvil con la app Echoplex llega desde cualquier sitio.",
                "Starting" => "Arrancando Tailscale…",
                "NeedsLogin" => "Inicia sesión en Tailscale (se abre en el navegador; puedes crear la cuenta ahí mismo, gratis). Usa la misma cuenta en el móvil.",
                "NeedsMachineAuth" => "Falta aprobar este PC en la consola de administración de Tailscale.",
                "Error" => "Tailscale no ha podido arrancar" + (_tsState.Error != null ? ": " + _tsState.Error : "."),
                _ when !Settings.RemoteTailscale => "Paquete listo. Inicia sesión en Tailscale para escuchar fuera de casa.",
                _ => "Tailscale está parado.",
            };
        }
    }

    public async Task DownloadTailscaleAsync()
    {
        _tsBusy = true;
        _tsMessage = "Descargando el paquete fuera de casa…";
        TsChanged();
        var progress = new Progress<double>(p => { _tsMessage = $"Descargando el paquete fuera de casa… {p:P0}"; OnPropertyChanged(nameof(RemoteTsText)); });
        var error = await TailscaleNode.DownloadAsync(progress, beforeSwap: () => _ui.Invoke(_ts.Stop));
        _tsBusy = false;
        _tsMessage = error;
        TsChanged();
        if (error == null) LoginTailscale();
    }

    /// <summary>Enciende el Tailscale de Echoplex y abre el inicio de sesión en el navegador.</summary>
    public void LoginTailscale()
    {
        if (!Settings.RemoteTailscale)
        {
            Settings.RemoteTailscale = true;
            SettingsStore.Save(Settings);
        }
        if (!Settings.RemoteEnabled) RemoteEnabled = true; // sin el servidor no tiene sentido
        _tsMessage = null;
        _tsOpenLogin = true;
        if (!_ts.IsRunning) ApplyTailscale();
        else if (_tsState is { State: "NeedsLogin", AuthUrl: { } url }) { _tsOpenLogin = false; OpenUrl(url); }
        else _ts.Login();
        TsChanged();
    }

    public void LogoutTailscale()
    {
        _ts.Logout();
        TsChanged();
    }

    /// <summary>Apaga y quita el paquete (y la sesión): para volver al principio.</summary>
    public void RemoveTailscale()
    {
        Settings.RemoteTailscale = false;
        SettingsStore.Save(Settings);
        _ts.Stop();
        TailscaleNode.RemovePackage();
        _tsState = new("Stopped");
        _tsMessage = null;
        TsChanged();
        RefreshRemoteAddresses();
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* sin navegador */ }
    }
}
