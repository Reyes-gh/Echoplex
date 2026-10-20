using System.Text.Json;
using System.Windows.Threading;
using Echoplex.Models;

namespace Echoplex.Services.Subsonic;

/// <summary>
/// Lo que Echoplex ya sabe hacer, servido al móvil: letras (sincronizadas si las hay), la cola para seguir en otro sitio
/// donde se dejó y canciones parecidas (la radio de Echoplex) para las mezclas instantáneas.
/// </summary>
public sealed partial class SubsonicServer
{
    // ======================================================================
    // Letras
    // ======================================================================

    /// <summary>getLyrics (por artista y título, solo texto) y getLyricsBySongId (OpenSubsonic, con tiempos).</summary>
    private async Task<HttpLite.Response> LyricsAsync(HttpLite.Request req, bool bySongId)
    {
        string? format = req["f"], callback = req["callback"];
        var song = await _host.Ui.InvokeAsync(() => bySongId ? Index.SongById(req["id"]) : FindByArtistTitle(req["artist"], req["title"]), DispatcherPriority.Background);
        var lines = song == null ? null : await await _host.Ui.InvokeAsync(() => _host.Lyrics(song), DispatcherPriority.Background);
        var root = SubsonicWriter.Ok();
        if (!bySongId)
        {
            var node = root.Child("lyrics");
            if (song != null && lines is { Count: > 0 })
            {
                node.Set("artist", song.Artist).Set("title", song.Title);
                node.Text = string.Join("\n", lines.Select(l => l.Text));
            }
            return Reply(root, format, callback);
        }
        var list = root.Child("lyricsList").EmptyList("structuredLyrics");
        if (song != null && lines is { Count: > 0 })
        {
            bool synced = lines.Any(l => l.Time != null);
            var lyric = list.Item("structuredLyrics")
                .Set("displayArtist", song.Artist)
                .Set("displayTitle", song.Title)
                .Set("lang", "und")
                .Set("offset", 0)
                .Set("synced", synced);
            foreach (var l in lines)
            {
                if (synced && l.Time == null) continue;
                var line = lyric.Item("line");
                if (synced) line.Set("start", (long)Math.Max(0, l.Time!.Value.TotalMilliseconds));
                line.Text = l.Text;
            }
        }
        return Reply(root, format, callback);
    }

    private Song? FindByArtistTitle(string? artist, string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string t = SearchQuery.Fold(title), a = SearchQuery.Fold(artist ?? "");
        return Index.Songs.FirstOrDefault(s => SearchQuery.Fold(s.MetaTitle) == t && (a.Length == 0 || SearchQuery.Fold(s.Artist).Contains(a)));
    }

    // ======================================================================
    // Cola: seguir donde se dejó
    // ======================================================================

    private sealed class SavedQueue
    {
        public List<string> Songs { get; set; } = new();
        public string? Current { get; set; }
        public long Position { get; set; }
        public DateTime Changed { get; set; }
        public string ChangedBy { get; set; } = "";
    }

    private static string QueueFile => Path.Combine(SettingsStore.DataDirectory, "movil-cola.json");
    private SavedQueue? _savedQueue;
    private bool _queueLoaded;

    private SavedQueue? LoadQueue()
    {
        if (_queueLoaded) return _savedQueue;
        _queueLoaded = true;
        try
        {
            if (File.Exists(QueueFile)) _savedQueue = JsonSerializer.Deserialize<SavedQueue>(File.ReadAllText(QueueFile));
        }
        catch
        {
            _savedQueue = null;
        }
        return _savedQueue;
    }

    /// <summary>savePlayQueue: la app guarda su cola y por dónde iba (para seguir en otro sitio).</summary>
    private void SaveQueue(SubsonicIndex ix, HttpLite.Request req)
    {
        var songs = req.All("id").Select(ix.SongById).Where(s => s != null).Cast<Song>().ToList();
        var queue = new SavedQueue
        {
            Songs = songs.Select(s => s.Path).ToList(),
            Current = ix.SongById(req["current"])?.Path ?? songs.FirstOrDefault()?.Path,
            Position = long.TryParse(req["position"], out var ms) ? Math.Max(0, ms) : 0,
            Changed = DateTime.Now,
            ChangedBy = req["c"] ?? "móvil",
        };
        _savedQueue = queue;
        _queueLoaded = true;
        try
        {
            File.WriteAllText(QueueFile, JsonSerializer.Serialize(queue));
        }
        catch
        {
            // sin guardar: vale para esta sesión
        }
    }

    /// <summary>
    /// getPlayQueue: lo último que sonó, sea en una app (lo que guardó) o en Echoplex del PC (lo que suena o sonó por última
    /// vez). Así, al abrir la app en el móvil se puede seguir justo donde se dejó en el PC.
    /// </summary>
    private void PlayQueue(SubsonicNode root, SubsonicIndex ix)
    {
        var saved = LoadQueue();
        var pc = _host.PcQueue();
        if (pc != null && (saved == null || pc.Value.changed >= saved.Changed))
        {
            var (current, position, next, changed) = pc.Value;
            var node = root.Child("playQueue").Set("current", ix.IdOf(current)).Set("position", (long)(position * 1000))
                .Set("username", User).Set("changed", changed).Set("changedBy", "Echoplex (PC)").EmptyList("entry");
            node.AddItem(SongNode("entry", current));
            foreach (var s in next) node.AddItem(SongNode("entry", s));
            return;
        }
        if (saved == null) return;
        var songs = _host.Library.Resolve(saved.Songs);
        if (songs.Count == 0) return;
        var q = root.Child("playQueue").Set("position", saved.Position).Set("username", User).Set("changed", saved.Changed)
            .Set("changedBy", saved.ChangedBy).EmptyList("entry");
        if (saved.Current != null && _host.Library.Find(saved.Current) is { } cur) q.Set("current", ix.IdOf(cur));
        foreach (var s in songs) q.AddItem(SongNode("entry", s));
    }

    // ======================================================================
    // Canciones parecidas (la radio de Echoplex)
    // ======================================================================

    /// <summary>getSimilarSongs (id de canción, álbum, artista o carpeta) y getSimilarSongs2 (id de artista).</summary>
    private void SimilarSongs(SubsonicNode node, SubsonicIndex ix, HttpLite.Request req)
    {
        var id = req["id"];
        Song? seed = ix.SongById(id);
        seed ??= ix.AlbumById(id)?.Songs is { Count: > 0 } albumSongs ? albumSongs[Random.Shared.Next(albumSongs.Count)] : null;
        seed ??= ix.ArtistById(id)?.Albums.SelectMany(a => a.Songs).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        seed ??= ix.DirById(id) is { } dir ? _host.Library.SongsUnder(dir).OrderBy(_ => Random.Shared.Next()).FirstOrDefault() : null;
        if (seed == null) return;
        int count = Int(req, "count", 50, 500);
        foreach (var s in _host.Radio(seed, count)) node.AddItem(SongNode("song", s));
    }
}
