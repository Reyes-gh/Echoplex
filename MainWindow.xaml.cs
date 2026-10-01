using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Echoplex.Models;
using Echoplex.Services;
using Echoplex.ViewModels;

namespace Echoplex;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand OpenArtistCommand = new("Ir al artista", "OpenArtist", typeof(MainWindow));
    public static readonly RoutedUICommand OpenAlbumCommand = new("Ir al álbum", "OpenAlbum", typeof(MainWindow));

    private readonly MainViewModel _vm;
    private Slider? _dragSlider;
    private MiniPlayerWindow? _mini;
    private ImageSource? _playImage;
    private ImageSource? _pauseImage;

    public MainWindow()
    {
        _vm = new MainViewModel(Dispatcher);
        App.ApplyTheme(_vm.ThemeKey);
        // orden y ancho de las columnas de la tabla, antes de que se pinte ninguna fila
        SongColumns.Load(_vm.Settings.ColumnOrder, _vm.Settings.ColumnSizes, _vm.Settings.HiddenColumns);
        SongColumns.Committed += () =>
        {
            _vm.Settings.ColumnOrder = SongColumns.Order;
            _vm.Settings.ColumnSizes = SongColumns.Sizes;
            _vm.Settings.HiddenColumns = SongColumns.Hidden;
            SettingsStore.Save(_vm.Settings);
        };
        InitializeComponent();
        DataContext = _vm;

        var s = _vm.Settings;
        Width = Math.Max(MinWidth, s.Width);
        Height = Math.Max(MinHeight, s.Height);
        if (s.Maximized) WindowState = WindowState.Maximized;
        SidebarCol.Width = new GridLength(Math.Clamp(s.SidebarWidth, SidebarCol.MinWidth, SidebarCol.MaxWidth));
        RightPanel.Width = Math.Clamp(s.RightPanelWidth, RightMin, RightMax);
        RightPanel.SizeChanged += (o, e) =>
        {
            _vm.DetailsCompact = RightPanel.ActualWidth < 300;
            ReleaseInfoScrollLock();
        };
        InfoScroll.ScrollChanged += (o, e) => WatchInfoScrollFlips();
        ((FrameworkElement)PlayerRight.Parent).SizeChanged += (o, e) => UpdatePlayerExtras();
        PlayerExtras.SizeChanged += (o, e) => UpdatePlayerExtras(); // p. ej. aparece el texto del temporizador
        ExtrasPopup.Opened += (o, e) => ExtrasToggle.IsHitTestVisible = false;
        ApplyZoom(s.Zoom, announce: false);
        PreviewMouseWheel += (o, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            ApplyZoom(_vm.Settings.Zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
            e.Handled = true;
        };

        _vm.Player.PropertyChanged += Player_PropertyChanged;
        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.PageChanging += OnPageChanging;
        InitAnimations();
        _vm.ThemeChanged += ApplyTitleBar;

        CommandBindings.Add(new CommandBinding(OpenArtistCommand, (o, e) => { if (e.Parameter is Song song) _vm.OpenArtist(song.PrimaryArtist); }));
        CommandBindings.Add(new CommandBinding(OpenAlbumCommand, (o, e) => { if (e.Parameter is Song song) _vm.OpenAlbum(LibraryService.AlbumKey(song)); }));

        AttachSliderDrag(SeekSlider, v => _vm.Player.Seek(v));
        AttachSliderDrag(VolumeSlider, null);
        VolumeSlider.PreviewMouseWheel += (o, e) =>
        {
            // pasos más finos en volúmenes bajos: 0,5 puntos por debajo del 10 %, 1 punto hasta el 25 %, 5 por encima
            double v = _vm.Player.Volume, up = e.Delta > 0 ? 1 : -1;
            double step = (up > 0 ? v : v - 1e-6) < 0.1 ? 0.005 : (up > 0 ? v : v - 1e-6) < 0.25 ? 0.01 : 0.05;
            _vm.Player.Volume = Math.Clamp(Math.Round((v + up * step) / step) * step, 0, 1);
            e.Handled = true;
        };
        VolumeAdvanced.CloseRequested += () => VolumePopup.IsOpen = false;
        // si mueves la letra con la rueda, el deslizamiento automático se aparta
        LyricsScroll.PreviewMouseWheel += (o, e) => StopLyricsGlide();

        // Los menús contextuales de plantillas se conectan aquí (conectarlos en XAML falla con x:Shared="False").
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(AnyMenu_Opened));

        SetupTaskbar();
        SourceInitialized += (o, e) =>
        {
            ApplyTitleBar();
            _vm.Player.AttachWindow(new WindowInteropHelper(this).Handle);
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _vm.RestartRequested += () =>
        {
            Close(); // guarda ajustes, cola y posición como siempre
            UpdateService.Restart();
        };
        await _vm.InitializeAsync();
        await _vm.CheckForUpdatesOnStartupAsync();
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await _vm.InstallUpdateAsync();
    private void SkipUpdate_Click(object sender, RoutedEventArgs e) => _vm.SkipUpdate();
    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => _vm.DismissUpdate();

    /// <summary>Al bajar por el panel lateral, el menú fijo aparece cuando el de navegación sale de la vista.</summary>
    private void SidebarScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!LastNavButton.IsLoaded) return;
        double bottom = LastNavButton.TranslatePoint(new Point(0, LastNavButton.ActualHeight), SidebarScroll).Y;
        var want = bottom <= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (StickyNav.Visibility != want) StickyNav.Visibility = want;
    }

    /// <summary>Interruptor "Nombres fichero" de la página de una carpeta.</summary>
    private void PageFileNames_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentPage is SongListPage { IsRealFolder: true, FolderPath: { } folder } && sender is CheckBox box)
            _vm.SetFolderFileNames(folder, box.IsChecked == true);
    }

    // ---------- carátulas personalizadas ----------

    /// <summary>Elige una imagen y la pone como carátula de la carpeta.</summary>
    private async void PickCustomCover(string folder)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Carátula para «{Path.GetFileName(folder)}»",
            Filter = "Imágenes|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|Todos los archivos|*.*",
        };
        if (Directory.Exists(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(this) == true) await _vm.SetCustomCoverAsync(folder, dlg.FileName);
    }

    private void AddCoverItems(ContextMenu menu, string folder)
    {
        menu.Items.Add(Item(_vm.HasCustomCover(folder) ? "Cambiar carátula…" : "Poner carátula…", "", () => PickCustomCover(folder)));
        if (_vm.HasCustomCover(folder))
            menu.Items.Add(Item("Quitar carátula personalizada", "", () => _vm.ClearCustomCover(folder)));
    }

    /// <summary>El recuadro con "+" de una página sin carátula.</summary>
    private void PageAddCover_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentPage is SongListPage { CoverFolder: { } folder }) PickCustomCover(folder);
    }

    /// <summary>Clic derecho en la carátula de la cabecera: cambiarla o quitar la personalizada.</summary>
    private void HeaderCover_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.CurrentPage is not SongListPage { CoverFolder: { } folder }) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };
        AddCoverItems(menu, folder);
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>Clic en la portada de la cabecera: muestra u oculta la portada en grande.</summary>
    private void HeaderCover_Click(object sender, MouseButtonEventArgs e) => _vm.ShowBigCover = !_vm.ShowBigCover;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _settings?.CloseNow(); // Ajustes abierto: se cierra ya, sin esperar a su animación
        var s = _vm.Settings;
        s.Maximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.Width > 0) { s.Width = bounds.Width; s.Height = bounds.Height; }
        s.SidebarWidth = SidebarCol.ActualWidth;
        s.RightPanelWidth = RightPanel.Width;
        _mini?.Close();
        _vm.Shutdown();
    }

    // ======================================================================
    // Barra de título y barra de tareas
    // ======================================================================

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = _vm.IsDark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            int rgb = Themes.Get(_vm.ThemeKey).Caption;
            int caption = (rgb & 0xFF) << 16 | (rgb & 0xFF00) | (rgb >> 16 & 0xFF); // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        }
        catch
        {
            // Windows antiguo: barra de título estándar
        }
    }

    /// <summary>
    /// Icono de un botón de la miniatura de la barra de tareas, dibujado al tamaño en que lo muestra Windows
    /// (16 px a escala 100 %) para que no se reescale y quede nítido.
    /// </summary>
    private static ImageSource GlyphImage(string glyph, int size)
    {
        var grid = new Grid { Width = size, Height = size, Background = Brushes.Transparent };
        grid.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size * 0.75,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        grid.Measure(new Size(size, size));
        grid.Arrange(new Rect(0, 0, size, size));
        grid.UpdateLayout();
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(grid);
        bmp.Freeze();
        return bmp;
    }

    private void SetupTaskbar()
    {
        // los cuatro con la variante rellena de la misma familia (anterior y siguiente de contorno apenas se veían)
        int size = (int)Math.Round(16 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        _playImage = GlyphImage("\uF5B0", size);
        _pauseImage = GlyphImage("\uF8AE", size);
        ThumbPrev.ImageSource = GlyphImage("\uF8AC", size);
        ThumbNext.ImageSource = GlyphImage("\uF8AD", size);
        ThumbPlay.ImageSource = _vm.Player.IsPlaying ? _pauseImage : _playImage;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        SetupTaskbar();
    }

    private void ThumbPrev_Click(object? sender, EventArgs e) => _vm.Player.Previous();
    private void ThumbPlay_Click(object? sender, EventArgs e) => _vm.TogglePlay();
    private void ThumbNext_Click(object? sender, EventArgs e) => _vm.Player.Next();

    // ======================================================================
    // Sincronización con el reproductor
    // ======================================================================

    private void Player_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var p = _vm.Player;
        switch (e.PropertyName)
        {
            case nameof(p.Position):
                if (_dragSlider != SeekSlider)
                {
                    SeekSlider.Value = p.Position;
                    // el texto solo cambia una vez por segundo: no se rehace en cada tic (4 por segundo)
                    long second = double.IsFinite(p.Position) ? (long)Math.Max(0, p.Position) : 0;
                    if (second != _shownSecond)
                    {
                        _shownSecond = second;
                        PosText.Text = FormatTime(p.Position);
                    }
                }
                if (p.Duration > 0) Taskbar.ProgressValue = Math.Clamp(p.Position / p.Duration, 0, 1);
                break;
            case nameof(p.Duration):
                SeekSlider.Maximum = Math.Max(p.Duration, 0.001);
                DurText.Text = FormatTime(p.Duration);
                break;
            case nameof(p.IsPlaying):
                ThumbPlay.ImageSource = p.IsPlaying ? _pauseImage : _playImage;
                Taskbar.ProgressState = p.Current == null ? TaskbarItemProgressState.None
                    : p.IsPlaying ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Paused;
                break;
            case nameof(p.Current):
                Title = p.Current != null ? $"{p.Current.Title} · {p.Current.Artist} — Echoplex" : "Echoplex";
                ReleaseInfoScrollLock(); // otra canción, otros detalles: la barra vuelve a ser automática
                break;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentPage):
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindDescendant<ScrollViewer>(PageHost)?.ScrollToTop());
                if (_vm.CurrentPage != null) AnimatePageIn();
                break;
            case nameof(MainViewModel.ActiveLyric):
                ScrollToActiveLyric();
                break;
        }
    }

    /// <summary>Lleva la línea que suena a un tercio de la altura: deslizándose al cambiar de línea, directo al abrir la pestaña.</summary>
    private void ScrollToActiveLyric() => ScrollToActiveLyric(smooth: true);

    private void ScrollToActiveLyric(bool smooth)
    {
        if (_vm.ActiveLyric is not { } line || !LyricsScroll.IsVisible) return;
        if (LyricsList.ItemContainerGenerator.ContainerFromItem(line) is not FrameworkElement el) return;
        var pos = el.TransformToAncestor(LyricsScroll).Transform(new Point(0, 0));
        // desde donde está ahora (si hay un deslizamiento a medias, desde su punto actual)
        var target = Math.Clamp(LyricsScroll.VerticalOffset + pos.Y - LyricsScroll.ViewportHeight * 0.35, 0, LyricsScroll.ScrollableHeight);
        if (smooth && AnimationsEnabled) GlideLyrics(target);
        else
        {
            StopLyricsGlide();
            LyricsScroll.ScrollToVerticalOffset(target);
        }
    }

    // deslizamiento de la letra: el ScrollViewer no anima su desplazamiento, así que se hace fotograma a fotograma
    private double _glideFrom, _glideTo;
    private bool _gliding;
    private readonly System.Diagnostics.Stopwatch _glideClock = new();
    private static readonly TimeSpan GlideDuration = TimeSpan.FromMilliseconds(550);

    private void GlideLyrics(double target)
    {
        if (Math.Abs(target - LyricsScroll.VerticalOffset) < 0.5) return;
        _glideFrom = LyricsScroll.VerticalOffset;
        _glideTo = target;
        _glideClock.Restart();
        if (!_gliding) CompositionTarget.Rendering += OnGlideFrame;
        _gliding = true;
    }

    private void OnGlideFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, _glideClock.Elapsed.TotalMilliseconds / GlideDuration.TotalMilliseconds);
        double eased = 1 - Math.Pow(1 - t, 3); // ease-out: arranca con decisión y se posa suave
        LyricsScroll.ScrollToVerticalOffset(_glideFrom + (_glideTo - _glideFrom) * eased);
        if (t >= 1) StopLyricsGlide();
    }

    private void StopLyricsGlide()
    {
        if (!_gliding) return;
        CompositionTarget.Rendering -= OnGlideFrame;
        _gliding = false;
    }

    private long _shownSecond = -1;

    // red de seguridad del panel Detalles: si la barra de desplazamiento entra en vaivén (aparece, el contenido
    // estrecha y deja de hacer falta, desaparece, vuelve a hacer falta…), se deja fija hasta que cambie algo
    private Visibility _infoBar = Visibility.Collapsed;
    private int _infoFlips;
    private DateTime _infoFlipsSince;

    private void WatchInfoScrollFlips()
    {
        var now = InfoScroll.ComputedVerticalScrollBarVisibility;
        if (now == _infoBar) return;
        _infoBar = now;
        if (DateTime.UtcNow - _infoFlipsSince > TimeSpan.FromSeconds(1))
        {
            _infoFlipsSince = DateTime.UtcNow;
            _infoFlips = 0;
        }
        if (++_infoFlips >= 4 && InfoScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto)
            InfoScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
    }

    /// <summary>Al cambiar el ancho del panel o la canción, la barra vuelve a ser automática.</summary>
    private void ReleaseInfoScrollLock()
    {
        _infoFlips = 0;
        if (InfoScroll.VerticalScrollBarVisibility != ScrollBarVisibility.Auto) InfoScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private static string FormatTime(double seconds) =>
        Song.FormatTime(TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0)));

    /// <summary>Clic o arrastre en cualquier punto del slider.</summary>
    private void AttachSliderDrag(Slider slider, Action<double>? commit)
    {
        void SetFromMouse(MouseEventArgs e)
        {
            if (slider.Template.FindName("PART_Track", slider) is not Track track || track.ActualWidth <= 0) return;
            // Proporción directa sobre el ancho de la pista (Track.ValueFromPoint depende de dónde estaba
            // el tirador en el último repintado y se desvía si el valor acaba de cambiar).
            double fraction = Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0, 1);
            slider.Value = slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
            if (slider == SeekSlider)
            {
                PosText.Text = FormatTime(slider.Value);
                _shownSecond = -1; // al soltar, el tiempo real vuelve a escribirse
            }
        }

        slider.PreviewMouseLeftButtonDown += (o, e) =>
        {
            _dragSlider = slider;
            slider.CaptureMouse();
            SetFromMouse(e);
            e.Handled = true;
        };
        slider.PreviewMouseMove += (o, e) =>
        {
            if (_dragSlider == slider) SetFromMouse(e);
        };
        slider.PreviewMouseLeftButtonUp += (o, e) =>
        {
            if (_dragSlider != slider) return;
            _dragSlider = null;
            slider.ReleaseMouseCapture();
            commit?.Invoke(slider.Value);
            e.Handled = true;
        };
        slider.LostMouseCapture += (o, e) =>
        {
            if (_dragSlider != slider) return;
            _dragSlider = null;
            commit?.Invoke(slider.Value);
        };
    }

    private void MainArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _vm.GalleryColumns = Math.Max(1, (int)((e.NewSize.Width - 56 - 40) / 192));
    }

    // ======================================================================
    // Teclado
    // ======================================================================

    /// <summary>Botones laterales del ratón: atrás (X1) y adelante (X2), como en el navegador.</summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) _vm.GoBack();
        else if (e.ChangedButton == MouseButton.XButton2) _vm.GoForward();
        else return;
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool inText = Keyboard.FocusedElement is TextBox;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = mods == ModifierKeys.Control;
        bool handled = true;

        if (key == Key.Space && !inText) _vm.TogglePlay();
        else if (ctrl && key == Key.K) _vm.OpenSearch();
        else if (ctrl && key == Key.F) FocusFilterOrSearch();
        else if (ctrl && key == Key.Right) _vm.Player.Next();
        else if (ctrl && key == Key.Left) _vm.Player.Previous();
        else if (ctrl && key == Key.Up) _vm.Player.Volume = Math.Min(1, _vm.Player.Volume + 0.1);
        else if (ctrl && key == Key.Down) _vm.Player.Volume = Math.Max(0, _vm.Player.Volume - 0.1);
        else if (ctrl && key == Key.S) _vm.Player.Shuffle = !_vm.Player.Shuffle;
        else if (ctrl && key == Key.R) _vm.Player.CycleRepeat();
        else if (ctrl && key == Key.L) { if (_vm.Player.Current is { } c) _vm.ToggleFavorite(c); }
        else if (ctrl && key == Key.Q) _vm.ToggleRightTab("queue");
        else if (ctrl && key == Key.M) ShowMiniPlayer();
        else if (ctrl && key == Key.D) _vm.ToggleTheme();
        else if (ctrl && key == Key.E) _vm.ToggleAnimations();
        else if (ctrl && key == Key.OemComma) ShowSettings();
        else if (ctrl && key is Key.OemPlus or Key.Add) ApplyZoom(_vm.Settings.Zoom + ZoomStep);
        else if (ctrl && key is Key.OemMinus or Key.Subtract) ApplyZoom(_vm.Settings.Zoom - ZoomStep);
        else if (ctrl && key is Key.D0 or Key.NumPad0) ApplyZoom(1.0);
        else if (ctrl && key == Key.N) NewPlaylist(null);
        else if ((ctrl && key == Key.OemQuestion) || key == Key.F1) ShowShortcuts();
        else if (mods == ModifierKeys.Alt && key == Key.Left) _vm.GoBack();
        else if (mods == ModifierKeys.Alt && key == Key.Right) _vm.GoForward();
        else if (key == Key.BrowserBack) _vm.GoBack();
        else if (key == Key.BrowserForward) _vm.GoForward();
        else if (key == Key.Escape && inText)
        {
            if (Keyboard.FocusedElement == FilterBox) FilterBox.Text = "";
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
        }
        else handled = false;

        if (handled) e.Handled = true;
    }

    // ---------- columnas de la tabla: ancho (bordes) y orden (arrastrar cabeceras) ----------

    private const string ColumnFormat = "Echoplex.Column";
    private Point _colDragStart;
    private string? _colDragKey;

    private void ColumnGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is FrameworkElement { Parent: Grid header } grip && SongColumns.GetKey(grip) is { } key)
            SongColumns.Resize(header, key, e.HorizontalChange);
    }

    private void ColumnGrip_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SongColumns.CommitResize();

    private void ColumnHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _colDragKey = sender is DependencyObject d ? SongColumns.GetKey(d) : null;
        _colDragStart = e.GetPosition(this);
    }

    private void ColumnHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_colDragKey == null || e.LeftButton != MouseButtonState.Pressed) return;
        if (Math.Abs(e.GetPosition(this).X - _colDragStart.X) < 8) return; // un clic normal sigue ordenando
        var key = _colDragKey;
        _colDragKey = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(ColumnFormat, key), DragDropEffects.Move);
        if (sender is FrameworkElement { Parent: Grid header }) HideDropLine(header);
    }

    private static System.Windows.Shapes.Rectangle? DropLine(Grid header) => header.Children.OfType<System.Windows.Shapes.Rectangle>().FirstOrDefault(r => r.Name == "ColumnDropLine");

    private static void HideDropLine(Grid header)
    {
        if (DropLine(header) is { } line) line.Visibility = Visibility.Collapsed;
    }

    private void ColumnHeader_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not Grid header || !e.Data.GetDataPresent(ColumnFormat)) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var (_, x) = SongColumns.DropTarget(header, e.GetPosition(header).X);
        if (DropLine(header) is { } line)
        {
            line.Margin = new Thickness(x - 1, 2, 0, 2);
            line.Visibility = Visibility.Visible;
        }
    }

    private void ColumnHeader_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Grid header) HideDropLine(header);
    }

    private void ColumnHeader_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Grid header || e.Data.GetData(ColumnFormat) is not string key) return;
        HideDropLine(header);
        var (index, _) = SongColumns.DropTarget(header, e.GetPosition(header).X);
        SongColumns.Move(key, index);
        e.Handled = true;
    }

    private void ColumnHeader_RightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Restablecer columnas", "", SongColumns.Reset));
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---------- iconos de la barra de reproducción: en línea o tras la flecha ----------

    private bool _extrasCompact;
    private double _extrasWidth;

    /// <summary>
    /// Si los iconos de la derecha (temporizador, letra, cola, detalles) no caben, pasan a un menú con flecha;
    /// Bit a bit, el mini reproductor y el volumen se quedan siempre a la vista. Vuelven a su sitio cuando hay hueco.
    /// </summary>
    private void UpdatePlayerExtras()
    {
        double avail = PlayerRightCol.ActualWidth;
        if (avail <= 0) return;
        // lo que ocuparía todo en línea (los hijos de un StackPanel horizontal se miden sin límite de ancho)
        double others = PlayerRight.Children.OfType<FrameworkElement>()
            .Where(c => c != ExtrasInline && c != ExtrasToggle && c.Visibility == Visibility.Visible)
            .Sum(c => c.DesiredSize.Width);
        if (!_extrasCompact)
        {
            double extras = ExtrasInline.DesiredSize.Width;
            if (others + extras <= avail + 0.5) return;
            _extrasWidth = PlayerExtras.DesiredSize.Width;
            _extrasCompact = true;
            ExtrasInline.Child = null;
            ExtrasPopupHost.Child = PlayerExtras;
            ExtrasToggle.Visibility = Visibility.Visible;
        }
        else
        {
            if (others + _extrasWidth > avail - 12) return; // margen para no parpadear en el límite
            _extrasCompact = false;
            ExtrasPopup.IsOpen = false;
            ExtrasPopupHost.Child = null;
            ExtrasInline.Child = PlayerExtras;
            ExtrasToggle.Visibility = Visibility.Collapsed;
        }
    }

    private void ExtrasToggle_Checked(object sender, RoutedEventArgs e) => ExtrasPopup.IsOpen = true;

    private void ExtrasPopup_Closed(object? sender, EventArgs e)
    {
        ExtrasToggle.IsChecked = false;
        // el mismo clic que cierra el menú no debe volver a abrirlo
        Dispatcher.BeginInvoke(() => ExtrasToggle.IsHitTestVisible = true, DispatcherPriority.Input);
    }

    // ---------- ancho del panel derecho ----------

    private const double RightMin = 180, RightMax = 640, RightDefault = 330;

    private void RightResizer_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // hacia la izquierda ensancha; el contenido central no baja de su mínimo
        const double centerMin = 480; // MinWidth de la columna central
        double room = MainArea.ActualWidth - centerMin + RightPanel.Width;
        RightPanel.Width = Math.Clamp(RightPanel.Width - e.HorizontalChange, RightMin, Math.Max(RightMin, Math.Min(RightMax, room)));
    }

    private void RightResizer_DoubleClick(object sender, MouseButtonEventArgs e) => RightPanel.Width = RightDefault;

    private const double ZoomStep = 0.1, MinZoom = 0.7, MaxZoom = 2.0;

    /// <summary>Zoom de toda la ventana, como en un navegador (se guarda en los ajustes).</summary>
    private void ApplyZoom(double zoom, bool announce = true)
    {
        zoom = Math.Round(Math.Clamp(zoom, MinZoom, MaxZoom), 1);
        if (Content is FrameworkElement root)
            root.LayoutTransform = Math.Abs(zoom - 1) < 0.001 ? Transform.Identity : new ScaleTransform(zoom, zoom);
        if (!announce) return;
        bool changed = Math.Abs(zoom - _vm.Settings.Zoom) > 0.001;
        _vm.Settings.Zoom = zoom;
        if (changed) SettingsStore.Save(_vm.Settings);
        _vm.ShowToast($"Zoom {zoom * 100:0} %" + (zoom >= MaxZoom ? " (máximo)" : zoom <= MinZoom ? " (mínimo)" : ""), 1.6);
    }

    private void FocusFilterOrSearch()
    {
        if (FilterBox.IsVisible)
        {
            FilterBox.Focus();
            FilterBox.SelectAll();
        }
        else
        {
            _vm.OpenSearch();
        }
    }

    private void ShowShortcuts() => DialogWindow.Info(this, "Atajos de teclado",
        "Espacio\tReproducir / pausa\n" +
        "Ctrl+→ / Ctrl+←\tSiguiente / anterior\n" +
        "Ctrl+↑ / Ctrl+↓\tVolumen\n" +
        "Ctrl+S\tAleatorio\n" +
        "Ctrl+R\tRepetir (no / todo / una)\n" +
        "Ctrl+L\tFavorita la canción actual\n" +
        "Ctrl+K\tBuscar\n" +
        "Ctrl+F\tFiltrar la lista actual\n" +
        "Ctrl+Q\tCola\n" +
        "Ctrl+N\tNueva playlist\n" +
        "Ctrl+M\tMini reproductor\n" +
        "Ctrl+D\tTema claro / oscuro\n" +
        "Ctrl+E\tAnimaciones sí / no\n" +
        "Ctrl+,\tAjustes\n" +
        "Ctrl + / Ctrl −\tZoom (Ctrl+0: 100 %)\n" +
        "Alt+← / Alt+→\tAtrás / adelante\n" +
        "Botones laterales del ratón\tAtrás / adelante\n" +
        "Supr\tQuitar de la playlist\n" +
        "Enter\tReproducir la canción seleccionada");

    // ======================================================================
    // Navegación del panel lateral
    // ======================================================================

    private void Home_Click(object sender, RoutedEventArgs e) => _vm.GoHome();
    private void Search_Click(object sender, RoutedEventArgs e) => _vm.OpenSearch();
    private void Favorites_Click(object sender, RoutedEventArgs e) => _vm.OpenFavorites();
    private void History_Click(object sender, RoutedEventArgs e) => _vm.OpenHistory();
    private void Recent_Click(object sender, RoutedEventArgs e) => _vm.OpenRecent();
    private void Stats_Click(object sender, RoutedEventArgs e) => _vm.OpenStats();
    private void Artists_Click(object sender, RoutedEventArgs e) => _vm.OpenArtists();
    private void Albums_Click(object sender, RoutedEventArgs e) => _vm.OpenAlbums();

    private void Folders_Click(object sender, RoutedEventArgs e) => _vm.OpenFolders();
    private void Back_Click(object sender, RoutedEventArgs e) => _vm.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => _vm.GoForward();
    private void NewPlaylist_Click(object sender, RoutedEventArgs e) => NewPlaylist(null);

    private void Playlist_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Playlist p) _vm.OpenPlaylist(p);
    }

    private void Group_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupHeader g) _vm.EnsureGroupCover(g);
    }

    private void GroupPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupHeader g) _vm.PlayGroup(g);
    }

    private void GroupShuffle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupHeader g) _vm.PlayGroup(g, shuffle: true);
    }

    private void GroupTitle_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupHeader g) _vm.OpenFolder(g.Directory);
    }

    private void TreeNode_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderNode node) _vm.EnsureNodeCover(node);
    }

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_vm.IsSyncingTree && e.NewValue is FolderNode node) _vm.OpenFolder(node.Path);
    }

    private void Workspace_Click(object sender, RoutedEventArgs e)
    {
        // solo lo rápido: el resto de opciones está en Ajustes (engranaje)
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item(_vm.IsDark ? "Tema claro" : "Tema oscuro", _vm.IsDark ? "\uE706" : "\uE708", _vm.ToggleTheme, "Ctrl+D"));
        menu.Items.Add(Item("Atajos de teclado", "\uE765", ShowShortcuts, "F1"));
        menu.Items.Add(Item("Ajustes", "\uE713", ShowSettings, "Ctrl+,"));
        menu.IsOpen = true;
    }

    /// <summary>Elige una o varias carpetas y las añade a la biblioteca.</summary>
    public async void AddMusicFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Añade carpetas de música", Multiselect = true };
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (Directory.Exists(music)) dlg.InitialDirectory = music;
        if (dlg.ShowDialog(this) != true) return;
        foreach (var folder in dlg.FolderNames) await _vm.AddMusicRootAsync(folder);
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e) => AddMusicFolder();

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private SettingsWindow? _settings;

    /// <summary>
    /// Abre Ajustes sobre la app oscurecida; si ya está abierto, lo cierra (Ctrl+, hace de interruptor).
    /// No es modal a la antigua: la capa oscura tapa la app y un clic en ella lo cierra.
    /// </summary>
    public void ShowSettings()
    {
        if (_settings != null)
        {
            _settings.Close();
            return;
        }
        var w = new SettingsWindow(_vm, this);
        _settings = w;
        // el fondo se oscurece a la vez que entra la ventana (y la capa ya bloquea clics mientras se monta)
        SettingsDim.Visibility = Visibility.Visible;
        bool closing = false;
        w.Opened += () => { if (!closing && _settings == w) FadeSettingsDim(true); };
        w.ClosingStarted += () =>
        {
            closing = true;
            FadeSettingsDim(false);
        };
        w.Closed += (s, e) =>
        {
            if (_settings == w) _settings = null;
            // cerrada del todo: la capa se quita siempre (aunque un fundido se hubiera cruzado con otro)
            if (_settings == null) HideSettingsDim();
            Activate();
        };
        w.Show();
    }

    private void SettingsDim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings != null) _settings.Close();
        else HideSettingsDim(); // nunca debería quedarse sola, pero si pasa, un clic la quita
        e.Handled = true;
    }

    private void HideSettingsDim()
    {
        SettingsDim.BeginAnimation(OpacityProperty, null);
        SettingsDim.Opacity = 0;
        SettingsDim.Visibility = Visibility.Collapsed;
    }

    private void FadeSettingsDim(bool show)
    {
        const double dimmed = 0.25; // solo un poco
        if (!AnimationsEnabled)
        {
            SettingsDim.BeginAnimation(OpacityProperty, null);
            SettingsDim.Opacity = show ? dimmed : 0;
            SettingsDim.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        SettingsDim.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(show ? dimmed : 0, TimeSpan.FromMilliseconds(show ? 260 : 180))
        {
            EasingFunction = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        if (!show) fade.Completed += (s, e) => { if (_settings == null || SettingsDim.Opacity < 0.01) SettingsDim.Visibility = Visibility.Collapsed; };
        SettingsDim.BeginAnimation(OpacityProperty, fade);
    }

    public void OpenMiniPlayer() => ShowMiniPlayer();

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await _vm.RescanAsync(announce: true);

    // ======================================================================
    // Lista de canciones
    // ======================================================================

    private void SongList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView lv)
            foreach (var h in e.AddedItems.OfType<ListHeader>().ToList()) lv.SelectedItems.Remove(h);
    }

    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) return;
        PlayRowFrom(sender);
    }

    private void Row_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PlayRowFrom(sender);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _vm.CurrentPage is SongListPage { Playlist: { } pl } && sender is FrameworkElement fe)
        {
            _vm.RemoveFromPlaylist(pl, SongsFor(fe));
            e.Handled = true;
        }
    }

    private void RowPlay_Click(object sender, RoutedEventArgs e) => PlayRowFrom(sender);

    private void PlayRowFrom(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is SongRow row && _vm.CurrentPage is SongListPage page) _vm.PlayRow(page, row);
    }

    private void RowHeart_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SongRow row) _vm.ToggleFavorite(row.Song);
    }

    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentPage is SongListPage page && sender is FrameworkElement fe)
            page.SortBy(fe.Tag is string { Length: > 0 } key ? key : null);
    }

    private SongListPage? PageOf(object sender) => ((sender as FrameworkElement)?.DataContext as ListHeader)?.Page;

    private void PagePlay_Click(object sender, RoutedEventArgs e)
    {
        if (PageOf(sender) is { } p) _vm.PlayPage(p);
    }

    private void PageShuffle_Click(object sender, RoutedEventArgs e)
    {
        if (PageOf(sender) is { } p) _vm.ShufflePage(p);
    }

    private void PageRadio_Click(object sender, RoutedEventArgs e)
    {
        if (PageOf(sender) is { AllSongs.Count: > 0 } p) _vm.StartRadio(p.AllSongs[Random.Shared.Next(p.AllSongs.Count)]);
    }

    private void PageDownload_Click(object sender, RoutedEventArgs e)
    {
        if (PageOf(sender) is not { } p) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        AddOfflineItems(menu, p.AllSongs, p.FolderPath);
        menu.IsOpen = true;
    }

    private void PageMore_Click(object sender, RoutedEventArgs e)
    {
        if (PageOf(sender) is not { } p) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        var songs = p.PlayAll ?? p.Visible;
        menu.Items.Add(Item("Añadir todo a la cola", "", () => _vm.Enqueue(songs)));
        menu.Items.Add(PlaylistSubmenu(() => songs, p.Playlist));
        if (p.Playlist is { } pl)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Renombrar playlist", "", () => RenamePlaylist(pl)));
            menu.Items.Add(Item("Eliminar playlist", "", () => DeletePlaylist(pl)));
        }
        if (p.FolderPath != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Abrir en el Explorador", "", () => OpenInExplorer(p.FolderPath, false)));
        }
        menu.IsOpen = true;
    }

    // ---------- menú contextual de canciones ----------

    private List<Song> SongsFor(FrameworkElement target)
    {
        if (target.DataContext is not SongRow row) return new();
        if (target is ListViewItem lvi && lvi.IsSelected && ItemsControl.ItemsControlFromItemContainer(lvi) is ListView lv && lv.SelectedItems.Count > 1)
            return lv.SelectedItems.OfType<SongRow>().OrderBy(r => r.Index).Select(r => r.Song).ToList();
        return new List<Song> { row.Song };
    }

    private void AnyMenu_Opened(object sender, RoutedEventArgs e)
    {
        switch ((sender as ContextMenu)?.Tag as string)
        {
            case "row": RowMenu_Opened(sender, e); break;
            case "folder": FolderMenu_Opened(sender, e); break;
            case "playlist": PlaylistMenu_Opened(sender, e); break;
        }
    }

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        if (menu.PlacementTarget is not FrameworkElement target || target.DataContext is not SongRow row) return;
        var songs = SongsFor(target);
        bool single = songs.Count == 1;
        var song = songs[0];

        if (single)
            menu.Items.Add(Item("Reproducir", "", () =>
            {
                if (_vm.CurrentPage is SongListPage page && target is ListViewItem) _vm.PlayRow(page, row);
                else if (_vm.CurrentPage is SearchPage sp) _vm.PlaySearchRow(sp, row);
                else _vm.PlaySong(song);
            }, "Enter"));
        menu.Items.Add(Item("Reproducir a continuación", "", () => _vm.PlayNext(songs)));
        menu.Items.Add(Item(single ? "Añadir a la cola" : $"Añadir {songs.Count} canciones a la cola", "", () => _vm.Enqueue(songs)));
        menu.Items.Add(PlaylistSubmenu(() => songs, (_vm.CurrentPage as SongListPage)?.Playlist));
        menu.Items.Add(new Separator());

        bool allFav = songs.All(s => s.IsFavorite);
        menu.Items.Add(Item(allFav ? "Quitar de Favoritas" : "Añadir a Favoritas", allFav ? "" : "", () => _vm.SetFavorite(songs, !allFav)));
        if (single)
        {
            menu.Items.Add(Item("Iniciar radio", "", () => _vm.StartRadio(song)));
            menu.Items.Add(Item("Ir al artista", "", () => _vm.OpenArtist(song.PrimaryArtist)));
            menu.Items.Add(Item("Ir al álbum", "", () => _vm.OpenAlbum(LibraryService.AlbumKey(song))));
            menu.Items.Add(Item("Ir a la carpeta", "", () => _vm.OpenFolder(song.Directory)));
        }
        menu.Items.Add(new Separator());
        AddOfflineItems(menu, songs, null);
        if (single)
        {
            menu.Items.Add(Item("Mostrar en el Explorador", "", () => OpenInExplorer(song.Path, true)));
            menu.Items.Add(Item("Copiar ruta", "", () => { try { Clipboard.SetText(song.Path); _vm.ShowToast("Ruta copiada"); } catch { } }));
            menu.Items.Add(Item("Detalles", "", () => { _vm.PlaySong(song); _vm.RightTab = "info"; _vm.ShowRightPanel = true; }));
        }

        if (_vm.CurrentPage is SongListPage { Playlist: { } pl } plPage)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Quitar de esta playlist", "", () => _vm.RemoveFromPlaylist(pl, songs), "Supr"));
            bool canMove = single && plPage.SortKey == null && plPage.Filter.Length == 0;
            if (canMove)
            {
                menu.Items.Add(Item("Subir", "", () => _vm.MoveInPlaylist(pl, song, -1)));
                menu.Items.Add(Item("Bajar", "", () => _vm.MoveInPlaylist(pl, song, +1)));
            }
        }
    }

    private MenuItem PlaylistSubmenu(Func<IList<Song>> songs, Playlist? exclude)
    {
        var sub = new MenuItem { Header = "Añadir a playlist", Tag = "" };
        sub.Items.Add(Item("Nueva playlist…", "", () => NewPlaylist(songs())));
        var others = _vm.Playlists.Where(p => p != exclude).ToList();
        if (others.Count > 0) sub.Items.Add(new Separator());
        foreach (var p in others)
        {
            var pl = p;
            sub.Items.Add(Item(pl.Name, "", () => _vm.AddToPlaylist(pl, songs())));
        }
        return sub;
    }

    private void AddOfflineItems(ContextMenu menu, IList<Song> songs, string? folder)
    {
        var (cloud, bytes) = _vm.CloudSummary(songs);
        int local = songs.Count - cloud;
        menu.Items.Add(Item(cloud > 0 ? $"Mantener en este dispositivo ({cloud} · {bytes / 1073741824.0:0.0} GB)" : "Mantener en este dispositivo",
            "", () => KeepOffline(songs, folder, bytes), enabled: songs.Count > 0));
        menu.Items.Add(Item(local > 0 ? $"Liberar espacio ({local} en el dispositivo)" : "Liberar espacio", "",
            () => FreeSpace(songs, folder), enabled: local > 0));
    }

    private void KeepOffline(IList<Song> songs, string? folder, long bytes)
    {
        if (bytes > 2L * 1073741824 && !DialogWindow.Confirm(this, "Descargar de OneDrive",
                $"Se descargarán {bytes / 1073741824.0:0.0} GB y se mantendrán siempre en este dispositivo. ¿Continuar?", "Descargar"))
            return;
        _vm.SetKeepOffline(songs, folder, true);
    }

    private void FreeSpace(IList<Song> songs, string? folder)
    {
        if (!DialogWindow.Confirm(this, "Liberar espacio",
                "Las canciones seguirán en OneDrive y en Echoplex, pero se borrará la copia local. Se volverán a descargar al reproducirlas.", "Liberar espacio"))
            return;
        _vm.SetKeepOffline(songs, folder, false);
    }

    // ---------- carpetas y playlists ----------

    private void FolderMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        if ((menu.PlacementTarget as FrameworkElement)?.DataContext is not FolderNode node) return;
        var path = node.Path;
        menu.Items.Add(Item("Reproducir", "", () => _vm.PlayFolder(path)));
        menu.Items.Add(Item("Reproducir en aleatorio", "", () => _vm.PlayFolder(path, shuffle: true)));
        menu.Items.Add(Item("Añadir a la cola", "", () => _vm.Enqueue(_vm.Library.SongsUnder(path))));
        menu.Items.Add(PlaylistSubmenu(() => _vm.Library.SongsUnder(path), null));
        menu.Items.Add(new Separator());
        AddOfflineItems(menu, _vm.Library.SongsUnder(path), path);
        // carátula elegida a mano
        menu.Items.Add(new Separator());
        AddCoverItems(menu, path);
        // títulos por nombre de archivo en esta carpeta y sus subcarpetas
        menu.Items.Add(new Separator());
        bool fileNames = _vm.UsesFileNames(path);
        menu.Items.Add(Item(fileNames ? "Nombres de fichero: activado" : "Nombres de fichero: desactivado", fileNames ? "" : "",
            () => _vm.SetFolderFileNames(path, !fileNames)));
        if (_vm.HasOwnFileNameMode(path))
            menu.Items.Add(Item("Títulos: seguir el ajuste general", "", () => _vm.ClearFolderFileNames(path)));
        if (node.IsRoot) menu.Items.Add(Item("Quitar de la biblioteca", "", () => _ = _vm.RemoveMusicRootAsync(path)));
        menu.Items.Add(Item("Abrir en el Explorador", "", () => OpenInExplorer(path, false)));
    }

    private void PlaylistMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        if ((menu.PlacementTarget as FrameworkElement)?.DataContext is not Playlist pl) return;
        menu.Items.Add(Item("Reproducir", "", () => _vm.PlayContextId(pl.ContextId)));
        menu.Items.Add(Item("Reproducir en aleatorio", "", () => _vm.PlayContextId(pl.ContextId, shuffle: true)));
        menu.Items.Add(Item("Añadir a la cola", "", () => _vm.Enqueue(_vm.Library.Resolve(pl.Songs))));
        menu.Items.Add(new Separator());
        AddOfflineItems(menu, _vm.Library.Resolve(pl.Songs), null);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Renombrar", "", () => RenamePlaylist(pl)));
        menu.Items.Add(Item("Eliminar", "", () => DeletePlaylist(pl)));
    }

    private void NewPlaylist(IList<Song>? songs)
    {
        var name = DialogWindow.Prompt(this, "Nueva playlist",
            songs is { Count: > 0 } ? $"Se añadirá{(songs.Count == 1 ? "" : "n")} {songs.Count} canci{(songs.Count == 1 ? "ón" : "ones")}." : "",
            $"Mi playlist n.º {_vm.Playlists.Count + 1}", "Crear");
        if (name == null) return;
        var pl = _vm.CreatePlaylist(name, songs);
        if (songs == null) _vm.OpenPlaylist(pl);
    }

    private void RenamePlaylist(Playlist pl)
    {
        var name = DialogWindow.Prompt(this, "Renombrar playlist", "", pl.Name, "Guardar");
        if (name != null) _vm.RenamePlaylist(pl, name);
    }

    private void DeletePlaylist(Playlist pl)
    {
        if (DialogWindow.Confirm(this, "Eliminar playlist", $"¿Eliminar «{pl.Name}»? Las canciones no se borran de tu disco.", "Eliminar", danger: true))
            _vm.DeletePlaylist(pl);
    }

    // ---------- tarjetas ----------

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CardVm card) _vm.EnsureCover(card);
        if (sender is FrameworkElement fe) AnimateCardIn(fe);
    }

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CardVm card) _vm.OpenCard(card);
    }

    private void CardPlay_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CardVm card) _vm.PlayCard(card);
        e.Handled = true;
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CardVm card) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Reproducir", "", () => _vm.PlayCard(card)));
        if (card.Kind != CardKind.Song)
            menu.Items.Add(Item("Reproducir en aleatorio", "", () => _vm.PlayCard(card, shuffle: true)));
        menu.Items.Add(Item("Abrir", "", () => _vm.OpenCard(card)));
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---------- búsqueda y resumen ----------

    private void BigSearch_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.Focus();
            tb.CaretIndex = tb.Text.Length;
        }
    }

    private void SearchRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is SongRow row && _vm.CurrentPage is SearchPage page)
            _vm.PlaySearchRow(page, row);
    }

    private void SearchAll_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentPage is SearchPage page) _vm.OpenAllSearchSongs(page);
    }

    private void Rank_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RankItem item) return;
        if (item.Song != null) _vm.PlaySong(item.Song); else _vm.OpenRank(item);
    }

    // ======================================================================
    // Panel derecho
    // ======================================================================

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string tab }) _vm.RightTab = tab;
        if (_vm.RightTab == "lyrics") Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToActiveLyric(smooth: false));
    }

    private void CloseRight_Click(object sender, RoutedEventArgs e) => _vm.ShowRightPanel = false;
    private void QueueToggle_Click(object sender, RoutedEventArgs e) => _vm.ToggleRightTab("queue");
    private void InfoToggle_Click(object sender, RoutedEventArgs e) => _vm.ToggleRightTab("info");

    private void LyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleRightTab("lyrics");
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ScrollToActiveLyric(smooth: false));
    }

    private void UpNext_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) return;
        if ((sender as FrameworkElement)?.DataContext is Song song) _vm.Player.PlayFromUpNext(song);
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Song song) _vm.Player.RemoveFromQueue(song);
        e.Handled = true;
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e) => _vm.Player.ClearQueue();

    private void Lyric_Click(object sender, MouseButtonEventArgs e)
    {
        // con desfase, se salta al momento en que esa línea se ilumina; y suena aunque estuviera en pausa
        if ((sender as FrameworkElement)?.DataContext is LyricLine { Time: { } t }) _vm.Player.PlayFrom(t.TotalSeconds - _vm.LyricsOffset);
    }

    private void LyricsOffset_Click(object sender, RoutedEventArgs e)
    {
        double sign = double.Parse((string)((FrameworkElement)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
        _vm.NudgeLyricsOffset(sign * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.5 : 0.1));
    }

    private void LyricsOffset_Wheel(object sender, MouseWheelEventArgs e)
    {
        _vm.NudgeLyricsOffset(Math.Sign(e.Delta) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.5 : 0.1));
        e.Handled = true;
    }

    private void LyricsOffsetReset_Click(object sender, RoutedEventArgs e) => _vm.SetLyricsOffset(0);

    // ======================================================================
    // Barra de reproducción
    // ======================================================================

    private void PlayPause_Click(object sender, RoutedEventArgs e) => _vm.TogglePlay();
    private void Next_Click(object sender, RoutedEventArgs e) => _vm.Player.Next();
    private void Previous_Click(object sender, RoutedEventArgs e) => _vm.Player.Previous();
    private void Repeat_Click(object sender, RoutedEventArgs e) => _vm.Player.CycleRepeat();
    private void Mute_Click(object sender, RoutedEventArgs e) => _vm.Player.IsMuted = !_vm.Player.IsMuted;

    /// <summary>Clic derecho en el volumen: panel ampliado para afinar (sobre todo en volúmenes bajos).</summary>
    private void Volume_RightClick(object sender, MouseButtonEventArgs e)
    {
        VolumeAdvanced.PrepareToShow();
        VolumePopup.IsOpen = true;
        VolumeAdvanced.Focus();
        e.Handled = true;
    }
    private void Mini_Click(object sender, RoutedEventArgs e) => ShowMiniPlayer();
    private void NowPlayingCover_Click(object sender, MouseButtonEventArgs e) => _vm.ToggleRightTab("info");

    private void CurrentHeart_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Player.Current is { } s) _vm.ToggleFavorite(s);
    }

    private void CurrentTitle_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Player.Current is { } s) _vm.OpenAlbum(LibraryService.AlbumKey(s));
    }

    private void CurrentArtist_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm.Player.Current is { } s) _vm.OpenArtist(s.PrimaryArtist);
    }

    private void BitPerfect_Click(object sender, RoutedEventArgs e)
    {
        var p = _vm.Player;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        menu.Items.Add(Item("Salida bit a bit (WASAPI exclusivo)", "", () => { }, enabled: false));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Desactivada (salida normal de Windows)", p.ExclusiveMode ? "" : "", () => _vm.SetExclusiveOutput(null)));
        List<OutputDevice> devices;
        try { devices = AudioOutput.ListDevices(); } catch { devices = new(); }
        foreach (var d in devices)
        {
            var dev = d;
            bool chosen = p.ExclusiveMode && dev.Id == p.ExclusiveDeviceId;
            var label = dev.IsDefault ? $"{dev.Name}  (predeterminado)" : dev.Name;
            menu.Items.Add(Item(label, chosen ? "" : "", () => _vm.SetExclusiveOutput(dev.Id)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(p.OutputInfo, "", () => { }, enabled: false));
        menu.Items.Add(Item("Abrir el panel de Sonido de Windows…", "", () =>
        {
            try { Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true }); } catch { }
        }));
        menu.IsOpen = true;
    }

    private void Sleep_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        menu.Items.Add(Item("Temporizador de apagado", "", () => { }, enabled: false));
        menu.Items.Add(new Separator());
        foreach (var m in new[] { 5, 15, 30, 45, 60, 90 })
        {
            var min = m;
            menu.Items.Add(Item($"{min} minutos", "", () => _vm.SetSleep(min)));
        }
        menu.Items.Add(Item("Al acabar la canción", "", () => _vm.SetSleep(-1)));
        if (_vm.SleepActive)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Desactivar", "", () => _vm.SetSleep(0)));
        }
        menu.IsOpen = true;
    }

    private void ShowMiniPlayer()
    {
        if (_mini != null) { _mini.Activate(); return; }
        _mini = new MiniPlayerWindow(_vm);
        _mini.ExpandRequested += RestoreFromMini;
        _mini.Closed += (s, e) =>
        {
            _mini = null;
            if (!IsVisible) RestoreFromMini();
        };
        _mini.Show();
        Hide();
    }

    private void RestoreFromMini()
    {
        var mini = _mini;
        _mini = null;
        mini?.Close();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ======================================================================
    // Utilidades
    // ======================================================================

    private static MenuItem Item(string header, string glyph, Action action, string? gesture = null, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, Tag = glyph, InputGestureText = gesture ?? "", IsEnabled = enabled };
        mi.Click += (s, e) => action();
        return mi;
    }

    private static void OpenInExplorer(string path, bool select)
    {
        try
        {
            Process.Start("explorer.exe", select ? $"/select,\"{path}\"" : $"\"{path}\"");
        }
        catch
        {
            // sin explorador
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        DependencyObject? cur = d;
        while (cur != null && cur is not T)
            cur = cur is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(cur) : LogicalTreeHelper.GetParent(cur);
        return cur as T;
    }
}
