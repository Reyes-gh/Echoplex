using System.Windows;
using System.Windows.Media;

namespace Echoplex.Services;

/// <summary>
/// Genera el diccionario de colores completo (las mismas claves que Themes/Colors.*.xaml) de un tema de "Más temas",
/// con las reglas de los temas hechos a mano (Neon night, 80's retro):
/// - las capas (tarjetas, hover, seleccionado, bordes, scroll) son el mismo tono del fondo con más luz, nunca gris;
/// - los textos secundarios van teñidos del tono del tema;
/// - un segundo color para reproducir y los avisos, que contrasta con el acento;
/// - en los oscuros con acento vivo, la sombra es un resplandor del acento.
/// </summary>
public static class ThemeFactory
{
    // tonos de las etiquetas de formato, mezclados con el fondo y el texto de cada tema
    private static readonly (string Name, Color Hue)[] TagHues =
    {
        ("Blue", ColorMath.Parse("#4A90E2")), ("Green", ColorMath.Parse("#4CAF7A")), ("Orange", ColorMath.Parse("#F08A3C")),
        ("Purple", ColorMath.Parse("#9B6DD6")), ("Yellow", ColorMath.Parse("#E6C23A")), ("Pink", ColorMath.Parse("#E05C9A")),
    };

    public static ResourceDictionary Build(ThemeInfo t) => t.IsDark ? BuildDark(t) : BuildLight(t);

    private static ResourceDictionary BuildDark(ThemeInfo t)
    {
        Color bg = ColorMath.Parse(t.Bg), side = ColorMath.Parse(t.Sidebar), text = ColorMath.Parse(t.Text);
        Color accent = ColorMath.Parse(t.Accent), accent2 = ColorMath.Parse(t.Accent2 ?? t.Fav), fav = ColorMath.Parse(t.Fav);
        var (h, s, l) = ColorMath.ToHsl(bg);
        var (_, _, ls) = ColorMath.ToHsl(side);
        double sat = Math.Min(s * 1.3, 0.55); // capas algo más saturadas que el fondo, como en los temas hechos a mano
        Color Up(double dl) => ColorMath.FromHsl(h, sat, Math.Min(0.9, l + dl));
        Color SideUp(double dl) => ColorMath.FromHsl(h, sat, Math.Min(0.9, ls + dl));
        var d = new Dictionary<string, Color>();

        // textos teñidos del tono del tema (como el crema/malva de 80's retro)
        var text2 = ColorMath.Mix(ColorMath.Mix(text, ColorMath.FromHsl(h, s, 0.72), 0.4), bg, 0.22);
        var text3 = ColorMath.Mix(ColorMath.Mix(text, ColorMath.FromHsl(h, s, 0.6), 0.45), bg, 0.5);
        d["BgBrush"] = bg;
        d["SidebarBrush"] = side;
        d["TextBrush"] = text;
        d["Text2Brush"] = text2;
        d["Text3Brush"] = text3;
        d["SidebarTextBrush"] = ColorMath.Mix(text, text2, 0.45);
        d["DividerBrush"] = Up(0.075);
        d["HoverBrush"] = Up(0.05);
        d["SidebarHoverBrush"] = SideUp(0.05);
        d["SelectedBrush"] = ColorMath.Mix(Up(0.11), accent, 0.06);
        d["AccentBrush"] = accent;
        d["AccentHoverBrush"] = ColorMath.Mix(accent, Colors.White, 0.22);
        d["AccentSoftBrush"] = ColorMath.Mix(bg, accent, 0.18);
        d["OnAccentBrush"] = ColorMath.OnColor(accent, bg);
        d["CardBrush"] = Up(0.035);
        d["CardBorderBrush"] = Up(0.12);
        d["PopupBrush"] = Up(0.055);
        d["PopupBorderBrush"] = Up(0.16);
        d["PlaceholderBrush"] = Up(0.07);
        d["InputBrush"] = Up(0.045);
        d["CalloutBrush"] = Up(0.035);
        d["FavBrush"] = fav;
        d["ToastBrush"] = accent2;
        d["ToastTextBrush"] = ColorMath.OnColor(accent2, bg);
        d["ScrollThumbBrush"] = Up(0.16);
        d["ScrollThumbHoverBrush"] = Up(0.25);
        d["TrackBrush"] = Up(0.16);
        d["PlayCircleBrush"] = accent2;
        d["OnPlayCircleBrush"] = ColorMath.OnColor(accent2, bg);
        d["TagGrayBg"] = Up(0.12);
        d["TagGrayFg"] = ColorMath.Mix(text, text2, 0.2);
        foreach (var (name, hue) in TagHues)
        {
            d[$"Tag{name}Bg"] = ColorMath.Mix(bg, hue, 0.3);
            d[$"Tag{name}Fg"] = ColorMath.Mix(text, hue, 0.22);
        }
        return ToDictionary(d);
    }

    private static ResourceDictionary BuildLight(ThemeInfo t)
    {
        Color bg = ColorMath.Parse(t.Bg), side = ColorMath.Parse(t.Sidebar), text = ColorMath.Parse(t.Text);
        Color accent = ColorMath.Parse(t.Accent), accent2 = ColorMath.Parse(t.Accent2 ?? t.Accent), fav = ColorMath.Parse(t.Fav);
        // el tono de las capas: el del fondo, o el de la barra lateral si el fondo es blanco puro
        var hb = ColorMath.ToHsl(bg);
        var hs = ColorMath.ToHsl(side);
        var (h, s) = hb.S >= hs.S ? (hb.H, hb.S) : (hs.H, hs.S);
        double sat = Math.Min(s, 0.45), l = hb.L, ls = hs.L;
        Color Down(double dl) => ColorMath.FromHsl(h, sat, Math.Max(0.05, l - dl));
        Color SideDown(double dl) => ColorMath.FromHsl(h, sat, Math.Max(0.05, ls - dl));
        var d = new Dictionary<string, Color>();

        var text2 = ColorMath.Mix(ColorMath.Mix(text, ColorMath.FromHsl(h, Math.Min(s, 0.4), 0.3), 0.25), bg, 0.38);
        var text3 = ColorMath.Mix(ColorMath.Mix(text, ColorMath.FromHsl(h, Math.Min(s, 0.4), 0.4), 0.3), bg, 0.58);
        d["BgBrush"] = bg;
        d["SidebarBrush"] = side;
        d["TextBrush"] = text;
        d["Text2Brush"] = text2;
        d["Text3Brush"] = text3;
        d["SidebarTextBrush"] = ColorMath.Mix(text, text2, 0.5);
        d["DividerBrush"] = Down(0.075);
        d["HoverBrush"] = Down(0.055);
        d["SidebarHoverBrush"] = SideDown(0.05);
        d["SelectedBrush"] = ColorMath.Mix(Down(0.1), accent, 0.06);
        d["AccentBrush"] = accent;
        d["AccentHoverBrush"] = ColorMath.Mix(accent, Colors.Black, 0.15);
        d["AccentSoftBrush"] = ColorMath.Mix(bg, accent, 0.13);
        d["OnAccentBrush"] = ColorMath.OnColor(accent, ColorMath.Mix(text, Colors.Black, 0.4));
        d["CardBrush"] = ColorMath.Mix(bg, Colors.White, 0.35);
        d["CardBorderBrush"] = Down(0.095);
        d["PopupBrush"] = ColorMath.Mix(bg, Colors.White, 0.5);
        d["PopupBorderBrush"] = Down(0.13);
        d["PlaceholderBrush"] = Down(0.06);
        d["InputBrush"] = Down(0.04);
        d["CalloutBrush"] = side;
        d["FavBrush"] = fav;
        d["ToastBrush"] = text;
        d["ToastTextBrush"] = bg;
        d["ScrollThumbBrush"] = Down(0.17);
        d["ScrollThumbHoverBrush"] = Down(0.28);
        d["TrackBrush"] = Down(0.13);
        d["PlayCircleBrush"] = accent2;
        d["OnPlayCircleBrush"] = ColorMath.OnColor(accent2, ColorMath.Mix(text, Colors.Black, 0.4));
        d["TagGrayBg"] = Down(0.1);
        d["TagGrayFg"] = ColorMath.Mix(text, Colors.Black, 0.2);
        foreach (var (name, hue) in TagHues)
        {
            d[$"Tag{name}Bg"] = ColorMath.Mix(bg, hue, 0.22);
            d[$"Tag{name}Fg"] = ColorMath.Mix(hue, Colors.Black, 0.6);
        }
        return ToDictionary(d);
    }

    private static ResourceDictionary ToDictionary(Dictionary<string, Color> colors)
    {
        var rd = new ResourceDictionary();
        foreach (var (key, c) in colors)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            rd[key] = b;
        }
        return rd;
    }
}

/// <summary>Mezclas y HSL para construir los temas.</summary>
public static class ColorMath
{
    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>De <paramref name="a"/> hacia <paramref name="b"/> en la proporción <paramref name="t"/> (0 = a, 1 = b).</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static double Luminance(Color c)
    {
        static double L(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
    }

    /// <summary>Texto que se lee sobre <paramref name="c"/>: el oscuro indicado si el color es claro, si no blanco.</summary>
    public static Color OnColor(Color c, Color dark) => Luminance(c) > 0.3 ? dark : Colors.White;

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q, hk = h / 360;
        double Ch(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        return Color.FromRgb((byte)Math.Round(Ch(hk + 1.0 / 3) * 255), (byte)Math.Round(Ch(hk) * 255), (byte)Math.Round(Ch(hk - 1.0 / 3) * 255));
    }
}
