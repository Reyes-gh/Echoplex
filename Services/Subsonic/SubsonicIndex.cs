using System.Security.Cryptography;
using System.Text;
using Echoplex.Models;

namespace Echoplex.Services.Subsonic;

/// <summary>
/// La biblioteca vista por Subsonic: identificadores fijos (los mismos en cada arranque, sacados de la ruta) para
/// canciones, álbumes, artistas y carpetas. Se hace en el hilo de la interfaz, de una vez, y se rehace cuando cambia la
/// biblioteca.
/// </summary>
public sealed class SubsonicIndex
{
    public sealed record ArtistEntry(string Id, string Name, List<AlbumInfo> Albums)
    {
        public int SongCount => Albums.Sum(a => a.Songs.Count);
    }

    public IReadOnlyList<Song> Songs { get; }
    public IReadOnlyList<AlbumInfo> Albums { get; }
    public IReadOnlyList<ArtistEntry> Artists { get; }
    public IReadOnlyList<string> Roots { get; }
    public DateTime Built { get; } = DateTime.Now;

    private readonly Dictionary<string, Song> _songById = new();
    private readonly Dictionary<Song, string> _idOfSong = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, AlbumInfo> _albumById = new();
    private readonly Dictionary<string, string> _albumIdByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ArtistEntry> _artistById = new();
    private readonly Dictionary<string, ArtistEntry> _artistByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _dirById = new();

    public SubsonicIndex(LibraryService library)
    {
        Songs = library.Songs.ToList();
        foreach (var s in Songs)
        {
            var id = SongId(s.Path);
            _songById[id] = s;
            _idOfSong[s] = id;
        }
        Albums = library.GetAlbums();
        foreach (var a in Albums)
        {
            var id = Id("al", a.Key);
            _albumById[id] = a;
            _albumIdByKey[a.Key] = id;
        }
        Artists = Albums.GroupBy(a => a.Artist, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .Select(g => new ArtistEntry(Id("ar", g.Key), g.First().Artist, g.OrderBy(a => a.Year == 0 ? uint.MaxValue : a.Year).ThenBy(a => a.Name, NaturalComparer.Instance).ToList()))
            .OrderBy(a => a.Name, NaturalComparer.Instance)
            .ToList();
        foreach (var a in Artists)
        {
            _artistById[a.Id] = a;
            _artistByName[a.Name] = a;
        }
        Roots = library.PresentRoots.ToList();
    }

    // ---------- identificadores ----------

    private static string Id(string kind, string key) =>
        kind + "-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant())))[..16].ToLowerInvariant();

    public static string SongId(string path) => Id("tr", path);
    public string DirId(string dir) { var id = Id("dr", dir); _dirById.TryAdd(id, dir); return id; }
    public static string PlaylistId(Playlist p) => "pl-" + p.Id;

    /// <summary>
    /// La carátula de una playlist: su id más una huella de sus canciones. Las apps guardan las carátulas por su dirección;
    /// así, cuando la portada cambia (canciones nuevas, el mosaico), la dirección cambia y la vuelven a pedir.
    /// </summary>
    public static string PlaylistCoverId(Playlist p) =>
        PlaylistId(p) + "-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("mosaico|" + string.Join("|", p.Songs.Take(200)).ToLowerInvariant())))[..8].ToLowerInvariant();

    public string IdOf(Song s) => _idOfSong.TryGetValue(s, out var id) ? id : SongId(s.Path);
    public string? AlbumIdOf(Song s) => _albumIdByKey.GetValueOrDefault(LibraryService.AlbumKey(s));
    public string AlbumIdOf(AlbumInfo a) => _albumIdByKey[a.Key];
    public ArtistEntry? ArtistOf(AlbumInfo a) => _artistByName.GetValueOrDefault(a.Artist);
    public ArtistEntry? ArtistOf(Song s) =>
        _albumIdByKey.TryGetValue(LibraryService.AlbumKey(s), out var aid) && _albumById.TryGetValue(aid, out var album) ? ArtistOf(album) : null;

    public Song? SongById(string? id) => id != null && _songById.TryGetValue(id, out var s) ? s : null;
    public AlbumInfo? AlbumById(string? id) => id != null && _albumById.TryGetValue(id, out var a) ? a : null;
    public ArtistEntry? ArtistById(string? id) => id != null && _artistById.TryGetValue(id, out var a) ? a : null;

    /// <summary>La carpeta de un id «dr-…» (o la carpeta de música n.º <paramref name="id"/> si es un número).</summary>
    public string? DirById(string? id)
    {
        if (id == null) return null;
        if (int.TryParse(id, out var n)) return n >= 1 && n <= Roots.Count ? Roots[n - 1] : null;
        if (_dirById.TryGetValue(id, out var dir)) return dir;
        // aún no se había nombrado: se busca entre las carpetas de la biblioteca
        foreach (var d in Songs.Select(s => s.Directory).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var cur = d; cur != null && cur.Length > 0; cur = Path.GetDirectoryName(cur))
            {
                if (DirId(cur) == id) return cur;
                if (Roots.Any(r => string.Equals(r, cur, StringComparison.OrdinalIgnoreCase))) break;
            }
        }
        return null;
    }

    /// <summary>La carpeta de música (1, 2…) en la que está una ruta, para filtrar por musicFolderId.</summary>
    public int FolderNumber(string path)
    {
        for (int i = 0; i < Roots.Count; i++)
            if (string.Equals(path, Roots[i], StringComparison.OrdinalIgnoreCase) || LibraryService.IsUnder(path, Roots[i])) return i + 1;
        return 0;
    }
}
