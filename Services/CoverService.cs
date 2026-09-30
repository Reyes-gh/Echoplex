using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using Echoplex.Models;

namespace Echoplex.Services;

/// <summary>
/// Busca y decodifica portadas. Orden para una carpeta:
///   1. imágenes de la propia carpeta (cover/folder/front primero) y de sus subcarpetas de arte,
///   2. imágenes de las subcarpetas, en orden natural (el primer disco primero),
///   3. la portada incrustada en las canciones del primer disco que esté en el dispositivo.
/// Una imagen que no se pueda decodificar se salta y se prueba la siguiente.
/// Con allowDownload=false nunca toca imágenes que estén solo en OneDrive.
/// </summary>
public sealed class CoverService
{
    private static readonly Dictionary<string, int> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = 0, [".jpeg"] = 0, [".png"] = 0, [".bmp"] = -1, [".gif"] = -2, [".tif"] = -3, [".tiff"] = -3, [".webp"] = -2,
    };
    private static readonly string[] ArtFolders = { "scans", "scan", "covers", "cover", "artwork", "art", "images" };
    private static readonly string[] Preferred = { "cover", "folder", "front", "portada", "album" };
    private static readonly string[] Avoid = { "back", "cd", "disc", "disk", "inlay", "tray", "inside", "obi" };
    private const int MaxImageAttempts = 12;
    private const int MaxEmbeddedAttempts = 8;

    private readonly ConcurrentDictionary<string, string[]> _dirImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BitmapSource?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(3);

    /// <summary>Carátulas elegidas por el usuario: carpeta → imagen (en %LocalAppData%\Echoplex\covers). Mandan sobre todo lo demás.</summary>
    private Dictionary<string, string> _custom = new(StringComparer.OrdinalIgnoreCase);

    public void SetCustomCovers(IDictionary<string, string>? map)
    {
        _custom = map == null ? new(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        ClearCache();
    }

    private string? CustomFor(string dir) => _custom.TryGetValue(dir, out var f) && File.Exists(f) ? f : null;

    public bool HasCustomCover(string dir) => CustomFor(dir) != null;

    /// <summary>La mejor imagen de una carpeta (sin mirar subcarpetas de discos); la personalizada primero.</summary>
    public string? FindCoverFile(string dir) => CustomFor(dir) ?? ImagesIn(dir).FirstOrDefault();

    /// <summary>Olvida las portadas encontradas (al cambiar de carpeta de música o de carátula personalizada).</summary>
    public void ClearCache()
    {
        _dirImages.Clear();
        _images.Clear();
    }

    /// <summary>
    /// Imágenes candidatas de una carpeta y sus subcarpetas de arte, de mejor a peor. Se ignoran las ocultas
    /// o de sistema: Windows deja en cada disco un AlbumArtSmall.jpg (75×75) y un Folder.jpg (200×200) ocultos.
    /// A igual puntuación gana el archivo más grande (más resolución).
    /// </summary>
    private string[] ImagesIn(string dir) => _dirImages.GetOrAdd(dir, d =>
    {
        try
        {
            var root = new DirectoryInfo(d);
            var files = Visible(root).ToList();
            foreach (var sub in root.EnumerateDirectories())
                if (ArtFolders.Contains(sub.Name.ToLowerInvariant()))
                    files.AddRange(Visible(sub));

            int Score(FileInfo f)
            {
                var n = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                int s = ImageExt[f.Extension];
                if (Preferred.Any(n.Contains)) s += 10;
                if (Avoid.Any(a => n.Contains(a))) s -= 5;
                if (n.Contains("small") || n.Contains("thumb")) s -= 8;
                if (!string.Equals(f.DirectoryName, d, StringComparison.OrdinalIgnoreCase)) s -= 2; // las de la propia carpeta antes que los escaneos
                return s;
            }
            return files.OrderByDescending(Score).ThenByDescending(f => f.Length).ThenBy(f => f.FullName, NaturalComparer.Instance)
                .Select(f => f.FullName).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    });

    private static IEnumerable<string> HiddenImagesIn(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles()
                .Where(f => ImageExt.ContainsKey(f.Extension) && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                .OrderByDescending(f => f.Length).Select(f => f.FullName).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<FileInfo> Visible(DirectoryInfo dir) =>
        dir.EnumerateFiles().Where(f => ImageExt.ContainsKey(f.Extension) && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0);

    /// <summary>Imágenes de la carpeta y luego de sus subcarpetas (primer disco primero).</summary>
    private IEnumerable<string> ImagesUnder(string dir, int depth = 0)
    {
        foreach (var f in ImagesIn(dir)) yield return f;
        if (depth > 6) yield break;
        string[] subs;
        try
        {
            subs = Directory.EnumerateDirectories(dir)
                .Where(s => (new DirectoryInfo(s).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Where(s => !ArtFolders.Contains(Path.GetFileName(s).ToLowerInvariant()))
                .OrderBy(s => s, NaturalComparer.Instance)
                .ToArray();
        }
        catch
        {
            yield break;
        }
        foreach (var sub in subs)
            foreach (var f in ImagesUnder(sub, depth + 1))
                yield return f;
    }

    /// <param name="priority">Lo que el usuario está mirando (cabecera de página, canción actual): no espera a la cola.</param>
    public async Task<BitmapSource?> GetSongCoverAsync(Song song, int size, bool allowDownload = true, bool priority = false)
    {
        if (priority) return await Task.Run(() => FolderCover(song.Directory, new[] { song }, size, allowDownload, recurse: false));
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => FolderCover(song.Directory, new[] { song }, size, allowDownload, recurse: false));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Portada de un álbum / de la carpeta de una canción, sin mirar subcarpetas: imagen de la carpeta o, si no hay,
    /// la incrustada en la primera de <paramref name="songs"/> que esté en el dispositivo (conviene pasar todas las de la carpeta).
    /// </summary>
    public async Task<BitmapSource?> GetAlbumCoverAsync(string dir, IEnumerable<Song> songs, int size, bool allowDownload = true, bool priority = false)
    {
        if (priority) return await Task.Run(() => FolderCover(dir, songs, size, allowDownload, recurse: false));
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => FolderCover(dir, songs, size, allowDownload, recurse: false));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Portada de una carpeta: imagen interna válida o, si no hay, la incrustada del primer disco.</summary>
    public async Task<BitmapSource?> GetFolderCoverAsync(string dir, IEnumerable<Song> songsUnder, int size, bool allowDownload = true, bool priority = false)
    {
        if (priority) return await Task.Run(() => FolderCover(dir, songsUnder, size, allowDownload, recurse: true));
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => FolderCover(dir, songsUnder, size, allowDownload, recurse: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    private BitmapSource? FolderCover(string dir, IEnumerable<Song> songs, int size, bool allowDownload, bool recurse)
    {
        var key = $"folder:{dir}|{size}|{recurse}";
        if (_images.TryGetValue(key, out var cached) && cached != null) return cached;

        // 0. la carátula que eligió el usuario para esta carpeta
        if (CustomFor(dir) is { } custom && LoadFile(custom, size) is { } chosen)
        {
            _images[key] = chosen;
            return chosen;
        }

        BitmapSource? img = null;
        int attempts = 0;
        foreach (var file in recurse ? ImagesUnder(dir) : ImagesIn(dir))
        {
            if (!allowDownload && OneDriveService.IsCloudOnly(file)) continue;
            img = LoadFile(file, size);
            if (img != null || ++attempts >= MaxImageAttempts) break;
        }

        if (img == null)
        {
            // Portada incrustada: solo de canciones que ya estén en el dispositivo (leer una de la nube la descargaría).
            foreach (var song in songs.Where(s => !s.IsCloud).Take(MaxEmbeddedAttempts))
            {
                img = EmbeddedCover(song, size);
                if (img != null) break;
            }
        }

        if (img == null)
        {
            // último recurso: las miniaturas ocultas de Windows (mejor eso que nada)
            foreach (var file in HiddenImagesIn(dir))
            {
                if (!allowDownload && OneDriveService.IsCloudOnly(file)) continue;
                img = LoadFile(file, size);
                if (img != null) break;
            }
        }

        if (img != null) _images[key] = img;
        return img;
    }

    private BitmapSource? EmbeddedCover(Song song, int size)
    {
        var key = $"embedded:{song.Directory}|{size}";
        if (_images.TryGetValue(key, out var cached)) return cached;
        var bytes = MetadataReader.ReadPicture(song.Path);
        var img = bytes != null ? Decode(new MemoryStream(bytes), size) : null;
        if (img != null) _images[key] = img; // si falla, se puede probar con otra canción del mismo disco
        return img;
    }

    private BitmapSource? LoadFile(string path, int size)
    {
        var key = $"file:{path}|{size}";
        if (_images.TryGetValue(key, out var cached)) return cached;
        BitmapSource? img = null;
        try
        {
            img = Decode(new MemoryStream(File.ReadAllBytes(path)), size);
        }
        catch
        {
            // imagen ilegible
        }
        _images[key] = img;
        return img;
    }

    private static BitmapSource? Decode(Stream stream, int size)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = stream;
            bi.DecodePixelWidth = size;
            bi.EndInit();
            bi.Freeze();
            return bi.PixelWidth > 0 ? bi : null;
        }
        catch
        {
            return null;
        }
    }
}
