using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Echoplex.Services;
using Echoplex.ViewModels;

namespace Echoplex;

/// <summary>
/// Pantalla completa: lo que suena a la izquierda y la letra a la derecha, sobre la portada difuminada con luces
/// de sus colores. Dos tamaños con el mismo aspecto: toda la app, o además toda la pantalla (F11).
/// </summary>
public partial class MainWindow
{
    private bool _screenFull;
    private (WindowState State, WindowStyle Style, ResizeMode Resize)? _beforeScreenFull;
    private long _fullShownSecond = -1;

    // fondo: dos capas que se funden al cambiar de portada
    private bool _backdropOnA;
    private object? _backdropFor;

    // luces: como la aurora de la app, pero más vivas; sus relojes se pausan si la vista no se ve
    private readonly List<AnimationClock> _fullClocks = new();
    private readonly List<GradientStop[]> _fullStops = new();
    private object? _fullLightsFor;

    // controles de arriba y volumen: se desvanecen (y el cursor se oculta) si no mueves el ratón
    private readonly DispatcherTimer _fullIdle = new() { Interval = TimeSpan.FromSeconds(3) };
    private Point _fullMouse = new(double.NaN, double.NaN);
    private bool _fullChromeShown = true;

    private void InitFullView()
    {
        _fullLyrics = new LyricsFollower(FullLyricsScroll, FullLyricsList, 0.36);
        AttachSliderDrag(FullSeek, v => _vm.Player.Seek(v), FullPos);
        AttachSliderDrag(FullVolumeSlider, null);
        FullVolume.PreviewMouseWheel += VolumeWheel; // la rueda sobre el volumen (botón o barra), como abajo
        _fullIdle.Tick += (s, e) =>
        {
            _fullIdle.Stop();
            ShowFullChrome(false);
        };
        _vm.PropertyChanged += (s, e) =>
        {
            if (!Dispatcher.CheckAccess()) return;
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.NowPlayingCover):
                    if (FullView.IsVisible) RefreshFullArt();
                    break;
                case nameof(MainViewModel.AnimationsOn):
                    // las luces se rehacen quietas o en movimiento (si la vista está abierta; si no, al abrirla)
                    ClearFullLights();
                    if (FullView.IsVisible) RefreshFullArt();
                    break;
                case nameof(MainViewModel.HasLyrics):
                    LayoutFullView(); // con letra, a dos columnas; sin ella, la portada centrada
                    break;
            }
        };
        LayoutFullView();
        StateChanged += (s, e) => UpdateFullRunning();
        IsVisibleChanged += (s, e) => UpdateFullRunning();
    }

    // ======================================================================
    // Abrir, cerrar y toda la pantalla
    // ======================================================================

    private void FullView_Click(object sender, RoutedEventArgs e) => OpenFullView(screen: false);

    private void FullView_RightClick(object sender, MouseButtonEventArgs e)
    {
        OpenFullView(screen: true);
        e.Handled = true;
    }

    private void FullScreenToggle_Click(object sender, RoutedEventArgs e) => SetScreenFull(!_screenFull);

    private void FullClose_Click(object sender, RoutedEventArgs e) => CloseFullView();

    /// <summary>F11: a toda la pantalla (abriendo la vista si hace falta) o de vuelta a la ventana.</summary>
    private void ToggleScreenFull()
    {
        if (!FullView.IsVisible) OpenFullView(screen: true);
        else SetScreenFull(!_screenFull);
    }

    private void OpenFullView(bool screen)
    {
        if (!FullView.IsVisible)
        {
            VolumePopup.IsOpen = false;
            ExtrasPopup.IsOpen = false;
            FullView.BeginAnimation(OpacityProperty, null);
            FullView.Opacity = 1;
            FullView.Visibility = Visibility.Visible;
            // la letra (bajo su máscara de bordes) y la sombra de la portada quedan en caché a la resolución real: con las
            // luces moviéndose detrás se repintaban en cada fotograma aunque no cambiaran (la letra se pintaba ya sin
            // ClearType por la máscara, así que se ve igual)
            double scale = DeviceScale;
            if (FullLyricsScroll.CacheMode is not BitmapCache { RenderAtScale: var s } || Math.Abs(s - scale) > 0.001)
                FullLyricsScroll.CacheMode = new BitmapCache { RenderAtScale = scale };
            if (FullCoverShadow.CacheMode is not BitmapCache { RenderAtScale: var s2 } || Math.Abs(s2 - scale) > 0.001)
                FullCoverShadow.CacheMode = new BitmapCache { RenderAtScale = scale };
            RefreshFullArt();
            UpdateFullRunning();
            UpdateAuroraRunning();
            _fullShownSecond = -1;
            SyncFullPosition();
            ShowFullChrome(true);
            _fullIdle.Start();
            if (AnimationsEnabled)
            {
                var t = TimeSpan.FromMilliseconds(320);
                FullView.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
                FullContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, t) { EasingFunction = FullEase });
                FullContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, t) { EasingFunction = FullEase });
            }
            // la letra, directamente en la línea que suena (cuando ya está medida)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _fullLyrics.Follow(_vm.ActiveLyric, smooth: false));
        }
        SetScreenFull(screen);
    }

    private static readonly IEasingFunction FullEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }

    /// <summary>Vuelve a la app (y a la ventana, si estaba a toda la pantalla).</summary>
    private void CloseFullView(bool animate = true)
    {
        SetScreenFull(false);
        if (FullView.Visibility != Visibility.Visible) return;
        _fullIdle.Stop();
        _fullLyrics.Stop();
        FullView.Cursor = null;

        void Hide()
        {
            FullView.BeginAnimation(OpacityProperty, null);
            FullView.Visibility = Visibility.Collapsed;
            UpdateFullRunning();
            UpdateAuroraRunning();
        }

        if (!animate || !AnimationsEnabled)
        {
            Hide();
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fade.Completed += (s, e) =>
        {
            if (FullView.Opacity < 0.01) Hide(); // si se volvió a abrir mientras se iba, se queda
        };
        FullView.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Toda la pantalla: sin marco y maximizada (tapa también la barra de tareas). Al salir, la ventana vuelve como estaba.</summary>
    private void SetScreenFull(bool on)
    {
        if (on == _screenFull) return;
        _screenFull = on;
        if (on)
        {
            _beforeScreenFull = (WindowState, WindowStyle, ResizeMode);
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal; // si ya estaba maximizada, Windows no la recolocaría
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else if (_beforeScreenFull is { } before)
        {
            _beforeScreenFull = null;
            WindowState = WindowState.Normal;
            WindowStyle = before.Style;
            ResizeMode = before.Resize;
            WindowState = before.State == WindowState.Minimized ? WindowState.Normal : before.State;
            ApplyTitleBar(); // al volver el marco, otra vez con los colores del tema
        }
        FullScreenButton.Content = on ? "" : "";
        FullScreenButton.ToolTip = on ? "Volver a la ventana (F11)" : "Toda la pantalla (F11)";
    }

    // ======================================================================
    // Contenido
    // ======================================================================

    /// <summary>
    /// Alto de lo que va debajo de la portada (título de hasta dos líneas, artista, álbum, barra y controles).
    /// Fijo a propósito: si dependiera de cuántas líneas ocupa el título, que depende del ancho de la portada,
    /// el tamaño podría entrar en vaivén.
    /// </summary>
    private const double FullBelowCover = 330;

    /// <summary>
    /// Portada (del mayor tamaño que quepa con lo de debajo), hueco y letra, centrados como un bloque.
    /// Todo sale del tamaño de la vista, que no depende de su contenido: sin vaivenes.
    /// </summary>
    private void FullView_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutFullView();

    /// <summary>
    /// Con letra: portada y controles, hueco y letra. Sin letra (no hay, o la has quitado con el micro): solo la portada
    /// y los controles, centrados. Todo sale del tamaño de la vista, que no depende de su contenido: sin vaivenes.
    /// </summary>
    private void LayoutFullView()
    {
        bool wanted = _vm.Settings.FullViewLyrics, lyrics = wanted && _vm.HasLyrics;
        FullLyricsHost.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;
        FullGapCol.Width = new GridLength(lyrics ? FullGap : 0);
        FullNoLyrics.Visibility = wanted && !_vm.HasLyrics ? Visibility.Visible : Visibility.Collapsed;
        FullLyricsButton.Tag = wanted;
        FullLyricsButton.ToolTip = wanted ? "Quitar la letra" : "Poner la letra";

        var m = FullContent.Margin;
        double width = FullView.ActualWidth - m.Left - m.Right - (lyrics ? FullGap : 0), height = FullView.ActualHeight - m.Top - m.Bottom;
        if (width <= 0 || height <= 0) return; // aún sin medir
        // nunca más estrecha que la fila de controles; si no cabe en alto, el Viewbox la reduce entera
        double below = FullBelowCover + (FullNoLyrics.Visibility == Visibility.Visible ? 56 : 0);
        double side = lyrics
            ? Math.Clamp(Math.Min(width * 0.42, height - below), MinFullSide, 560)
            : Math.Clamp(height - below, MinFullSide, 620);
        FullCoverHost.Width = FullCoverHost.Height = side;
        FullLeft.Width = side;
        FullLyricsHost.Width = Math.Clamp(width - side, 280, 680);
        // la primera línea de la letra puede llegar a su altura de lectura; la última, también
        FullLyricsPad.Margin = new Thickness(0, Math.Max(40, height * 0.36), 24, Math.Max(120, height * 0.6));
    }

    /// <summary>Lo que ocupa la fila de controles (favorita, aleatorio… repetir y el hueco que la equilibra).</summary>
    private const double MinFullSide = 400;

    private void FullLyricsToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.FullViewLyrics = !_vm.Settings.FullViewLyrics;
        SettingsStore.Save(_vm.Settings);
        LayoutFullView();
        if (_vm.Settings.FullViewLyrics)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _fullLyrics.Follow(_vm.ActiveLyric, smooth: false));
    }

    /// <summary>Hueco entre la portada y la letra (la columna del medio).</summary>
    private const double FullGap = 80;

    private void SyncFullPosition()
    {
        var p = _vm.Player;
        FullSeek.Maximum = Math.Max(p.Duration, 0.001);
        FullDur.Text = FormatTime(p.Duration);
        if (_dragSlider == FullSeek) return;
        FullSeek.Value = p.Position;
        long second = double.IsFinite(p.Position) ? (long)Math.Max(0, p.Position) : 0;
        if (second == _fullShownSecond) return;
        _fullShownSecond = second;
        FullPos.Text = FormatTime(p.Position);
    }

    // ======================================================================
    // Fondo: portada difuminada y luces de sus colores
    // ======================================================================

    private void RefreshFullArt()
    {
        var cover = _vm.NowPlayingCover;
        object key = cover ?? (object)DBNull.Value;
        if (!ReferenceEquals(key, _backdropFor))
        {
            _backdropFor = key;
            var (show, hide) = _backdropOnA ? (FullBackdropB, FullBackdropA) : (FullBackdropA, FullBackdropB);
            _backdropOnA = !_backdropOnA;
            show.Source = Blurred(cover);
            var fade = AnimationsEnabled ? TimeSpan.FromMilliseconds(900) : TimeSpan.Zero;
            show.BeginAnimation(OpacityProperty, new DoubleAnimation(cover != null ? 0.8 : 0, fade));
            hide.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fade));
        }
        if (_fullStops.Count == 0) BuildFullLights();
        if (ReferenceEquals(key, _fullLightsFor)) return;
        _fullLightsFor = key;
        var colors = Palette.Extract(cover);
        for (int i = 0; i < _fullStops.Count; i++)
        {
            var c = colors[i % colors.Length];
            var stops = _fullStops[i];
            AnimateStop(stops[0], Color.FromArgb(150, c.R, c.G, c.B));
            AnimateStop(stops[1], Color.FromArgb(62, c.R, c.G, c.B));
            AnimateStop(stops[2], Color.FromArgb(0, c.R, c.G, c.B));
        }
    }

    /// <summary>
    /// La portada difuminada una sola vez, en pequeño: estirada a toda la pantalla queda suave y no cuesta nada
    /// (un desenfoque en vivo a pantalla completa se recalcularía en cada fotograma de las luces).
    /// </summary>
    private static BitmapSource? Blurred(BitmapSource? cover)
    {
        if (cover == null) return null;
        const int n = 96, bleed = 16; // el borde se estira hacia fuera para que el desenfoque no se aclare en las esquinas
        var dv = new DrawingVisual { Effect = new BlurEffect { Radius = 14, KernelType = KernelType.Gaussian } };
        using (var dc = dv.RenderOpen()) dc.DrawImage(cover, new Rect(-bleed, -bleed, n + bleed * 2, n + bleed * 2));
        var bmp = new RenderTargetBitmap(n, n, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private void BuildFullLights()
    {
        var layout = new (HorizontalAlignment h, VerticalAlignment v, double w, double hgt, Thickness margin, double dx, double dy, double secs)[]
        {
            (HorizontalAlignment.Left, VerticalAlignment.Top, 1000, 760, new Thickness(-300, -260, 0, 0), 180, 120, 19),
            (HorizontalAlignment.Right, VerticalAlignment.Top, 900, 700, new Thickness(0, -200, -280, 0), -160, 150, 23),
            (HorizontalAlignment.Center, VerticalAlignment.Bottom, 1100, 640, new Thickness(0, 0, 0, -320), 150, -120, 27),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom, 760, 620, new Thickness(0, 0, -200, -220), -130, -90, 31),
        };
        foreach (var l in layout)
        {
            var stops = new[] { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, 0.5), new GradientStop(Colors.Transparent, 1) };
            var move = new TranslateTransform();
            var scale = new ScaleTransform(1, 1);
            FullLights.Children.Add(new AuroraBlob
            {
                Width = l.w,
                Height = l.hgt,
                HorizontalAlignment = l.h,
                VerticalAlignment = l.v,
                Margin = l.margin,
                Fill = new RadialGradientBrush { GradientStops = new GradientStopCollection(stops) },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { scale, move } },
            });
            _fullStops.Add(stops);
            if (!AnimationsEnabled) continue;
            var t = TimeSpan.FromSeconds(l.secs);
            DriveFull(move, TranslateTransform.XProperty, Forever(0, l.dx, t));
            DriveFull(move, TranslateTransform.YProperty, Forever(0, l.dy, TimeSpan.FromSeconds(l.secs * 0.77)));
            DriveFull(scale, ScaleTransform.ScaleXProperty, Forever(0.88, 1.2, TimeSpan.FromSeconds(l.secs * 1.3)));
            DriveFull(scale, ScaleTransform.ScaleYProperty, Forever(1.15, 0.9, TimeSpan.FromSeconds(l.secs * 1.1)));
        }
        if (AnimationsEnabled)
        {
            // la portada de fondo "respira" muy despacio
            foreach (var s in new[] { FullBackdropScaleA, FullBackdropScaleB })
            {
                DriveFull(s, ScaleTransform.ScaleXProperty, Forever(1.12, 1.26, TimeSpan.FromSeconds(29)));
                DriveFull(s, ScaleTransform.ScaleYProperty, Forever(1.12, 1.26, TimeSpan.FromSeconds(29)));
            }
        }
        _fullLightsFor = null; // capas nuevas: hay que darles color
        UpdateFullRunning();
    }

    private void ClearFullLights()
    {
        foreach (var c in _fullClocks) c.Controller?.Remove();
        _fullClocks.Clear();
        _fullStops.Clear();
        FullLights.Children.Clear();
        _fullLightsFor = null;
    }

    private void DriveFull(Animatable target, DependencyProperty property, AnimationTimeline animation)
    {
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock);
        _fullClocks.Add(clock);
    }

    /// <summary>Las luces solo se mueven mientras la vista se ve (cerrada o minimizada se pausan donde estaban).</summary>
    private void UpdateFullRunning()
    {
        bool run = IsVisible && WindowState != WindowState.Minimized && FullView.Visibility == Visibility.Visible && !_sizingMove;
        foreach (var c in _fullClocks)
        {
            if (c.Controller == null) continue;
            if (run) c.Controller.Resume(); else c.Controller.Pause();
        }
    }

    // ======================================================================
    // Ratón quieto: fuera controles de arriba, volumen y cursor
    // ======================================================================

    private void FullView_MouseMove(object sender, MouseEventArgs e)
    {
        // solo cuenta si el ratón se mueve de verdad (la letra deslizándose debajo también lanza este evento)
        var p = e.GetPosition(FullView);
        if (!double.IsNaN(_fullMouse.X) && Math.Abs(p.X - _fullMouse.X) < 3 && Math.Abs(p.Y - _fullMouse.Y) < 3) return;
        _fullMouse = p;
        ShowFullChrome(true);
        _fullIdle.Stop();
        _fullIdle.Start();
    }

    private void ShowFullChrome(bool show)
    {
        // con el ratón encima de los botones o el volumen, no se esconden
        if (!show && (FullChrome.IsMouseOver || FullVolume.IsMouseOver || _dragSlider != null))
        {
            _fullIdle.Start();
            return;
        }
        if (show == _fullChromeShown) return;
        _fullChromeShown = show;
        FullView.Cursor = show ? null : Cursors.None;
        var to = show ? 1.0 : 0.0;
        var t = TimeSpan.FromMilliseconds(AnimationsEnabled ? (show ? 160 : 600) : 0);
        FullChrome.BeginAnimation(OpacityProperty, new DoubleAnimation(to, t));
        FullVolume.BeginAnimation(OpacityProperty, new DoubleAnimation(to, t));
        FullChrome.IsHitTestVisible = FullVolume.IsHitTestVisible = show;
    }
}
