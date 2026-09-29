using System.Text.RegularExpressions;
using Echoplex.Models;

namespace Echoplex.Services;

public sealed record LyricEntry(TimeSpan? Time, string Text);

/// <summary>Letras desde un .lrc junto al archivo o incrustadas en las etiquetas.</summary>
public static partial class LyricsService
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex Stamp();

    [GeneratedRegex(@"^\[[a-zA-Z]+:.*\]$")]
    private static partial Regex MetaTag();

    public static List<LyricEntry>? Load(Song song)
    {
        try
        {
            var lrc = FindLrc(song.Path);
            if (lrc != null) return Parse(File.ReadAllText(lrc));
            if (!song.IsCloud)
            {
                using var f = TagLib.File.Create(song.Path);
                var text = f.Tag.Lyrics;
                if (!string.IsNullOrWhiteSpace(text)) return Parse(text);
            }
        }
        catch
        {
            // sin letra
        }
        return null;
    }

    private static string? FindLrc(string audioPath)
    {
        var candidate = Path.ChangeExtension(audioPath, ".lrc");
        if (File.Exists(candidate)) return candidate;
        var dir = Path.GetDirectoryName(audioPath);
        if (dir == null) return null;
        var name = Path.GetFileNameWithoutExtension(audioPath);
        return Directory.EnumerateFiles(dir, "*.lrc")
            .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
    }

    public static List<LyricEntry> Parse(string text)
    {
        var timed = new List<LyricEntry>();
        var plain = new List<LyricEntry>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var matches = Stamp().Matches(line);
            if (matches.Count == 0)
            {
                if (!MetaTag().IsMatch(line)) plain.Add(new LyricEntry(null, line));
                continue;
            }
            var content = Stamp().Replace(line, "").Trim();
            foreach (Match m in matches)
            {
                var sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), System.Globalization.CultureInfo.InvariantCulture);
                timed.Add(new LyricEntry(TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(sec), content));
            }
        }
        if (timed.Count > 0) return timed.OrderBy(l => l.Time).ToList();
        while (plain.Count > 0 && plain[0].Text.Length == 0) plain.RemoveAt(0);
        return plain;
    }
}
