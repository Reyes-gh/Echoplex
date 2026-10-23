using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Echoplex.Services;

/// <summary>Una carpeta en línea: el nombre que le pone el usuario (el de la raíz en el árbol) y su URL.</summary>
public sealed class OnlineSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>
    /// Conectada: sus canciones se ven y suenan. Desconectada, sigue en el panel lateral (atenuada, con el mundo tachado)
    /// pero no aporta canciones ni se le pide nada a la nube.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Un archivo de una carpeta en línea: dónde está dentro de ella y de dónde se descarga.</summary>
public sealed class OnlineEntry
{
    /// <summary>Ruta dentro de la carpeta en línea, con «\» (como en el disco).</summary>
    public string Path { get; set; } = "";
    public string Url { get; set; } = "";
    /// <summary>Página para abrirlo en el navegador (Drive), si la hay.</summary>
    public string? Web { get; set; }
}

/// <summary>Índice guardado de una carpeta en línea (solo nombres y enlaces: la música nunca se guarda).</summary>
public sealed class OnlineIndex
{
    public DateTime Updated { get; set; }
    public List<OnlineEntry> Files { get; set; } = new();
    /// <summary>Página de cada subcarpeta (ruta dentro → URL), para «Abrir en el navegador».</summary>
    public Dictionary<string, string> Folders { get; set; } = new();
    /// <summary>OneDrive: dirección para preguntar solo por lo que ha cambiado desde este listado (una petición en vez de cientos).</summary>
    public string? Delta { get; set; }
}

/// <summary>
/// Carpetas en línea (una carpeta compartida de Google Drive o de OneDrive, el listado de carpetas de un servidor web o enlaces
/// directos a archivos de audio). Las canciones se reproducen directamente de la nube, sin copiarlas al PC: en la
/// biblioteca tienen una ruta propia bajo %LocalAppData%\Echoplex\online\&lt;id&gt; que no existe en el disco y solo sirve
/// para ordenarlas en carpetas como las demás. Aquí se guarda únicamente el índice (nombres y enlaces).
/// </summary>
public static partial class OnlineLibrary
{
    private static readonly object Lock = new();
    private static List<OnlineSource> _sources = new();
    private static Dictionary<string, OnlineIndex> _indexes = new(StringComparer.OrdinalIgnoreCase);
    // ruta en la biblioteca → archivo en línea (canciones, carátulas y letras)
    private static Dictionary<string, OnlineEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    // carpeta (ruta en la biblioteca) → imágenes y letras que hay en ella
    private static Dictionary<string, List<string>> _extrasByDir = new(StringComparer.OrdinalIgnoreCase);
    // carpeta (ruta en la biblioteca) → sus subcarpetas
    private static Dictionary<string, HashSet<string>> _childDirs = new(StringComparer.OrdinalIgnoreCase);

    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

    public static string BaseDir => Path.Combine(SettingsStore.DataDirectory, "online");

    public static string RootPath(OnlineSource s) => Path.Combine(BaseDir, s.Id);
    private static string IndexFile(string id) => Path.Combine(BaseDir, id + ".json");

    public static IReadOnlyList<OnlineSource> Sources
    {
        get { lock (Lock) return _sources.ToList(); }
    }

    public static IEnumerable<string> RootPaths => Sources.Select(RootPath);

    public static bool IsOnline(string path) =>
        path.StartsWith(LibraryService.WithSeparator(BaseDir), StringComparison.OrdinalIgnoreCase) && path.Length > BaseDir.Length + 1;

    public static OnlineSource? SourceOf(string path)
    {
        if (!IsOnline(path)) return null;
        var rest = path.Substring(BaseDir.Length + 1);
        int slash = rest.IndexOf(System.IO.Path.DirectorySeparatorChar);
        var id = slash < 0 ? rest : rest[..slash];
        lock (Lock) return _sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsOnlineRoot(string path) => SourceOf(path) is { } s && string.Equals(RootPath(s), path, StringComparison.OrdinalIgnoreCase);

    /// <summary>¿Está conectada la carpeta en línea de esta ruta? (las del disco, siempre)</summary>
    public static bool IsConnected(string path) => SourceOf(path) is not { Enabled: false };

    /// <summary>Subcarpetas de una carpeta en línea, según su índice (en el disco no existen).</summary>
    public static IReadOnlyList<string> SubfoldersOf(string dir)
    {
        lock (Lock)
            return _childDirs.TryGetValue(dir, out var l) ? l.ToList() : Array.Empty<string>();
    }

    /// <summary>El nombre que le puso el usuario, si es la raíz de una carpeta en línea.</summary>
    public static string? RootName(string path) => IsOnlineRoot(path) ? SourceOf(path)?.Name : null;

    public static OnlineEntry? Find(string path)
    {
        lock (Lock) return _byPath.GetValueOrDefault(path);
    }

    public static OnlineIndex? IndexOf(OnlineSource s)
    {
        lock (Lock) return _indexes.GetValueOrDefault(s.Id);
    }

    /// <summary>Página web de un archivo o carpeta en línea (para abrirla en el navegador), o la URL de la carpeta.</summary>
    public static string? WebUrl(string path)
    {
        if (Find(path)?.Web is { } web) return web;
        if (SourceOf(path) is not { } s) return null;
        if (string.Equals(RootPath(s), path, StringComparison.OrdinalIgnoreCase)) return s.Url;
        var rel = System.IO.Path.GetRelativePath(RootPath(s), path);
        return IndexOf(s)?.Folders.GetValueOrDefault(rel) ?? s.Url;
    }

    /// <summary>Imágenes (posibles carátulas) de una carpeta en línea, por su ruta en la biblioteca.</summary>
    public static IReadOnlyList<string> ImagesIn(string dir)
    {
        lock (Lock)
            return _extrasByDir.TryGetValue(dir, out var l)
                ? l.Where(p => ImageExtensions.Contains(System.IO.Path.GetExtension(p))).ToList()
                : Array.Empty<string>();
    }

    /// <summary>El .lrc con el mismo nombre que la canción, si lo hay en la carpeta en línea.</summary>
    public static string? LrcFor(string songPath)
    {
        var dir = System.IO.Path.GetDirectoryName(songPath);
        var name = System.IO.Path.GetFileNameWithoutExtension(songPath);
        if (dir == null) return null;
        lock (Lock)
            return _extrasByDir.TryGetValue(dir, out var l)
                ? l.FirstOrDefault(p => System.IO.Path.GetExtension(p).Equals(".lrc", StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(System.IO.Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase))
                : null;
    }

    // carátulas y letras ya bajadas en esta sesión (se piden a varios tamaños): solo en memoria
    private static readonly Dictionary<string, byte[]> Small = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> SmallOrder = new();

    /// <summary>Un archivo pequeño entero (carátula, letra), descargado a memoria.</summary>
    public static byte[]? ReadAllBytes(string path)
    {
        if (Find(path) is not { } e) return null;
        lock (Small)
            if (Small.TryGetValue(e.Url, out var cached)) return cached;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var bytes = RemoteFile.Client.GetByteArrayAsync(e.Url, timeout.Token).GetAwaiter().GetResult();
            lock (Small)
            {
                if (Small.TryAdd(e.Url, bytes)) SmallOrder.Enqueue(e.Url);
                while (SmallOrder.Count > 120) Small.Remove(SmallOrder.Dequeue());
            }
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Abre un archivo en línea para leer sus etiquetas (pidiendo solo los trozos que hagan falta).</summary>
    public static TagLib.File? OpenTags(string path)
    {
        if (Find(path) is not { } e) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var remote = RemoteFile.Open(e.Url, blockSize: 64 * 1024, timeout.Token, name: "etiquetas " + System.IO.Path.GetFileName(path));
        return TagLib.File.Create(new RemoteTagFile(System.IO.Path.GetFileName(path), remote));
    }

    /// <summary>Lo que se reproduce: un flujo que va leyendo de la nube (y bajando por delante para no cortarse).</summary>
    public static Stream OpenStream(string path)
    {
        if (Find(path) is not { } e) throw new FileNotFoundException("La canción ya no está en la carpeta en línea", path);
        return new RemoteStream(RemoteFiles.Get(e.Url, System.IO.Path.GetFileName(path)));
    }

    /// <summary>
    /// Lo ya bajado de la que suena, para analizarla según llega sin pedir nada aparte (null si no está abierta para sonar).
    /// </summary>
    public static DownloadedStream? OpenDownloaded(string path, CancellationToken ct) =>
        Find(path) is { } e && RemoteFiles.Opened(e.Url) is { } file ? new DownloadedStream(file, ct) : null;

    /// <summary>Esta es la que suena: el resto de canciones en línea abiertas dejan de descargarse.</summary>
    public static void Focus(string path)
    {
        if (Find(path) is { } e) RemoteFiles.Focus(e.Url);
    }

    // ======================================================================
    // Configuración e índices
    // ======================================================================

    /// <summary>Carga las carpetas configuradas y sus índices guardados (al abrir: así se ven al momento, sin red).</summary>
    public static void Load(IEnumerable<OnlineSource>? sources)
    {
        var list = (sources ?? Enumerable.Empty<OnlineSource>()).Where(s => s.Id.Length > 0).ToList();
        foreach (var s in list.Where(s => OneDrive.IsLink(s.Url))) OneDrive.Register(s.Url);
        var indexes = new Dictionary<string, OnlineIndex>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in list)
        {
            try
            {
                if (File.Exists(IndexFile(s.Id)) && JsonSerializer.Deserialize<OnlineIndex>(File.ReadAllText(IndexFile(s.Id))) is { } index)
                    indexes[s.Id] = index;
            }
            catch
            {
                // índice dañado: se volverá a leer de internet
            }
        }
        lock (Lock)
        {
            _sources = list;
            _indexes = indexes;
            Rebuild();
        }
    }

    private static void Rebuild()
    {
        var byPath = new Dictionary<string, OnlineEntry>(StringComparer.OrdinalIgnoreCase);
        var extras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var childDirs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _sources)
        {
            if (!_indexes.TryGetValue(s.Id, out var index)) continue;
            var root = RootPath(s);
            foreach (var f in index.Files)
            {
                var path = System.IO.Path.Combine(root, f.Path);
                byPath[path] = f;
                // cada carpeta del camino, colgada de la de encima
                for (var d = System.IO.Path.GetDirectoryName(path); d != null && d.Length > root.Length; d = System.IO.Path.GetDirectoryName(d))
                {
                    var up = System.IO.Path.GetDirectoryName(d)!;
                    if (!childDirs.TryGetValue(up, out var set)) childDirs[up] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (!set.Add(d)) break; // lo de más arriba ya está
                }
                var ext = System.IO.Path.GetExtension(path);
                if (ImageExtensions.Contains(ext) || ext.Equals(".lrc", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = System.IO.Path.GetDirectoryName(path)!;
                    if (!extras.TryGetValue(dir, out var l)) extras[dir] = l = new List<string>();
                    l.Add(path);
                }
            }
        }
        _byPath = byPath;
        _extrasByDir = extras;
        _childDirs = childDirs;
    }

    /// <summary>Canciones de una carpeta en línea (rutas en la biblioteca), para el escaneo de la biblioteca.</summary>
    public static IEnumerable<string> AudioFiles(string root, ISet<string> audioExtensions)
    {
        if (SourceOf(root) is not { Enabled: true } s || IndexOf(s) is not { } index) return Array.Empty<string>();
        return index.Files.Where(f => audioExtensions.Contains(System.IO.Path.GetExtension(f.Path)))
            .Select(f => System.IO.Path.Combine(root, f.Path)).ToList();
    }

    /// <summary>Guarda el índice nuevo de una carpeta. Devuelve si cambió algo (archivos nuevos, quitados o movidos).</summary>
    public static bool SetIndex(OnlineSource s, OnlineIndex index)
    {
        bool changed;
        lock (Lock)
        {
            var old = _indexes.GetValueOrDefault(s.Id);
            changed = old == null || !old.Files.Select(f => f.Path + "|" + f.Url).SequenceEqual(index.Files.Select(f => f.Path + "|" + f.Url));
            _indexes[s.Id] = index;
            Rebuild();
        }
        SaveIndex(s.Id, index);
        return changed;
    }

    private static void SaveIndex(string id, OnlineIndex index)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(IndexFile(id), JsonSerializer.Serialize(index));
        }
        catch
        {
            // sin índice guardado: se volverá a leer al abrir
        }
    }

    /// <summary>
    /// Un índice guardado antes de que hubiera comprobación rápida (sale de un listado completo): se le pone el punto de
    /// partida de ahora en vez de volver a recorrerlo todo. Si el listado completo nunca llegaba a terminar (Echoplex se
    /// cerraba antes), sin esto cada arranque volvía a empezar de cero.
    /// </summary>
    private static async Task<bool> AdoptDeltaAsync(OnlineSource s, OnlineIndex index, CancellationToken ct)
    {
        try
        {
            var root = await OneDriveJsonAsync($"{OneDrive.Api}/shares/{OneDrive.ShareId(s.Url)}/driveitem?$select=id,parentReference", ct, root: true);
            if (root.GetProperty("parentReference").GetProperty("driveId").GetString() is not { } drive
                || root.GetProperty("id").GetString() is not { } id
                || await LatestDeltaAsync(drive, id, ct) is not { } latest)
                return false;
            index.Delta = latest;
            SaveIndex(s.Id, index);
            RemoteFile.Trace?.Invoke($"OneDrive «{s.Name}»: desde ahora los cambios se comprueban en una sola petición");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// ¿Seguro que no ha cambiado nada desde el último listado? OneDrive lo dice en una sola petición (en vez de volver a
    /// recorrer cientos de carpetas al abrir Echoplex); de las demás no se sabe sin listarlas, así que da false.
    /// </summary>
    public static async Task<bool> UnchangedAsync(OnlineSource s, CancellationToken ct = default)
    {
        if (!OneDrive.IsLink(s.Url) || IndexOf(s) is not { } index) return false;
        if (index.Delta is not { } link) return await AdoptDeltaAsync(s, index, ct);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int changes = 0;
            string? next = link, latest = null;
            for (int page = 0; next != null && changes == 0 && page < 50; page++)
            {
                var json = await OneDriveJsonAsync(next, ct, root: false);
                changes += json.GetProperty("value").GetArrayLength();
                if (json.TryGetProperty("@odata.deltaLink", out var d)) latest = d.GetString();
                next = json.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null;
            }
            RemoteFile.Trace?.Invoke(changes == 0
                ? $"OneDrive «{s.Name}»: sin cambios (comprobado en {clock.ElapsedMilliseconds} ms)"
                : $"OneDrive «{s.Name}»: hay cambios, se vuelve a listar");
            if (changes > 0 || latest == null) return false;
            index.Delta = latest;
            SaveIndex(s.Id, index);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RemoteFile.Trace?.Invoke($"OneDrive «{s.Name}»: no se pudo preguntar por los cambios ({ex.Message}), se vuelve a listar");
            return false;
        }
    }

    public static void Forget(OnlineSource s)
    {
        lock (Lock)
        {
            _sources.RemoveAll(x => x.Id == s.Id);
            _indexes.Remove(s.Id);
            if (!_sources.Any(x => x.Url.Trim() == s.Url.Trim())) OneDrive.Unregister(s.Url);
            Rebuild();
        }
        try { File.Delete(IndexFile(s.Id)); } catch { }
        ForgetCovers(s);
    }

    public static void Add(OnlineSource s)
    {
        lock (Lock)
        {
            _sources.RemoveAll(x => x.Id == s.Id);
            _sources.Add(s);
            if (OneDrive.IsLink(s.Url)) OneDrive.Register(s.Url);
            Rebuild();
        }
    }

    // ======================================================================
    // Leer qué hay en una URL
    // ======================================================================

    private static readonly HashSet<string> Wanted = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma", ".ogg", ".opus", ".aiff", ".aif", ".alac", ".mp4",
        ".lrc", ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
    };

    [GeneratedRegex(@"(?:/folders/|[?&]id=|/folderview\?id=)([A-Za-z0-9_-]{10,})")]
    private static partial Regex DriveFolderId();

    [GeneratedRegex(@"(?:/file/d/|/uc\?(?:[^#]*&)?id=|/open\?id=)([A-Za-z0-9_-]{10,})")]
    private static partial Regex DriveFileId();

    [GeneratedRegex(@"[?&]resourcekey=([A-Za-z0-9_-]+)")]
    private static partial Regex ResourceKey();

    [GeneratedRegex(@"<div class=""flip-entry"" id=""entry-([^""]+)""[^>]*>.*?<a href=""([^""]+)""[^>]*>.*?<div class=""flip-entry-title"">([^<]*)</div>", RegexOptions.Singleline)]
    private static partial Regex DriveEntry();

    [GeneratedRegex(@"<a\s[^>]*href\s*=\s*[""']([^""'#]+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    private static string DriveDownload(string id) => $"https://drive.usercontent.google.com/download?id={id}&export=download&confirm=t";

    /// <summary>
    /// Lee el contenido de una URL: una carpeta compartida de Google Drive o de OneDrive (con sus subcarpetas), un archivo de Drive,
    /// el listado de carpetas de un servidor web (con sus subcarpetas) o un enlace directo a un archivo de audio.
    /// </summary>
    /// <param name="progress">Por dónde va: canciones encontradas hasta ahora y la carpeta que está leyendo.</param>
    public static async Task<OnlineIndex> ListAsync(string url, Action<int, string>? progress = null, CancellationToken ct = default)
    {
        url = url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException("Eso no es una dirección web (tiene que empezar por http:// o https://)");

        var index = new OnlineIndex { Updated = DateTime.UtcNow };
        bool drive = uri.Host.EndsWith("drive.google.com", StringComparison.OrdinalIgnoreCase)
                     || uri.Host.EndsWith("docs.google.com", StringComparison.OrdinalIgnoreCase);
        if (OneDrive.IsLink(uri))
        {
            await ListOneDriveAsync(url, index, progress, ct);
            if (index.Files.Count == 0)
                throw new InvalidOperationException("En esa carpeta de OneDrive no hay archivos de audio que Echoplex pueda leer");
        }
        else if (drive && !url.Contains("/file/d/") && DriveFolderId().Match(url) is { Success: true } folder)
        {
            var key = ResourceKey().Match(url) is { Success: true } k ? k.Groups[1].Value : null;
            await ListDriveFolderAsync(folder.Groups[1].Value, key, "", index, progress, 0, ct);
            if (index.Files.Count == 0)
                throw new InvalidOperationException("No se ve ningún archivo: comprueba que la carpeta está compartida como «Cualquier persona con el enlace» y que tiene música");
        }
        else if (drive && DriveFileId().Match(url) is { Success: true } file)
        {
            var id = file.Groups[1].Value;
            var name = await FileNameAsync(DriveDownload(id), ct) ?? id;
            index.Files.Add(new OnlineEntry { Path = Clean(name), Url = DriveDownload(id), Web = $"https://drive.google.com/file/d/{id}/view" });
        }
        else
        {
            await ListWebAsync(uri, uri, "", index, progress, 0, new HashSet<string>(), ct);
            if (index.Files.Count == 0)
                throw new InvalidOperationException("En esa dirección no hay archivos de audio que Echoplex pueda leer");
        }
        if (!index.Files.Any(f => LibraryService.AudioExtensions.Contains(System.IO.Path.GetExtension(f.Path))))
            throw new InvalidOperationException("En esa dirección no hay ninguna canción (solo otros archivos)");
        return index;
    }

    private static async Task ListDriveFolderAsync(string id, string? key, string rel, OnlineIndex index, Action<int, string>? progress, int depth, CancellationToken ct)
    {
        if (depth > 12) return;
        progress?.Invoke(SongsIn(index), rel);
        var url = $"https://drive.google.com/embeddedfolderview?id={id}" + (key != null ? $"&resourcekey={key}" : "");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await RemoteFile.Client.GetAsync(url, timeout.Token);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Drive no deja ver la carpeta: compártela como «Cualquier persona con el enlace»");
        resp.EnsureSuccessStatusCode();
        var html = await resp.Content.ReadAsStringAsync(timeout.Token);
        if (!html.Contains("flip-entries") && !html.Contains("flip-entry"))
        {
            if (depth == 0) throw new InvalidOperationException("Drive no deja ver la carpeta: compártela como «Cualquier persona con el enlace»");
            return;
        }
        if (depth == 0) index.Folders[""] = $"https://drive.google.com/drive/folders/{id}";
        foreach (Match m in DriveEntry().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[2].Value);
            var title = WebUtility.HtmlDecode(m.Groups[3].Value).Trim();
            if (title.Length == 0) continue;
            if (href.Contains("/folders/") || href.Contains("folderview"))
            {
                if (DriveFolderId().Match(href) is not { Success: true } sub) continue;
                var subKey = ResourceKey().Match(href) is { Success: true } k ? k.Groups[1].Value : null;
                var subRel = rel.Length == 0 ? Clean(title) : rel + "\\" + Clean(title);
                index.Folders[subRel] = $"https://drive.google.com/drive/folders/{sub.Groups[1].Value}";
                await ListDriveFolderAsync(sub.Groups[1].Value, subKey, subRel, index, progress, depth + 1, ct);
            }
            else if (DriveFileId().Match(href) is { Success: true } f && Wanted.Contains(System.IO.Path.GetExtension(title)))
            {
                var fileId = f.Groups[1].Value;
                index.Files.Add(new OnlineEntry
                {
                    Path = rel.Length == 0 ? Clean(title) : rel + "\\" + Clean(title),
                    Url = DriveDownload(fileId),
                    Web = $"https://drive.google.com/file/d/{fileId}/view",
                });
            }
        }
        progress?.Invoke(SongsIn(index), rel);
    }

    /// <summary>Listado de carpetas de un servidor web (Apache, nginx…): enlaces a archivos y a subcarpetas de debajo.</summary>
    private static async Task ListWebAsync(Uri baseUri, Uri uri, string rel, OnlineIndex index, Action<int, string>? progress, int depth,
        HashSet<string> seen, CancellationToken ct)
    {
        if (depth > 12 || !seen.Add(uri.AbsoluteUri) || seen.Count > 2000) return;
        // un enlace directo a un archivo de audio
        var ext = System.IO.Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath));
        if (Wanted.Contains(ext) && depth == 0)
        {
            index.Files.Add(new OnlineEntry { Path = Clean(Uri.UnescapeDataString(System.IO.Path.GetFileName(uri.AbsolutePath))), Url = uri.AbsoluteUri, Web = uri.AbsoluteUri });
            return;
        }
        progress?.Invoke(SongsIn(index), rel);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await RemoteFile.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        resp.EnsureSuccessStatusCode();
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && depth == 0)
        {
            var name = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                       ?? Uri.UnescapeDataString(System.IO.Path.GetFileName(uri.AbsolutePath));
            index.Files.Add(new OnlineEntry { Path = Clean(name), Url = uri.AbsoluteUri, Web = uri.AbsoluteUri });
            return;
        }
        if (!type.Contains("html", StringComparison.OrdinalIgnoreCase)) return;
        if (depth == 0) index.Folders[""] = uri.AbsoluteUri;
        var html = await resp.Content.ReadAsStringAsync(timeout.Token);
        var basePath = baseUri.AbsolutePath.EndsWith('/') ? baseUri.AbsolutePath : baseUri.AbsolutePath[..(baseUri.AbsolutePath.LastIndexOf('/') + 1)];
        foreach (Match m in Href().Matches(html))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value);
            if (href.StartsWith('?') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(uri, href, out var target) || target.Host != uri.Host) continue;
            // solo lo que cuelga de la carpeta pedida (nada de «subir un nivel» ni otras partes del sitio)
            if (!target.AbsolutePath.StartsWith(basePath, StringComparison.Ordinal) || target.AbsolutePath.Length <= uri.AbsolutePath.Length
                || !target.AbsolutePath.StartsWith(uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/", StringComparison.Ordinal))
                continue;
            var name = Uri.UnescapeDataString(target.AbsolutePath.TrimEnd('/').Split('/')[^1]);
            if (name.Length == 0 || !string.IsNullOrEmpty(target.Query)) continue;
            if (target.AbsolutePath.EndsWith('/'))
            {
                var subRel = rel.Length == 0 ? Clean(name) : rel + "\\" + Clean(name);
                index.Folders[subRel] = target.AbsoluteUri;
                await ListWebAsync(baseUri, target, subRel, index, progress, depth + 1, seen, ct);
            }
            else if (Wanted.Contains(System.IO.Path.GetExtension(name)))
            {
                index.Files.Add(new OnlineEntry { Path = rel.Length == 0 ? Clean(name) : rel + "\\" + Clean(name), Url = target.AbsoluteUri, Web = target.AbsoluteUri });
            }
        }
    }

    private static int SongsIn(OnlineIndex index) =>
        index.Files.Count(f => LibraryService.AudioExtensions.Contains(System.IO.Path.GetExtension(f.Path)));

    /// <summary>Nombre del archivo que da el servidor al descargarlo (cabecera Content-Disposition).</summary>
    private static async Task<string?> FileNameAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await RemoteFile.Client.SendAsync(req, timeout.Token);
            var cd = resp.Content.Headers.ContentDisposition;
            return cd?.FileNameStar ?? cd?.FileName?.Trim('"');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Nombre válido como nombre de archivo o carpeta en Windows (para la ruta en la biblioteca).</summary>
    private static string Clean(string name)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }
}
