using System.IO;
using System.Text;
using Echoplex.Models;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>Playlists inteligentes (por reglas) e importar y exportar playlists en M3U.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Las canciones que cumplen las reglas, en su orden y hasta su límite.</summary>
    public List<Song> SmartSongs(SmartRules r)
    {
        IEnumerable<Song> songs = r.Scope switch
        {
            SmartRules.ScopeArtist when r.ScopeValue is { Length: > 0 } a => Library.SongsByArtist(a),
            SmartRules.ScopeGenre when r.ScopeValue is { Length: > 0 } g => Library.SongsByGenre(g),
            SmartRules.ScopeFolder when r.ScopeValue is { Length: > 0 } f && Library.HasFolder(f) => Library.SongsUnder(f),
            _ => Library.Songs,
        };
        var now = DateTime.Now;
        if (r.FavoritesOnly) songs = songs.Where(s => UserData.IsFavorite(s.Path));
        if (r.NotPlayedDays > 0)
            songs = songs.Where(s => UserData.GetStat(s.Path) is not { } st || st.Last < now.AddDays(-r.NotPlayedDays));
        if (r.MinPlays > 0) songs = songs.Where(s => (UserData.GetStat(s.Path)?.Count ?? 0) >= r.MinPlays);
        if (r.AddedDays > 0)
        {
            long since = DateTime.UtcNow.AddDays(-r.AddedDays).Ticks;
            songs = songs.Where(s => s.CreatedTicks >= since);
        }
        if (r.YearFrom > 0) songs = songs.Where(s => s.Year >= r.YearFrom);
        if (r.YearTo > 0) songs = songs.Where(s => s.Year > 0 && s.Year <= r.YearTo);

        int Plays(Song s) => UserData.GetStat(s.Path)?.Count ?? 0;
        songs = r.Order switch
        {
            SmartRules.OrderLeast => songs.OrderBy(Plays),
            SmartRules.OrderRecent => songs.OrderByDescending(s => UserData.GetStat(s.Path)?.Last ?? DateTime.MinValue),
            SmartRules.OrderAdded => songs.OrderByDescending(s => s.CreatedTicks),
            SmartRules.OrderRandom => songs.OrderBy(_ => Random.Shared.Next()),
            SmartRules.OrderAlbum => songs.OrderBy(s => s.ArtistKey, NaturalComparer.Instance).ThenBy(s => s.Year)
                .ThenBy(s => s.Album, NaturalComparer.Instance).ThenBy(s => s.DiscNumber).ThenBy(s => s.TrackNumber),
            _ => songs.OrderByDescending(Plays),
        };
        if (r.Limit > 0) songs = songs.Take(r.Limit);
        return songs.ToList();
    }

    /// <summary>Rehace una playlist inteligente con lo que dan sus reglas ahora (al abrirla, al sonar, al cambiar la biblioteca).</summary>
    public void RefreshSmart(Playlist p)
    {
        if (p.Smart is not { } rules) return;
        // el orden aleatorio no se rebaraja a cada vistazo: solo si cambió lo que entra
        if (rules.Order == SmartRules.OrderRandom)
        {
            var now = SmartSongs(rules).Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (now.SetEquals(p.Songs)) return;
        }
        UserData.SetSongs(p, SmartSongs(rules).Select(s => s.Path).ToList());
    }

    private void RefreshAllSmart()
    {
        foreach (var p in UserData.Playlists.Where(p => p.IsSmart).ToList()) RefreshSmart(p);
    }

    public Playlist CreateSmartPlaylist(string name, SmartRules rules)
    {
        var p = UserData.CreateSmartPlaylist(name, rules);
        RefreshSmart(p);
        ShowToast($"Playlist inteligente «{name}» creada: {p.Songs.Count} canciones");
        return p;
    }

    public void UpdateSmartPlaylist(Playlist p, string name, SmartRules rules)
    {
        if (p.Name != name) UserData.RenamePlaylist(p, name);
        UserData.SetRules(p, rules);
        RefreshSmart(p);
        if (CurrentPage is SongListPage { Playlist: { } cur } && cur == p) OpenPlaylist(p, replace: true);
    }

    /// <summary>Nombres de artistas, géneros o carpetas para elegir en el diálogo de reglas.</summary>
    public IReadOnlyList<string> SmartScopeChoices(string scope) => scope switch
    {
        SmartRules.ScopeArtist => Library.GetArtists().Select(a => a.Name).ToList(),
        SmartRules.ScopeGenre => Library.GetGenres().Select(g => g.Name).ToList(),
        SmartRules.ScopeFolder => Folders.SelectMany(r => new[] { r }.Concat(r.Descendants())).Select(n => n.Path).ToList(),
        _ => Array.Empty<string>(),
    };

    // ---------- M3U ----------

    /// <summary>
    /// Guarda la playlist en un .m3u8 (UTF-8) con rutas completas y la duración y el título de cada canción, para
    /// llevarla a otro reproductor o al móvil. Las de carpetas en línea van con su dirección web.
    /// </summary>
    public void ExportM3u(Playlist p, string file)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var s in Library.Resolve(p.Songs))
        {
            int secs = (int)(s.Duration?.TotalSeconds ?? -1);
            sb.Append($"#EXTINF:{secs},{s.Artist} - {s.Title}\n");
            sb.Append(OnlineLibrary.IsOnline(s.Path) ? Services.OnlineLibrary.Find(s.Path)?.Url ?? s.Path : s.Path).Append('\n');
        }
        File.WriteAllText(file, sb.ToString().Replace("\n", "\r\n"), new UTF8Encoding(false));
        ShowToast($"Playlist «{p.Name}» exportada");
    }

    /// <summary>
    /// Crea una playlist desde un .m3u o .m3u8: cada ruta (completa o relativa al archivo) se busca en la biblioteca;
    /// si no está tal cual, por el nombre del archivo. Devuelve cuántas no se encontraron.
    /// </summary>
    public (Playlist? playlist, int missing) ImportM3u(string file)
    {
        var bytes = File.ReadAllBytes(file);
        // .m3u8 es UTF-8; un .m3u viejo puede venir en la codificación de Windows
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        var dir = Path.GetDirectoryName(file) ?? "";
        var byName = Library.Songs.GroupBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var found = new List<Song>();
        int missing = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            Song? song = null;
            if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile) line = uri.LocalPath;
            if (!line.Contains("://"))
            {
                string full;
                try { full = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(dir, line)); }
                catch { full = line; }
                song = Library.Find(full);
            }
            song ??= Library.Songs.FirstOrDefault(s => OnlineLibrary.IsOnline(s.Path) && Services.OnlineLibrary.Find(s.Path)?.Url == line);
            if (song == null)
            {
                var name = line.Replace('/', Path.DirectorySeparatorChar);
                byName.TryGetValue(Path.GetFileName(name), out song);
            }
            if (song != null) found.Add(song); else missing++;
        }
        if (found.Count == 0) return (null, missing);
        var p = UserData.CreatePlaylist(Path.GetFileNameWithoutExtension(file), found.Distinct().ToList());
        return (p, missing);
    }
}
