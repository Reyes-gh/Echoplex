namespace Echoplex.Services;

/// <summary>Un tema: su diccionario de colores (Themes/Colors.{File}.xaml) y los colores de su muestra en Ajustes.</summary>
/// <param name="Caption">Color de la barra de título de Windows (0xRRGGBB).</param>
public sealed record ThemeInfo(string Key, string Name, string File, bool IsDark, int Caption,
    string Bg, string Sidebar, string Text, string Accent);

public static class Themes
{
    public static readonly IReadOnlyList<ThemeInfo> All = new[]
    {
        new ThemeInfo("light", "Claro", "Light", false, 0xF7F7F5, "#FFFFFF", "#F7F7F5", "#37352F", "#2383E2"),
        new ThemeInfo("dark", "Oscuro", "Dark", true, 0x202020, "#191919", "#202020", "#D4D4D4", "#2383E2"),
        new ThemeInfo("truedark", "TRUE dark", "TrueDark", true, 0x000000, "#000000", "#050505", "#C8C8C8", "#8A8AFF"),
        new ThemeInfo("neon", "Neon night", "Neon", true, 0x0C0F1E, "#080A16", "#0C0F1E", "#DDE6FF", "#00E5FF"),
        new ThemeInfo("retro", "80's retro", "Retro", true, 0x1F1030, "#1A0D2B", "#1F1030", "#FFE4C9", "#FF3C9E"),
    };

    public static ThemeInfo Get(string key) => All.FirstOrDefault(t => t.Key == key) ?? All[0];
}
