using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echoplex.Services;

/// <summary>
/// Ajustes del usuario. Viven solo en %LocalAppData%\Echoplex\settings.json, nunca junto al programa,
/// así la carpeta de la aplicación no lleva datos personales (rutas, historial…).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Carpetas de música. Vacía la primera vez: se propone la carpeta Música de Windows.</summary>
    public List<string> MusicRoots { get; set; } = new();

    /// <summary>Ajuste antiguo (una sola carpeta): se migra a <see cref="MusicRoots"/> al cargar y ya no se escribe.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MusicRoot { get; set; }

    public double Volume { get; set; } = 0.7;
    public bool Shuffle { get; set; }
    public int Repeat { get; set; }
    public bool AutoRadio { get; set; } = true;
    public bool ExclusiveMode { get; set; }
    public string? ExclusiveDeviceId { get; set; }
    /// <summary>Ecualizador de 15 bandas encendido (solo actúa en la salida normal, no en bit a bit).</summary>
    public bool EqEnabled { get; set; }
    /// <summary>La X esconde Echoplex en la bandeja (la música sigue) en vez de cerrarlo. De serie, cierra.</summary>
    public bool CloseToTray { get; set; }
    /// <summary>Carpetas en línea (Drive, servidores web…): su nombre en el árbol y su URL. Se escuchan desde la nube.</summary>
    public List<OnlineSource>? OnlineSources { get; set; }
    /// <summary>Ganancia de cada banda en dB (25 Hz … 16 kHz); null = todo plano.</summary>
    public double[]? EqGains { get; set; }
    /// <summary>Preamplificador del ecualizador, en dB.</summary>
    public double EqPreamp { get; set; }
    /// <summary>Segundos de fundido entre canciones (0 = apagado). Nunca entre pistas seguidas de un mismo disco.</summary>
    public double Crossfade { get; set; }
    /// <summary>Fundir también las pistas seguidas de un mismo disco. De serie, sí (el fundido en sí viene apagado).</summary>
    public bool CrossfadeSameAlbum { get; set; } = true;
    /// <summary>Acceso desde el móvil: Echoplex como servidor Subsonic en la red. De serie, apagado.</summary>
    public bool RemoteEnabled { get; set; }
    public int RemotePort { get; set; } = 4040;
    public string RemoteUser { get; set; } = "echoplex";
    /// <summary>Fuera de casa (por Tailscale), si la app no pide otra cosa: MP3 de estos kbps (0 = el archivo tal cual).</summary>
    public int RemoteAwayKbps { get; set; } = 192;
    /// <summary>El Tailscale de Echoplex (paquete fuera de casa) encendido: arranca con el acceso desde el móvil.</summary>
    public bool RemoteTailscale { get; set; }
    /// <summary>La contraseña del acceso desde el móvil, cifrada con la cuenta de Windows (se genera sola).</summary>
    public string? RemotePassword { get; set; }
    /// <summary>Atajos globales (Ctrl+Alt+Espacio, flechas, L) desde cualquier programa. De serie, apagados.</summary>
    public bool GlobalHotkeys { get; set; }
    /// <summary>Los atajos globales llevan también Mayús (Ctrl+Alt+Mayús+…).</summary>
    public bool GlobalHotkeyShift { get; set; }
    /// <summary>Scrobbling en Last.fm: la API key del usuario (la suya, de su cuenta de API).</summary>
    public string? LastFmKey { get; set; }
    /// <summary>El «Shared secret» de esa API key, cifrado con la cuenta de Windows (ver <see cref="Secret"/>).</summary>
    public string? LastFmSecret { get; set; }
    /// <summary>La sesión de Last.fm (cifrada); null = sin conectar.</summary>
    public string? LastFmSession { get; set; }
    public string? LastFmUser { get; set; }
    /// <summary>Token de usuario de ListenBrainz (cifrado); null = sin conectar.</summary>
    public string? ListenBrainzToken { get; set; }
    public string? ListenBrainzUser { get; set; }
    /// <summary>Forma de onda de la canción en la barra de progreso (solo las del PC). De serie, apagada.</summary>
    public bool WaveformSeek { get; set; }
    /// <summary>Normalización de volumen: off, track, album o smart (ver <see cref="Normalization"/>). De serie, apagada.</summary>
    public string Normalization { get; set; } = Services.Normalization.Off;
    /// <summary>Margen automático: baja el volumen lo justo para que la curva del ecualizador nunca sature. De serie, apagado.</summary>
    public bool EqAutoHeadroom { get; set; }
    /// <summary>Preajuste elegido (o "custom" si se movió alguna banda a mano).</summary>
    public string EqPreset { get; set; } = "flat";
    /// <summary>Clave del tema (ver <see cref="Themes"/>).</summary>
    public string Theme { get; set; } = "light";
    /// <summary>Último tema oscuro elegido: al que vuelve el interruptor claro/oscuro.</summary>
    public string DarkTheme { get; set; } = "dark";
    /// <summary>Último tema claro elegido: al que vuelve el interruptor desde uno oscuro.</summary>
    public string LightTheme { get; set; } = "light";
    public bool Animations { get; set; } = true;
    /// <summary>Mostrar y ordenar por el nombre del archivo (sin extensión) en lugar del título de los metadatos.</summary>
    public bool PreferFileName { get; set; }
    /// <summary>
    /// Excepciones por carpeta al ajuste anterior: ruta completa → true (nombres de fichero) / false (metadatos).
    /// Se aplican a la carpeta y a todas sus subcarpetas que no tengan la suya.
    /// </summary>
    public Dictionary<string, bool>? FolderFileNames { get; set; }
    /// <summary>Carátulas elegidas por el usuario: ruta completa de la carpeta → nombre del archivo en %LocalAppData%\Echoplex\covers.</summary>
    public Dictionary<string, string>? CustomCovers { get; set; }
    /// <summary>Desfase de la letra por canción (ruta completa → segundos; positivo = la letra va antes).</summary>
    public Dictionary<string, double>? LyricOffsets { get; set; }
    /// <summary>Portada de la cabecera en grande (se activa y desactiva con un clic en la portada pequeña).</summary>
    public bool ShowBigCover { get; set; }
    /// <summary>«Mostrar solo locales» encima del árbol: oculta las carpetas que están enteras en la nube (OneDrive).</summary>
    public bool OnlyLocalFolders { get; set; }
    /// <summary>«Solo favoritas» en las carpetas: su página enseña y reproduce solo sus favoritas (con las de sus subcarpetas).</summary>
    public bool FolderFavoritesOnly { get; set; }
    /// <summary>Buscar en LRCLIB las letras que no estén en los archivos (desactivado de serie).</summary>
    public bool OnlineLyrics { get; set; }
    /// <summary>Buscar en Deezer el BPM de las canciones que no lo traen en sus etiquetas. De serie, encendido.</summary>
    public bool BpmFromDeezer { get; set; } = true;
    public bool BpmFromAudio { get; set; } = true;
    /// <summary>Enseñar en Discord lo que suena (Rich Presence). Desactivado de serie.</summary>
    public bool DiscordPresence { get; set; }
    /// <summary>Qué sale en la lista de miembros de Discord tras «Escuchando a»: "artist", "title" o "app".</summary>
    public string DiscordShow { get; set; } = "artist";
    /// <summary>Icono de sonando / en pausa en la esquina de la carátula de Discord.</summary>
    public bool DiscordPlayState { get; set; } = true;
    /// <summary>Programa que sube una carátula y devuelve su URL (recibe la ruta por la entrada estándar, como en foo_discord_rich).</summary>
    public string? DiscordUploadCommand { get; set; }
    /// <summary>
    /// Carátulas manuales (con el programa de subida) en vez de buscarlas en internet. null = sin elegir todavía: quien ya
    /// tenía un programa puesto (antes de la 1.1.13) sigue con él.
    /// </summary>
    public bool? DiscordManualCovers { get; set; }
    /// <summary>Al abrir, busca en GitHub una versión nueva y la instala sola.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Versión de la que el usuario pidió no volver a avisar («Omitir esta versión»).</summary>
    public string? SkippedVersion { get; set; }
    public double SidebarWidth { get; set; } = 260;
    /// <summary>Orden de las columnas de la tabla de canciones (claves de SongColumns).</summary>
    public List<string>? ColumnOrder { get; set; }
    /// <summary>Ancho de cada columna: peso si es proporcional, píxeles si es fija.</summary>
    public Dictionary<string, double>? ColumnSizes { get; set; }
    /// <summary>Columnas ocultas desde Ajustes → Estructura.</summary>
    public List<string>? HiddenColumns { get; set; }
    /// <summary>Ancho del panel derecho (cola, letra, detalles).</summary>
    public double RightPanelWidth { get; set; } = 330;
    /// <summary>Zoom de la interfaz (Ctrl + / Ctrl − / Ctrl+0): 1 = 100 %.</summary>
    public double Zoom { get; set; } = 1.0;
    public bool ShowRightPanel { get; set; } = true;
    /// <summary>Letra a la derecha en la pantalla completa (se pone y se quita con el micro de abajo a la derecha).</summary>
    public bool FullViewLyrics { get; set; } = true;
    public string RightPanelTab { get; set; } = "queue";
    public double Width { get; set; } = 1440;
    public double Height { get; set; } = 900;
    public bool Maximized { get; set; }
    public string? LastSong { get; set; }
    public string? LastContext { get; set; }
    /// <summary>Contextos reproducidos recientemente: folder:…, playlist:…, album:…, artist:…</summary>
    public List<string> RecentContexts { get; set; } = new();
}

public static class SettingsStore
{
    /// <summary>%LocalAppData%\Echoplex, o la carpeta de ECHOPLEX_DATA (las pruebas usan una copia y nunca tocan los datos reales).</summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("ECHOPLEX_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Echoplex");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings() : new AppSettings();
        }
        catch
        {
            s = new AppSettings(); // ajustes corruptos: valores por defecto
        }

        s.MusicRoots ??= new List<string>();
        if (!string.IsNullOrWhiteSpace(s.MusicRoot) && s.MusicRoots.Count == 0) s.MusicRoots.Add(s.MusicRoot);
        s.MusicRoot = null;
        if (s.MusicRoots.Count == 0)
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (Directory.Exists(music)) s.MusicRoots.Add(music);
        }
        if (!Themes.All.Any(t => t.Key == s.Theme)) s.Theme = "light";
        if (!Themes.All.Any(t => t.Key == s.DarkTheme && t.IsDark)) s.DarkTheme = "dark";
        if (!Themes.All.Any(t => t.Key == s.LightTheme && !t.IsDark)) s.LightTheme = "light";
        if (s.LyricOffsets != null) s.LyricOffsets = new Dictionary<string, double>(s.LyricOffsets, StringComparer.OrdinalIgnoreCase);
        return s;
    }

    // una sola instancia: así System.Text.Json reaprovecha lo que ya sabe del tipo en cada guardado
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            // a un temporal y luego en su sitio: un cierre a medias nunca deja los ajustes rotos
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, SaveOptions));
            File.Move(tmp, FilePath, true);
        }
        catch
        {
            // no crítico
        }
    }
}
