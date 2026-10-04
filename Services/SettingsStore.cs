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
    /// <summary>Buscar en LRCLIB las letras que no estén en los archivos (desactivado de serie).</summary>
    public bool OnlineLyrics { get; set; }
    /// <summary>Enseñar en Discord lo que suena (Rich Presence). Desactivado de serie.</summary>
    public bool DiscordPresence { get; set; }
    /// <summary>Qué sale en la lista de miembros de Discord tras «Escuchando a»: "artist", "title" o "app".</summary>
    public string DiscordShow { get; set; } = "artist";
    /// <summary>Icono de sonando / en pausa en la esquina de la carátula de Discord.</summary>
    public bool DiscordPlayState { get; set; } = true;
    /// <summary>Programa que sube una carátula y devuelve su URL (recibe la ruta por la entrada estándar, como en foo_discord_rich).</summary>
    public string? DiscordUploadCommand { get; set; }
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

    /// <summary>
    /// La app se llamaba Sonora: la primera vez se copian sus datos (ajustes, playlists, historial, caché)
    /// a la carpeta nueva. La antigua se deja tal cual, por si acaso.
    /// </summary>
    static SettingsStore()
    {
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonora");
            if (Directory.Exists(DataDirectory) || !Directory.Exists(legacy)) return;
            Directory.CreateDirectory(DataDirectory);
            foreach (var file in Directory.GetFiles(legacy))
                File.Copy(file, Path.Combine(DataDirectory, Path.GetFileName(file)), overwrite: false);
        }
        catch
        {
            // sin migración: se empieza de cero
        }
    }

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
