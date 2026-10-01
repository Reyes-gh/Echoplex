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
    // las portadas grandes (cabeceras, reproductor: ~1,6 MB cada una) no se guardan todas para siempre:
    // solo las más recientes; si se vuelve a pedir una más antigua, se decodifica otra vez del mismo archivo
    private const int LargeSize = 500, MaxLarge = 80;
    private readonly ConcurrentQueue<string> _largeKeys = new();

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
        _largeKeys.Clear();
    }

    private void Remember(string key, BitmapSource? img, int size)
    {
        bool isNew = !_images.ContainsKey(key);
        _images[key] = img;
        if (size < LargeSize || img == null || !isNew) return;
        _largeKeys.Enqueue(key);
        while (_largeKeys.Count > MaxLarge && _largeKeys.TryDequeue(out var oldest)) _images.TryRemove(oldest, out _);
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

    /// <summary>
    /// Portada de una canción. Si su carpeta tiene imagen (o carátula elegida), esa: el álbum de siempre. Si no, la incrustada
    /// en la propia canción, para que en una carpeta de canciones sueltas cada una lleve la suya. Si la canción está solo
    /// en la nube o no trae imagen, la incrustada en otra de <paramref name="siblings"/> del mismo álbum que esté en el PC.
    /// </summary>
    /// <param name="deepImages">Mirar también imágenes de subcarpetas (cabecera de la página de un álbum).</param>
    /// <param name="priority">Lo que el usuario está mirando (cabecera de página, canción actual): no espera a la cola.</param>
    public async Task<BitmapSource?> GetSongCoverAsync(Song song, IEnumerable<Song> siblings, int size, bool allowDownload = true, bool priority = false, bool deepImages = false)
    {
        if (priority) return await Task.Run(() => SongCover(song, siblings, size, allowDownload, deepImages));
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => SongCover(song, siblings, size, allowDownload, deepImages));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>La incrustada en esta canción y nada más (null si no trae o está solo en la nube).</summary>
    public async Task<BitmapSource?> GetEmbeddedCoverAsync(Song song, int size, bool priority = false)
    {
        if (song.IsCloud) return null;
        if (priority) return await Task.Run(() => EmbeddedCover(song, size));
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() => EmbeddedCover(song, size));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bytes de la portada de una canción para los controles multimedia de Windows (imagen de la carpeta o incrustada).</summary>
    public byte[]? CoverBytes(Song song)
    {
        try
        {
            if (FindCoverFile(song.Directory) is { } file) return File.ReadAllBytes(file);
        }
        catch
        {
            // imagen ilegible: se prueba la incrustada
        }
        return song.IsCloud ? null : MetadataReader.ReadPicture(song.Path);
    }

    private BitmapSource? SongCover(Song song, IEnumerable<Song> siblings, int size, bool allowDownload, bool deepImages)
    {
        var dir = song.Directory;
        if (CustomFor(dir) is { } custom && LoadFile(custom, size) is { } chosen) return chosen;
        if (FirstImage(deepImages ? ImagesUnder(dir) : ImagesIn(dir), size, allowDownload) is { } img) return img;
        var sameAlbum = siblings.Where(s => s != song && string.Equals(s.Album.Trim(), song.Album.Trim(), StringComparison.OrdinalIgnoreCase));
        foreach (var s in new[] { song }.Concat(sameAlbum).Where(s => !s.IsCloud).Take(MaxEmbeddedAttempts))
            if (EmbeddedCover(s, size) is { } embedded) return embedded;
        return FirstImage(HiddenImagesIn(dir), size, allowDownload, MaxImageAttempts);
    }

    private BitmapSource? FirstImage(IEnumerable<string> files, int size, bool allowDownload, int maxAttempts = MaxImageAttempts)
    {
        int attempts = 0;
        foreach (var file in files)
        {
            if (!allowDownload && OneDriveService.IsCloudOnly(file)) continue;
            if (LoadFile(file, size) is { } img) return img;
            if (++attempts >= maxAttempts) break;
        }
        return null;
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
            Remember(key, chosen, size);
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

        if (img != null) Remember(key, img, size);
        return img;
    }

    private BitmapSource? EmbeddedCover(Song song, int size)
    {
        var key = $"embedded:{song.Path}|{size}"; // por canción: en una carpeta de sueltas cada una trae la suya
        if (_images.TryGetValue(key, out var cached)) return cached;
        var bytes = MetadataReader.ReadPicture(song.Path);
        var img = bytes != null ? Decode(new MemoryStream(bytes), size) : null;
        if (img != null) Remember(key, img, size); // si falla, se puede probar con otra canción del mismo álbum
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
        Remember(key, img, size);
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
