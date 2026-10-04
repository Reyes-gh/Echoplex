namespace Echoplex.Services;

/// <summary>
/// Un tema. Los de Echoplex tienen su diccionario de colores a mano (Themes/Colors.{File}.xaml); los demás (File null)
/// se generan con <see cref="ThemeFactory"/> a partir de fondo, barra lateral, texto, acento, segundo color y favorito.
/// Bg, Sidebar, Text y Accent sirven también para la miniatura de Ajustes. En Ajustes, los de Echoplex van arriba y el
/// resto en Más temas, por Claros u Oscuros (IsDark) y por familias (Group, en el orden de <see cref="Themes.Families"/>).
/// </summary>
/// <param name="Caption">Color de la barra de título de Windows (0xRRGGBB).</param>
public sealed record ThemeInfo(string Key, string Name, string? File, bool IsDark, int Caption,
    string Bg, string Sidebar, string Text, string Accent, string Group = "", string Fav = "#E03E3E", string? Accent2 = null);

public static class Themes
{
    /// <summary>Los de Echoplex: van arriba en Ajustes, fuera de Más temas.</summary>
    public const string Main = "Echoplex";
    // familias de Más temas, en el orden en que salen (dentro de Claros y de Oscuros)
    public const string Editors = "Editores de código";
    public const string Nature = "Naturaleza y ambientes";
    public const string Pastel = "Suaves y pastel";
    public const string Retro = "Retro y videojuegos";
    public const string Vivid = "Intensos y alto contraste";
    public static readonly IReadOnlyList<string> Families = new[] { Editors, Nature, Pastel, Retro, Vivid };

    public static readonly IReadOnlyList<ThemeInfo> All = new[]
    {
        // ===== principales (los de Echoplex, hechos a mano) =====
        new ThemeInfo("light", "Claro", "Light", false, 0xF7F7F5, "#FFFFFF", "#F7F7F5", "#37352F", "#2383E2", Main),
        new ThemeInfo("dark", "Oscuro", "Dark", true, 0x202020, "#191919", "#202020", "#D4D4D4", "#2383E2", Main),
        new ThemeInfo("truedark", "TRUE dark", "TrueDark", true, 0x000000, "#000000", "#050505", "#C8C8C8", "#8A8AFF", Main),
        new ThemeInfo("neon", "Neon night", "Neon", true, 0x0C0F1E, "#080A16", "#0C0F1E", "#DDE6FF", "#00E5FF", Main),
        new ThemeInfo("retro", "80's retro", "Retro", true, 0x1F1030, "#1A0D2B", "#1F1030", "#FFE4C9", "#FF3C9E", Main),

        // ===== editores de código =====
        // Dark(clave, nombre, grupo, fondo, texto, acento, segundo color, favorito): el fondo se lleva a un negro con su matiz
        Dark("dracula", "Dracula", Editors, "#282A36", "#F8F8F2", "#BD93F9", "#FF79C6", "#FF5555"),
        Dark("nord", "Nord", Editors, "#2E3440", "#ECEFF4", "#88C0D0", "#EBCB8B", "#BF616A"),
        Light("nordsnow", "Nord Snow", Editors, "#ECEFF4", "#E5E9F0", "#2E3440", "#5E81AC", "#5E81AC", "#BF616A"),
        Dark("onedark", "One Dark", Editors, "#282C34", "#D7DAE0", "#61AFEF", "#C678DD", "#E06C75"),
        Dark("monokai", "Monokai", Editors, "#272822", "#F8F8F2", "#A6E22E", "#FD971F", "#F92672"),
        Dark("solarizeddark", "Solarized Dark", Editors, "#002B36", "#D2DBDB", "#268BD2", "#B58900", "#DC322F"),
        Light("solarizedlight", "Solarized Light", Editors, "#FDF6E3", "#EEE8D5", "#3B4F56", "#268BD2", "#CB4B16", "#DC322F"),
        Dark("gruvboxdark", "Gruvbox Dark", Editors, "#2A2520", "#EBDBB2", "#FABD2F", "#FE8019", "#FB4934"),
        Light("gruvboxlight", "Gruvbox Light", Editors, "#FBF1C7", "#F2E5BC", "#3C3836", "#AF3A03", "#076678", "#CC241D"),
        Dark("tokyonight", "Tokyo Night", Editors, "#1A1B26", "#C0CAF5", "#7AA2F7", "#BB9AF7", "#F7768E"),
        Dark("mocha", "Catppuccin Mocha", Editors, "#1E1E2E", "#CDD6F4", "#CBA6F7", "#F5C2E7", "#F38BA8"),
        Light("latte", "Catppuccin Latte", Editors, "#EFF1F5", "#E6E9EF", "#4C4F69", "#8839EF", "#EA76CB", "#D20F39"),
        Dark("rosepine", "Rosé Pine", Editors, "#191724", "#E0DEF4", "#EBBCBA", "#C4A7E7", "#EB6F92"),
        Light("rosepinedawn", "Rosé Pine Dawn", Editors, "#FAF4ED", "#F2E9E1", "#3E3A5E", "#D7827E", "#907AA9", "#B4637A"),
        Dark("kanagawa", "Kanagawa", Editors, "#1F1F28", "#DCD7BA", "#7E9CD8", "#FFA066", "#E46876"),
        Dark("everforest", "Everforest", Editors, "#2D353B", "#D3C6AA", "#A7C080", "#DBBC7F", "#E67E80"),
        Dark("ayudark", "Ayu Dark", Editors, "#0B0E14", "#D0CEC7", "#E6B450", "#FF8F40", "#F07178"),
        Light("ayulight", "Ayu Light", Editors, "#FCFCFC", "#F3F4F5", "#3D4247", "#FF9940", "#55B4D4", "#F07171"),
        Dark("githubdark", "GitHub Dark", Editors, "#0D1117", "#E6EDF3", "#2F81F7", "#3FB950", "#F85149"),
        Light("githublight", "GitHub Light", Editors, "#FFFFFF", "#F6F8FA", "#1F2328", "#0969DA", "#1A7F37", "#CF222E"),
        Dark("palenight", "Palenight", Editors, "#292D3E", "#C3C8E6", "#C792EA", "#82AAFF", "#F07178"),
        Dark("nightowl", "Night Owl", Editors, "#011627", "#D6DEEB", "#82AAFF", "#C792EA", "#EF5350"),
        Dark("cobalt2", "Cobalt2", Editors, "#193549", "#FFFFFF", "#FFC600", "#FF9D00", "#FF628C"),
        Dark("frappe", "Catppuccin Frappé", Editors, "#303446", "#C6D0F5", "#CA9EE6", "#F4B8E4", "#E78284"),
        Dark("rosepinemoon", "Rosé Pine Moon", Editors, "#232136", "#E0DEF4", "#EA9A97", "#C4A7E7", "#EB6F92"),
        Dark("poimandres", "Poimandres", Editors, "#1B1E28", "#E4F0FB", "#5DE4C7", "#ADD7FF", "#D0679D"),
        Dark("darcula", "Darcula", Editors, "#2B2B2B", "#BBC6D3", "#CC7832", "#6897BB", "#FF6B68"),
        Dark("shadesofpurple", "Shades of Purple", Editors, "#2D2B55", "#FFFFFF", "#FAD000", "#A599E9", "#EC3A37"),
        Dark("vesper", "Vesper", Editors, "#101010", "#FFFFFF", "#FFC799", "#99FFE4", "#FF8080"),
        Light("onelight", "One Light", Editors, "#FAFAFA", "#F0F0F1", "#383A42", "#4078F2", "#A626A4", "#E45649"),
        Light("tokyoday", "Tokyo Night Day", Editors, "#E1E2E7", "#D5D6DB", "#343B58", "#9854F1", "#2E7DE9", "#F52A65"),
        Light("nightowllight", "Night Owl Light", Editors, "#FBFBFB", "#F0F0F0", "#403F53", "#2AA298", "#4876D6", "#DE3D3B"),
        Light("vitesselight", "Vitesse Light", Editors, "#FFFFFF", "#F7F7F7", "#393A34", "#1C6B48", "#B07D48", "#AB5959"),

        // ===== naturaleza y ambientes =====
        Dark("forest", "Bosque", Nature, "#16211B", "#DCEBE0", "#5FBF7F", "#E8C35A", "#E0735F"),
        Dark("ocean", "Océano", Nature, "#0B1D2E", "#D3E6F5", "#3FB6E8", "#5EF2D6", "#FF6B6B"),
        Dark("aurora", "Aurora boreal", Nature, "#0B1A1E", "#D6F2EE", "#33E0A1", "#B387FF", "#FF5FA2"),
        Dark("sunset", "Atardecer", Nature, "#2A1A1F", "#FBE3D6", "#FF8A4C", "#FF4D8D", "#FF4D6D"),
        Dark("volcano", "Volcán", Nature, "#1A0F0E", "#F2DCD6", "#FF5A1F", "#FFB020", "#FF2E63"),
        Dark("midnight", "Medianoche", Nature, "#0F1224", "#D8DCF0", "#6C7BFF", "#FF5C8A", "#FF5C8A"),
        Dark("coffee", "Café", Nature, "#1E1714", "#EADBCF", "#C8925A", "#E8B87A", "#E06B5A"),
        Light("sakura", "Sakura", Nature, "#FFF7F9", "#FCEEF2", "#3E2F35", "#E26D95", "#E26D95", "#D6336C"),
        Light("lavender", "Lavanda", Nature, "#F7F5FC", "#EFEBF8", "#302A48", "#7C5CD6", "#7C5CD6", "#D6336C"),
        Light("mint", "Menta", Nature, "#F4FBF7", "#E9F5EE", "#1E342A", "#1FA870", "#1FA870", "#E0484F"),
        Light("arctic", "Ártico", Nature, "#F5F9FC", "#EAF1F7", "#1A2C40", "#2F8FD8", "#2F8FD8", "#E0484F"),
        Light("desert", "Desierto", Nature, "#FAF3E8", "#F2E7D5", "#3F3122", "#C8742F", "#C8742F", "#B83A2E"),
        Light("paper", "Papel", Nature, "#F4ECD8", "#EDE3CB", "#382B1B", "#9C6B30", "#9C6B30", "#B03A2E"),
        Dark("starrynight", "Noche estrellada", Nature, "#141B3D", "#E3E8FF", "#FFD166", "#8EA7FF", "#FF6B8B"),
        Dark("storm", "Tormenta", Nature, "#1F2630", "#DCE3EE", "#8AB4F8", "#FDD663", "#F28B82"),
        Dark("lake", "Lago", Nature, "#0F2A2E", "#D5EEF0", "#2EC4B6", "#FFD166", "#EF476F"),
        Light("sky", "Cielo", Nature, "#F3F8FF", "#E6F0FC", "#1C2E45", "#3A86FF", "#FF9F1C", "#E63946"),
        Light("meadow", "Pradera", Nature, "#F6FAEF", "#EAF2DC", "#26331A", "#5A9E2F", "#E0A526", "#D64545"),
        Light("fog", "Niebla", Nature, "#F2F4F5", "#E4E8EA", "#2C3A42", "#4F7C8A", "#8E6C88", "#C0392B"),
        Light("autumn", "Otoño", Nature, "#FBF4EC", "#F2E6D6", "#3B2A1E", "#C0582B", "#8A9A3B", "#B83A2E"),

        // ===== suaves y pastel =====
        Light("bubblegum", "Chicle", Pastel, "#FFF7FB", "#FCE8F2", "#4A2F3E", "#E05FA6", "#5BB8E8", "#E11D48"),
        Light("peach", "Melocotón", Pastel, "#FFF5EE", "#FCE6D8", "#45302A", "#D9663F", "#E0577D", "#D64545"),
        Light("vanilla", "Vainilla", Pastel, "#FFFBF2", "#FAF1DC", "#3D3326", "#B8741A", "#6FAE9A", "#D64545"),
        Light("pistachio", "Pistacho", Pastel, "#F6FBF1", "#E9F4DE", "#2F3B26", "#5A9139", "#E29A55", "#D64545"),
        Light("cloud", "Nube", Pastel, "#F7F9FC", "#ECF1F8", "#2B3440", "#6C8FD0", "#E68AA3", "#E05A6E"),
        Dark("marshmallow", "Malvavisco", Pastel, "#2A2333", "#F2E9F7", "#F2A7D8", "#A7D8F2", "#F27E9A"),

        // ===== retro y videojuegos =====
        Dark("synthwave", "Synthwave '84", Retro, "#262335", "#F0EFF1", "#FF7EDB", "#FEDE5D", "#FE4450"),
        Dark("cyberpunk", "Cyberpunk", Retro, "#0A0A12", "#EAEAEA", "#FCEE0A", "#00F0FF", "#FF003C"),
        Dark("matrix", "Matrix", Retro, "#000A00", "#9CFFB0", "#00FF41", "#00FF41", "#B6FF00"),
        Dark("amber", "Terminal ámbar", Retro, "#120B00", "#FFC24D", "#FFB000", "#FF6A00", "#FF6A00"),
        Dark("msdos", "MS-DOS", Retro, "#0000AA", "#E0E0FF", "#FFFF55", "#55FFFF", "#FF5555", deep: false),
        Dark("c64", "Commodore 64", Retro, "#3E31A2", "#C4BFFF", "#A0E0FF", "#FFF07A", "#FF8A8A", deep: false),
        Light("gameboy", "Game Boy", Retro, "#9BBC0F", "#8BAC0F", "#0F380F", "#306230", "#0F380F", "#0F380F"),
        Light("win95", "Windows 95", Retro, "#C3C3C3", "#B5B5B5", "#111111", "#000080", "#000080", "#AA0000"),
        Light("vaporwave", "Vaporwave", Retro, "#FDF0FF", "#F6E3FB", "#3A2352", "#FF71CE", "#01B4E4", "#FF71CE"),
        Dark("virtualboy", "Virtual Boy", Retro, "#1A0000", "#FF8A8A", "#FF2A2A", "#FF6B6B", "#FF2A2A"),
        Dark("snes", "Super Nintendo", Retro, "#24213D", "#E8E6F5", "#A59CE4", "#E0533E", "#E0533E"),
        Light("winxp", "Windows XP", Retro, "#ECE9D8", "#D6DFF5", "#1E1E1E", "#245EDC", "#3C9A3C", "#E04343"),
        Light("macos9", "Mac OS 9", Retro, "#E3E3E3", "#D2D2D2", "#111111", "#5B5BC0", "#3F7FBF", "#CC3333"),

        // ===== intensos y alto contraste =====
        Dark("musicgreen", "Verde música", Vivid, "#121212", "#EDEDED", "#1DB954", "#1DB954", "#1DB954"),
        Dark("crimson", "Carmesí", Vivid, "#16090B", "#F3DADD", "#E5173F", "#FF7A45", "#FF4D6D"),
        Dark("gold", "Oro", Vivid, "#14110A", "#EEE3C8", "#D4AF37", "#F5D77A", "#E0674B"),
        Dark("hcdark", "Alto contraste", Vivid, "#000000", "#FFFFFF", "#FFFF00", "#00FFFF", "#FF3B3B"),
        Light("hclight", "Alto contraste claro", Vivid, "#FFFFFF", "#FFFFFF", "#000000", "#0000CC", "#0000CC", "#CC0000"),
        Dark("ultraviolet", "Ultravioleta", Vivid, "#170A2B", "#EFE6FF", "#9D4EDD", "#FF4FD8", "#FF4D6D"),
        Light("redink", "Tinta roja", Vivid, "#FFFFFF", "#F3F3F3", "#111111", "#D7263D", "#1B1B1B", "#D7263D"),
    };

    public static ThemeInfo Get(string key) => All.FirstOrDefault(t => t.Key == key) ?? All[0];

    /// <summary>
    /// Tema oscuro: el fondo se lleva a un negro profundo que conserva su matiz (como Neon night o 80's retro) y la barra
    /// lateral es el mismo tono con un poco más de luz. <paramref name="deep"/> = false deja el fondo tal cual (MS-DOS, C64).
    /// </summary>
    private static ThemeInfo Dark(string key, string name, string group, string bg, string text, string accent, string accent2, string fav, bool deep = true)
    {
        var (h, s, l) = ColorMath.ToHsl(ColorMath.Parse(bg));
        double deepL = deep ? Math.Min(l, 0.075 + Math.Max(0, l - 0.075) * 0.3) : l;
        double deepS = deep ? Math.Min(1, s * 1.4) : s; // algo más de color: que se note el matiz del tema
        var back = ColorMath.FromHsl(h, deepS, deepL);
        var side = ColorMath.FromHsl(h, Math.Min(deepS, 0.55), deepL + 0.025);
        return new(key, name, null, true, Rgb(ColorMath.Hex(side)), ColorMath.Hex(back), ColorMath.Hex(side), text, accent, group, fav, accent2);
    }

    private static ThemeInfo Light(string key, string name, string group, string bg, string sidebar, string text, string accent, string accent2, string fav) =>
        new(key, name, null, false, Rgb(sidebar), bg, sidebar, text, accent, group, fav, accent2);

    private static int Rgb(string hex) => Convert.ToInt32(hex.TrimStart('#'), 16);
}