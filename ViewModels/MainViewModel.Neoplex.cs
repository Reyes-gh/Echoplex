using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>
/// Qué accesos del menú de la izquierda se ven, para enlazarlos en XAML como NavShown[clave]. Sin Neoplex se ven todos;
/// con Neoplex, los que no estén quitados en Ajustes. Carpetas y las playlists no están aquí: siempre se ven.
/// </summary>
public sealed class NavShownMap : INotifyPropertyChanged
{
    private readonly MainViewModel _vm;

    public NavShownMap(MainViewModel vm) => _vm = vm;

    public bool this[string key] => _vm.IsNavShown(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>
/// Neoplex: otra cara de Echoplex, apagada de serie (Ajustes → Apariencia). Cada parte se quita por separado y todas se
/// aplican al momento, sin reiniciar. Aquí, los ajustes y lo que la vista necesita saber de ellos; cómo se ve, en
/// MainWindow.Neoplex.cs.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Los accesos del menú de la izquierda que se pueden quitar (clave, nombre, icono), en su orden.</summary>
    public static readonly (string Key, string Name, string Glyph)[] OptionalNav =
    {
        ("home", "Inicio", ""), ("search", "Buscar", ""), ("favorites", "Favoritas", ""),
        ("history", "Historial", ""), ("recent", "Añadidas recientemente", ""), ("stats", "Tu resumen", ""),
        ("artists", "Artistas", ""), ("albums", "Álbumes", ""), ("genres", "Géneros", ""),
    };

    private NavShownMap? _navShown;
    public NavShownMap NavShown => _navShown ??= new NavShownMap(this);

    /// <summary>Algo de Neoplex cambió: la ventana vuelve a aplicar la barra de arriba, el tamaño del menú…</summary>
    public event Action? NeoplexChanged;

    // ---------- interruptores (Ajustes) ----------

    public bool Neoplex
    {
        get => Settings.Neoplex;
        set => SetNeo(Settings.Neoplex, value, v => Settings.Neoplex = v);
    }

    public bool NeoBigSidebar
    {
        get => Settings.NeoBigSidebar;
        set => SetNeo(Settings.NeoBigSidebar, value, v => Settings.NeoBigSidebar = v);
    }

    /// <summary>Tamaño del menú de la izquierda en %, de 100 a 150, de 5 en 5.</summary>
    public double NeoSidebarPercent
    {
        get => Math.Round(ClampScale(Settings.NeoSidebarScale) * 100);
        set
        {
            double scale = Math.Clamp(Math.Round(value / 5) * 5, 100, 150) / 100;
            if (Math.Abs(scale - Settings.NeoSidebarScale) < 0.001) return;
            Settings.NeoSidebarScale = scale;
            SettingsStore.Save(Settings);
            OnPropertyChanged();
            RefreshNeoplex();
        }
    }

    public bool NeoSidebarFit
    {
        get => Settings.NeoSidebarFit;
        set => SetNeo(Settings.NeoSidebarFit, value, v => Settings.NeoSidebarFit = v);
    }

    public bool NeoExploreFoldable
    {
        get => Settings.NeoExploreFoldable;
        set => SetNeo(Settings.NeoExploreFoldable, value, v => Settings.NeoExploreFoldable = v);
    }

    public bool NeoNowPlaying
    {
        get => Settings.NeoNowPlaying;
        set => SetNeo(Settings.NeoNowPlaying, value, v => Settings.NeoNowPlaying = v);
    }

    public bool NeoTopBar
    {
        get => Settings.NeoTopBar;
        set => SetNeo(Settings.NeoTopBar, value, v => Settings.NeoTopBar = v);
    }

    public bool NeoMiniResize
    {
        get => Settings.NeoMiniResize;
        set => SetNeo(Settings.NeoMiniResize, value, v => Settings.NeoMiniResize = v);
    }

    public bool NeoSettingsResize
    {
        get => Settings.NeoSettingsResize;
        set => SetNeo(Settings.NeoSettingsResize, value, v => Settings.NeoSettingsResize = v);
    }

    private void SetNeo(bool current, bool value, Action<bool> store, [CallerMemberName] string? name = null)
    {
        if (current == value) return;
        store(value);
        SettingsStore.Save(Settings);
        OnPropertyChanged(name);
        RefreshNeoplex();
    }

    /// <summary>Un acceso del menú, quitado o puesto otra vez (solo cuenta con Neoplex).</summary>
    public void SetNavShown(string key, bool shown)
    {
        var hidden = Settings.NeoHiddenNav ??= new List<string>();
        if (shown) hidden.RemoveAll(k => k == key);
        else if (!hidden.Contains(key)) hidden.Add(key);
        SettingsStore.Save(Settings);
        RefreshNeoplex();
    }

    /// <summary>El acceso se ve: sin Neoplex, todos; con Neoplex, los que no se han quitado en Ajustes.</summary>
    public bool IsNavShown(string key) => !Settings.Neoplex || Settings.NeoHiddenNav?.Contains(key) != true;

    // ---------- lo que se aplica (cada parte, solo con Neoplex puesto) ----------

    private static double ClampScale(double scale) => double.IsFinite(scale) ? Math.Clamp(scale, 1, 1.5) : 1.2;

    /// <summary>Escala del menú de la izquierda (iconos, texto y alturas): 1 sin Neoplex.</summary>
    public double SidebarScale => Settings.Neoplex && Settings.NeoBigSidebar ? ClampScale(Settings.NeoSidebarScale) : 1;
    public bool SidebarFit => Settings.Neoplex && Settings.NeoSidebarFit;
    public bool ExploreFoldable => Settings.Neoplex && Settings.NeoExploreFoldable;
    /// <summary>Se ven los accesos de Explorar (sin Neoplex, o plegable y desplegado).</summary>
    public bool ExploreOpen => !ExploreFoldable || !Settings.NeoExploreFolded;
    public bool TopBarOn => Settings.Neoplex && Settings.NeoTopBar;
    public bool MiniResizable => Settings.Neoplex && Settings.NeoMiniResize;
    public bool SettingsResizable => Settings.Neoplex && Settings.NeoSettingsResize;
    /// <summary>El vinilo de Inicio: con su parte de Neoplex puesta y algo sonando (o en pausa).</summary>
    public bool ShowNowPlayingCard => Settings.Neoplex && Settings.NeoNowPlaying && Player.Current != null;

    public void ToggleExplore()
    {
        if (!ExploreFoldable) return;
        Settings.NeoExploreFolded = !Settings.NeoExploreFolded;
        SettingsStore.Save(Settings);
        OnPropertyChanged(nameof(ExploreOpen));
    }

    private static readonly string[] NeoDerived =
    {
        nameof(SidebarScale), nameof(SidebarFit), nameof(ExploreFoldable), nameof(ExploreOpen), nameof(TopBarOn),
        nameof(MiniResizable), nameof(SettingsResizable), nameof(ShowNowPlayingCard),
    };

    private void RefreshNeoplex()
    {
        foreach (var name in NeoDerived) OnPropertyChanged(name);
        NavShown.Refresh();
        NeoplexChanged?.Invoke();
    }

    // ---------- el buscador de la barra de arriba ----------

    private string _topSearch = "";
    private bool _settingTopSearch;

    /// <summary>
    /// El buscador siempre a la vista (barra de arriba de Neoplex). Al escribir lleva a Buscar con lo escrito; en Buscar,
    /// es su búsqueda. Fuera de Buscar se vacía.
    /// </summary>
    public string TopSearch
    {
        get => _topSearch;
        set
        {
            value ??= "";
            if (_topSearch == value) return;
            _topSearch = value;
            _settingTopSearch = true;
            try
            {
                if (CurrentPage is SearchPage page) page.Query = value;
                else if (value.Trim().Length > 0)
                {
                    OpenSearch();
                    if (CurrentPage is SearchPage opened) opened.Query = value;
                }
            }
            finally
            {
                _settingTopSearch = false;
            }
            OnPropertyChanged();
        }
    }

    /// <summary>Al cambiar de página: el buscador de arriba enseña la búsqueda de la página (o nada, fuera de Buscar).</summary>
    private void SyncTopSearch()
    {
        if (_settingTopSearch) return;
        var query = (CurrentPage as SearchPage)?.Query ?? "";
        if (_topSearch == query) return;
        _topSearch = query;
        OnPropertyChanged(nameof(TopSearch));
    }

    // ---------- la tarjeta del vinilo de Inicio ----------

    /// <summary>Las luces de la tarjeta: dos manchas con los colores de la portada, como en la pantalla completa.</summary>
    [ObservableProperty] private Brush _nowPlayingLightA = Light(null, 0);
    [ObservableProperty] private Brush _nowPlayingLightB = Light(null, 1);
    /// <summary>«FLAC · 24 bits · 44,1 kHz» de lo que suena.</summary>
    [ObservableProperty] private string? _nowPlayingFormat;

    partial void OnNowPlayingCoverChanged(BitmapSource? value)
    {
        NowPlayingLightA = Light(value, 0);
        NowPlayingLightB = Light(value, 1);
    }

    private static Brush Light(BitmapSource? cover, int index)
    {
        var colors = Palette.Extract(cover);
        var c = colors[index % colors.Length];
        var brush = new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(110, c.R, c.G, c.B), 0), new(Color.FromArgb(44, c.R, c.G, c.B), 0.5), new(Color.FromArgb(0, c.R, c.G, c.B), 1),
        });
        brush.Freeze();
        return brush;
    }
}
