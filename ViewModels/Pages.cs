using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;

namespace Echoplex.ViewModels;

public abstract class PageBase : ObservableObject
{
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Breadcrumb { get; init; } = "";
    public virtual bool HasFilter => false;
}

public sealed record PropertyItem(string Glyph, string Label, string Value);

public enum CardKind { Folder, Album, Artist, Playlist, Song, Favorites, AllMusic, History, Recent }

public sealed partial class CardVm : ObservableObject
{
    public CardVm(CardKind kind, string key, string title, string subtitle, string glyph = "")
    {
        Kind = kind;
        Key = key;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
    }

    public CardKind Kind { get; }
    public string Key { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public Song? Song { get; init; }
    public bool AllowDownload { get; init; }
    public bool CoverRequested { get; set; }

    [ObservableProperty] private BitmapSource? _cover;
}

public sealed class SongRow
{
    private readonly int? _displayNumber;

    public SongRow(Song song, int index, string? extra, int? displayNumber = null)
    {
        Song = song;
        Index = index;
        Extra = extra;
        _displayNumber = displayNumber;
    }

    public Song Song { get; }
    /// <summary>Posición en la lista visible (la que se usa para reproducir).</summary>
    public int Index { get; }
    /// <summary>Número mostrado: dentro de su módulo cuando la lista está agrupada.</summary>
    public int Number => _displayNumber ?? Index + 1;
    public string? Extra { get; }
}

/// <summary>Cabecera de módulo en mitad de la lista: un disco / subcarpeta con su portada.</summary>
public sealed partial class GroupHeader : ObservableObject
{
    public GroupHeader(string directory, string kicker, string title, List<Song> songs)
    {
        Directory = directory;
        Kicker = kicker;
        Title = title;
        Songs = songs;
        long ticks = songs.Where(s => s.Duration.HasValue).Sum(s => s.Duration!.Value.Ticks);
        var count = songs.Count == 1 ? "1 canción" : $"{songs.Count} canciones";
        if (ticks == 0)
        {
            Subtitle = count;
        }
        else
        {
            var t = TimeSpan.FromTicks(ticks);
            Subtitle = count + " · " + (t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min");
        }
    }

    public string Directory { get; }
    public string Kicker { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public List<Song> Songs { get; }
    public bool CoverRequested { get; set; }

    [ObservableProperty] private BitmapSource? _cover;
}

public sealed class ListHeader
{
    public ListHeader(SongListPage page) => Page = page;
    public SongListPage Page { get; }
}

public enum ListKind { Folder, Playlist, Favorites, History, Recent, Artist, Album, Search }

/// <summary>Página tipo "base de datos" de Notion: cabecera con propiedades y tabla de canciones.</summary>
public sealed partial class SongListPage : PageBase
{
    private readonly ListHeader _header;
    private readonly List<string?>? _extras;
    private readonly Dictionary<string, GroupHeader> _groups = new(StringComparer.OrdinalIgnoreCase);

    public SongListPage(string id, ListKind kind, List<Song> songs, List<string?>? extras = null)
    {
        Id = id;
        Kind = kind;
        Songs = songs;
        _extras = extras;
        _header = new ListHeader(this);
        // La lista se construye en EnsureBuilt(): aquí aún no se han aplicado las propiedades init
        // (GroupByFolder, GroupRoot…) y la agrupación saldría desactivada.
    }

    private bool _built;

    /// <summary>Construye la lista la primera vez que se muestra la página.</summary>
    public void EnsureBuilt()
    {
        if (_built) return;
        Rebuild();
        RefreshProperties();
    }

    public string Id { get; }
    public ListKind Kind { get; }
    public List<Song> Songs { get; }
    public List<Song> Visible { get; private set; } = new();
    public override bool HasFilter => true;

    public string? Description { get; init; }
    public string? FolderPath { get; init; }

    /// <summary>Carpeta a la que pertenece la carátula de la cabecera (carpeta o álbum): se le puede poner una a mano.</summary>
    public string? CoverFolder { get; init; }
    public bool CanSetCover => CoverFolder != null;

    /// <summary>Carpeta real (no "Toda tu música"): lleva el interruptor "Nombres fichero".</summary>
    public bool IsRealFolder => FolderPath != null && FolderPath != Services.LibraryService.AllKey;

    /// <summary>Esta carpeta muestra nombres de fichero (propio o heredado del padre / ajuste general).</summary>
    [ObservableProperty] private bool _fileNamesOn;

    /// <summary>Carpeta con «Solo favoritas»: la lista son solo sus favoritas (con las de sus subcarpetas).</summary>
    public bool FavoritesOnly { get; init; }
    public Playlist? Playlist { get; init; }
    public string? AlbumKey { get; init; }
    public string? ArtistName { get; init; }
    public string? ExtraHeader { get; init; }
    public bool ShowExtra => ExtraHeader != null;
    public List<PropertyItem> BaseProperties { get; init; } = new();

    /// <summary>Divide la lista en módulos por carpeta (disco) cuando no hay un orden aplicado.</summary>
    public bool GroupByFolder { get; init; }
    /// <summary>Carpeta de referencia para mostrar la ruta de cada módulo.</summary>
    public string? GroupRoot { get; init; }
    /// <summary>Usar el nombre de álbum como título del módulo (página de artista).</summary>
    public bool GroupTitleFromAlbum { get; init; }

    /// <summary>Todo lo que se reproduce/descarga desde la cabecera (p. ej. una carpeta con subcarpetas).</summary>
    public List<Song>? PlayAll { get; init; }
    public List<Song> AllSongs => PlayAll ?? Songs;
    public bool HasRows => Songs.Count > 0;
    public bool IsPlaylist => Kind == ListKind.Playlist;
    public bool CanDownload => Kind is not ListKind.History and not ListKind.Search;
    public bool IsEmpty => AllSongs.Count == 0;
    /// <summary>La fila de botones de la cabecera: sin canciones no sale, salvo con «Solo favoritas» (para poder quitarlo).</summary>
    public bool ShowActions => !IsEmpty || FavoritesOnly;
    public bool ShowDownload => CanDownload && !IsEmpty;
    public string EmptyText => Kind switch
    {
        ListKind.Folder when FavoritesOnly => "No hay favoritas en esta carpeta ni en sus subcarpetas. Apaga «Solo favoritas» para ver todas sus canciones.",
        ListKind.Playlist => "Esta playlist está vacía. Haz clic derecho en cualquier canción → Añadir a playlist.",
        ListKind.Favorites => "Aún no tienes favoritas. Pulsa el corazón de una canción para guardarla aquí.",
        ListKind.History => "Todavía no has escuchado nada.",
        _ => "No hay canciones.",
    };

    public ObservableCollection<PropertyItem> Properties { get; } = new();

    [ObservableProperty] private IList<object> _items = new List<object>();
    [ObservableProperty] private BitmapSource? _cover;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string? _sortKey;
    [ObservableProperty] private bool _sortDescending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph), nameof(PlayText))]
    private bool _isThisPlaying;

    public string PlayGlyph => IsThisPlaying ? "" : "";
    public string PlayText => IsThisPlaying ? "Pausar" : "Reproducir";

    public string SortGlyph(string key) => SortKey == key ? (SortDescending ? " ↓" : " ↑") : "";
    public string TitleSort => SortGlyph("title");
    public string ArtistSort => SortGlyph("artist");
    public string AlbumSort => SortGlyph("album");
    public string DurationSort => SortGlyph("duration");
    public string FormatSort => SortGlyph("format");
    public string ExtraSort => SortGlyph("extra");

    partial void OnFilterChanged(string value) => Rebuild();

    public void SortBy(string? key)
    {
        if (key == null || (SortKey == key && SortDescending))
        {
            SortKey = null;
            SortDescending = false;
        }
        else if (SortKey == key)
        {
            SortDescending = true;
        }
        else
        {
            SortKey = key;
            SortDescending = false;
        }
        foreach (var n in new[] { nameof(TitleSort), nameof(ArtistSort), nameof(AlbumSort), nameof(DurationSort), nameof(FormatSort), nameof(ExtraSort) })
            OnPropertyChanged(n);
        Rebuild();
    }

    public void Rebuild()
    {
        _built = true;
        IEnumerable<(Song s, int i)> q = Songs.Select((s, i) => (s, i));

        var terms = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 0)
        {
            var ci = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            q = q.Where(x => terms.All(t =>
                ci.IndexOf(x.s.Title, t, opts) >= 0 || ci.IndexOf(x.s.Artist, t, opts) >= 0 || ci.IndexOf(x.s.Album, t, opts) >= 0));
        }

        if (SortKey != null)
        {
            Func<(Song s, int i), object> sel = SortKey switch
            {
                "title" => x => x.s.Title,
                "artist" => x => x.s.Artist,
                "album" => x => x.s.Album,
                "duration" => x => x.s.Duration ?? TimeSpan.Zero,
                "format" => x => x.s.Format,
                "extra" => x => x.i,
                _ => x => x.i,
            };
            var cmp = Comparer<object>.Create((a, b) => a is string sa && b is string sb
                ? Services.NaturalComparer.Instance.Compare(sa, sb)
                : Comparer<object>.Default.Compare(a, b));
            q = SortDescending ? q.OrderByDescending(sel, cmp) : q.OrderBy(sel, cmp);
        }

        var list = q.ToList();
        Visible = list.Select(x => x.s).ToList();
        var items = new List<object>(list.Count + 1) { _header };
        bool group = GroupByFolder && SortKey == null;
        string? dir = null;
        int inGroup = 0;
        Dictionary<string, List<Song>>? byDir = null; // canciones por carpeta, en una sola pasada y solo si hace falta
        for (int k = 0; k < list.Count; k++)
        {
            var s = list[k].s;
            if (group && !string.Equals(s.Directory, dir, StringComparison.OrdinalIgnoreCase))
            {
                dir = s.Directory;
                inGroup = 0;
                items.Add(GetGroup(dir, ref byDir));
            }
            inGroup++;
            items.Add(new SongRow(s, k, _extras != null && list[k].i < _extras.Count ? _extras[list[k].i] : null, group ? inGroup : null));
        }
        Items = items;
    }

    private GroupHeader GetGroup(string dir, ref Dictionary<string, List<Song>>? byDir)
    {
        if (_groups.TryGetValue(dir, out var g)) return g;
        if (byDir == null)
        {
            byDir = new Dictionary<string, List<Song>>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in Songs)
            {
                if (!byDir.TryGetValue(x.Directory, out var l)) byDir[x.Directory] = l = new List<Song>();
                l.Add(x);
            }
        }
        var songs = byDir.TryGetValue(dir, out var found) ? found : new List<Song>();
        string title, kicker;
        if (GroupTitleFromAlbum && songs.Count > 0)
        {
            title = songs[0].Album;
            kicker = "Álbum";
        }
        else
        {
            title = Path.GetFileName(dir);
            var parent = Path.GetDirectoryName(dir);
            var rel = GroupRoot != null && parent != null && parent.Length > GroupRoot.Length
                ? Path.GetRelativePath(GroupRoot, parent).Replace(Path.DirectorySeparatorChar.ToString(), " / ")
                : null;
            kicker = string.Equals(dir, GroupRoot, StringComparison.OrdinalIgnoreCase) ? "Canciones sueltas"
                : rel != null ? "Carpeta · " + rel : "Carpeta";
        }
        return _groups[dir] = new GroupHeader(dir, kicker, title, songs);
    }

    public void RefreshProperties()
    {
        var props = new List<PropertyItem>(BaseProperties);
        var all = AllSongs;
        int count = all.Count;
        props.Add(new PropertyItem("", "Canciones", count == 1 ? "1 canción" : $"{count:N0} canciones"));

        long ticks = 0;
        int unknown = 0, cloud = 0;
        foreach (var s in all)
        {
            if (s.Duration is { } d) ticks += d.Ticks; else unknown++;
            if (s.IsCloud) cloud++;
        }
        if (ticks > 0)
        {
            var t = TimeSpan.FromTicks(ticks);
            var dur = t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min {t.Seconds} s";
            props.Add(new PropertyItem("", "Duración", unknown > 0 ? $"más de {dur}" : dur));
        }
        if (count > 0 && Kind != ListKind.History)
        {
            var local = count - cloud;
            var text = cloud == 0 ? "Todo en este dispositivo"
                : local == 0 ? "Todo en OneDrive (se descarga al reproducir)"
                : $"{local:N0} en el dispositivo · {cloud:N0} en OneDrive";
            props.Add(new PropertyItem(cloud == 0 ? "" : "", "Disponibilidad", text));
        }
        // se llama a menudo mientras se leen metadatos: si nada cambió, no se rehacen las filas de la cabecera
        if (props.SequenceEqual(Properties)) return;
        Properties.Clear();
        foreach (var p in props) Properties.Add(p);
    }
}

public sealed class GalleryHeader
{
    public GalleryHeader(GalleryPage page) => Page = page;
    public GalleryPage Page { get; }
}

/// <summary>Galería de tarjetas (artistas / álbumes), virtualizada por filas.</summary>
public sealed partial class GalleryPage : PageBase
{
    private readonly GalleryHeader _header;
    private int _columns = 5;

    public GalleryPage(List<CardVm> cards)
    {
        Cards = cards;
        _header = new GalleryHeader(this);
        Rebuild();
    }

    public List<CardVm> Cards { get; }
    public override bool HasFilter => true;
    public string Summary => Cards.Count == 1 ? "1 elemento" : $"{Cards.Count:N0} elementos";

    [ObservableProperty] private IList<object> _rows = new List<object>();
    [ObservableProperty] private string _filter = "";

    partial void OnFilterChanged(string value) => Rebuild();

    public void SetColumns(int columns)
    {
        if (columns == _columns || columns < 1) return;
        _columns = columns;
        Rebuild();
    }

    private void Rebuild()
    {
        IEnumerable<CardVm> q = Cards;
        var terms = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 0)
        {
            var ci = CultureInfo.InvariantCulture.CompareInfo;
            const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            q = q.Where(c => terms.All(t => ci.IndexOf(c.Title, t, opts) >= 0 || ci.IndexOf(c.Subtitle, t, opts) >= 0));
        }
        var rows = new List<object> { _header };
        rows.AddRange(q.Chunk(_columns).Select(c => (object)c.ToList()));
        Rows = rows;
    }
}

/// <summary>
/// Una fila de tarjetas del inicio. Solo se muestra una fila; <see cref="Visible"/> tiene las que caben en ella
/// (la vista lo ajusta al ancho) para no crear, animar ni cargar la portada de las que no se verían.
/// </summary>
public sealed class HomeSection
{
    public HomeSection(string title, List<CardVm> cards)
    {
        Title = title;
        Cards = cards;
    }

    public string Title { get; }
    public List<CardVm> Cards { get; }
    public ObservableCollection<CardVm> Visible { get; } = new();

    /// <summary>Deja a la vista las primeras <paramref name="count"/>, añadiendo o quitando por el final (las que ya están no se rehacen).</summary>
    public void Fit(int count)
    {
        count = Math.Clamp(count, 0, Cards.Count);
        while (Visible.Count > count) Visible.RemoveAt(Visible.Count - 1);
        while (Visible.Count < count) Visible.Add(Cards[Visible.Count]);
    }
}

public sealed record GreetChar(string Ch, int Index);

public sealed class HomePage : PageBase
{
    /// <summary>Sin filtro en esta página (el cuadro de filtro está oculto); evita que su enlace falle.</summary>
    public string Filter { get; set; } = "";

    public string Greeting { get; init; } = "";
    /// <summary>El saludo letra a letra, para animarlo en cascada.</summary>
    public List<GreetChar> GreetingChars => Greeting.Select((c, i) => new GreetChar(c.ToString(), i)).ToList();
    public string DateText { get; init; } = "";
    public List<CardVm> QuickLinks { get; init; } = new();
    public List<HomeSection> Sections { get; init; } = new();
}

public sealed partial class SearchPage : PageBase
{
    /// <summary>Sin filtro en esta página (el cuadro de filtro está oculto); evita que su enlace falle.</summary>
    public string Filter { get; set; } = "";

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private List<SongRow> _songs = new();
    [ObservableProperty] private List<CardVm> _artists = new();
    [ObservableProperty] private List<CardVm> _albums = new();
    [ObservableProperty] private List<CardVm> _folders = new();
    [ObservableProperty] private List<CardVm> _playlists = new();
    [ObservableProperty] private int _totalSongs;
    [ObservableProperty] private bool _hasQuery;
    [ObservableProperty] private bool _noResults;

    public void SetResults(List<SongRow> songs, int total, List<CardVm> artists, List<CardVm> albums, List<CardVm> folders, List<CardVm> playlists)
    {
        Songs = songs;
        TotalSongs = total;
        Artists = artists;
        Albums = albums;
        Folders = folders;
        Playlists = playlists;
        HasQuery = Query.Trim().Length > 0;
        NoResults = HasQuery && total == 0 && artists.Count == 0 && albums.Count == 0 && folders.Count == 0 && playlists.Count == 0;
    }
}

public sealed record RankItem(int Rank, string Title, string Subtitle, string Value, Song? Song, string? Key);
public sealed record BarItem(string Label, double Height, string Tip);

public sealed class StatsPage : PageBase
{
    /// <summary>Sin filtro en esta página (el cuadro de filtro está oculto); evita que su enlace falle.</summary>
    public string Filter { get; set; } = "";

    public string TotalTime { get; init; } = "";
    public string MonthTime { get; init; } = "";
    public string PlayCount { get; init; } = "";
    public string DistinctSongs { get; init; } = "";
    public List<RankItem> TopSongs { get; init; } = new();
    public List<RankItem> TopArtists { get; init; } = new();
    public List<RankItem> TopFolders { get; init; } = new();
    public List<BarItem> Days { get; init; } = new();
    public bool HasData => TopSongs.Count > 0;
}

public sealed partial class LyricLine : ObservableObject
{
    public LyricLine(TimeSpan? time, string text)
    {
        Time = time;
        Text = text.Length == 0 ? "♪" : text;
    }

    public TimeSpan? Time { get; }
    public string Text { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Emphasis))] private bool _isActive;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Emphasis))] private bool _isPast;

    /// <summary>Opacidad de la línea: la que suena entera, las ya cantadas a medias, las que vienen más apagadas.</summary>
    public double Emphasis => IsActive ? 1 : IsPast ? 0.55 : 0.4;
}
