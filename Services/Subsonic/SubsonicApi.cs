using Echoplex.Models;

namespace Echoplex.Services.Subsonic;

/// <summary>Las llamadas de la API que devuelven datos (en el hilo de la interfaz, con el índice al día).</summary>
public sealed partial class SubsonicServer
{
    private const string IgnoredArticles = "The El La Los Las Le Les Die Der Das";
    private (Song song, DateTime at, string client)? _nowPlaying;

    private SubsonicNode Api(string method, HttpLite.Request req)
    {
        var ix = Index;
        var root = SubsonicWriter.Ok();
        switch (method)
        {
            case "ping":
                break;
            case "getlicense":
                root.Child("license").Set("valid", true);
                break;
            case "getopensubsonicextensions":
                root.Item("openSubsonicExtensions").Set("name", "formPost").AddItem(new SubsonicNode("versions") { Text = "1" });
                root.Item("openSubsonicExtensions").Set("name", "transcodeOffset").AddItem(new SubsonicNode("versions") { Text = "1" });
                root.Item("openSubsonicExtensions").Set("name", "songLyrics").AddItem(new SubsonicNode("versions") { Text = "1" });
                break;
            case "getmusicfolders":
            {
                var folders = root.Child("musicFolders").EmptyList("musicFolder");
                for (int i = 0; i < ix.Roots.Count; i++)
                    folders.Item("musicFolder").Set("id", i + 1).Set("name", LibraryService.RootName(ix.Roots[i]));
                break;
            }
            case "getuser":
            case "getusers":
            {
                var user = method == "getusers" ? root.Child("users").Item("user") : root.Child("user");
                user.Set("username", User).Set("scrobblingEnabled", true).Set("adminRole", false).Set("settingsRole", false)
                    .Set("downloadRole", true).Set("uploadRole", false).Set("playlistRole", true).Set("coverArtRole", true)
                    .Set("commentRole", false).Set("podcastRole", false).Set("streamRole", true).Set("jukeboxRole", false)
                    .Set("shareRole", false).Set("videoConversionRole", false);
                for (int i = 0; i < ix.Roots.Count; i++) user.AddItem(new SubsonicNode("folder") { Text = (i + 1).ToString() });
                break;
            }
            case "getscanstatus":
            case "startscan":
                root.Child("scanStatus").Set("scanning", false).Set("count", ix.Songs.Count);
                break;

            // ---------- por etiquetas (ID3) ----------
            case "getartists":
                Artists(root.Child("artists").Set("ignoredArticles", IgnoredArticles), ix, FilterFolder(ix, req));
                break;
            case "getartist":
            {
                var artist = ix.ArtistById(req["id"]);
                if (artist == null) return NotFound("artista");
                var node = ArtistNode("artist", artist);
                foreach (var a in artist.Albums) node.AddItem(AlbumNode("album", a));
                root.Add(node);
                break;
            }
            case "getalbum":
            {
                var album = ix.AlbumById(req["id"]);
                if (album == null) return NotFound("álbum");
                var node = AlbumNode("album", album);
                foreach (var s in album.Songs) node.AddItem(SongNode("song", s));
                root.Add(node);
                break;
            }
            case "getsong":
            {
                var song = ix.SongById(req["id"]);
                if (song == null) return NotFound("canción");
                root.Add(SongNode("song", song));
                break;
            }
            case "getalbumlist2":
            case "getalbumlist":
            {
                bool id3 = method == "getalbumlist2";
                var list = root.Child(id3 ? "albumList2" : "albumList").EmptyList("album");
                var albums = AlbumList(ix, req);
                if (albums == null) return SubsonicWriter.Error(10, "Falta o no vale el tipo de lista (type)");
                foreach (var a in albums) list.AddItem(id3 ? AlbumNode("album", a) : AlbumAsDir(a));
                break;
            }
            case "getrandomsongs":
            {
                var songs = FilterFolder(ix, req).SelectMany(a => a.Songs).Where(s => MatchesGenreYear(s, req)).ToList();
                var list = root.Child("randomSongs").EmptyList("song");
                foreach (var s in songs.OrderBy(_ => Random.Shared.Next()).Take(Int(req, "size", 10, 500))) list.AddItem(SongNode("song", s));
                break;
            }
            case "getgenres":
            {
                var genres = root.Child("genres").EmptyList("genre");
                foreach (var g in ix.Songs.Where(s => s.Genre.Length > 0).GroupBy(s => s.Genre.Trim(), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, NaturalComparer.Instance))
                    genres.AddItem(new SubsonicNode("genre") { Text = g.First().Genre.Trim() })
                        .Set("songCount", g.Count()).Set("albumCount", g.Select(LibraryService.AlbumKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                break;
            }
            case "getsongsbygenre":
            {
                var genre = req["genre"] ?? "";
                var list = root.Child("songsByGenre").EmptyList("song");
                foreach (var s in ix.Songs.Where(s => string.Equals(s.Genre.Trim(), genre.Trim(), StringComparison.OrdinalIgnoreCase))
                             .Skip(Int(req, "offset", 0, int.MaxValue)).Take(Int(req, "count", 10, 500)))
                    list.AddItem(SongNode("song", s));
                break;
            }
            case "gettopsongs":
            {
                // las más escuchadas de un artista, según lo que lleva contado Echoplex
                var name = req["artist"] ?? "";
                var list = root.Child("topSongs").EmptyList("song");
                foreach (var s in ix.Songs.Where(s => string.Equals(s.ArtistKey, name, StringComparison.OrdinalIgnoreCase) || string.Equals(s.PrimaryArtist, name, StringComparison.OrdinalIgnoreCase))
                             .Select(s => (s, n: _host.UserData.GetStat(s.Path)?.Count ?? 0)).Where(x => x.n > 0).OrderByDescending(x => x.n)
                             .Take(Int(req, "count", 50, 500)))
                    list.AddItem(SongNode("song", s.s));
                break;
            }
            case "search3":
            case "search2":
                Search(root.Child(method == "search3" ? "searchResult3" : "searchResult2"), ix, req, id3: method == "search3");
                break;

            // ---------- por carpetas ----------
            case "getindexes":
                Indexes(root.Child("indexes").Set("ignoredArticles", IgnoredArticles).Set("lastModified", new DateTimeOffset(ix.Built).ToUnixTimeMilliseconds()), ix, req);
                break;
            case "getmusicdirectory":
            {
                var dir = ix.DirById(req["id"]);
                if (dir == null) return NotFound("carpeta");
                root.Add(DirectoryNode(ix, dir));
                break;
            }

            // ---------- favoritas ----------
            case "getstarred2":
            case "getstarred":
            {
                var starred = root.Child(method == "getstarred2" ? "starred2" : "starred").EmptyList("song").EmptyList("album").EmptyList("artist");
                foreach (var path in _host.UserData.Favorites)
                    if (_host.Library.Find(path) is { } s) starred.AddItem(SongNode("song", s));
                break;
            }
            case "star":
            case "unstar":
            {
                bool value = method == "star";
                foreach (var id in req.All("id"))
                    if (ix.SongById(id) is { } s) _host.UserData.SetFavorite(s, value);
                // Echoplex guarda favoritas de canciones; las estrellas de álbumes y artistas se aceptan sin más
                break;
            }
            case "setrating":
                break;

            // ---------- playlists ----------
            case "getplaylists":
            {
                var list = root.Child("playlists").EmptyList("playlist");
                foreach (var p in _host.UserData.Playlists) list.AddItem(PlaylistNode(p, withSongs: false));
                break;
            }
            case "getplaylist":
            {
                var p = PlaylistById(req["id"]);
                if (p == null) return NotFound("playlist");
                root.Add(PlaylistNode(p, withSongs: true));
                break;
            }
            case "createplaylist":
            {
                var songs = req.All("songId").Select(ix.SongById).Where(s => s != null).Cast<Song>().ToList();
                Playlist p;
                if (req["playlistId"] is { } existing)
                {
                    p = PlaylistById(existing) ?? throw new InvalidOperationException("No existe esa playlist");
                    if (p.IsSmart) return SubsonicWriter.Error(50, "Es una playlist inteligente: se rellena sola");
                    _host.UserData.SetSongs(p, songs.Select(s => s.Path).ToList());
                    if (req["name"] is { Length: > 0 } rename) _host.UserData.RenamePlaylist(p, rename);
                }
                else
                {
                    p = _host.UserData.CreatePlaylist(req["name"] is { Length: > 0 } n ? n : "Playlist nueva", songs);
                }
                root.Add(PlaylistNode(p, withSongs: true));
                break;
            }
            case "updateplaylist":
            {
                var p = PlaylistById(req["playlistId"]);
                if (p == null) return NotFound("playlist");
                if (req["name"] is { Length: > 0 } name) _host.UserData.RenamePlaylist(p, name);
                if (p.IsSmart)
                {
                    if (req.All("songIdToAdd").Count > 0 || req.All("songIndexToRemove").Count > 0)
                        return SubsonicWriter.Error(50, "Es una playlist inteligente: se rellena sola");
                    break;
                }
                var remove = req.All("songIndexToRemove").Select(x => int.TryParse(x, out var i) ? i : -1).Where(i => i >= 0).ToHashSet();
                if (remove.Count > 0)
                    _host.UserData.SetSongs(p, p.Songs.Where((_, i) => !remove.Contains(i)).ToList());
                var add = req.All("songIdToAdd").Select(ix.SongById).Where(s => s != null).Cast<Song>().ToList();
                if (add.Count > 0) _host.UserData.AddToPlaylist(p, add);
                break;
            }
            case "deleteplaylist":
            {
                var p = PlaylistById(req["id"]);
                if (p == null) return NotFound("playlist");
                _host.UserData.DeletePlaylist(p);
                break;
            }

            // ---------- escuchas ----------
            case "scrobble":
                Scrobble(ix, req);
                break;
            case "getnowplaying":
            {
                var list = root.Child("nowPlaying").EmptyList("entry");
                if (_nowPlaying is { } np && DateTime.Now - np.at < TimeSpan.FromMinutes(15))
                    list.AddItem(SongNode("entry", np.song)).Set("username", User).Set("minutesAgo", (int)(DateTime.Now - np.at).TotalMinutes)
                        .Set("playerId", 1).Set("playerName", np.client);
                break;
            }

            // ---------- lo que Echoplex no tiene: respuestas vacías para que las apps no se quejen ----------
            case "getplayqueue":
                PlayQueue(root, ix);
                break;
            case "saveplayqueue":
                SaveQueue(ix, req);
                break;
            case "createbookmark":
            case "deletebookmark":
                break;
            case "getartistinfo":
            case "getartistinfo2":
                root.Child(method == "getartistinfo" ? "artistInfo" : "artistInfo2");
                break;
            case "getalbuminfo":
            case "getalbuminfo2":
                root.Child("albumInfo");
                break;
            case "getsimilarsongs":
            case "getsimilarsongs2":
                SimilarSongs(root.Child(method == "getsimilarsongs" ? "similarSongs" : "similarSongs2").EmptyList("song"), ix, req);
                break;
            case "getpodcasts":
                root.Child("podcasts").EmptyList("channel");
                break;
            case "getnewestpodcasts":
                root.Child("newestPodcasts").EmptyList("episode");
                break;
            case "getinternetradiostations":
                root.Child("internetRadioStations").EmptyList("internetRadioStation");
                break;
            case "getbookmarks":
                root.Child("bookmarks").EmptyList("bookmark");
                break;
            case "getshares":
                root.Child("shares").EmptyList("share");
                break;
            case "getchatmessages":
                root.Child("chatMessages").EmptyList("chatMessage");
                break;
            default:
                Log($"{req.Remote} {req["c"]} pidió «{method}», que Echoplex aún no sabe hacer");
                return SubsonicWriter.Error(0, $"Echoplex aún no sabe hacer «{method}»");
        }
        return root;
    }

    private static SubsonicNode NotFound(string what) => SubsonicWriter.Error(70, $"No existe ese {what}");

    private static int Int(HttpLite.Request req, string key, int fallback, int max) =>
        int.TryParse(req[key], out var v) ? Math.Clamp(v, 0, max) : fallback;

    // ======================================================================
    // Elementos
    // ======================================================================

    private SubsonicNode SongNode(string name, Song s)
    {
        var ix = Index;
        var stat = _host.UserData.GetStat(s.Path);
        double seconds = s.Duration?.TotalSeconds ?? 0;
        var albumId = ix.AlbumIdOf(s);
        var n = new SubsonicNode(name)
            .Set("id", ix.IdOf(s))
            .Set("parent", ix.DirId(s.Directory))
            .Set("isDir", false)
            .Set("title", s.Title)
            .Set("album", s.Album)
            .Set("artist", s.Artist);
        if (s.TrackNumber > 0) n.Set("track", (int)s.TrackNumber);
        if (s.Year > 0) n.Set("year", (int)s.Year);
        n.Set("genre", s.Genre.Trim())
            .Set("coverArt", albumId ?? ix.IdOf(s))
            .Set("size", s.Length)
            .Set("contentType", ContentType(s.Path))
            .Set("suffix", Path.GetExtension(s.Path).TrimStart('.').ToLowerInvariant());
        if (seconds > 0)
        {
            n.Set("duration", (int)Math.Round(seconds));
            n.Set("bitRate", (int)Math.Round(s.Length * 8 / seconds / 1000));
        }
        n.Set("path", s.RelativePath.Replace('\\', '/'))
            .Set("isVideo", false);
        if (stat is { Count: > 0 }) n.Set("playCount", stat.Count).Set("played", stat.Last);
        if (s.DiscNumber > 0) n.Set("discNumber", (int)s.DiscNumber);
        var created = s.CreatedTicks > 0 ? new DateTime(s.CreatedTicks) : DateTime.Now;
        n.Set("created", created);
        if (s.IsFavorite) n.Set("starred", created); // Echoplex no guarda cuándo: basta con que esté
        n.Set("albumId", albumId)
            .Set("artistId", ix.ArtistOf(s)?.Id)
            .Set("type", "music")
            .Set("mediaType", "song");
        // el volumen que Echoplex conoce (etiquetas ReplayGain o medido), para que la app normalice igual que el PC
        var (tg, tp, ag, ap) = Normalization.Raw(s, ix.AlbumById(albumId)?.Songs ?? new List<Song>());
        if (tg != null || ag != null)
        {
            var rg = n.Child("replayGain");
            if (tg is { } t) rg.Set("trackGain", Math.Round(t, 2));
            if (ag is { } a) rg.Set("albumGain", Math.Round(a, 2));
            if (tp is { } p) rg.Set("trackPeak", Math.Round(p, 6));
            if (ap is { } q) rg.Set("albumPeak", Math.Round(q, 6));
        }
        return n;
    }

    private SubsonicNode AlbumNode(string name, AlbumInfo a)
    {
        var ix = Index;
        var id = ix.AlbumIdOf(a);
        double seconds = a.Songs.Sum(s => s.Duration?.TotalSeconds ?? 0);
        var stats = a.Songs.Select(s => _host.UserData.GetStat(s.Path)).Where(st => st != null).ToList();
        var genre = a.Songs.Where(s => s.Genre.Length > 0).GroupBy(s => s.Genre.Trim(), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var n = new SubsonicNode(name)
            .Set("id", id)
            .Set("name", a.Name.Length > 0 ? a.Name : Path.GetFileName(a.Directory))
            .Set("title", a.Name)
            .Set("album", a.Name)
            .Set("artist", a.Artist)
            .Set("artistId", ix.ArtistOf(a)?.Id)
            .Set("coverArt", id)
            .Set("songCount", a.Songs.Count)
            .Set("duration", (int)Math.Round(seconds))
            .Set("created", new DateTime(a.Songs.Min(s => s.CreatedTicks)))
            .Set("isDir", true)
            .Set("parent", ix.DirId(Path.GetDirectoryName(a.Directory) ?? a.Directory));
        if (a.Year > 0) n.Set("year", (int)a.Year);
        n.Set("genre", genre);
        if (stats.Count > 0) n.Set("playCount", stats.Sum(st => st!.Count)).Set("played", stats.Max(st => st!.Last));
        return n;
    }

    /// <summary>Un álbum como carpeta (para las llamadas «por carpetas»: getAlbumList, search2).</summary>
    private SubsonicNode AlbumAsDir(AlbumInfo a)
    {
        var ix = Index;
        return new SubsonicNode("album")
            .Set("id", ix.DirId(a.Directory))
            .Set("parent", ix.DirId(Path.GetDirectoryName(a.Directory) ?? a.Directory))
            .Set("isDir", true)
            .Set("title", a.Name.Length > 0 ? a.Name : Path.GetFileName(a.Directory))
            .Set("album", a.Name)
            .Set("artist", a.Artist)
            .Set("coverArt", ix.AlbumIdOf(a));
    }

    private static SubsonicNode ArtistNode(string name, SubsonicIndex.ArtistEntry a) =>
        new SubsonicNode(name).Set("id", a.Id).Set("name", a.Name).Set("coverArt", a.Id).Set("albumCount", a.Albums.Count);

    private SubsonicNode PlaylistNode(Playlist p, bool withSongs)
    {
        var songs = _host.Library.Resolve(p.Songs);
        var n = new SubsonicNode("playlist")
            .Set("id", SubsonicIndex.PlaylistId(p))
            .Set("name", p.Name)
            .Set("comment", p.IsSmart ? "Inteligente: se rellena sola en Echoplex" : null)
            .Set("owner", User)
            .Set("public", false)
            .Set("songCount", songs.Count)
            .Set("duration", (int)Math.Round(songs.Sum(s => s.Duration?.TotalSeconds ?? 0)))
            .Set("created", p.Created)
            .Set("changed", p.Created)
            .Set("coverArt", SubsonicIndex.PlaylistCoverId(p))
            .Set("readonly", p.IsSmart);
        if (withSongs)
        {
            n.EmptyList("entry");
            foreach (var s in songs) n.AddItem(SongNode("entry", s));
        }
        return n;
    }

    private Playlist? PlaylistById(string? id) =>
        id == null ? null : _host.UserData.Playlists.FirstOrDefault(p => SubsonicIndex.PlaylistId(p) == id || p.Id == id);

    // ======================================================================
    // Listas
    // ======================================================================

    /// <summary>Álbumes de una carpeta de música (musicFolderId) o de todas.</summary>
    private static IEnumerable<AlbumInfo> FilterFolder(SubsonicIndex ix, HttpLite.Request req) =>
        int.TryParse(req["musicFolderId"], out var folder) ? ix.Albums.Where(a => ix.FolderNumber(a.Directory) == folder) : ix.Albums;

    private static void Artists(SubsonicNode node, SubsonicIndex ix, IEnumerable<AlbumInfo> albums)
    {
        node.EmptyList("index");
        var keep = albums.ToHashSet();
        foreach (var group in ix.Artists.Where(a => a.Albums.Any(keep.Contains))
                     .GroupBy(a => IndexLetter(a.Name))
                     .OrderBy(g => g.Key == "#" ? "~" : g.Key, StringComparer.Ordinal))
        {
            var index = node.Item("index").Set("name", group.Key);
            foreach (var artist in group.OrderBy(a => SortName(a.Name), NaturalComparer.Instance))
                index.AddItem(ArtistNode("artist", artist)).Set("albumCount", artist.Albums.Count(keep.Contains));
        }
    }

    /// <summary>Sin «The», «El», «Los»… delante, para ordenar y agrupar por letra.</summary>
    private static string SortName(string name)
    {
        foreach (var article in IgnoredArticles.Split(' '))
            if (name.Length > article.Length + 1 && name.StartsWith(article + " ", StringComparison.OrdinalIgnoreCase))
                return name[(article.Length + 1)..];
        return name;
    }

    private static string IndexLetter(string name)
    {
        var folded = SearchQuery.Fold(SortName(name));
        return folded.Length > 0 && char.IsLetter(folded[0]) ? char.ToUpperInvariant(folded[0]).ToString() : "#";
    }

    private List<AlbumInfo>? AlbumList(SubsonicIndex ix, HttpLite.Request req)
    {
        var albums = FilterFolder(ix, req);
        DateTime LastPlayed(AlbumInfo a) => a.Songs.Select(s => _host.UserData.GetStat(s.Path)?.Last ?? DateTime.MinValue).DefaultIfEmpty(DateTime.MinValue).Max();
        int Plays(AlbumInfo a) => a.Songs.Sum(s => _host.UserData.GetStat(s.Path)?.Count ?? 0);
        IEnumerable<AlbumInfo>? sorted = req["type"] switch
        {
            "random" => albums.OrderBy(_ => Random.Shared.Next()),
            "newest" => albums.OrderByDescending(a => a.Songs.Max(s => s.CreatedTicks)),
            "frequent" or "highest" => albums.Select(a => (a, n: Plays(a))).Where(x => x.n > 0).OrderByDescending(x => x.n).Select(x => x.a),
            "recent" => albums.Select(a => (a, t: LastPlayed(a))).Where(x => x.t > DateTime.MinValue).OrderByDescending(x => x.t).Select(x => x.a),
            "alphabeticalByName" => albums.OrderBy(a => a.Name, NaturalComparer.Instance),
            "alphabeticalByArtist" => albums.OrderBy(a => SortName(a.Artist), NaturalComparer.Instance).ThenBy(a => a.Year).ThenBy(a => a.Name, NaturalComparer.Instance),
            "starred" => albums.Where(a => a.Songs.Count > 0 && a.Songs.All(s => s.IsFavorite)),
            "byYear" => ByYear(albums, req),
            "byGenre" => albums.Where(a => a.Songs.Any(s => string.Equals(s.Genre.Trim(), (req["genre"] ?? "").Trim(), StringComparison.OrdinalIgnoreCase))),
            _ => null,
        };
        return sorted?.Skip(Int(req, "offset", 0, int.MaxValue)).Take(Int(req, "size", 10, 500)).ToList();
    }

    private static IEnumerable<AlbumInfo> ByYear(IEnumerable<AlbumInfo> albums, HttpLite.Request req)
    {
        int from = Int(req, "fromYear", 0, 9999), to = Int(req, "toYear", 9999, 9999);
        return from <= to
            ? albums.Where(a => a.Year >= from && a.Year <= to).OrderBy(a => a.Year)
            : albums.Where(a => a.Year <= from && a.Year >= to).OrderByDescending(a => a.Year);
    }

    private static bool MatchesGenreYear(Song s, HttpLite.Request req)
    {
        if (req["genre"] is { Length: > 0 } g && !string.Equals(s.Genre.Trim(), g.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (int.TryParse(req["fromYear"], out var from) && s.Year < from) return false;
        if (int.TryParse(req["toYear"], out var to) && (s.Year == 0 || s.Year > to)) return false;
        return true;
    }

    /// <summary>
    /// Buscar, con la búsqueda que perdona de Echoplex. Sin texto (o «""») devuelve todo, por páginas: así sincronizan
    /// la biblioteca entera algunas apps.
    /// </summary>
    private void Search(SubsonicNode node, SubsonicIndex ix, HttpLite.Request req, bool id3)
    {
        node.EmptyList("artist").EmptyList("album").EmptyList("song");
        var text = (req["query"] ?? "").Trim().Trim('"').Trim();
        var albums = FilterFolder(ix, req).ToList();
        var albumSet = albums.ToHashSet();
        var artists = ix.Artists.Where(a => a.Albums.Any(albumSet.Contains));
        var songs = albums.SelectMany(a => a.Songs);
        var q = new SearchQuery(text);
        if (!q.IsEmpty)
        {
            artists = artists.Select(a => (a, sc: q.Score((a.Name, 3)))).Where(x => x.sc > 0).OrderByDescending(x => x.sc).Select(x => x.a);
            albums = albums.Select(a => (a, sc: q.Score((a.Name, 3), (a.Artist, 2)))).Where(x => x.sc > 0).OrderByDescending(x => x.sc).Select(x => x.a).ToList();
            songs = songs.Select(s => (s, sc: q.Score((s.Title, 3), (s.Artist, 2), (s.Album, 1.5), (s.Genre, 1)))).Where(x => x.sc > 0)
                .OrderByDescending(x => x.sc).Select(x => x.s);
        }
        foreach (var a in artists.Skip(Int(req, "artistOffset", 0, int.MaxValue)).Take(Int(req, "artistCount", 20, 5000)))
            node.AddItem(id3 ? ArtistNode("artist", a) : new SubsonicNode("artist").Set("id", a.Id).Set("name", a.Name));
        foreach (var a in albums.Skip(Int(req, "albumOffset", 0, int.MaxValue)).Take(Int(req, "albumCount", 20, 5000)))
            node.AddItem(id3 ? AlbumNode("album", a) : AlbumAsDir(a));
        foreach (var s in songs.Skip(Int(req, "songOffset", 0, int.MaxValue)).Take(Int(req, "songCount", 20, 5000)))
            node.AddItem(SongNode("song", s));
    }

    /// <summary>Las carpetas de primer nivel de cada carpeta de música, por letras (y las canciones sueltas de la raíz).</summary>
    private void Indexes(SubsonicNode node, SubsonicIndex ix, HttpLite.Request req)
    {
        node.EmptyList("index");
        var roots = int.TryParse(req["musicFolderId"], out var f) && f >= 1 && f <= ix.Roots.Count ? new[] { ix.Roots[f - 1] } : ix.Roots.ToArray();
        var dirs = roots.SelectMany(r => _host.Library.GetChildren(r)).ToList();
        foreach (var group in dirs.GroupBy(d => IndexLetter(Path.GetFileName(d))).OrderBy(g => g.Key == "#" ? "~" : g.Key, StringComparer.Ordinal))
        {
            var index = node.Item("index").Set("name", group.Key);
            foreach (var d in group.OrderBy(d => SortName(Path.GetFileName(d)), NaturalComparer.Instance))
                index.Item("artist").Set("id", ix.DirId(d)).Set("name", Path.GetFileName(d));
        }
        foreach (var r in roots)
            foreach (var s in SortSongs(_host.Library.SongsIn(r))) node.AddItem(SongNode("child", s));
    }

    private SubsonicNode DirectoryNode(SubsonicIndex ix, string dir)
    {
        var node = new SubsonicNode("directory").Set("id", ix.DirId(dir)).Set("name", ix.Roots.Contains(dir, StringComparer.OrdinalIgnoreCase) ? LibraryService.RootName(dir) : Path.GetFileName(dir));
        if (!ix.Roots.Contains(dir, StringComparer.OrdinalIgnoreCase) && Path.GetDirectoryName(dir) is { } parent) node.Set("parent", ix.DirId(parent));
        node.EmptyList("child");
        foreach (var sub in _host.Library.GetChildren(dir).OrderBy(d => Path.GetFileName(d), NaturalComparer.Instance))
        {
            var first = _host.Library.SongsUnder(sub).FirstOrDefault();
            node.Item("child").Set("id", ix.DirId(sub)).Set("parent", ix.DirId(dir)).Set("isDir", true).Set("title", Path.GetFileName(sub))
                .Set("artist", first?.Artist).Set("coverArt", ix.DirId(sub));
        }
        foreach (var s in SortSongs(_host.Library.SongsIn(dir))) node.AddItem(SongNode("child", s));
        return node;
    }

    private static IEnumerable<Song> SortSongs(IEnumerable<Song> songs) => songs
        .OrderBy(s => s.DiscNumber == 0 ? uint.MaxValue : s.DiscNumber)
        .ThenBy(s => s.TrackNumber == 0 ? uint.MaxValue : s.TrackNumber)
        .ThenBy(s => s.FileName, NaturalComparer.Instance);

    // ======================================================================
    // Escuchas desde el móvil: cuentan en Echoplex (Tu resumen, historial) y van a Last.fm / ListenBrainz
    // ======================================================================

    private void Scrobble(SubsonicIndex ix, HttpLite.Request req)
    {
        bool submission = !string.Equals(req["submission"], "false", StringComparison.OrdinalIgnoreCase);
        var times = req.All("time");
        var ids = req.All("id");
        for (int i = 0; i < ids.Count; i++)
        {
            if (ix.SongById(ids[i]) is not { } song) continue;
            var at = i < times.Count && long.TryParse(times[i], out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.Now;
            var listen = Scrobbler.ListenOf(song, at);
            if (submission)
            {
                _host.UserData.RecordPlay(song);
                if (song.Duration is { } d) _host.UserData.RecordListen(song, d.TotalSeconds);
                if (listen != null && (_host.Scrobbler.LastFmOn || _host.Scrobbler.ListenBrainzOn)) _host.Scrobbler.Scrobble(listen);
                Log($"escuchada en el móvil: {Path.GetFileName(song.Path)}");
            }
            else
            {
                _nowPlaying = (song, DateTime.Now, req["c"] ?? "móvil");
                if (listen != null && (_host.Scrobbler.LastFmOn || _host.Scrobbler.ListenBrainzOn)) _host.Scrobbler.NowPlaying(listen);
            }
        }
    }
}
