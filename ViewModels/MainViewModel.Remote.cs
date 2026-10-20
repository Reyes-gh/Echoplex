using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;
using Echoplex.Services;
using Echoplex.Services.Subsonic;

namespace Echoplex.ViewModels;

/// <summary>Acceso desde el móvil (Ajustes → Móvil): Echoplex como servidor Subsonic. Apagado de serie.</summary>
public sealed partial class MainViewModel
{
    private SubsonicServer? _server;

    /// <summary>Si está escuchando, o por qué no.</summary>
    [ObservableProperty] private string? _remoteStatus;
    /// <summary>La última app que se conectó, desde dónde y cuándo.</summary>
    [ObservableProperty] private string? _remoteLastAccess;

    /// <summary>La dirección para fuera de casa (por Tailscale), o null si Tailscale no está en este PC.</summary>
    [ObservableProperty] private string? _remoteAwayAddress;
    /// <summary>Tailscale instalado pero desconectado: hay que iniciarlo.</summary>
    [ObservableProperty] private bool _remoteTailscaleStopped;

    private void InitRemote()
    {
        _server = new SubsonicServer(new SubsonicHost
        {
            Ui = _ui,
            Library = Library,
            UserData = UserData,
            Covers = Covers,
            Scrobbler = Scrobbler,
            SongCover = (song, size) => SongCoverAsync(song, size),
            FolderCover = (dir, size) => FolderArtAsync(dir, size, allowDownload: true, priority: false),
            PlaylistCover = (pl, size) => PlaylistArtAsync(pl, size, allowDownload: true, priority: false),
            Radio = (seed, count) => BuildRadio(seed, new[] { seed }, count),
            Lyrics = LyricsForRemoteAsync,
            PcQueue = PcQueueSnapshot,
        });
        _server.Accessed += (client, ip) => _ui.BeginInvoke(() =>
            RemoteLastAccess = $"Última conexión: {(client.Length > 0 ? client : "una app")} desde {ip}, a las {DateTime.Now:HH:mm}.");
        _worker.Updated += () => _server.Invalidate();
        Player.Listened += (_, _) => _lastPcPlay = DateTime.Now;
        InitTailscale();
        ApplyRemote();
    }

    public bool RemoteEnabled
    {
        get => Settings.RemoteEnabled;
        set
        {
            if (Settings.RemoteEnabled == value) return;
            Settings.RemoteEnabled = value;
            OnPropertyChanged();
            SettingsStore.Save(Settings);
            ApplyRemote();
        }
    }

    public string RemoteUser
    {
        get => Settings.RemoteUser;
        set
        {
            value = new string((value ?? "").Trim().Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (value.Length == 0 || value == Settings.RemoteUser)
            {
                OnPropertyChanged();
                return;
            }
            Settings.RemoteUser = value;
            OnPropertyChanged();
            SettingsStore.Save(Settings);
            if (_server != null) _server.User = value;
        }
    }

    public int RemotePort
    {
        get => Settings.RemotePort;
        set
        {
            value = Math.Clamp(value, 1024, 65535);
            if (value == Settings.RemotePort) return;
            Settings.RemotePort = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RemoteAddresses));
            SettingsStore.Save(Settings);
            ApplyRemote();
        }
    }

    /// <summary>Cuándo sonó algo por última vez en el PC (para que el móvil siga desde ahí si es lo más reciente).</summary>
    private DateTime _lastPcPlay = DateTime.MinValue;

    /// <summary>La letra para el móvil: la del archivo o .lrc y, si está activado, la de internet; con el desfase de esa canción.</summary>
    private async Task<List<LyricEntry>?> LyricsForRemoteAsync(Song song)
    {
        var lines = await Task.Run(() => LyricsService.Load(song));
        if (lines is not { Count: > 0 } && Settings.OnlineLyrics)
        {
            var found = await OnlineLyricsService.FindAsync(song);
            if (found.Text != null) lines = LyricsService.Parse(found.Text);
        }
        if (lines is not { Count: > 0 }) return null;
        double offset = Settings.LyricOffsets?.GetValueOrDefault(song.Path) ?? 0; // positivo = la letra va antes
        return offset == 0 ? lines : lines.Select(l => l with { Time = l.Time is { } t ? t - TimeSpan.FromSeconds(offset) : null }).ToList();
    }

    /// <summary>Lo que suena en el PC (o lo último que sonó): canción, posición, lo que sigue y cuándo.</summary>
    private (Song current, double position, IReadOnlyList<Song> next, DateTime changed)? PcQueueSnapshot()
    {
        if (Player.Current is not { } current || _lastPcPlay == DateTime.MinValue) return null;
        var next = Player.QueueNext.Concat(Player.ContextNext).Take(100).ToList();
        return (current, Player.Position, next, Player.IsPlaying ? DateTime.Now : _lastPcPlay);
    }

    /// <summary>Calidad fuera de casa (kbps; 0 = original).</summary>
    public int RemoteAwayKbps
    {
        get => Settings.RemoteAwayKbps;
        set
        {
            if (value == Settings.RemoteAwayKbps) return;
            Settings.RemoteAwayKbps = value;
            OnPropertyChanged();
            SettingsStore.Save(Settings);
            if (_server != null) _server.AwayKbps = value;
        }
    }

    /// <summary>La contraseña que hay que poner en la app (se guarda cifrada con la cuenta de Windows).</summary>
    public string RemotePassword
    {
        get
        {
            var plain = Secret.Unprotect(Settings.RemotePassword);
            if (string.IsNullOrEmpty(plain))
            {
                plain = NewPassword();
                Settings.RemotePassword = Secret.Protect(plain);
                SettingsStore.Save(Settings);
            }
            return plain;
        }
    }

    /// <summary>Otra contraseña: los móviles conectados tendrán que poner la nueva.</summary>
    public void NewRemotePassword()
    {
        Settings.RemotePassword = Secret.Protect(NewPassword());
        SettingsStore.Save(Settings);
        if (_server != null) _server.Password = RemotePassword;
        OnPropertyChanged(nameof(RemotePassword));
    }

    private static string NewPassword()
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // sin 0/O ni 1/l/I, que se confunden
        return new string(Enumerable.Range(0, 16).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }

    /// <summary>
    /// Lo que lleva el QR para la app Echoplex del móvil: nombre del PC, dirección de casa, la de fuera (Tailscale) si la hay,
    /// usuario y contraseña. Con escanearlo, la app queda conectada sin escribir nada.
    /// </summary>
    public string RemoteQrPayload()
    {
        static string E(string? s) => Uri.EscapeDataString(s ?? "");
        var home = LocalAddresses().FirstOrDefault() is { } ip ? $"http://{ip}:{Settings.RemotePort}" : "";
        return $"echoplex://conectar?v=1&n={E(Environment.MachineName)}&l={E(home)}&r={E(RemoteAwayAddress)}&u={E(Settings.RemoteUser)}&p={E(RemotePassword)}";
    }

    /// <summary>Las direcciones de este PC en la red de casa, para escribirlas en la app.</summary>
    public string RemoteAddresses
    {
        get
        {
            var ips = LocalAddresses();
            return ips.Count == 0 ? $"http://localhost:{Settings.RemotePort}" : string.Join("\n", ips.Select(ip => $"http://{ip}:{Settings.RemotePort}"));
        }
    }

    private static List<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork)) // la de verdad primero
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.") && !Tailscale.IsTailscaleAddress(a))
                .Distinct()
                .ToList();
        }
        catch
        {
            return new List<IPAddress>();
        }
    }

    /// <summary>Vuelve a mirar las direcciones (la de casa y la de Tailscale), sin parar nada. Al abrir Ajustes y al activar.</summary>
    public void RefreshRemoteAddresses()
    {
        OnPropertyChanged(nameof(RemoteAddresses));
        int port = Settings.RemotePort;
        // el Tailscale de Echoplex manda; si no está conectado, se mira si hay una app de Tailscale instalada
        if (_tsState is { State: "Running", Ip: { } tsIp } && _ts.IsRunning)
        {
            RemoteAwayAddress = $"http://{tsIp}:{port}";
            RemoteTailscaleStopped = false;
            return;
        }
        _ = Task.Run(Tailscale.Detect).ContinueWith(t => _ui.BeginInvoke(() =>
        {
            var info = t.IsCompletedSuccessfully ? t.Result : null;
            string? host = info?.Name ?? info?.Ip?.ToString();
            RemoteAwayAddress = host == null ? null : $"http://{host}:{port}";
            RemoteTailscaleStopped = info != null && !info.Running;
        }));
    }

    /// <summary>Enciende, apaga o reinicia el servidor según los ajustes.</summary>
    private void ApplyRemote()
    {
        if (_server == null) return;
        _server.Stop();
        if (!Settings.RemoteEnabled)
        {
            RemoteStatus = null;
            ApplyTailscale();
            return;
        }
        _server.User = Settings.RemoteUser;
        _server.AwayKbps = Settings.RemoteAwayKbps;
        _server.Password = RemotePassword;
        try
        {
            _server.Start(IPAddress.Any, Settings.RemotePort);
            RemoteStatus = "Activo: el móvil ya se puede conectar.";
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            RemoteStatus = $"El puerto {Settings.RemotePort} ya lo usa otro programa: prueba con otro.";
        }
        catch (Exception ex)
        {
            RemoteStatus = "No se pudo activar: " + ex.Message;
        }
        ApplyTailscale();
        RefreshRemoteAddresses();
    }
}
