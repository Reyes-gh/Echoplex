using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Echoplex.ViewModels;

namespace Echoplex;

/// <summary>
/// Neoplex en la ventana: la barra de arriba sin la de título de Windows (WindowChrome), el menú de la izquierda a escala,
/// sin cortar texto y con los accesos elegidos, y el buscador de arriba. Todo se aplica al momento, sin reiniciar.
/// </summary>
public partial class MainWindow
{
    private const double TopBarHeight = 48;
    // ancho de serie del menú de la izquierda (a escala 1) y sus límites
    private const double SidebarDefault = 260, SidebarMin = 210, SidebarMax = 440, SidebarFitMax = 560;

    private double _sidebarScale = 1;
    private WindowChrome? _chrome;
    private HwndSource? _neoSource;
    private bool _maxPressed;

    /// <summary>En el constructor, antes de poner el ancho guardado: la escala y los límites del menú ya cuentan.</summary>
    private void InitNeoplex(double savedSidebarWidth)
    {
        _sidebarScale = _vm.SidebarScale;
        SidebarDock.LayoutTransform = ScaleOf(_sidebarScale);
        UpdateSidebarLimits();
        SidebarCol.Width = new GridLength(Math.Clamp(savedSidebarWidth, SidebarCol.MinWidth, SidebarCol.MaxWidth));
        TopBarRow.Height = new GridLength(_vm.TopBarOn ? TopBarHeight : 0);
        _vm.NeoplexChanged += ApplyNeoplex;
        StateChanged += (s, e) => UpdateChromeMetrics();
        SizeChanged += (s, e) => { if (WindowState == WindowState.Maximized) UpdateChromeMetrics(); };
        DpiChanged += (s, e) => UpdateChromeMetrics();
        Loaded += (s, e) =>
        {
            UpdateSidebarLimits(); // ya con las fuentes de verdad
            UpdateChromeMetrics();
        };
        FullView.IsVisibleChanged += (s, e) => UpdateCaptionLook();
    }

    /// <summary>Con la ventana ya creada: la barra de arriba (si va) y el gancho de los botones de la ventana.</summary>
    private void InitNeoplexChrome(IntPtr hwnd)
    {
        _neoSource = HwndSource.FromHwnd(hwnd);
        ApplyTopBar();
    }

    private void ApplyNeoplex()
    {
        double scale = _vm.SidebarScale;
        if (Math.Abs(scale - _sidebarScale) > 0.001)
        {
            // el mismo menú, más grande o más pequeño: el ancho crece o mengua con él (así no se corta nada)
            double width = SidebarCol.Width.IsAbsolute
                ? Math.Clamp(SidebarCol.Width.Value, SidebarCol.MinWidth, SidebarCol.MaxWidth) // lo que se ve (y lo último pedido)
                : SidebarCol.ActualWidth;
            width *= scale / _sidebarScale;
            _sidebarScale = scale;
            SidebarDock.LayoutTransform = ScaleOf(scale);
            UpdateSidebarLimits();
            SidebarCol.Width = new GridLength(Math.Clamp(width, SidebarCol.MinWidth, SidebarCol.MaxWidth));
        }
        else UpdateSidebarLimits();
        ApplyTopBar();
        _mini?.ApplyScale();
        UpdateStickyNav();
    }

    private static Transform ScaleOf(double scale) => Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);

    // ======================================================================
    // Menú de la izquierda: ancho
    // ======================================================================

    /// <summary>
    /// Límites del ancho del menú, a su escala. Con «sin cortar texto», el mínimo es lo que ocupa su acceso más largo
    /// (sin cortarlo) y se puede ensanchar más.
    /// </summary>
    private void UpdateSidebarLimits()
    {
        double s = _sidebarScale, min = SidebarMin * s, max = SidebarMax * s;
        if (_vm.SidebarFit)
        {
            min = Math.Max(min, Math.Ceiling(SidebarNeed() * s) + 1); // + el borde
            max = SidebarFitMax * s;
        }
        SidebarCol.MinWidth = min;
        SidebarCol.MaxWidth = Math.Max(min, max);
        if (SidebarCol.Width.IsAbsolute && SidebarCol.Width.Value < min) SidebarCol.Width = new GridLength(min);
    }

    /// <summary>Lo que ocupan, sin cortarse, los accesos que se ven (a escala 1), con la barra de desplazamiento.</summary>
    private double SidebarNeed()
    {
        double need = 0;
        foreach (var b in new[]
                 {
                     NavHomeButton, NavSearchButton, NavFavButton, NavHistoryButton, NavRecentButton, LastNavButton,
                     NavArtistsButton, NavAlbumsButton, NavGenresButton, NavFoldersButton,
                 })
            if (b.Visibility == Visibility.Visible) need = Math.Max(need, NeedOf(b));
        return need + 12; // la barra de desplazamiento del menú
    }

    private double NeedOf(Button b)
    {
        if (b.Content is not Panel panel) return 0;
        double w = 0;
        int n = 0;
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            n++;
            w += child.Margin.Left + child.Margin.Right +
                 (!double.IsNaN(child.Width) ? child.Width : child is TextBlock tb ? TextWidth(tb) : child.DesiredSize.Width);
        }
        if (n > 2) w += 12 * (n - 2); // «Buscar … Ctrl+K»: algo de aire entre el nombre y lo de la derecha
        return w + b.Padding.Left + b.Padding.Right + b.Margin.Left + b.Margin.Right;
    }

    /// <summary>Ancho del texto en seminegrita (como sale el acceso de la página abierta, el más ancho).</summary>
    private double TextWidth(TextBlock tb)
    {
        var ft = new FormattedText(tb.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, tb.FontStyle, FontWeights.SemiBold, tb.FontStretch), tb.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 2;
    }

    /// <summary>Doble clic en el borde del menú: su ancho de siempre (a su escala).</summary>
    private void SidebarSplitter_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.SidebarFit) return;
        SidebarCol.Width = new GridLength(Math.Clamp(SidebarDefault * _sidebarScale, SidebarCol.MinWidth, SidebarCol.MaxWidth));
        e.Handled = true;
    }

    private void ExploreFold_Click(object sender, RoutedEventArgs e) => _vm.ToggleExplore();

    /// <summary>Al bajar por el menú, el menú fijo aparece cuando el último acceso de arriba (de los que se ven) sale de la vista.</summary>
    private void UpdateStickyNav()
    {
        var last = new FrameworkElement[] { LastNavButton, NavRecentButton, NavHistoryButton, NavFavButton, NavSearchButton, NavHomeButton }
            .FirstOrDefault(b => b.Visibility == Visibility.Visible);
        var want = Visibility.Collapsed;
        if (last is { IsLoaded: true })
        {
            double bottom = last.TranslatePoint(new Point(0, last.ActualHeight), SidebarScroll).Y;
            if (bottom <= 0) want = Visibility.Visible;
        }
        if (StickyNav.Visibility != want) StickyNav.Visibility = want;
    }

    // ======================================================================
    // Barra de arriba (sin la barra de título de Windows)
    // ======================================================================

    private void ApplyTopBar()
    {
        bool on = _vm.TopBarOn;
        TopBarRow.Height = new GridLength(on ? TopBarHeight : 0);
        if (_neoSource == null) return; // aún sin ventana: se aplica al crearla
        if (on && _chrome == null)
        {
            _chrome = new WindowChrome
            {
                CaptionHeight = TopBarHeight,
                ResizeBorderThickness = new Thickness(6),
                // un píxel de cristal abajo: Windows sigue dibujando la sombra de la ventana
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
                NonClientFrameEdges = NonClientFrameEdges.None,
            };
            WindowChrome.SetWindowChrome(this, _chrome);
            // el gancho, por delante del de WindowChrome (los últimos en llegar son los primeros en responder)
            _neoSource.RemoveHook(CaptionHook);
            _neoSource.AddHook(CaptionHook);
        }
        else if (!on && _chrome != null)
        {
            _neoSource.RemoveHook(CaptionHook);
            WindowChrome.SetWindowChrome(this, null);
            _chrome = null;
            RootGrid.Margin = new Thickness(0);
            ApplyTitleBar(); // la barra de Windows, otra vez con los colores del tema
        }
        UpdateChromeMetrics();
    }

    /// <summary>
    /// Lo que depende del estado de la ventana: maximizada, Windows la saca unos píxeles por cada lado (el marco que ya no
    /// se ve), así que el contenido se mete hacia dentro; la franja que arrastra la ventana sigue a la barra (y al zoom).
    /// </summary>
    private void UpdateChromeMetrics()
    {
        bool on = _chrome != null;
        CaptionButtons.Visibility = on && !_screenFull ? Visibility.Visible : Visibility.Collapsed;
        UpdateCaptionLook();
        if (_chrome == null) return;
        bool max = WindowState == WindowState.Maximized;
        var pad = max && !_screenFull ? MaximizedOverhang() : new Thickness(0);
        if (RootGrid.Margin != pad) RootGrid.Margin = pad;
        _chrome.CaptionHeight = _screenFull ? 0 : pad.Top + TopBarHeight * _vm.Settings.Zoom;
        _chrome.ResizeBorderThickness = max || _screenFull ? new Thickness(0) : new Thickness(6);
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Restaurar" : "Maximizar";
    }

    /// <summary>Cuánto sobresale la ventana maximizada del área de trabajo de su pantalla, en unidades de WPF.</summary>
    private Thickness MaximizedOverhang()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return new Thickness(0);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) return new Thickness(0);
        var w = info.rcWork;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Thickness(Math.Max(0, w.Left - r.Left) / dpi.DpiScaleX, Math.Max(0, w.Top - r.Top) / dpi.DpiScaleY,
            Math.Max(0, r.Right - w.Right) / dpi.DpiScaleX, Math.Max(0, r.Bottom - w.Bottom) / dpi.DpiScaleY);
    }

    /// <summary>
    /// Con la pantalla completa abierta, los botones de la ventana van en blanco (su fondo siempre es oscuro) y su «salir»
    /// se aparta a su izquierda, con el icono de volver a la ventana para no confundirse con cerrar.
    /// </summary>
    private void UpdateCaptionLook()
    {
        bool over = CaptionButtons.Visibility == Visibility.Visible && FullView.Visibility == Visibility.Visible;
        if (over && CaptionButtons.Resources.Count == 0)
            foreach (var key in new[] { "TextBrush", "HoverBrush", "SelectedBrush" })
                CaptionButtons.Resources[key] = FullView.Resources[key];
        else if (!over && CaptionButtons.Resources.Count > 0) CaptionButtons.Resources.Clear();
        FullChrome.Margin = CaptionButtons.Visibility == Visibility.Visible ? new Thickness(0, 6, 16 + 3 * 46, 0) : new Thickness(0, 14, 16, 0);
        if (FullChrome.Children.Count > 0 && FullChrome.Children[0] is Button exit)
            exit.Content = CaptionButtons.Visibility == Visibility.Visible ? "" : "";
    }

    private void CaptionMin_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CaptionMax_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CaptionClose_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>
    /// El botón de maximizar se le presenta a Windows como el suyo (HTMAXBUTTON): así, al pasar por encima, Windows 11
    /// enseña su menú para ajustar la ventana a una parte de la pantalla. Sus clics llegan entonces como de fuera de la
    /// ventana y se atienden aquí; su aspecto de «encima» y «pulsado» se pone a mano (Tag).
    /// </summary>
    private IntPtr CaptionHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2,
            WM_NCMOUSELEAVE = 0x02A2, WM_MOUSEMOVE = 0x0200, HTMAXBUTTON = 9;
        if (_chrome == null) return IntPtr.Zero;
        switch (msg)
        {
            case WM_NCHITTEST:
                if (OverMaxButton(lParam))
                {
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
                break;
            case WM_NCMOUSEMOVE:
                if (wParam.ToInt64() == HTMAXBUTTON)
                {
                    SetMaxLook(_maxPressed ? "press" : "hover");
                    var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 | 0x10, hwndTrack = hwnd };
                    TrackMouseEvent(ref track); // TME_LEAVE | TME_NONCLIENT: avisa al salir
                }
                else ClearMaxLook();
                break;
            case WM_NCMOUSELEAVE:
            case WM_MOUSEMOVE:
                ClearMaxLook();
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt64() == HTMAXBUTTON:
                _maxPressed = true;
                SetMaxLook("press");
                handled = true; // sin el bucle de Windows, que pintaría su botón antiguo
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                bool click = _maxPressed;
                ClearMaxLook();
                if (click) ToggleMaximize();
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void SetMaxLook(string look)
    {
        if (!Equals(MaxButton.Tag, look)) MaxButton.Tag = look;
    }

    private void ClearMaxLook()
    {
        _maxPressed = false;
        if (MaxButton.Tag != null) MaxButton.Tag = null;
    }

    private bool OverMaxButton(IntPtr lParam)
    {
        if (CaptionButtons.Visibility != Visibility.Visible || !MaxButton.IsVisible || SettingsDim.Visibility == Visibility.Visible) return false;
        long v = lParam.ToInt64();
        int x = (short)(v & 0xFFFF), y = (short)((v >> 16) & 0xFFFF);
        var a = MaxButton.PointToScreen(new Point(0, 0));
        var b = MaxButton.PointToScreen(new Point(MaxButton.ActualWidth, MaxButton.ActualHeight));
        return x >= Math.Min(a.X, b.X) && x < Math.Max(a.X, b.X) && y >= Math.Min(a.Y, b.Y) && y < Math.Max(a.Y, b.Y);
    }

    // ======================================================================
    // Buscador de arriba
    // ======================================================================

    /// <summary>Ctrl+K, Buscar en el menú: la página de Buscar y, con la barra de arriba, el cursor en su buscador.</summary>
    private void OpenSearchUi()
    {
        _vm.OpenSearch();
        if (!_vm.TopBarOn) return;
        TopSearchBox.Focus();
        TopSearchBox.SelectAll();
    }

    private void TopSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // Esc: primero borra lo escrito; con el cuadro vacío, deja de escribir
        if (TopSearchBox.Text.Length > 0) _vm.TopSearch = "";
        else Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void TopSearchClear_Click(object sender, RoutedEventArgs e)
    {
        _vm.TopSearch = "";
        TopSearchBox.Focus();
    }

    private void NowPlayingPlay_Click(object sender, RoutedEventArgs e) => _vm.TogglePlay();

    /// <summary>
    /// La tarjeta del vinilo según su ancho: el texto se queda al menos 200 y el vinilo se lleva casi la mitad (hasta
    /// su tamaño entero); si así no llegaría a 150 de ancho, no sale y el texto ocupa toda la tarjeta. El título va en una
    /// línea o, si el texto queda estrecho, en dos; el alto de la tarjeta sigue a eso y a nada más (ni a la letra ni a la
    /// canción), así que no se mueve mientras suena.
    /// </summary>
    private void NowPlayingCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || sender is not Grid { ColumnDefinitions.Count: 2 } grid) return;
        const double TextMin = 200, DeckMax = 320, DeckMin = 150, TwoLinesBelow = 380, Line = 36; // el vinilo, con su margen de 20
        double width = grid.ActualWidth;
        double deck = Math.Min(DeckMax, Math.Min(width * 0.48, width - TextMin));
        if (deck < DeckMin) deck = 0;
        if (Math.Abs(grid.ColumnDefinitions[1].Width.Value - deck) > 0.5) grid.ColumnDefinitions[1].Width = new GridLength(deck);
        foreach (var disc in grid.Children.OfType<Vinyl>())
            disc.Visibility = deck > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool twoLines = width - deck < TwoLinesBelow;
        if (grid.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault(t => Equals(t.Tag, "title")) is { } title)
        {
            title.TextWrapping = twoLines ? TextWrapping.Wrap : TextWrapping.NoWrap;
            title.MaxHeight = twoLines ? 2 * Line : Line;
        }
        if (grid.Parent is FrameworkElement { Parent: Border card })
        {
            double height = twoLines ? 244 + Line : 244;
            if (Math.Abs(card.Height - height) > 0.5) card.Height = height;
        }
    }

    /// <summary>Lo de dentro de la tarjeta (la portada difuminada, las luces), recortado a sus esquinas redondeadas.</summary>
    private void NowPlayingCardClip_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement inner) return;
        var clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);
        clip.Freeze();
        inner.Clip = clip;
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize, dwFlags;
        public IntPtr hwndTrack;
        public int dwHoverTime;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);
}
