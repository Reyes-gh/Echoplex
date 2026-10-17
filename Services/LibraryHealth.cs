using Echoplex.Models;

namespace Echoplex.Services;

/// <summary>Un tipo de problema de la biblioteca y las canciones que lo tienen.</summary>
public sealed record HealthIssue(string Key, string Glyph, string Title, string Description, List<Song> Songs, List<string?>? Extras,
    int Count, string Unit, bool GroupByFolder)
{
    public bool HasSongs => Count > 0;
    public string CountText => Count == 0 ? "Todo bien" : $"{Count:N0} {Unit}";
}

/// <summary>
/// Revisa la biblioteca: canciones repetidas, álbumes sin carátula, etiquetas que faltan, archivos que no se pueden leer
/// y carpetas vacías. Solo mira: no cambia, mueve ni borra nada. Las etiquetas salen de la caché de Echoplex (no se
/// releen los archivos); para las carátulas incrustadas sí se abre una canción por carpeta, salvo las que están solo en
/// la nube (no se descargan) y las de carpetas en línea.
/// </summary>
public static class LibraryHealth
{
    public sealed record Entry(Song Song, CachedMeta? Meta);

    public sealed record Report(List<HealthIssue> Issues, List<string> EmptyFolders, int Unread);

    public static Report Analyze(IReadOnlyList<Entry> entries, IReadOnlyList<string> roots, Func<string, string?> coverFile,
        Action<string>? progress, CancellationToken ct)
    {
        var issues = new List<HealthIssue>();
        var known = entries.Where(e => e.Meta is { V: >= CachedMeta.Current }).ToList();
        int unread = entries.Count - known.Count;

        // --- repetidas: mismo artista y título (sin acentos ni signos) y casi la misma duración
        var dupSongs = new List<Song>();
        var dupExtras = new List<string?>();
        int dupGroups = 0;
        foreach (var g in entries
                     .Where(e => e.Song.Artist.Length > 0 && e.Song.MetaTitle.Length > 0)
                     .GroupBy(e => SearchQuery.Fold(e.Song.PrimaryArtist) + "\n" + SearchQuery.Fold(e.Song.MetaTitle))
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // dentro del grupo, por duración: una versión en directo de 7 minutos no es la misma que la de 4
            var byLength = new List<List<Song>>();
            foreach (var s in g.Select(e => e.Song).OrderBy(s => s.Duration?.TotalSeconds ?? 0))
            {
                var last = byLength.LastOrDefault();
                double d = s.Duration?.TotalSeconds ?? 0, prev = last?[^1].Duration?.TotalSeconds ?? -100;
                if (last != null && (d == 0 || prev == 0 || Math.Abs(d - prev) <= 3)) last.Add(s);
                else byLength.Add(new List<Song> { s });
            }
            foreach (var copies in byLength.Where(c => c.Count > 1))
            {
                dupGroups++;
                foreach (var s in copies)
                {
                    dupSongs.Add(s);
                    dupExtras.Add($"{copies.Count} copias");
                }
            }
        }
        issues.Add(new HealthIssue("duplicates", "", "Canciones repetidas",
            "El mismo artista y título con casi la misma duración, en más de un archivo. A veces es a propósito (un recopilatorio, una edición especial).",
            dupSongs, dupExtras, dupGroups, dupGroups == 1 ? "canción" : "canciones", false));
        ct.ThrowIfCancellationRequested();

        // --- archivos que no se pudieron leer y etiquetas que faltan (solo las ya leídas)
        bool Unreadable(CachedMeta m) => m.Ms == 0 && string.IsNullOrWhiteSpace(m.T) && string.IsNullOrWhiteSpace(m.A) && string.IsNullOrWhiteSpace(m.B);
        var bad = known.Where(e => Unreadable(e.Meta!)).Select(e => e.Song).ToList();
        var readable = known.Where(e => !Unreadable(e.Meta!)).ToList();
        void Add(string key, string glyph, string title, string description, Func<CachedMeta, bool> missing)
        {
            var songs = readable.Where(e => missing(e.Meta!)).Select(e => e.Song).ToList();
            issues.Add(new HealthIssue(key, glyph, title, description, songs, null, songs.Count, songs.Count == 1 ? "canción" : "canciones", true));
        }
        issues.Add(new HealthIssue("unreadable", "", "Archivos que no se pueden leer",
            "Echoplex no entiende su formato o están dañados: puede que no suenen. Prueba a abrirlos con otro programa.",
            bad, null, bad.Count, bad.Count == 1 ? "archivo" : "archivos", true));
        Add("notags", "", "Sin título o sin artista",
            "Les falta el título o el artista en las etiquetas: Echoplex los saca del nombre del archivo y de la carpeta.",
            m => string.IsNullOrWhiteSpace(m.T) || string.IsNullOrWhiteSpace(m.A));
        Add("noalbum", "", "Sin álbum", "No dicen de qué disco son: se agrupan por el nombre de su carpeta.",
            m => string.IsNullOrWhiteSpace(m.B));
        Add("notrack", "", "Sin número de pista", "Son de un álbum pero no dicen su posición: el disco se ordena por el nombre del archivo.",
            m => m.N == 0 && !string.IsNullOrWhiteSpace(m.B));
        Add("noyear", "", "Sin año", "No salen ordenadas por años en la página de su artista.", m => m.Y == 0);
        Add("nogenre", "", "Sin género", "No salen en Explorar → Géneros.", m => string.IsNullOrWhiteSpace(m.G));
        ct.ThrowIfCancellationRequested();

        // --- álbumes sin carátula (ni imagen en la carpeta ni incrustada); solo las del PC ya descargadas
        var dirs = entries.Select(e => e.Song)
            .Where(s => !OnlineLibrary.IsOnline(s.Path))
            .GroupBy(s => s.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var noCover = new List<Song>();
        int noCoverDirs = 0, done = 0;
        foreach (var d in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 25 == 0) progress?.Invoke($"Revisando carátulas: {done:N0} de {dirs.Count:N0} carpetas…");
            if (coverFile(d.Key) != null) continue;
            var album = LibraryService.AlbumDirectory(d.Key);
            if (!string.Equals(album, d.Key, StringComparison.OrdinalIgnoreCase) && coverFile(album) != null) continue;
            var local = d.FirstOrDefault(s => !s.IsCloud);
            if (local == null) continue; // solo en la nube: abrirla la descargaría
            if (!MetadataReader.TryReadPicture(local.Path, out var pic) || pic is { Length: > 0 }) continue;
            noCoverDirs++;
            noCover.AddRange(d.OrderBy(s => s.DiscNumber).ThenBy(s => s.TrackNumber));
        }
        issues.Add(new HealthIssue("nocover", "", "Álbumes sin carátula",
            "Ni imagen en la carpeta ni carátula dentro de los archivos. Puedes ponerle una con el clic derecho en la portada de la carpeta.",
            noCover, null, noCoverDirs, noCoverDirs == 1 ? "carpeta" : "carpetas", true));

        // --- carpetas vacías (sin ningún archivo dentro, tampoco en sus subcarpetas); solo la de más arriba
        progress?.Invoke("Buscando carpetas vacías…");
        var empty = new List<string>();
        foreach (var root in roots.Where(r => !OnlineLibrary.IsOnline(r) && Directory.Exists(r)))
        {
            ct.ThrowIfCancellationRequested();
            IsEmpty(root, empty, ct);
        }
        return new Report(issues, empty, unread);
    }

    private static readonly EnumerationOptions Options = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };

    /// <summary>
    /// true si la carpeta no tiene ningún archivo, tampoco en sus subcarpetas (quien la llama la apunta, o a su madre si
    /// también está vacía). Si no, apunta en <paramref name="empty"/> sus subcarpetas vacías de más arriba.
    /// </summary>
    private static bool IsEmpty(string dir, List<string> empty, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool hasFiles;
        string[] subs;
        try
        {
            hasFiles = Directory.EnumerateFiles(dir, "*", Options).Any();
            subs = Directory.GetDirectories(dir, "*", Options);
        }
        catch
        {
            return false;
        }
        var found = new List<string>();
        bool allSubsEmpty = true;
        foreach (var sub in subs)
        {
            bool link;
            try { link = new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint); } catch { link = true; }
            if (link) { allSubsEmpty = false; continue; } // enlaces: no se siguen
            if (IsEmpty(sub, found, ct)) found.Add(sub);
            else allSubsEmpty = false;
        }
        if (!hasFiles && allSubsEmpty) return true;
        empty.AddRange(found);
        return false;
    }
}
