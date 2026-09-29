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
    /// <summary>light, dark, neon, truedark o retro (ver <see cref="Themes"/>).</summary>
    public string Theme { get; set; } = "light";
    /// <summary>Último tema oscuro elegido: al que vuelve el interruptor claro/oscuro.</summary>
    public string DarkTheme { get; set; } = "dark";
    public bool Animations { get; set; } = true;
    /// <summary>Al abrir, busca en GitHub una versión nueva y la instala sola.</summary>
    public bool AutoUpdate { get; set; } = true;
    public double SidebarWidth { get; set; } = 260;
    /// <summary>Orden de las columnas de la tabla de canciones (claves de SongColumns).</summary>
    public List<string>? ColumnOrder { get; set; }
    /// <summary>Ancho de cada columna: peso si es proporcional, píxeles si es fija.</summary>
    public Dictionary<string, double>? ColumnSizes { get; set; }
    /// <summary>Ancho del panel derecho (cola, letra, detalles).</summary>
    public double RightPanelWidth { get; set; } = 330;
    /// <summary>Zoom de la interfaz (Ctrl + / Ctrl − / Ctrl+0): 1 = 100 %.</summary>
    public double Zoom { get; set; } = 1.0;
    public bool ShowRightPanel { get; set; } = true;
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
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Echoplex");

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
        return s;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // no crítico
        }
    }
}
