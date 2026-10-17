using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>Scrobbling en Last.fm y ListenBrainz (Ajustes → Scrobbling). Apagado hasta conectar una cuenta.</summary>
public sealed partial class MainViewModel
{
    public Scrobbler Scrobbler { get; } = new();

    private Song? _scrobbleSong;
    private DateTimeOffset _scrobbleStart;
    private double _scrobbleListened;
    private bool _scrobbled, _nowPlayingSent;
    private int _lastFmConnect;

    /// <summary>Cómo va la conexión con Last.fm (o por qué falló); null si nada que contar.</summary>
    [ObservableProperty] private string? _lastFmStatus;
    [ObservableProperty] private string? _listenBrainzStatus;

    private void InitScrobbling()
    {
        Scrobbler.LastFmKey = Settings.LastFmKey;
        Scrobbler.LastFmSecret = Secret.Unprotect(Settings.LastFmSecret);
        Scrobbler.LastFmSession = Secret.Unprotect(Settings.LastFmSession);
        Scrobbler.ListenBrainzToken = Secret.Unprotect(Settings.ListenBrainzToken);
        Scrobbler.Changed += () => _ui.BeginInvoke(() => OnPropertyChanged(nameof(ScrobbleStatus)));
        Scrobbler.AuthLost += service => _ui.BeginInvoke(() =>
        {
            if (service == Scrobbler.LastFmName) DisconnectLastFm(forget: false);
            else DisconnectListenBrainz(forget: false);
            (service == Scrobbler.LastFmName ? (Action<string>)(t => LastFmStatus = t) : t => ListenBrainzStatus = t)
                ("Se perdió la conexión (¿se quitó el permiso?). Vuelve a conectar: lo pendiente se enviará entonces.");
        });
        Player.SongOpened += song =>
        {
            _scrobbleSong = song;
            _scrobbleStart = DateTimeOffset.UtcNow;
            _scrobbleListened = 0;
            _scrobbled = _nowPlayingSent = false;
        };
        Player.Listened += OnScrobbleListened;
    }

    private bool ScrobblingOn => Scrobbler.LastFmOn || Scrobbler.ListenBrainzOn;

    /// <summary>Cuenta lo escuchado de verdad (sin pausas ni saltos): con la mitad o 4 minutos, se apunta.</summary>
    private void OnScrobbleListened(Song song, double seconds)
    {
        if (!ScrobblingOn || song != _scrobbleSong || _scrobbled) return;
        if (!_nowPlayingSent)
        {
            _nowPlayingSent = true;
            if (Scrobbler.ListenOf(song, _scrobbleStart) is { } now) Scrobbler.NowPlaying(now);
        }
        _scrobbleListened += seconds;
        double duration = song.Duration?.TotalSeconds ?? Player.Duration;
        if (duration > 0 && duration < 30) return; // las de menos de 30 s no cuentan (regla de Last.fm)
        double needed = duration > 0 ? Math.Min(240, duration / 2) : 240;
        if (_scrobbleListened < needed) return;
        _scrobbled = true;
        if (Scrobbler.ListenOf(song, _scrobbleStart) is { } listen) Scrobbler.Scrobble(listen);
    }

    // ---------- Last.fm ----------

    /// <summary>Usuario de Last.fm conectado, o null.</summary>
    public string? LastFmUser => Scrobbler.LastFmOn ? Settings.LastFmUser ?? "" : null;

    public string LastFmApiKey => Settings.LastFmKey ?? "";

    /// <summary>
    /// Conectar: pide un permiso a Last.fm, abre su página en el navegador para aprobarlo y espera (preguntando cada
    /// 3 segundos, hasta 5 minutos) a que se apruebe.
    /// </summary>
    public async Task ConnectLastFmAsync(string key, string secret)
    {
        key = key.Trim();
        secret = secret.Trim();
        if (key.Length == 0 || secret.Length == 0)
        {
            LastFmStatus = "Pega la API key y el Shared secret de tu cuenta de API de Last.fm.";
            return;
        }
        int ver = ++_lastFmConnect;
        LastFmStatus = "Conectando…";
        var (token, error) = await Scrobbler.LastFmTokenAsync(key, secret);
        if (ver != _lastFmConnect) return;
        if (token == null)
        {
            LastFmStatus = error;
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(Scrobbler.LastFmAuthUrl(key, token)) { UseShellExecute = true });
        }
        catch
        {
            LastFmStatus = "No se pudo abrir el navegador.";
            return;
        }
        LastFmStatus = "Pulsa «Yes, allow access» en la página de Last.fm que se ha abierto en el navegador…";
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(3000);
            if (ver != _lastFmConnect) return;
            var (session, user, pending, err) = await Scrobbler.LastFmSessionAsync(key, secret, token);
            if (ver != _lastFmConnect) return;
            if (session != null)
            {
                Settings.LastFmKey = key;
                Settings.LastFmSecret = Secret.Protect(secret);
                Settings.LastFmSession = Secret.Protect(session);
                Settings.LastFmUser = user;
                SettingsStore.Save(Settings);
                Scrobbler.LastFmKey = key;
                Scrobbler.LastFmSecret = secret;
                Scrobbler.LastFmSession = session;
                LastFmStatus = null;
                OnPropertyChanged(nameof(LastFmUser));
                OnPropertyChanged(nameof(LastFmApiKey));
                OnPropertyChanged(nameof(ScrobbleStatus));
                Scrobbler.Flush();
                return;
            }
            if (!pending)
            {
                LastFmStatus = err;
                return;
            }
        }
        LastFmStatus = "No llegó el permiso. Vuelve a pulsar Conectar.";
    }

    /// <summary>Desconecta Last.fm (la API key se queda, para volver a conectar con un clic).</summary>
    public void DisconnectLastFm(bool forget = true)
    {
        _lastFmConnect++;
        Settings.LastFmSession = null;
        Settings.LastFmUser = null;
        SettingsStore.Save(Settings);
        Scrobbler.LastFmSession = null;
        if (forget) Scrobbler.Forget(Scrobbler.LastFmName);
        LastFmStatus = null;
        OnPropertyChanged(nameof(LastFmUser));
        OnPropertyChanged(nameof(ScrobbleStatus));
    }

    // ---------- ListenBrainz ----------

    public string? ListenBrainzUser => Scrobbler.ListenBrainzOn ? Settings.ListenBrainzUser ?? "" : null;

    public async Task ConnectListenBrainzAsync(string token)
    {
        token = token.Trim();
        if (token.Length == 0)
        {
            ListenBrainzStatus = "Pega tu token de usuario de ListenBrainz.";
            return;
        }
        ListenBrainzStatus = "Comprobando el token…";
        var (user, error) = await Scrobbler.ListenBrainzValidateAsync(token);
        if (user == null)
        {
            ListenBrainzStatus = error;
            return;
        }
        Settings.ListenBrainzToken = Secret.Protect(token);
        Settings.ListenBrainzUser = user;
        SettingsStore.Save(Settings);
        Scrobbler.ListenBrainzToken = token;
        ListenBrainzStatus = null;
        OnPropertyChanged(nameof(ListenBrainzUser));
        OnPropertyChanged(nameof(ScrobbleStatus));
        Scrobbler.Flush();
    }

    public void DisconnectListenBrainz(bool forget = true)
    {
        Settings.ListenBrainzToken = null;
        Settings.ListenBrainzUser = null;
        SettingsStore.Save(Settings);
        Scrobbler.ListenBrainzToken = null;
        if (forget) Scrobbler.Forget(Scrobbler.ListenBrainzName);
        ListenBrainzStatus = null;
        OnPropertyChanged(nameof(ListenBrainzUser));
        OnPropertyChanged(nameof(ScrobbleStatus));
    }

    /// <summary>Escuchas pendientes de enviar y el último problema, o null si está todo enviado.</summary>
    public string? ScrobbleStatus
    {
        get
        {
            if (!ScrobblingOn) return null;
            int pending = Scrobbler.PendingCount;
            if (pending == 0) return null;
            var text = pending == 1 ? "1 escucha pendiente de enviar" : $"{pending:N0} escuchas pendientes de enviar";
            return Scrobbler.LastError is { } e ? $"{text} ({e}). Se reintentará sola." : text + "…";
        }
    }

    public void FlushScrobbles() => Scrobbler.Flush();
}
