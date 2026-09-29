using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;
using Echoplex.Services;

namespace Echoplex.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private readonly Dispatcher _ui;
    private readonly MetadataCache _cache;
    private readonly MetadataWorker _worker;
    private readonly Stack<PageBase> _back = new();
    private readonly Stack<PageBase> _forward = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _sleepTimer;
    private readonly Random _rng = new();
    private FolderNode? _selectedNode;
    private FolderNode? _activeNode;
    private DateTime _sleepEnd;
    private int _coverVersion;
    private int _lyricsVersion;
    private int _infoVersion;
    private SearchPage? _pendingSearch;

    public MainViewModel(Dispatcher ui)
    {
        _ui = ui;
        Settings = SettingsStore.Load();
        _cache = new MetadataCache(Path.Combine(SettingsStore.DataDirectory, "metadata.json"));
        Library = new LibraryService(Settings.MusicRoots);
        UserData = new UserDataService();

        Player = new PlayerService(ui)
        {
            Volume = Settings.Volume,
            Shuffle = Settings.Shuffle,
            Repeat = (RepeatMode)Math.Clamp(Settings.Repeat, 0, 2),
            AutoRadio = Settings.AutoRadio,
            ExclusiveDeviceId = Settings.ExclusiveDeviceId,
            ExclusiveMode = Settings.ExclusiveMode,
        };
        Player.Notice += text => ShowToast(text, 7);
        Player.CoverFileProvider = s => Covers.FindCoverFile(s.Directory);
        Player.RadioProvider = (seed, context) => BuildRadio(seed, context, 25);
        Player.PropertyChanged += OnPlayerPropertyChanged;
        Player.SongOpened += OnSongOpened;
        Player.ContextStarted += OnContextStarted;
        Player.Listened += (s, sec) => UserData.RecordListen(s, sec);
        Player.PlayCounted += s => UserData.RecordPlay(s);

        _worker = new MetadataWorker(_cache, ui);
        _worker.Updated += () => (CurrentPage as SongListPage)?.RefreshProperties();

        _showRightPanel = Settings.ShowRightPanel;
        _rightTab = Settings.RightPanelTab is "queue" or "lyrics" or "info" ? Settings.RightPanelTab : "queue";
        _isDark = Themes.Get(Settings.Theme).IsDark;
        _animationsOn = Settings.Animations;

        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal,
            (s, e) => { ((DispatcherTimer)s!).Stop(); if (_pendingSearch != null) RunSearch(_pendingSearch); }, ui);
        _searchTimer.Stop();
        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(45), DispatcherPriority.Background, (s, e) =>
        {
            UserData.Save();
            Task.Run(_cache.Save);
        }, ui);
        _saveTimer.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(2.6), DispatcherPriority.Normal,
            (s, e) => { ((DispatcherTimer)s!).Stop(); Toast = null; }, ui);
        _toastTimer.Stop();
        _sleepTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (s, e) => SleepTick(), ui);
        _sleepTimer.Stop();
    }

    public AppSettings Settings { get; }
    public LibraryService Library { get; }
    public PlayerService Player { get; }
    public UserDataService UserData { get; }
    public CoverService Covers { get; } = new();
    public ObservableCollection<FolderNode> Folders { get; } = new();
    public ObservableCollection<Playlist> Playlists => UserData.Playlists;
    public ObservableCollection<LyricLine> Lyrics { get; } = new();
    public ObservableCollection<PropertyItem> InfoItems { get; } = new();
    public bool IsSyncingTree { get; private set; }

    public event Action? ThemeChanged;

    [ObservableProperty] private PageBase? _currentPage;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _statusText = "Cargando tu biblioteca…";
    [ObservableProperty] private bool _showRightPanel;
    [ObservableProperty] private string _rightTab;
    [ObservableProperty] private BitmapSource? _nowPlayingCover;
    [ObservableProperty] private bool _canGoBack;
    [ObservableProperty] private bool _canGoForward;
    [ObservableProperty] private int _songCount;
    [ObservableProperty] private string _activeNav = "home";
    [ObservableProperty] private string? _toast;
    [ObservableProperty] private string? _sleepText;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private bool _animationsOn;
    [ObservableProperty] private bool _hasLyrics;
    [ObservableProperty] private bool _lyricsSynced;
    [ObservableProperty] private LyricLine? _activeLyric;
    [ObservableProperty] private int _galleryColumns = 5;
    [ObservableProperty] private bool _isMiniPlayer;

    public bool QueueTabActive => ShowRightPanel && RightTab == "queue";
    public bool LyricsTabActive => ShowRightPanel && RightTab == "lyrics";
    public bool InfoTabActive => ShowRightPanel && RightTab == "info";
    public bool SleepActive => SleepText != null;

    partial void OnShowRightPanelChanged(bool value)
    {
        Settings.ShowRightPanel = value;
        NotifyTabs();
    }

    partial void OnRightTabChanged(string value)
    {
        Settings.RightPanelTab = value;
        NotifyTabs();
    }

    partial void OnSleepTextChanged(string? value) => OnPropertyChanged(nameof(SleepActive));

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(QueueTabActive));
        OnPropertyChanged(nameof(LyricsTabActive));
        OnPropertyChanged(nameof(InfoTabActive));
    }

    /// <summary>Abre el panel derecho en una pestaña, o lo cierra si ya estaba en ella.</summary>
    public void ToggleRightTab(string tab)
    {
        if (ShowRightPanel && RightTab == tab) ShowRightPanel = false;
        else
        {
            RightTab = tab;
            ShowRightPanel = true;
        }
    }
    public int TileColumns => GalleryColumns >= 5 ? 4 : 2;

    partial void OnGalleryColumnsChanged(int value)
    {
        (CurrentPage as GalleryPage)?.SetColumns(value);
        OnPropertyChanged(nameof(TileColumns));
    }

    // ======================================================================
    // Arranque / cierre
    // ======================================================================

    public async Task InitializeAsync()
    {
        await Task.Run(() =>
        {
            _cache.Load();
            UserData.Load();
        });
        UserData.PopulatePlaylists();
        OnPropertyChanged(nameof(Playlists));
        _saveTimer.Start();

        if (!Library.Roots.Any(Directory.Exists))
        {
            NeedsFolder = true;
            StatusText = Library.Roots.Count == 0
                ? "Elige la carpeta donde tienes tu música."
                : "No se encuentra tu carpeta de música:\n" + string.Join("\n", Library.Roots);
            return;
        }

        await Task.Run(() => Library.Load(_cache));
        OnLibraryLoaded(resetNavigation: true, previousCount: -1);
        RestoreLastSession();
    }

    // ======================================================================
    // Actualizaciones (GitHub Releases, al abrir la app)
    // ======================================================================

    public string VersionText => $"Echoplex {UpdateService.CurrentVersion}";

    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private bool _isCheckingUpdates;
    /// <summary>Versión ya instalada que espera un reinicio (muestra el botón "Reiniciar para actualizar").</summary>
    [ObservableProperty] private string? _updateReady;

    public UpdateService Updates { get; set; } = new();

    public bool AutoUpdate
    {
        get => Settings.AutoUpdate;
        set
        {
            if (Settings.AutoUpdate == value) return;
            Settings.AutoUpdate = value;
            SettingsStore.Save(Settings);
            OnPropertyChanged();
        }
    }

    /// <summary>Al abrir: si está activado, busca e instala en segundo plano sin molestar.</summary>
    public async Task CheckForUpdatesOnStartupAsync()
    {
        if (!Settings.AutoUpdate) return;
        await Task.Delay(TimeSpan.FromSeconds(4)); // que la biblioteca cargue antes
        await CheckForUpdatesAsync(manual: false);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (IsCheckingUpdates || UpdateReady != null) return;
        IsCheckingUpdates = true;
        UpdateStatus = "Buscando actualizaciones…";
        var result = await Task.Run(() => Updates.CheckAndInstallAsync());
        IsCheckingUpdates = false;
        UpdateStatus = result.Message;
        switch (result.State)
        {
            case UpdateState.Installed:
                UpdateReady = result.Version?.ToString();
                ShowToast($"Echoplex {UpdateReady} está listo: reinicia para usarlo", 8);
                break;
            case UpdateState.Failed when manual || result.Version != null:
                // sin conexión al abrir no se avisa; un fallo con una versión nueva delante, sí
                ShowToast(result.Message, 8);
                break;
            case UpdateState.UpToDate when manual:
                ShowToast(result.Message, 4);
                break;
        }
    }

    /// <summary>Lo pide la ventana: cierra guardando todo y arranca la versión nueva.</summary>
    public event Action? RestartRequested;

    public void RestartToUpdate() => RestartRequested?.Invoke();

    // ======================================================================
    // Carpetas de música: añadir/quitar en caliente, reescaneo y vigilancia
    // ======================================================================

    /// <summary>Carpetas de música configuradas (para Ajustes).</summary>
    public ObservableCollection<MusicFolderItem> MusicFolders { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _needsFolder;

    private bool _workerStarted;
    private bool _rescanPending;
    private readonly List<FileSystemWatcher> _watchers = new();
    private DispatcherTimer? _rescanTimer;

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".lrc", ".log", ".cue", ".txt", ".nfo",
        ".tmp", ".db", ".ini", ".m3u", ".m3u8", ".url", ".sfk", ".afpk", ".torrent", ".md", ".py", ".sqlite",
        ".zip", ".rar", ".7z", ".iso", ".xml", ".pdf", ".accurip", ".sfv", ".md5",
    };

    /// <summary>Añade una carpeta de música y recarga todo sin reiniciar.</summary>
    public async Task AddMusicRootAsync(string path)
    {
        if (!Directory.Exists(path))
        {
            ShowToast("Esa carpeta no existe", 4);
            return;
        }
        path = LibraryService.Normalize(path);
        if (Library.Roots.Any(r => Eq(r, path) || LibraryService.IsUnder(path, r)))
        {
            ShowToast("Esa carpeta ya está en tu biblioteca", 4);
            return;
        }
        var inside = Library.Roots.Where(r => LibraryService.IsUnder(r, path)).ToList();
        var roots = Library.Roots.Except(inside).Append(path).ToList();
        int before = Library.Songs.Count;
        if (await ApplyMusicRootsAsync(roots))
        {
            int added = SongCount - before;
            ShowToast(inside.Count > 0 ? $"Carpeta añadida (incluye {inside.Count} que ya tenías): {SongCount:N0} canciones"
                : added > 0 ? $"Carpeta añadida: {added:N0} canciones más" : "Carpeta añadida, pero no tiene canciones", 5);
        }
    }

    /// <summary>Quita una carpeta de la biblioteca (no borra nada del disco).</summary>
    public async Task RemoveMusicRootAsync(string path)
    {
        var roots = Library.Roots.Where(r => !Eq(r, path)).ToList();
        if (roots.Count == Library.Roots.Count) return;
        if (await ApplyMusicRootsAsync(roots)) ShowToast($"Carpeta quitada de la biblioteca: {Path.GetFileName(path)}", 4);
    }

    private async Task<bool> ApplyMusicRootsAsync(List<string> roots)
    {
        Settings.MusicRoots = roots;
        SettingsStore.Save(Settings);
        Covers.ClearCache();
        if (roots.Count == 0)
        {
            Library.Load(_cache, roots);
            Folders.Clear();
            RefreshMusicFolders();
            SongCount = 0;
            CurrentPage = null;
            IsLoading = true;
            NeedsFolder = true;
            StatusText = "Elige la carpeta donde tienes tu música.";
            return true;
        }
        return await ReloadLibraryAsync(roots, reset: true);
    }

    private void RefreshMusicFolders()
    {
        MusicFolders.Clear();
        foreach (var r in Library.Roots)
            MusicFolders.Add(new MusicFolderItem(r, Directory.Exists(r), Library.GetCount(r)));
    }

    /// <summary>Vuelve a leer la carpeta de música (canciones nuevas, borradas o movidas).</summary>
    public async Task RescanAsync(bool announce)
    {
        if (IsLoading) return;
        int before = Library.Songs.Count;
        if (!await ReloadLibraryAsync(null, reset: false) || !announce && before == SongCount) return;
        int diff = SongCount - before;
        ShowToast(diff == 0 ? "La biblioteca ya estaba al día"
            : diff > 0 ? $"Biblioteca actualizada: {diff} canciones nuevas"
            : $"Biblioteca actualizada: {-diff} canciones menos", 4);
    }

    private async Task<bool> ReloadLibraryAsync(List<string>? newRoots, bool reset)
    {
        if (IsScanning)
        {
            _rescanPending = true;
            return false;
        }
        IsScanning = true;
        if (reset)
        {
            IsLoading = true;
            NeedsFolder = false;
            StatusText = "Cargando tu biblioteca…";
            CurrentPage = null;
        }
        try
        {
            await Task.Run(() => Library.Load(_cache, newRoots));
        }
        catch (Exception ex)
        {
            IsScanning = false;
            if (reset)
            {
                NeedsFolder = true;
                StatusText = "No se pudo leer la carpeta:\n" + ex.Message;
            }
            else
            {
                ShowToast("No se pudo leer la carpeta: " + ex.Message, 6);
            }
            return false;
        }
        OnLibraryLoaded(reset, 0);
        IsScanning = false;
        if (_rescanPending)
        {
            _rescanPending = false;
            ScheduleRescan();
        }
        return true;
    }

    private void OnLibraryLoaded(bool resetNavigation, int previousCount)
    {
        foreach (var path in UserData.Favorites)
            if (Library.Find(path) is { } s) s.IsFavorite = true;

        // una raíz por carpeta de música, cada una con sus subcarpetas
        Folders.Clear();
        _selectedNode = null;
        _activeNode = null;
        foreach (var root in Library.PresentRoots)
        {
            var node = BuildNode(root, null);
            node.IsExpanded = true;
            Folders.Add(node);
        }
        SongCount = Library.Songs.Count;
        RefreshMusicFolders();

        if (_workerStarted) _worker.Reset(Library.Songs);
        else
        {
            _worker.Start(Library.Songs);
            _workerStarted = true;
        }
        Player.Remap(Library.Find);
        UpdateActiveContext();
        StartWatcher();

        if (Library.Songs.Count == 0)
        {
            // carpeta vacía (p. ej. la carpeta Música de Windows la primera vez): pantalla de "añade tu carpeta"
            CurrentPage = null;
            NeedsFolder = true;
            IsLoading = true;
            StatusText = Library.PresentRoots.Count == 0
                ? "Elige la carpeta donde tienes tu música."
                : "No hay canciones en " + string.Join(", ", Library.PresentRoots.Select(Path.GetFileName)) + ".\nAñade la carpeta donde tienes tu música.";
            return;
        }

        NeedsFolder = false;
        IsLoading = false;
        if (resetNavigation || CurrentPage == null)
        {
            _back.Clear();
            _forward.Clear();
            Navigate(BuildHome(), addHistory: false);
        }
        else
        {
            RefreshCurrentPage();
        }
    }

    /// <summary>Tras un reescaneo, rehace la página visible con los datos nuevos.</summary>
    private void RefreshCurrentPage()
    {
        switch (CurrentPage)
        {
            case SongListPage { Kind: ListKind.Folder, FolderPath: { } folder }:
                if (Library.HasFolder(folder)) OpenFolder(folder, replace: true);
                else ReplacePage(BuildHome());
                break;
            case SongListPage { Kind: ListKind.Favorites }:
                OpenFavorites(replace: true);
                break;
            case HomePage:
                ReplacePage(BuildHome());
                break;
        }
    }

    private void StartWatcher()
    {
        foreach (var old in _watchers) old.Dispose();
        _watchers.Clear();
        if (_rescanTimer == null)
        {
            _rescanTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background,
                (s, e) => { ((DispatcherTimer)s!).Stop(); _ = RescanAsync(announce: false); }, _ui);
            _rescanTimer.Stop();
        }
        foreach (var root in Library.PresentRoots)
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (s, e) => OnFileSystemEvent(e.FullPath);
                w.Deleted += (s, e) => OnFileSystemEvent(e.FullPath);
                w.Renamed += (s, e) => OnFileSystemEvent(e.FullPath);
                w.Error += (s, e) => _ui.BeginInvoke(ScheduleRescan);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch
            {
                // sin vigilancia en esta carpeta: queda el botón de reescanear
            }
        }
    }

    private void OnFileSystemEvent(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('~') || name.StartsWith('.')) return;
        var ext = Path.GetExtension(path);
        // audio, o algo sin extensión conocida (probablemente una carpeta): hay que reescanear
        if (!LibraryService.AudioExtensions.Contains(ext) && IgnoredExtensions.Contains(ext)) return;
        _ui.BeginInvoke(ScheduleRescan);
    }

    private void ScheduleRescan()
    {
        if (_rescanTimer == null) return;
        _rescanTimer.Stop();
        _rescanTimer.Start();
    }

    public void Shutdown()
    {
        Settings.Volume = Player.Volume;
        Settings.Shuffle = Player.Shuffle;
        Settings.Repeat = (int)Player.Repeat;
        Settings.AutoRadio = Player.AutoRadio;
        Settings.ExclusiveMode = Player.ExclusiveMode;
        Settings.ExclusiveDeviceId = Player.ExclusiveDeviceId;
        Settings.LastSong = Player.Current?.Path;
        Settings.LastContext = Player.ContextId;
        SettingsStore.Save(Settings);
        UserData.Save(force: true);
        _cache.Save();
        foreach (var w in _watchers) w.Dispose();
        Player.Dispose();
    }

    private FolderNode BuildNode(string path, FolderNode? parent)
    {
        var node = new FolderNode(Path.GetFileName(path) is { Length: > 0 } n ? n : path, path, Library.GetCount(path), parent);
        foreach (var child in Library.GetChildren(path)) node.Children.Add(BuildNode(child, node));
        return node;
    }

    private void RestoreLastSession()
    {
        if (Settings.LastSong is not { } last || Library.Find(last) is not { } song) return;
        var ctx = Settings.LastContext;
        var songs = ctx != null ? SongsForContext(ctx) : null;
        if (songs == null || !songs.Contains(song))
        {
            ctx = "folder:" + song.Directory;
            songs = Library.SongsUnder(song.Directory);
        }
        int idx = songs.IndexOf(song);
        if (idx >= 0) Player.Prepare(ctx!, ContextName(ctx!), songs, idx);
    }

    // ======================================================================
    // Navegación
    // ======================================================================

    private void Navigate(PageBase page, bool addHistory = true)
    {
        (page as SongListPage)?.EnsureBuilt();
        if (addHistory && CurrentPage != null)
        {
            _back.Push(CurrentPage);
            _forward.Clear();
        }
        CurrentPage = page;
        OnPageChanged();
    }

    private void ReplacePage(PageBase page)
    {
        (page as SongListPage)?.EnsureBuilt();
        _replacingPage = true;
        CurrentPage = page;
        _replacingPage = false;
        OnPageChanged();
    }

    private bool _replacingPage;

    /// <summary>
    /// Se lanza justo antes de cambiar de página (la vista captura la que se va para la transición).
    /// El segundo argumento indica un refresco de la misma página, que no se anima.
    /// </summary>
    public event Action<PageBase?, bool>? PageChanging;

    partial void OnCurrentPageChanging(PageBase? value) => PageChanging?.Invoke(value, _replacingPage);

    private void OnPageChanged()
    {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
        foreach (var p in Playlists) p.IsSelected = false;

        switch (CurrentPage)
        {
            case SongListPage p:
                _worker.Prioritize(p.Songs);
                p.RefreshProperties();
                if (p.FolderPath != null) SyncTree(p.FolderPath); else ClearTreeSelection();
                if (p.Playlist != null) p.Playlist.IsSelected = true;
                ActiveNav = p.Kind switch
                {
                    ListKind.Favorites => "favorites",
                    ListKind.History => "history",
                    ListKind.Recent => "recent",
                    ListKind.Artist => "artists",
                    ListKind.Album => "albums",
                    ListKind.Search => "search",
                    _ => "",
                };
                break;
            case GalleryPage g:
                ClearTreeSelection();
                g.SetColumns(GalleryColumns);
                ActiveNav = g.Title == "Artistas" ? "artists" : "albums";
                break;
            case SearchPage:
                ClearTreeSelection();
                ActiveNav = "search";
                break;
            case StatsPage:
                ClearTreeSelection();
                ActiveNav = "stats";
                break;
            default:
                ClearTreeSelection();
                ActiveNav = "home";
                break;
        }
        UpdatePagePlaying();
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        if (CurrentPage != null) _forward.Push(CurrentPage);
        CurrentPage = _back.Pop();
        OnPageChanged();
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        if (CurrentPage != null) _back.Push(CurrentPage);
        CurrentPage = _forward.Pop();
        OnPageChanged();
    }

    public void GoHome()
    {
        if (!IsLoading) Navigate(BuildHome());
    }

    public void OpenFolder(string path, bool replace = false)
    {
        if (IsLoading) return;
        if (!replace && CurrentPage is SongListPage cur && Eq(cur.FolderPath, path))
        {
            SyncTree(path);
            return;
        }
        var all = Library.SongsUnder(path);
        var children = Library.GetChildren(path);
        bool hasChildren = children.Count > 0;
        int discs = all.Select(s => s.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        bool isAll = Eq(path, LibraryService.AllKey);
        bool isRoot = Library.IsRoot(path);
        var props = new List<PropertyItem>
        {
            new("", "Tipo", isAll ? "Biblioteca" : isRoot ? "Carpeta de música" : "Carpeta"),
            new("", "Ubicación", isAll ? $"{Library.PresentRoots.Count} carpetas de música" : isRoot ? path : Library.DisplayPath(path)),
        };
        if (hasChildren) props.Add(new PropertyItem("", "Discos", discs.ToString()));
        var page = new SongListPage("folder:" + path, ListKind.Folder, all)
        {
            Title = FolderName(path),
            Glyph = isAll || isRoot ?"" : "",
            Breadcrumb = isAll ? "Carpetas" : "Carpetas / " + Library.DisplayPath(path),
            FolderPath = path,
            GroupByFolder = hasChildren,
            GroupRoot = path,
            BaseProperties = props,
        };
        if (replace) ReplacePage(page); else Navigate(page);
        _ = LoadFolderCoverAsync(page, path, children, all.FirstOrDefault());
    }

    public void OpenPlaylist(Playlist pl, bool replace = false)
    {
        var page = new SongListPage(pl.ContextId, ListKind.Playlist, Library.Resolve(pl.Songs))
        {
            Title = pl.Name,
            Glyph = "",
            Breadcrumb = "Playlists / " + pl.Name,
            Playlist = pl,
            BaseProperties =
            {
                new PropertyItem("", "Tipo", "Playlist"),
                new PropertyItem("", "Creada", pl.Created.ToString("d 'de' MMMM 'de' yyyy", Es)),
            },
        };
        if (replace) ReplacePage(page); else Navigate(page);
        _ = LoadPageCoverAsync(page, null, page.Songs.FirstOrDefault());
    }

    public void OpenFavorites(bool replace = false)
    {
        var page = new SongListPage("favorites", ListKind.Favorites, Library.Resolve(UserData.Favorites))
        {
            Title = "Favoritas",
            Glyph = "",
            Breadcrumb = "Favoritas",
            Description = "Las canciones que has marcado con el corazón.",
            BaseProperties = { new PropertyItem("", "Tipo", "Colección") },
        };
        if (replace) ReplacePage(page); else Navigate(page);
    }

    public void OpenHistory()
    {
        var entries = UserData.History.Take(500).Select(h => (song: Library.Find(h.P), h.At)).Where(x => x.song != null).ToList();
        var page = new SongListPage("history", ListKind.History, entries.Select(x => x.song!).ToList(), entries.Select(x => (string?)RelativeTime(x.At)).ToList())
        {
            Title = "Historial",
            Glyph = "",
            Breadcrumb = "Historial",
            Description = "Lo último que has escuchado (cuenta a partir de 30 segundos).",
            ExtraHeader = "Escuchada",
            BaseProperties = { new PropertyItem("", "Tipo", "Historial") },
        };
        Navigate(page);
    }

    public void OpenRecent()
    {
        var songs = Library.Songs.OrderByDescending(s => s.CreatedTicks).Take(300).ToList();
        var page = new SongListPage("recent", ListKind.Recent, songs,
            songs.Select(s => (string?)new DateTime(s.CreatedTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy", Es)).ToList())
        {
            Title = "Añadidas recientemente",
            Glyph = "",
            Breadcrumb = "Añadidas recientemente",
            Description = "Las 300 canciones que llegaron más tarde a tu carpeta de música.",
            ExtraHeader = "Añadida",
            BaseProperties = { new PropertyItem("", "Tipo", "Colección") },
        };
        Navigate(page);
    }

    public void OpenArtist(string name)
    {
        var songs = Library.SongsByArtist(name)
            .OrderBy(s => s.Directory, NaturalComparer.Instance)
            .ThenBy(s => s.TrackNumber == 0 ? uint.MaxValue : s.TrackNumber)
            .ThenBy(s => s.FileName, NaturalComparer.Instance).ToList();
        var albums = songs.GroupBy(LibraryService.AlbumKey)
            .Select(g => new CardVm(CardKind.Album, g.Key, g.First().Album, g.Count() == 1 ? "1 canción" : $"{g.Count()} canciones", "") { Song = g.First() })
            .ToList();
        var page = new SongListPage("artist:" + name, ListKind.Artist, songs)
        {
            Title = name,
            Glyph = "",
            Breadcrumb = "Artistas / " + name,
            ArtistName = name,
            GroupByFolder = true,
            GroupTitleFromAlbum = true,
            BaseProperties =
            {
                new PropertyItem("", "Tipo", "Artista"),
                new PropertyItem("", "Álbumes", albums.Count.ToString()),
            },
        };
        Navigate(page);
        _ = LoadPageCoverAsync(page, null, songs.FirstOrDefault());
    }

    public void OpenAlbum(string key)
    {
        var album = Library.GetAlbum(key);
        if (album == null) return;
        var page = new SongListPage("album:" + key, ListKind.Album, album.Songs)
        {
            Title = album.Name,
            Glyph = "",
            Breadcrumb = "Álbumes / " + album.Name,
            AlbumKey = key,
            FolderPath = null,
            BaseProperties =
            {
                new PropertyItem("", "Tipo", "Álbum"),
                new PropertyItem("", "Artista", album.Artist),
                new PropertyItem("", "Carpeta", Library.DisplayPath(album.Directory)),
            },
        };
        Navigate(page);
        _ = LoadPageCoverAsync(page, album.Directory, album.Songs.FirstOrDefault());
    }

    public void OpenArtists()
    {
        var cards = Library.GetArtists()
            .Select(a => new CardVm(CardKind.Artist, a.Name, a.Name, a.Songs.Count == 1 ? "1 canción" : $"{a.Songs.Count} canciones", "") { Song = a.Songs[0] })
            .ToList();
        Navigate(new GalleryPage(cards) { Title = "Artistas", Glyph = "", Breadcrumb = "Artistas" });
    }

    public void OpenAlbums()
    {
        var cards = Library.GetAlbums()
            .Select(a => new CardVm(CardKind.Album, a.Key, a.Name, a.Artist, "") { Song = a.Songs[0] })
            .ToList();
        Navigate(new GalleryPage(cards) { Title = "Álbumes", Glyph = "", Breadcrumb = "Álbumes" });
    }

    public void OpenSearch()
    {
        if (IsLoading || CurrentPage is SearchPage) return;
        var page = new SearchPage { Title = "Buscar", Glyph = "", Breadcrumb = "Buscar" };
        page.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName != nameof(SearchPage.Query)) return;
            _pendingSearch = page;
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        Navigate(page);
    }

    public void OpenStats()
    {
        if (IsLoading) return;
        var stats = UserData.Stats;
        var played = stats.Where(kv => kv.Value.Count > 0)
            .Select(kv => (song: Library.Find(kv.Key), st: kv.Value))
            .Where(x => x.song != null).ToList();

        double total = stats.Values.Sum(s => s.Seconds);
        var from = DateTime.Today.AddDays(-29);
        double month = UserData.Daily.Where(d => DateTime.TryParse(d.Key, out var day) && day >= from).Sum(d => d.Value);

        var topSongs = played.OrderByDescending(x => x.st.Count).ThenByDescending(x => x.st.Seconds).Take(10)
            .Select((x, i) => new RankItem(i + 1, x.song!.Title, x.song.Artist, Plays(x.st.Count), x.song, null)).ToList();
        var topArtists = played.GroupBy(x => x.song!.PrimaryArtist, StringComparer.OrdinalIgnoreCase)
            .Select(g => (name: g.First().song!.PrimaryArtist, count: g.Sum(x => x.st.Count), secs: g.Sum(x => x.st.Seconds)))
            .OrderByDescending(x => x.count).Take(10)
            .Select((x, i) => new RankItem(i + 1, x.name, FormatDuration(x.secs), Plays(x.count), null, x.name)).ToList();
        var topFolders = played.Where(x => x.song!.TopFolderPath.Length > 0).GroupBy(x => x.song!.TopFolderPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (path: g.Key, count: g.Sum(x => x.st.Count)))
            .OrderByDescending(x => x.count).Take(10)
            .Select((x, i) => new RankItem(i + 1, Path.GetFileName(x.path), "Carpeta", Plays(x.count), null, x.path)).ToList();

        var days = Enumerable.Range(0, 14).Select(i => DateTime.Today.AddDays(i - 13)).ToList();
        var minutes = days.Select(d => UserData.Daily.GetValueOrDefault(d.ToString("yyyy-MM-dd")) / 60).ToList();
        double max = Math.Max(1, minutes.Max());
        var bars = days.Select((d, i) => new BarItem(d.ToString("ddd", Es)[..2], Math.Max(2, minutes[i] / max * 120),
            $"{d.ToString("dddd d MMM", Es)}: {Math.Round(minutes[i])} min")).ToList();

        Navigate(new StatsPage
        {
            Title = "Tu resumen",
            Glyph = "",
            Breadcrumb = "Resumen",
            TotalTime = FormatDuration(total),
            MonthTime = FormatDuration(month),
            PlayCount = stats.Values.Sum(s => s.Count).ToString("N0", Es),
            DistinctSongs = played.Count.ToString("N0", Es),
            TopSongs = topSongs,
            TopArtists = topArtists,
            TopFolders = topFolders,
            Days = bars,
        });
    }

    public void OpenCard(CardVm card)
    {
        switch (card.Kind)
        {
            case CardKind.Folder: OpenFolder(card.Key); break;
            case CardKind.AllMusic: OpenFolder(LibraryService.AllKey); break;
            case CardKind.Album: OpenAlbum(card.Key); break;
            case CardKind.Artist: OpenArtist(card.Key); break;
            case CardKind.Favorites: OpenFavorites(); break;
            case CardKind.History: OpenHistory(); break;
            case CardKind.Recent: OpenRecent(); break;
            case CardKind.Playlist:
                if (Playlists.FirstOrDefault(p => p.Id == card.Key) is { } pl) OpenPlaylist(pl);
                break;
            case CardKind.Song:
                if (card.Song != null) OpenAlbum(LibraryService.AlbumKey(card.Song));
                break;
        }
    }

    public void OpenRank(RankItem item)
    {
        if (item.Song != null) OpenAlbum(LibraryService.AlbumKey(item.Song));
        else if (item.Key != null && Directory.Exists(item.Key)) OpenFolder(item.Key);
        else if (item.Key != null) OpenArtist(item.Key);
    }

    private async Task LoadPageCoverAsync(SongListPage page, string? dir, Song? first)
    {
        // la cabecera de la página abierta tiene prioridad sobre las miniaturas en cola
        page.Cover = dir != null
            ? await Covers.GetFolderCoverAsync(dir, page.Songs, 400, priority: true)
            : first != null ? await Covers.GetSongCoverAsync(first, 400, priority: true) : null;
    }

    private async Task LoadFolderCoverAsync(SongListPage page, string path, IReadOnlyList<string> children, Song? first) =>
        page.Cover = await FolderArtAsync(path, 400, allowDownload: true, priority: true);

    /// <summary>
    /// Portada de una carpeta. Si tiene imagen propia, esa. Si no, y tiene varias subcarpetas, un mosaico con
    /// sus portadas distintas: 2 o 3 en franjas diagonales, 4 o más en cuadrícula 2×2 (no pasa de 4).
    /// </summary>
    private async Task<BitmapSource?> FolderArtAsync(string path, int size, bool allowDownload, bool priority)
    {
        var children = Library.GetChildren(path);
        if (children.Count < 2 || await Task.Run(() => Covers.FindCoverFile(path)) != null)
            return await Covers.GetFolderCoverAsync(path, Library.SongsUnder(path), size, allowDownload, priority);

        var imgs = new List<BitmapSource>();
        var prints = new List<byte[]>();
        foreach (var child in children.Take(12))
        {
            var img = await Covers.GetFolderCoverAsync(child, Library.SongsUnder(child), size, allowDownload, priority);
            if (img == null) continue;
            var print = Fingerprint(img);
            if (prints.Any(p => SameArt(p, print))) continue; // Disc 1 y Disc 2 con la misma carátula cuentan como una
            imgs.Add(img);
            prints.Add(print);
            if (imgs.Count == 4) break;
        }
        return imgs.Count switch
        {
            0 => await Covers.GetFolderCoverAsync(path, Library.SongsUnder(path), size, allowDownload, priority),
            1 => imgs[0],
            _ => Mosaic(imgs, size),
        };
    }

    /// <summary>Huella de 8×8 en grises para reconocer carátulas repetidas aunque vengan de archivos distintos.</summary>
    private static byte[] Fingerprint(BitmapSource img)
    {
        var small = new TransformedBitmap(img, new System.Windows.Media.ScaleTransform(8.0 / img.PixelWidth, 8.0 / img.PixelHeight));
        var gray = new FormatConvertedBitmap(small, System.Windows.Media.PixelFormats.Gray8, null, 0);
        int w = gray.PixelWidth, h = gray.PixelHeight;
        var px = new byte[w * h];
        gray.CopyPixels(px, w, 0);
        return px;
    }

    private static bool SameArt(byte[] a, byte[] b) =>
        a.Length == b.Length && a.Zip(b, (x, y) => Math.Abs(x - y)).Average() < 14;

    /// <summary>Mosaico cuadrado: 2 o 3 portadas en franjas diagonales con una línea entre ellas, 4 en cuadrícula.</summary>
    private static BitmapSource Mosaic(IList<BitmapSource> imgs, int size)
    {
        double s = size;
        var full = new System.Windows.Rect(0, 0, s, s);
        System.Windows.Media.ImageBrush Brush(BitmapSource img) => new(img) { Stretch = System.Windows.Media.Stretch.UniformToFill };
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (imgs.Count >= 4)
            {
                double half = s / 2;
                for (int i = 0; i < 4; i++)
                    dc.DrawRectangle(Brush(imgs[i]), null, new System.Windows.Rect(i % 2 * half, i / 2 * half, half, half));
            }
            else
            {
                // cortes a 45° (rectas x + y = c, de arriba-derecha a abajo-izquierda) que dejan franjas de igual superficie
                double t = s * Math.Sqrt(2.0 / 3.0);
                var cuts = imgs.Count == 2 ? new[] { s } : new[] { t, 2 * s - t };
                double from = 0;
                for (int i = 0; i < imgs.Count; i++)
                {
                    double to = i < cuts.Length ? cuts[i] : 2 * s;
                    dc.PushClip(Band(from, to, s));
                    dc.DrawRectangle(Brush(imgs[i]), null, full);
                    dc.Pop();
                    from = to;
                }
                var pen = new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(235, 255, 255, 255)),
                    s < 128 ? s / 21 : s / 110); // en miniatura (árbol) más gruesa, para que se vea a 22 px
                foreach (var c in cuts)
                {
                    var (a, b) = c <= s
                        ? (new System.Windows.Point(c, 0), new System.Windows.Point(0, c))
                        : (new System.Windows.Point(s, c - s), new System.Windows.Point(c - s, s));
                    dc.DrawLine(pen, a, b);
                }
            }
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>La parte del cuadrado entre las rectas x + y = from y x + y = to.</summary>
    private static System.Windows.Media.Geometry Band(double from, double to, double s)
    {
        const double m = 10000;
        var strip = new System.Windows.Media.StreamGeometry();
        using (var g = strip.Open())
        {
            g.BeginFigure(new System.Windows.Point(from + m, -m), true, true);
            g.LineTo(new System.Windows.Point(to + m, -m), false, false);
            g.LineTo(new System.Windows.Point(-m, to + m), false, false);
            g.LineTo(new System.Windows.Point(-m, from + m), false, false);
        }
        return new System.Windows.Media.CombinedGeometry(System.Windows.Media.GeometryCombineMode.Intersect,
            new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, s, s)), strip);
    }

    public async void EnsureGroupCover(GroupHeader group)
    {
        if (group.CoverRequested) return;
        group.CoverRequested = true;
        group.Cover = await Covers.GetFolderCoverAsync(group.Directory, group.Songs, 200);
    }

    public void PlayGroup(GroupHeader group, bool shuffle = false)
    {
        var id = "folder:" + group.Directory;
        if (!shuffle && Player.ContextId == id && Player.Current != null)
        {
            Player.TogglePlayPause();
            return;
        }
        if (shuffle) Player.Shuffle = true;
        Player.PlayContext(id, group.Title, group.Songs, -1);
    }

    public async void EnsureNodeCover(FolderNode node)
    {
        if (node.CoverRequested || node.IsRoot) return;
        node.CoverRequested = true;
        // Misma regla que la cabecera: imagen propia, o mosaico de sus discos (diagonal / cuadrícula), o la del primer disco.
        node.Cover = await FolderArtAsync(node.Path, 64, allowDownload: true, priority: false);
    }

    public async void EnsureCover(CardVm card)
    {
        if (card.CoverRequested) return;
        card.CoverRequested = true;
        card.Cover = card.Kind switch
        {
            CardKind.Folder => await FolderArtAsync(card.Key, 300, card.AllowDownload, priority: false),
            CardKind.Playlist => Playlists.FirstOrDefault(p => p.Id == card.Key) is { Songs.Count: > 0 } pl && Library.Find(pl.Songs[0]) is { } s
                ? await Covers.GetSongCoverAsync(s, 300, allowDownload: false) : null,
            _ when card.Song != null => await Covers.GetSongCoverAsync(card.Song, 300, allowDownload: false),
            _ => null,
        };
    }

    // ======================================================================
    // Inicio
    // ======================================================================

    private HomePage BuildHome()
    {
        var hour = DateTime.Now.Hour;
        var greeting = hour < 6 ? "Buenas noches" : hour < 13 ? "Buenos días" : hour < 21 ? "Buenas tardes" : "Buenas noches";

        var quick = new List<CardVm>
        {
            new(CardKind.Favorites, "favorites", "Favoritas", $"{UserData.Favorites.Count} canciones", ""),
            new(CardKind.AllMusic, LibraryService.AllKey,"Toda tu música", $"{Library.Songs.Count:N0} canciones", ""),
            new(CardKind.History, "history", "Historial", "Lo último que escuchaste", ""),
            new(CardKind.Recent, "recent", "Añadidas recientemente", "Novedades en tu carpeta", ""),
        };

        var sections = new List<HomeSection>();

        var recent = Settings.RecentContexts.Select(ContextCard).Where(c => c != null).Cast<CardVm>().Take(12).ToList();
        if (recent.Count > 0) sections.Add(new HomeSection("Reproducido recientemente", recent));

        var top = UserData.Stats.Where(kv => kv.Value.Count > 0)
            .OrderByDescending(kv => kv.Value.Count).ThenByDescending(kv => kv.Value.Seconds)
            .Select(kv => Library.Find(kv.Key)).Where(s => s != null).Take(12)
            .Select(s => new CardVm(CardKind.Song, s!.Path, s.Title, s.Artist, "") { Song = s }).ToList();
        if (top.Count > 0) sections.Add(new HomeSection("Tus más escuchadas", top));

        if (Playlists.Count > 0)
            sections.Add(new HomeSection("Tus playlists", Playlists.Select(PlaylistCard).ToList()));

        var albums = Library.GetAlbums();
        var newest = albums.OrderByDescending(a => a.Songs.Max(s => s.CreatedTicks)).Take(12)
            .Select(AlbumCard).ToList();
        if (newest.Count > 0) sections.Add(new HomeSection("Añadido recientemente", newest));

        var daily = new Random(DateTime.Today.DayOfYear + DateTime.Today.Year * 400);
        var rediscover = albums.OrderBy(_ => daily.Next()).Take(12).Select(AlbumCard).ToList();
        if (rediscover.Count > 0) sections.Add(new HomeSection("Redescubre", rediscover));

        var folders = Library.PresentRoots.SelectMany(Library.GetChildren).Select(FolderCard).ToList();
        if (folders.Count > 0) sections.Add(new HomeSection("Carpetas", folders));

        return new HomePage
        {
            Title = "Inicio",
            Glyph = "",
            Breadcrumb = "Inicio",
            Greeting = greeting,
            DateText = Capitalize(DateTime.Now.ToString("dddd, d 'de' MMMM", Es)),
            QuickLinks = quick,
            Sections = sections,
        };
    }

    private CardVm FolderCard(string path) => FolderCard(path, false);

    private CardVm FolderCard(string path, bool allowDownload)
    {
        int n = Library.GetCount(path);
        int sub = Library.GetChildren(path).Count;
        var songs = n == 1 ? "1 canción" : $"{n:N0} canciones";
        return new CardVm(CardKind.Folder, path, FolderName(path), sub > 0 ? $"{sub} carpetas · {songs}" : songs) { AllowDownload = allowDownload };
    }

    private static CardVm AlbumCard(AlbumInfo a) => new(CardKind.Album, a.Key, a.Name, a.Artist, "") { Song = a.Songs[0] };

    private static CardVm PlaylistCard(Playlist p) => new(CardKind.Playlist, p.Id, p.Name, p.CountText, "");

    private CardVm? ContextCard(string id)
    {
        var (kind, key) = SplitContext(id);
        switch (kind)
        {
            case "folder" when Library.HasFolder(key):
                return Eq(key, LibraryService.AllKey)
                    ? new CardVm(CardKind.AllMusic, key, "Toda tu música", $"{Library.Songs.Count:N0} canciones", "")
                    : FolderCard(key);
            case "playlist":
                return Playlists.FirstOrDefault(p => p.Id == key) is { } pl ? PlaylistCard(pl) : null;
            case "album":
                return Library.GetAlbum(key) is { } a ? AlbumCard(a) : null;
            case "artist":
                var songs = Library.SongsByArtist(key);
                return songs.Count > 0 ? new CardVm(CardKind.Artist, key, key, "Artista", "") { Song = songs[0] } : null;
            case "favorites":
                return new CardVm(CardKind.Favorites, "favorites", "Favoritas", $"{UserData.Favorites.Count} canciones", "");
            default:
                return null;
        }
    }

    // ======================================================================
    // Búsqueda
    // ======================================================================

    private void RunSearch(SearchPage page)
    {
        var q = page.Query.Trim();
        if (q.Length == 0)
        {
            page.SetResults(new(), 0, new(), new(), new(), new());
            return;
        }
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ci = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        bool Match(string text) => terms.All(t => ci.IndexOf(text, t, opts) >= 0);

        var songs = Library.Songs.Where(s => Match($"{s.Title} {s.Artist} {s.Album} {s.RelativePath}")).ToList();
        var rows = songs.Take(30).Select((s, i) => new SongRow(s, i, null)).ToList();
        var artists = Library.GetArtists().Where(a => Match(a.Name)).Take(10)
            .Select(a => new CardVm(CardKind.Artist, a.Name, a.Name, $"{a.Songs.Count} canciones", "") { Song = a.Songs[0] }).ToList();
        var albums = Library.GetAlbums().Where(a => Match(a.Name + " " + a.Artist)).Take(10).Select(AlbumCard).ToList();
        var folders = Folders.SelectMany(r => r.Descendants()).Where(n => Match(n.Name)).Take(12)
            .Select(n => FolderCard(n.Path)).ToList();
        var pls = Playlists.Where(p => Match(p.Name)).Select(PlaylistCard).ToList();
        _searchResults = songs;
        page.SetResults(rows, songs.Count, artists, albums, folders, pls);
    }

    private List<Song> _searchResults = new();

    public void OpenAllSearchSongs(SearchPage page)
    {
        var q = page.Query.Trim();
        Navigate(new SongListPage("search:" + q, ListKind.Search, _searchResults.ToList())
        {
            Title = $"«{q}»",
            Glyph = "",
            Breadcrumb = "Buscar / Canciones",
            BaseProperties = { new PropertyItem("", "Tipo", "Resultados de búsqueda") },
        });
    }

    // ======================================================================
    // Reproducción
    // ======================================================================

    public void PlayPage(SongListPage page)
    {
        if (Player.ContextId == page.Id && Player.Current != null)
        {
            Player.TogglePlayPause();
            return;
        }
        Player.PlayContext(page.Id, page.Title, page.PlayAll ?? page.Visible, -1);
    }

    public void ShufflePage(SongListPage page)
    {
        Player.Shuffle = true;
        Player.PlayContext(page.Id, page.Title, page.PlayAll ?? page.Visible, -1);
    }

    public void PlayRow(SongListPage page, SongRow row)
    {
        if (row.Song.IsCurrent && Player.ContextId == page.Id)
        {
            Player.TogglePlayPause();
            return;
        }
        Player.PlayContext(page.Id, page.Title, page.Visible, row.Index);
    }

    public void PlaySearchRow(SearchPage page, SongRow row)
    {
        var list = page.Songs.Select(r => r.Song).ToList();
        Player.PlayContext("search:" + page.Query.Trim(), $"Búsqueda «{page.Query.Trim()}»", list, row.Index);
    }

    public void PlaySong(Song song)
    {
        if (song.IsCurrent) { Player.TogglePlayPause(); return; }
        var songs = Library.SongsUnder(song.Directory);
        Player.PlayContext("folder:" + song.Directory, FolderName(song.Directory), songs, Math.Max(0, songs.IndexOf(song)));
    }

    public void PlayFolder(string path, bool shuffle = false) => PlayContextId("folder:" + path, shuffle);

    public void PlayCard(CardVm card, bool shuffle = false)
    {
        if (card.Kind == CardKind.Song && card.Song != null) { PlaySong(card.Song); return; }
        var id = card.Kind switch
        {
            CardKind.Folder or CardKind.AllMusic => "folder:" + card.Key,
            CardKind.Album => "album:" + card.Key,
            CardKind.Artist => "artist:" + card.Key,
            CardKind.Playlist => "playlist:" + card.Key,
            CardKind.Favorites => "favorites",
            CardKind.History => "history",
            CardKind.Recent => "recent",
            _ => null,
        };
        if (id != null) PlayContextId(id, shuffle);
    }

    public void PlayContextId(string id, bool shuffle = false)
    {
        if (IsLoading) return;
        if (!shuffle && Player.ContextId == id && Player.Current != null)
        {
            Player.TogglePlayPause();
            return;
        }
        var songs = SongsForContext(id);
        if (songs == null || songs.Count == 0) return;
        if (shuffle) Player.Shuffle = true;
        Player.PlayContext(id, ContextName(id), songs, -1);
    }

    public void TogglePlay()
    {
        if (Player.Current != null) Player.TogglePlayPause();
        else if (CurrentPage is SongListPage p && !p.IsEmpty) PlayPage(p);
        else if (!IsLoading) PlayFolder(LibraryService.AllKey);
    }

    public void StartRadio(Song seed)
    {
        var list = new List<Song> { seed };
        list.AddRange(BuildRadio(seed, list, 40));
        Player.PlayContext("radio:" + seed.Path, $"Radio · {seed.Title}", list, 0);
        ShowToast($"Radio de «{seed.Title}»");
    }

    /// <summary>Canciones "parecidas": mismo artista, misma carpeta principal y algo al azar.</summary>
    private List<Song> BuildRadio(Song seed, IReadOnlyCollection<Song> exclude, int count)
    {
        var skip = new HashSet<Song>(exclude);
        foreach (var h in UserData.History.Take(40))
            if (Library.Find(h.P) is { } s) skip.Add(s);

        List<Song> Pick(IEnumerable<Song> src, int n) => src.Where(s => !skip.Contains(s)).OrderBy(_ => _rng.Next()).Take(n).ToList();

        var result = new List<Song>();
        void Add(IEnumerable<Song> items)
        {
            foreach (var s in items) { if (skip.Add(s)) result.Add(s); }
        }
        Add(Pick(Library.SongsByArtist(seed.PrimaryArtist), count / 3));
        if (seed.TopFolder.Length > 0)
            Add(Pick(Library.Songs.Where(s => string.Equals(s.TopFolder, seed.TopFolder, StringComparison.OrdinalIgnoreCase)), count / 3));
        var favs = Library.Resolve(UserData.Favorites);
        Add(Pick(favs, count / 6));
        Add(Pick(Library.Songs, count - result.Count));
        return result.OrderBy(_ => _rng.Next()).ToList();
    }

    public void Enqueue(IList<Song> songs)
    {
        Player.Enqueue(songs);
        ShowToast(songs.Count == 1 ? "Añadida a la cola" : $"{songs.Count} canciones añadidas a la cola");
    }

    public void PlayNext(IList<Song> songs)
    {
        Player.PlayNext(songs);
        ShowToast(songs.Count == 1 ? "Sonará a continuación" : $"{songs.Count} canciones sonarán a continuación");
    }

    private void OnContextStarted(string id)
    {
        var (kind, _) = SplitContext(id);
        if (kind is not ("folder" or "playlist" or "album" or "artist" or "favorites")) return;
        Settings.RecentContexts.Remove(id);
        Settings.RecentContexts.Insert(0, id);
        if (Settings.RecentContexts.Count > 16) Settings.RecentContexts.RemoveRange(16, Settings.RecentContexts.Count - 16);
    }

    private void OnSongOpened(Song song)
    {
        if (song.IsCloud || !song.MetadataLoaded)
            _worker.ReadNow(song, () =>
            {
                Player.RefreshSmtc();
                if (Player.Current != song) return;
                _ = LoadNowPlayingCoverAsync(song);
                _ = LoadLyricsAsync(song);
                _ = LoadInfoAsync(song);
            });
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerService.Current):
                if (Player.Current is { } song)
                {
                    _ = LoadNowPlayingCoverAsync(song);
                    _ = LoadLyricsAsync(song);
                    _ = LoadInfoAsync(song);
                }
                else
                {
                    NowPlayingCover = null;
                }
                break;
            case nameof(PlayerService.IsPlaying):
                UpdatePagePlaying();
                break;
            case nameof(PlayerService.ContextId):
                UpdatePagePlaying();
                UpdateActiveContext();
                break;
            case nameof(PlayerService.Position):
                UpdateActiveLyric();
                break;
            case nameof(PlayerService.StopAfterCurrent):
                if (!Player.StopAfterCurrent && SleepText == "Al acabar la canción") SleepText = null;
                break;
        }
    }

    private async Task LoadNowPlayingCoverAsync(Song song)
    {
        int ver = ++_coverVersion;
        var img = await Covers.GetSongCoverAsync(song, 640, priority: true);
        if (ver == _coverVersion) NowPlayingCover = img;
    }

    private void UpdatePagePlaying()
    {
        if (CurrentPage is SongListPage p) p.IsThisPlaying = Player.IsPlaying && Player.ContextId == p.Id;
    }

    private void UpdateActiveContext()
    {
        if (_activeNode != null) _activeNode.IsActive = false;
        _activeNode = null;
        foreach (var p in Playlists) p.IsActive = p.ContextId == Player.ContextId;
        var (kind, key) = SplitContext(Player.ContextId ?? "");
        if (kind != "folder") return;
        _activeNode = FindNode(key);
        if (_activeNode != null) _activeNode.IsActive = true;
    }

    // ======================================================================
    // Letras y detalles
    // ======================================================================

    private async Task LoadLyricsAsync(Song song)
    {
        int ver = ++_lyricsVersion;
        var lines = await Task.Run(() => LyricsService.Load(song));
        if (ver != _lyricsVersion) return;
        Lyrics.Clear();
        ActiveLyric = null;
        if (lines != null)
            foreach (var l in lines) Lyrics.Add(new LyricLine(l.Time, l.Text));
        HasLyrics = Lyrics.Count > 0;
        LyricsSynced = Lyrics.Count > 0 && Lyrics[0].Time != null;
    }

    private void UpdateActiveLyric()
    {
        if (!LyricsSynced || Lyrics.Count == 0) return;
        var pos = TimeSpan.FromSeconds(Player.Position + 0.25);
        int lo = 0, hi = Lyrics.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lyrics[mid].Time <= pos) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        var active = found >= 0 ? Lyrics[found] : null;
        if (active == ActiveLyric) return;
        if (ActiveLyric != null) ActiveLyric.IsActive = false;
        for (int i = 0; i < Lyrics.Count; i++) Lyrics[i].IsPast = i < found;
        if (active != null) active.IsActive = true;
        ActiveLyric = active;
    }

    private async Task LoadInfoAsync(Song song)
    {
        int ver = ++_infoVersion;
        var tech = await Task.Run(() =>
        {
            if (song.IsCloud) return null;
            try
            {
                using var f = TagLib.File.Create(song.Path);
                var p = f.Properties;
                return new[]
                {
                    p.AudioBitrate > 0 ? $"{p.AudioBitrate} kbps" : null,
                    p.AudioSampleRate > 0 ? $"{p.AudioSampleRate / 1000.0:0.#} kHz" : null,
                    p.BitsPerSample > 0 ? $"{p.BitsPerSample} bits" : null,
                    p.AudioChannels > 0 ? (p.AudioChannels == 1 ? "Mono" : p.AudioChannels == 2 ? "Estéreo" : $"{p.AudioChannels} canales") : null,
                };
            }
            catch
            {
                return null;
            }
        });
        if (ver != _infoVersion) return;

        InfoItems.Clear();
        void Add(string glyph, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) InfoItems.Add(new PropertyItem(glyph, label, value));
        }
        Add("", "Título", song.Title);
        Add("", "Artista", song.Artist);
        Add("", "Álbum", song.Album);
        Add("", "Pista", song.TrackNumber > 0 ? song.TrackNumber.ToString() : null);
        Add("", "Duración", song.DurationText);
        Add("", "Formato", song.Format);
        if (tech != null)
        {
            Add("", "Bitrate", tech[0]);
            Add("", "Frecuencia", tech[1]);
            Add("", "Profundidad", tech[2]);
            Add("", "Canales", tech[3]);
        }
        Add("", "Tamaño", $"{song.Length / 1048576.0:0.0} MB");
        Add(song.IsCloud ? "" : "", "Disponibilidad", song.IsCloud ? "Solo en OneDrive" : "En este dispositivo");
        var st = UserData.GetStat(song.Path);
        Add("", "Reproducciones", st != null ? st.Count.ToString() : "0");
        if (st?.Last is { } last && last > DateTime.MinValue) Add("", "Última vez", RelativeTime(last));
        Add("", "Añadida", new DateTime(song.CreatedTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy", Es));
        Add("", "Ubicación", song.RelativePath);
    }

    // ======================================================================
    // Favoritas y playlists
    // ======================================================================

    public void ToggleFavorite(Song song)
    {
        bool value = !song.IsFavorite;
        UserData.SetFavorite(song, value);
        ShowToast(value ? "Añadida a Favoritas" : "Quitada de Favoritas");
    }

    public void SetFavorite(IList<Song> songs, bool value)
    {
        foreach (var s in songs) UserData.SetFavorite(s, value);
        ShowToast(value ? $"{songs.Count} añadidas a Favoritas" : $"{songs.Count} quitadas de Favoritas");
    }

    public Playlist CreatePlaylist(string name, IList<Song>? songs = null)
    {
        var p = UserData.CreatePlaylist(name, songs);
        ShowToast($"Playlist «{name}» creada");
        return p;
    }

    public void RenamePlaylist(Playlist p, string name)
    {
        UserData.RenamePlaylist(p, name);
        if (CurrentPage is SongListPage { Playlist: { } cur } && cur == p) OpenPlaylist(p, replace: true);
    }

    public void DeletePlaylist(Playlist p)
    {
        UserData.DeletePlaylist(p);
        Settings.RecentContexts.Remove(p.ContextId);
        if (CurrentPage is SongListPage { Playlist: { } cur } && cur == p) GoHome();
        ShowToast($"Playlist «{p.Name}» eliminada");
    }

    public void AddToPlaylist(Playlist p, IList<Song> songs)
    {
        int added = UserData.AddToPlaylist(p, songs);
        ShowToast(added == 0 ? $"Ya estaba en «{p.Name}»" : added == 1 ? $"Añadida a «{p.Name}»" : $"{added} canciones añadidas a «{p.Name}»");
    }

    public void RemoveFromPlaylist(Playlist p, IList<Song> songs)
    {
        UserData.RemoveFromPlaylist(p, songs);
        if (CurrentPage is SongListPage { Playlist: { } cur } && cur == p) OpenPlaylist(p, replace: true);
    }

    public void MoveInPlaylist(Playlist p, Song song, int delta)
    {
        UserData.MoveInPlaylist(p, song, delta);
        if (CurrentPage is SongListPage { Playlist: { } cur } && cur == p) OpenPlaylist(p, replace: true);
    }

    // ======================================================================
    // Descargas (OneDrive)
    // ======================================================================

    public (int cloud, long bytes) CloudSummary(IList<Song> songs) =>
        (songs.Count(s => s.IsCloud), songs.Where(s => s.IsCloud).Sum(s => s.Length));

    public void SetKeepOffline(IList<Song> songs, string? folder, bool keep)
    {
        var list = songs.ToList();
        Task.Run(() =>
        {
            if (folder != null) OneDriveService.SetKeepOffline(folder, keep);
            foreach (var s in list) OneDriveService.SetKeepOffline(s.Path, keep);
        });
        ShowToast(keep ? $"Descargando {list.Count(s => s.IsCloud)} canciones de OneDrive…" : "Liberando espacio: quedarán solo en OneDrive");
        WatchCloudState(list, targetCloud: !keep);
    }

    private void WatchCloudState(List<Song> songs, bool targetCloud)
    {
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += async (s, e) =>
        {
            ticks++;
            var states = await Task.Run(() => songs.Select(x => OneDriveService.IsCloudOnly(x.Path)).ToList());
            bool done = true;
            for (int i = 0; i < songs.Count; i++)
            {
                songs[i].IsCloud = states[i];
                if (states[i] != targetCloud) done = false;
            }
            (CurrentPage as SongListPage)?.RefreshProperties();
            if (done || ticks > 400) timer.Stop();
        };
        timer.Start();
    }

    // ======================================================================
    // Temporizador, tema, avisos
    // ======================================================================

    public void SetSleep(int minutes)
    {
        _sleepTimer.Stop();
        Player.StopAfterCurrent = false;
        SleepText = null;
        if (minutes == 0)
        {
            ShowToast("Temporizador desactivado");
        }
        else if (minutes < 0)
        {
            Player.StopAfterCurrent = true;
            SleepText = "Al acabar la canción";
            ShowToast("Se parará al acabar la canción");
        }
        else
        {
            _sleepEnd = DateTime.Now.AddMinutes(minutes);
            _sleepTimer.Start();
            SleepTick();
            ShowToast($"Se parará dentro de {minutes} min");
        }
    }

    private void SleepTick()
    {
        var left = _sleepEnd - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            _sleepTimer.Stop();
            SleepText = null;
            Player.Pause();
            return;
        }
        SleepText = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss");
    }

    /// <summary>Interruptor claro/oscuro: vuelve al último tema oscuro elegido.</summary>
    public void ToggleTheme() => SetTheme(IsDark ? "light" : Settings.DarkTheme);

    public string ThemeKey => Settings.Theme;

    public void SetTheme(string key)
    {
        var theme = Themes.Get(key);
        Settings.Theme = theme.Key;
        if (theme.IsDark) Settings.DarkTheme = theme.Key;
        IsDark = theme.IsDark;
        App.ApplyTheme(theme.Key);
        SettingsStore.Save(Settings);
        OnPropertyChanged(nameof(ThemeKey));
        ThemeChanged?.Invoke();
    }

    public void ToggleAnimations()
    {
        AnimationsOn = !AnimationsOn;
        Settings.Animations = AnimationsOn;
        SettingsStore.Save(Settings);
        ShowToast(AnimationsOn ? "Animaciones activadas" : "Animaciones desactivadas");
    }

    public void ShowToast(string text, double seconds = 2.6)
    {
        Toast = text;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
    }

    /// <summary>Activa la salida bit a bit por un dispositivo (null = desactivarla) y lo guarda.</summary>
    public void SetExclusiveOutput(string? deviceId)
    {
        if (deviceId == null)
        {
            Player.ExclusiveMode = false;
            ShowToast("Salida bit a bit desactivada");
        }
        else
        {
            Player.ExclusiveDeviceId = deviceId;
            Player.ExclusiveMode = true;
        }
        Settings.ExclusiveMode = Player.ExclusiveMode;
        Settings.ExclusiveDeviceId = Player.ExclusiveDeviceId;
        SettingsStore.Save(Settings);
    }


    // ======================================================================
    // Árbol de carpetas
    // ======================================================================

    /// <summary>La raíz del árbol que contiene esta ruta.</summary>
    private FolderNode? RootNodeFor(string path) =>
        Folders.FirstOrDefault(r => Eq(r.Path, path) || path.StartsWith(r.Path + "\\", StringComparison.OrdinalIgnoreCase));

    private FolderNode? FindNode(string path)
    {
        var node = RootNodeFor(path);
        while (node != null && !Eq(node.Path, path))
            node = node.Children.FirstOrDefault(c => Eq(c.Path, path) || path.StartsWith(c.Path + "\\", StringComparison.OrdinalIgnoreCase));
        return node;
    }

    private void SyncTree(string path)
    {
        IsSyncingTree = true;
        try
        {
            var node = RootNodeFor(path);
            while (node != null && !Eq(node.Path, path))
            {
                node.IsExpanded = true;
                node = node.Children.FirstOrDefault(c => Eq(c.Path, path) || path.StartsWith(c.Path + "\\", StringComparison.OrdinalIgnoreCase));
            }
            if (node == null) return;
            if (_selectedNode != null && _selectedNode != node) _selectedNode.IsSelected = false;
            node.IsSelected = true;
            _selectedNode = node;
        }
        finally
        {
            IsSyncingTree = false;
        }
    }

    private void ClearTreeSelection()
    {
        if (_selectedNode == null) return;
        IsSyncingTree = true;
        _selectedNode.IsSelected = false;
        IsSyncingTree = false;
        _selectedNode = null;
    }

    // ======================================================================
    // Utilidades
    // ======================================================================

    public List<Song>? SongsForContext(string id)
    {
        var (kind, key) = SplitContext(id);
        return kind switch
        {
            "folder" when Library.HasFolder(key) => Library.SongsUnder(key),
            "playlist" => Playlists.FirstOrDefault(p => p.Id == key) is { } pl ? Library.Resolve(pl.Songs) : null,
            "album" => Library.GetAlbum(key)?.Songs,
            "artist" => Library.SongsByArtist(key),
            "favorites" => Library.Resolve(UserData.Favorites),
            "history" => Library.Resolve(UserData.History.Take(500).Select(h => h.P)),
            "recent" => Library.Songs.OrderByDescending(s => s.CreatedTicks).Take(300).ToList(),
            _ => null,
        };
    }

    private string ContextName(string id)
    {
        var (kind, key) = SplitContext(id);
        return kind switch
        {
            "folder" => FolderName(key),
            "playlist" => Playlists.FirstOrDefault(p => p.Id == key)?.Name ?? "Playlist",
            "album" => Library.GetAlbum(key)?.Name ?? "Álbum",
            "artist" => key,
            "favorites" => "Favoritas",
            "history" => "Historial",
            "recent" => "Añadidas recientemente",
            _ => "Cola",
        };
    }

    private static (string kind, string key) SplitContext(string id)
    {
        int i = id.IndexOf(':');
        return i < 0 ? (id, "") : (id[..i], id[(i + 1)..]);
    }

    private Song? FirstSongUnder(string path)
    {
        var prefix = path + "\\";
        return Library.Songs.FirstOrDefault(s => s.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private string FolderName(string path) => Eq(path, LibraryService.AllKey) ? "Toda tu música" : Path.GetFileName(path) is { Length: > 0 } n ? n : path;

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Es) + s[1..];

    private static string Plays(int n) => n == 1 ? "1 reproducción" : $"{n} reproducciones";

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} min";
        return $"{t.Seconds} s";
    }

    private static string RelativeTime(DateTime at)
    {
        var d = DateTime.Now - at;
        if (d.TotalMinutes < 1) return "ahora mismo";
        if (d.TotalMinutes < 60) return $"hace {(int)d.TotalMinutes} min";
        if (d.TotalHours < 24 && at.Date == DateTime.Today) return $"hace {(int)d.TotalHours} h";
        if (at.Date == DateTime.Today.AddDays(-1)) return "ayer, " + at.ToString("HH:mm");
        if (d.TotalDays < 7) return at.ToString("dddd, HH:mm", Es);
        return at.ToString("d MMM yyyy", Es);
    }

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
