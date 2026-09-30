using System.Text.RegularExpressions;
using Echoplex.Models;

namespace Echoplex.Services;

public sealed record AlbumInfo(string Key, string Name, string Artist, string Directory, List<Song> Songs);
public sealed record ArtistInfo(string Name, List<Song> Songs);

/// <summary>
/// Índice de todos los archivos de audio bajo las carpetas de música (sin leer su contenido).
/// Puede haber varias carpetas raíz; <see cref="AllKey"/> es la carpeta virtual "Toda tu música" que las reúne.
/// </summary>
public sealed partial class LibraryService
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus", ".wma", ".aiff", ".aif"
    };

    /// <summary>Clave de "Toda tu música" (no es una ruta real: sus hijas son las carpetas raíz).</summary>
    public const string AllKey = "::todo";

    private Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Song> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public LibraryService(IEnumerable<string> roots) => Roots = Clean(roots);

    /// <summary>Carpetas de música configuradas (existan o no).</summary>
    public IReadOnlyList<string> Roots { get; private set; }
    public List<Song> Songs { get; private set; } = new();

    public static string Normalize(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>Normaliza, quita duplicados y las carpetas que ya están dentro de otra de la lista.</summary>
    public static List<string> Clean(IEnumerable<string> roots)
    {
        var list = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Where(r => !list.Any(o => !Eq(o, r) && IsUnder(r, o))).ToList();
    }

    public static bool IsUnder(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public bool IsRoot(string path) => Roots.Any(r => Eq(r, path));

    /// <summary>La carpeta de música que contiene esta ruta.</summary>
    public string? RootOf(string path) => Roots.FirstOrDefault(r => Eq(r, path) || IsUnder(path, r));

    /// <summary>Ruta para mostrar: "Raíz / sub / carpeta" (con el nombre de su carpeta de música delante si hay varias).</summary>
    public string DisplayPath(string path)
    {
        var root = RootOf(path);
        if (root == null) return path;
        var parts = new List<string>();
        if (Roots.Count > 1 || Eq(root, path)) parts.Add(Path.GetFileName(root));
        if (!Eq(root, path)) parts.AddRange(Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar));
        return string.Join(" / ", parts);
    }

    [GeneratedRegex(@"^(?:\d{1,2}[-.])?(?:(?<n>\d{2,3})(?:\s*[-._)]\s*|\s+)|(?<n>\d)\s*[-._)]\s*)")]
    private static partial Regex TrackPrefix();

    public static bool IsCloudOnly(FileAttributes a) =>
        ((int)a & (0x400000 /* RECALL_ON_DATA_ACCESS */ | 0x40000 /* RECALL_ON_OPEN */ | (int)FileAttributes.Offline)) != 0;

    /// <summary>
    /// Recorre las carpetas de música (o una lista nueva, si se indica) y sustituye el índice de golpe al terminar,
    /// así la interfaz nunca ve datos a medias durante un reescaneo. Las carpetas que no existen se saltan.
    /// </summary>
    public void Load(MetadataCache cache, IEnumerable<string>? newRoots = null)
    {
        var roots = newRoots != null ? Clean(newRoots) : Roots.ToList();
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };

        var list = new List<Song>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var present = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            present.Add(root);
            counts[root] = 0; // la raíz existe aunque aún no tenga canciones
            var songs = new List<Song>();
            foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", opts))
            {
                if (!AudioExtensions.Contains(fi.Extension)) continue;
                var song = new Song(fi.FullName, Path.GetRelativePath(root, fi.FullName), fi.Length,
                    fi.LastWriteTimeUtc.Ticks, fi.CreationTimeUtc.Ticks, IsCloudOnly(fi.Attributes));
                ApplyFallback(song, root);
                if (cache.TryGet(song, out var meta)) ApplyMeta(song, meta);
                songs.Add(song);
            }
            songs.Sort((a, b) =>
            {
                int c = NaturalComparer.Instance.Compare(a.Directory, b.Directory);
                return c != 0 ? c : NaturalComparer.Instance.Compare(a.FileName, b.FileName);
            });

            foreach (var s in songs)
            {
                var dir = s.Directory;
                while (true)
                {
                    counts[dir] = counts.GetValueOrDefault(dir) + 1;
                    if (Eq(dir, root)) break;
                    var parent = Path.GetDirectoryName(dir);
                    if (parent == null) break;
                    if (!children.TryGetValue(parent, out var kids)) children[parent] = kids = new List<string>();
                    if (!kids.Contains(dir, StringComparer.OrdinalIgnoreCase)) kids.Add(dir);
                    dir = parent;
                }
            }
            list.AddRange(songs); // en el orden de las carpetas de música
        }
        foreach (var kids in children.Values)
            kids.Sort((a, b) => NaturalComparer.Instance.Compare(Path.GetFileName(a), Path.GetFileName(b)));
        children[AllKey] = present;
        counts[AllKey] = list.Count;
        var byPath = list.GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        _counts = counts;
        _children = children;
        _byPath = byPath;
        Songs = list;
        Roots = roots;
    }

    /// <summary>Carpetas de música que existen ahora mismo (las hijas de "Toda tu música").</summary>
    public IReadOnlyList<string> PresentRoots => GetChildren(AllKey);

    public Song? Find(string path) => _byPath.GetValueOrDefault(path);

    public List<Song> Resolve(IEnumerable<string> paths) => paths.Select(Find).Where(s => s != null).Cast<Song>().ToList();

    public int GetCount(string dir) => _counts.GetValueOrDefault(dir);

    public bool HasFolder(string dir) => _counts.ContainsKey(dir);

    public IReadOnlyList<string> GetChildren(string dir) =>
        _children.TryGetValue(dir, out var kids) ? kids : Array.Empty<string>();

    public List<Song> SongsUnder(string dir)
    {
        if (Eq(dir, AllKey)) return Songs.ToList();
        var prefix = dir + Path.DirectorySeparatorChar;
        return Songs.Where(s => s.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static string AlbumKey(Song s) => s.Directory + "|" + s.Album.ToLowerInvariant();

    /// <summary>Álbumes: canciones de la misma carpeta con el mismo nombre de álbum.</summary>
    public List<AlbumInfo> GetAlbums() => Songs
        .GroupBy(AlbumKey, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            // por número de pista de los metadatos, o por nombre de archivo si esa carpeta usa nombres de fichero
            var songs = (g.First().UseFileName
                ? g.OrderBy(s => s.FileName, NaturalComparer.Instance)
                : g.OrderBy(s => s.TrackNumber == 0 ? uint.MaxValue : s.TrackNumber).ThenBy(s => s.FileName, NaturalComparer.Instance)).ToList();
            var artist = songs.GroupBy(s => s.PrimaryArtist).OrderByDescending(x => x.Count()).First().Key;
            return new AlbumInfo(g.Key, songs[0].Album, artist, songs[0].Directory, songs);
        })
        .OrderBy(a => a.Name, NaturalComparer.Instance)
        .ToList();

    public AlbumInfo? GetAlbum(string key) => GetAlbums().FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));

    public List<ArtistInfo> GetArtists() => Songs
        .GroupBy(s => s.PrimaryArtist, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Key.Length > 0)
        .Select(g => new ArtistInfo(g.First().PrimaryArtist, g.ToList()))
        .OrderBy(a => a.Name, NaturalComparer.Instance)
        .ToList();

    public List<Song> SongsByArtist(string name) =>
        Songs.Where(s => string.Equals(s.PrimaryArtist, name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Título/artista/álbum deducidos de la ruta, para archivos sin etiquetas leídas.</summary>
    private static void ApplyFallback(Song s, string root)
    {
        var name = Path.GetFileNameWithoutExtension(s.FileName);
        var m = TrackPrefix().Match(name);
        if (m.Success && m.Length < name.Length)
        {
            if (uint.TryParse(m.Groups["n"].Value, out var n)) s.TrackNumber = n;
            name = name[m.Length..];
        }
        s.Title = name.Trim();

        var rel = Path.GetRelativePath(root, s.Directory);
        var parts = rel == "." ? Array.Empty<string>() : rel.Split(Path.DirectorySeparatorChar);
        s.Artist = parts.Length > 0 ? parts[0] : "Artista desconocido";
        s.Album = parts.Length > 0 ? parts[^1] : "Música";
    }

    public static void ApplyMeta(Song s, CachedMeta m)
    {
        if (!string.IsNullOrWhiteSpace(m.T)) s.Title = m.T.Trim();
        if (!string.IsNullOrWhiteSpace(m.A)) s.Artist = m.A.Trim();
        if (!string.IsNullOrWhiteSpace(m.B)) s.Album = m.B.Trim();
        if (m.N > 0) s.TrackNumber = m.N;
        if (m.Ms > 0) s.Duration = TimeSpan.FromMilliseconds(m.Ms);
        s.MetadataLoaded = true;
    }
}
