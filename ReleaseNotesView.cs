using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Echoplex.Services;

namespace Echoplex;

/// <summary>
/// Notas de la versión instalada para Ajustes: las mismas de .github/notas/&lt;versión&gt;.md que publica GitHub,
/// incrustadas en el ejecutable. Se muestran con un Markdown sencillo (títulos, viñetas, negrita, código y enlaces)
/// y sin las secciones pensadas para quien descarga el zip (Actualizar, Instalación, Requisitos).
/// </summary>
public static partial class ReleaseNotesView
{
    private static readonly HashSet<string> SkippedSections = new(StringComparer.OrdinalIgnoreCase) { "Actualizar", "Instalación", "Requisitos" };

    /// <summary>El texto de las notas de esta versión, o null si no vienen incrustadas.</summary>
    public static string? Current()
    {
        var v = UpdateService.CurrentVersion;
        using var s = typeof(ReleaseNotesView).Assembly.GetManifestResourceStream($"notas/{v.Major}.{v.Minor}.{v.Build}.md");
        if (s == null) return null;
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    public static FrameworkElement Render(string markdown)
    {
        var root = new StackPanel();
        bool skipping = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ")) continue; // el título ("Echoplex 1.1.0") ya está en Ajustes
            if (line.StartsWith("## "))
            {
                var title = line[3..].Trim();
                skipping = SkippedSections.Contains(title);
                if (!skipping) root.Children.Add(Heading(title, root.Children.Count == 0));
                continue;
            }
            if (skipping || line.Trim().Length == 0 || line.TrimStart().StartsWith(">")) continue;

            var bullet = BulletRegex().Match(line);
            if (bullet.Success)
            {
                int level = bullet.Groups[1].Value.Length / 2;
                root.Children.Add(Bullet(bullet.Groups[2].Value, level));
            }
            else
            {
                var p = Text(line.Trim());
                p.Margin = new Thickness(0, 2, 0, 4);
                root.Children.Add(p);
            }
        }
        return root;
    }

    private static TextBlock Heading(string text, bool first)
    {
        var t = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, first ? 0 : 12, 0, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        return t;
    }

    private static FrameworkElement Bullet(string text, int level)
    {
        var grid = new Grid { Margin = new Thickness(level * 18, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var dot = new TextBlock { Text = level == 0 ? "•" : "◦", FontSize = 14 };
        dot.SetResourceReference(TextBlock.ForegroundProperty, level == 0 ? "AccentBrush" : "Text3Brush");
        var body = Text(text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(dot);
        grid.Children.Add(body);
        return grid;
    }

    /// <summary>Párrafo con **negrita**, *cursiva*, `código` y [enlaces](https://…).</summary>
    private static TextBlock Text(string text)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 20 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        int pos = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Index > pos) t.Inlines.Add(new Run(text[pos..m.Index]));
            if (m.Groups["bold"].Success)
            {
                var r = new Run(m.Groups["bold"].Value) { FontWeight = FontWeights.SemiBold };
                r.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
                t.Inlines.Add(r);
            }
            else if (m.Groups["em"].Success)
            {
                var r = new Run(m.Groups["em"].Value) { FontStyle = FontStyles.Italic };
                r.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
                t.Inlines.Add(r);
            }
            else if (m.Groups["code"].Success)
            {
                var r = new Run(m.Groups["code"].Value) { FontFamily = new FontFamily("Consolas") };
                r.SetResourceReference(TextElement.BackgroundProperty, "InputBrush");
                r.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
                t.Inlines.Add(r);
            }
            else
            {
                var url = m.Groups["url"].Value;
                var link = new Hyperlink(new Run(m.Groups["link"].Value)) { NavigateUri = new Uri(url), TextDecorations = null };
                link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                link.RequestNavigate += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
                    e.Handled = true;
                };
                t.Inlines.Add(link);
            }
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) t.Inlines.Add(new Run(text[pos..]));
        return t;
    }

    [GeneratedRegex(@"^(\s*)[-*] (.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|\*(?<em>[^*\s][^*]*)\*|`(?<code>[^`]+)`|\[(?<link>[^\]]+)\]\((?<url>https?://[^)\s]+)\)")]
    private static partial Regex InlineRegex();
}
