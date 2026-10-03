using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Echoplex.Services;
using Echoplex.ViewModels;

namespace Echoplex;

/// <summary>
/// Animaciones de la zona principal: caóticas pero elegantes. Solo se animan transformaciones,
/// opacidades, colores y desenfoques temporales (se quitan al terminar) para que el coste sea bajo.
/// </summary>
public partial class MainWindow
{
    private enum TransitionStyle { Diffuse, Vortex, Shatter }

    // congeladas: WPF no tiene que copiarlas cada vez que empieza una animación (mismas curvas)
    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseIn = Frozen(new CubicEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction Silk = Frozen(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 });
    private static readonly IEasingFunction Drift = Frozen(new SineEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Pop = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });
    private static readonly IEasingFunction SoftPop = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
    private static readonly IEasingFunction Sway = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private readonly Stopwatch _cardClock = Stopwatch.StartNew();
    private readonly Stopwatch _pageClock = Stopwatch.StartNew();
    private int _cardStagger;

    private TransitionStyle _style;
    private TransitionStyle _lastStyle = TransitionStyle.Shatter;
    private Vector _transitionDir;
    private double _transitionBlur;
    private double _transitionSpin;
    private bool _crossfadePending;

    private readonly List<GradientStop[]> _auroraStops = new();
    // relojes de la deriva de la aurora: se pausan con la ventana minimizada u oculta (mini reproductor)
    private readonly List<AnimationClock> _auroraClocks = new();
    // última portada y opacidad aplicadas: si no cambian, no hace falta volver a fundir los colores
    private object? _auroraSource;
    private byte _auroraAlpha;
    private INotifyPropertyChanged? _watchedPage;

    /// <summary>El ajuste del usuario (Ctrl+E) y la opción de Windows "Mostrar animaciones".</summary>
    private bool AnimationsEnabled => _vm.AnimationsOn && SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Píxeles reales por unidad de la interfaz (zoom de Echoplex × escala de Windows). Una BitmapCache no tiene en cuenta
    /// ninguno de los dos: hay que dárselo en RenderAtScale para que la caché salga a la resolución real de la pantalla.
    /// </summary>
    private double DeviceScale => _vm.Settings.Zoom * VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>
    /// Caché a media resolución real para una capa que se está desenfocando: WPF calcula los desenfoques a la resolución
    /// de la pantalla, así que una página entera desenfocada cuesta mucho; pintada a la mitad cuesta 4 veces menos
    /// y, con el desenfoque encima, no se distingue (0,2 niveles de diferencia media).
    /// </summary>
    private BitmapCache HalfResCache() => new() { RenderAtScale = 0.5 * DeviceScale };

    /// <summary>Pinta la capa a media resolución hasta que <paramref name="done"/> se cumpla (se comprueba en cada fotograma).</summary>
    private void HalfResWhile(UIElement layer, Func<bool> done)
    {
        var cache = HalfResCache();
        layer.CacheMode = cache;
        EventHandler? tick = null;
        tick = (s, e) =>
        {
            if (layer.CacheMode == cache && !done()) return;
            CompositionTarget.Rendering -= tick;
            if (layer.CacheMode == cache) layer.CacheMode = null;
        };
        CompositionTarget.Rendering += tick;
    }

    private static Random Rnd => Random.Shared;

    private void InitAnimations()
    {
        Anim.SetEnabled(this, AnimationsEnabled);
        BuildAurora();
        _vm.PropertyChanged += (s, e) =>
        {
            if (!Dispatcher.CheckAccess()) return; // la vista solo reacciona en su propio hilo
            switch (e.PropertyName)
            {
                case nameof(MainViewModel.AnimationsOn):
                    // las de XAML leen la propiedad heredada; la aurora se rehace quieta o en movimiento
                    Anim.SetEnabled(this, AnimationsEnabled);
                    foreach (var c in _auroraClocks) c.Controller?.Remove(); // las de la aurora anterior se paran de verdad
                    _auroraClocks.Clear();
                    Aurora.Children.Clear();
                    _auroraStops.Clear();
                    BuildAurora();
                    break;
                case nameof(MainViewModel.CurrentPage):
                    WatchPageCover();
                    RefreshAurora();
                    break;
                case nameof(MainViewModel.NowPlayingCover):
                    if (_vm.NowPlayingCover != null) FlipPlayerCover();
                    if ((_vm.CurrentPage as SongListPage)?.Cover == null) RefreshAurora();
                    break;
            }
        };
        _vm.ThemeChanged += RefreshAurora;
        // minimizada u oculta no se ve nada: la deriva se pausa y sigue desde el mismo punto al volver
        StateChanged += (s, e) => UpdateAuroraRunning();
        IsVisibleChanged += (s, e) => UpdateAuroraRunning();
    }

    private void UpdateAuroraRunning()
    {
        // con la vista a pantalla completa delante, la aurora de la app no se ve: también se pausa. Mientras arrastras el
        // borde o mueves la ventana, también: cada paso ya repinta todo y la deriva (lentísima) no se nota que se detenga
        bool run = IsVisible && WindowState != WindowState.Minimized && FullView.Visibility != Visibility.Visible && !_sizingMove;
        foreach (var c in _auroraClocks)
        {
            if (c.Controller == null) continue;
            if (run) c.Controller.Resume(); else c.Controller.Pause();
        }
    }

    // ======================================================================
    // Transiciones entre páginas: difusa, vórtice o estallido (al azar)
    // ======================================================================

    private void OnPageChanging(PageBase? next, bool refresh)
    {
        _crossfadePending = false;
        if (refresh || next == null || _vm.CurrentPage == null || !AnimationsEnabled || !IsVisible) return;
        double w = PageHost.ActualWidth, h = PageHost.ActualHeight;
        if (w < 20 || h < 20) return;

        RenderTargetBitmap shot;
        try
        {
            // a media resolución: va desenfocada o hecha pedazos, no hace falta más
            shot = new RenderTargetBitmap((int)(w / 2), (int)(h / 2), 48, 48, PixelFormats.Pbgra32);
            shot.Render(PageHost);
            shot.Freeze();
        }
        catch
        {
            return;
        }

        // estilo al azar, nunca el mismo dos veces seguidas
        var options = Enum.GetValues<TransitionStyle>().Where(s => s != _lastStyle).ToArray();
        _style = options[Rnd.Next(options.Length)];
        _lastStyle = _style;

        double angle = Rnd.NextDouble() * Math.PI * 2;
        double reach = 14 + Rnd.NextDouble() * 14;
        _transitionDir = new Vector(Math.Cos(angle) * reach, Math.Sin(angle) * reach * 0.7);
        _transitionBlur = 26 + Rnd.NextDouble() * 14;
        _transitionSpin = (Rnd.Next(2) == 0 ? -1 : 1) * (9 + Rnd.NextDouble() * 9);
        _crossfadePending = true;

        switch (_style)
        {
            case TransitionStyle.Shatter: Shatter(shot, w, h); break;
            case TransitionStyle.Vortex: Vortex(shot); break;
            default: Diffuse(shot); break;
        }
    }

    /// <summary>La página se disuelve con un desenfoque gaussiano fuerte, un leve zoom, deriva y giro.</summary>
    private void Diffuse(BitmapSource shot)
    {
        var (scale, rotate, move, blur) = PrepareShot(shot);
        var t = TimeSpan.FromMilliseconds(440);
        double grow = 1.035 + Rnd.NextDouble() * 0.03;
        FadeShotOut(shot, t, Drift);
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, _transitionBlur, t) { EasingFunction = Drift });
        Animate(scale, ScaleTransform.ScaleXProperty, 1, grow, t, Drift);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, grow, t, Drift);
        Animate(rotate, RotateTransform.AngleProperty, 0, (Rnd.NextDouble() - 0.5) * 1.6, t, Drift);
        Animate(move, TranslateTransform.XProperty, 0, _transitionDir.X, t, Drift);
        Animate(move, TranslateTransform.YProperty, 0, _transitionDir.Y, t, Drift);
    }

    /// <summary>La página se hunde girando hacia el centro, desenfocándose.</summary>
    private void Vortex(BitmapSource shot)
    {
        var (scale, rotate, _, blur) = PrepareShot(shot);
        var t = TimeSpan.FromMilliseconds(520);
        FadeShotOut(shot, t, EaseIn);
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, _transitionBlur, t) { EasingFunction = EaseIn });
        Animate(scale, ScaleTransform.ScaleXProperty, 1, 0.58, t, EaseIn);
        Animate(scale, ScaleTransform.ScaleYProperty, 1, 0.58, t, EaseIn);
        Animate(rotate, RotateTransform.AngleProperty, 0, _transitionSpin, t, EaseIn);
    }

    /// <summary>La página estalla en teselas que salen despedidas girando y se difuminan.</summary>
    private void Shatter(BitmapSource shot, double w, double h)
    {
        Shards.Children.Clear();
        Shards.Visibility = Visibility.Visible;
        var blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        Shards.Effect = blur;
        // las teselas son trozos de una captura a media resolución: pintarlas a media resolución no cambia nada y abarata el desenfoque
        var cache = HalfResCache();
        ShardsLayer.CacheMode = cache;

        const int cols = 7, rows = 5;
        double tw = w / cols, th = h / rows;
        var origin = new Point(w * (0.3 + Rnd.NextDouble() * 0.4), h * (0.25 + Rnd.NextDouble() * 0.4));
        double longest = 0;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var tile = new Rectangle
                {
                    Width = tw + 1,
                    Height = th + 1,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    Fill = new ImageBrush(shot)
                    {
                        Viewbox = new Rect((double)c / cols, (double)r / rows, 1.0 / cols, 1.0 / rows),
                        ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                        Stretch = Stretch.Fill,
                    },
                };
                Canvas.SetLeft(tile, c * tw);
                Canvas.SetTop(tile, r * th);
                var scale = new ScaleTransform(1, 1);
                var rotate = new RotateTransform(0);
                var move = new TranslateTransform(0, 0);
                tile.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
                Shards.Children.Add(tile);

                // salen desde el punto de impacto, con algo de azar y un poco de "gravedad"
                var center = new Point(c * tw + tw / 2, r * th + th / 2);
                var dir = center - origin;
                double distanceToOrigin = dir.Length;
                if (dir.Length < 1) dir = new Vector(0, -1);
                dir.Normalize();
                dir += new Vector((Rnd.NextDouble() - 0.5) * 0.7, (Rnd.NextDouble() - 0.5) * 0.7);
                double dist = 90 + Rnd.NextDouble() * 230;
                var delay = TimeSpan.FromMilliseconds(distanceToOrigin / Math.Max(w, h) * 140 + Rnd.NextDouble() * 50);
                var t = TimeSpan.FromMilliseconds(560 + Rnd.NextDouble() * 220);
                longest = Math.Max(longest, (delay + t).TotalMilliseconds);

                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dir.X * dist, t) { BeginTime = delay, EasingFunction = Ease });
                move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dir.Y * dist + 60, t) { BeginTime = delay, EasingFunction = Ease });
                rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, (Rnd.NextDouble() - 0.5) * 90, t) { BeginTime = delay, EasingFunction = Ease });
                double shrink = 0.45 + Rnd.NextDouble() * 0.35;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, shrink, t) { BeginTime = delay, EasingFunction = Ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, shrink, t) { BeginTime = delay, EasingFunction = Ease });
                tile.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, t) { BeginTime = delay, EasingFunction = EaseIn });
            }
        }

        var cleanup = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(longest)) { EasingFunction = EaseIn };
        cleanup.Completed += (s, e) =>
        {
            if (Shards.Effect != blur) return; // ya empezó otro estallido
            Shards.Children.Clear();
            Shards.Visibility = Visibility.Collapsed;
            Shards.Effect = null;
            if (ShardsLayer.CacheMode == cache) ShardsLayer.CacheMode = null;
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, cleanup);
    }

    private (ScaleTransform, RotateTransform, TranslateTransform, BlurEffect) PrepareShot(BitmapSource shot)
    {
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var move = new TranslateTransform(0, 0);
        TransitionShot.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
        var blur = new BlurEffect { Radius = 0, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        TransitionShot.Effect = blur;
        TransitionShot.Source = shot;
        TransitionShot.Visibility = Visibility.Visible;
        // la captura ya es de media resolución: pintarla a media resolución no cambia nada y el desenfoque cuesta 4 veces menos
        ShotLayer.CacheMode = HalfResCache();
        return (scale, rotate, move, blur);
    }

    private void FadeShotOut(BitmapSource shot, TimeSpan t, IEasingFunction easing)
    {
        var fade = new DoubleAnimation(1, 0, t) { EasingFunction = easing };
        fade.Completed += (s, e) =>
        {
            if (TransitionShot.Source != shot) return; // ya empezó otra transición
            TransitionShot.Visibility = Visibility.Collapsed;
            TransitionShot.Source = null;
            TransitionShot.Effect = null;
            ShotLayer.CacheMode = null;
        };
        TransitionShot.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// La página nueva sale de un desenfoque gaussiano intenso y se asienta con un frenado suave;
    /// cada estilo le da su propio gesto (deriva, giro inverso o subida desde abajo).
    /// </summary>
    private void AnimatePageIn()
    {
        _cardStagger = 0;
        _pageClock.Restart();
        if (!AnimationsEnabled) return;

        bool cross = _crossfadePending;
        _crossfadePending = false;
        double startBlur = cross ? _transitionBlur * 0.85 : 8;
        Vector from;
        double startScale, startAngle = 0;
        if (!cross)
        {
            from = new Vector(0, 12);
            startScale = 1;
        }
        else
        {
            switch (_style)
            {
                case TransitionStyle.Vortex:
                    from = new Vector(0, 0);
                    startScale = 1.1;
                    startAngle = -_transitionSpin * 0.35;
                    break;
                case TransitionStyle.Shatter:
                    from = new Vector(0, 46);
                    startScale = 0.93;
                    break;
                default:
                    from = -_transitionDir * 0.6 + new Vector(0, 8);
                    startScale = 0.965;
                    break;
            }
        }
        var inTime = TimeSpan.FromMilliseconds(cross ? 620 : 300);
        var delay = TimeSpan.FromMilliseconds(cross ? (_style == TransitionStyle.Shatter ? 120 : 50) : 0);

        var blur = new BlurEffect { Radius = startBlur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        var scale = new ScaleTransform(startScale, startScale);
        var rotate = new RotateTransform(startAngle);
        var move = new TranslateTransform(from.X, from.Y);
        PageHost.Effect = blur;
        PageHost.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
        PageHost.Opacity = 0;
        // mientras el desenfoque es fuerte, la página se pinta a media resolución; al afinarse vuelve a la completa
        if (startBlur > 10) HalfResWhile(PageLayer, () => PageHost.Effect != blur || blur.Radius < 6);

        // el enfoque frena más despacio que el movimiento: el cruce difuso entre páginas se aprecia
        var sharpen = new DoubleAnimation(startBlur, 0, cross ? TimeSpan.FromMilliseconds(680) : inTime) { BeginTime = delay, EasingFunction = cross ? Ease : Silk };
        // el desenfoque se quita al acabar para que no cueste nada al desplazarse por la página
        sharpen.Completed += (s, e) => { if (PageHost.Effect == blur) PageHost.Effect = null; };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(cross ? 460 : 260)) { BeginTime = delay, EasingFunction = Drift });
        blur.BeginAnimation(BlurEffect.RadiusProperty, sharpen);
        var settle = _style == TransitionStyle.Shatter && cross ? SoftPop : Silk;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(startScale, 1, inTime) { BeginTime = delay, EasingFunction = settle });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(startScale, 1, inTime) { BeginTime = delay, EasingFunction = settle });
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(startAngle, 0, inTime) { BeginTime = delay, EasingFunction = Silk });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from.X, 0, inTime) { BeginTime = delay, EasingFunction = Silk });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from.Y, 0, inTime) { BeginTime = delay, EasingFunction = settle });
    }

    // ======================================================================
    // Portadas
    // ======================================================================

    /// <summary>La portada de la cabecera entra girando y rebotando, y lanza un destello de sí misma.</summary>
    private void HeaderCover_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || sender is not Border cover) return;
        // solo al abrir la página, no al volver a ella desplazándose
        if (!AnimationsEnabled || _pageClock.ElapsedMilliseconds > 2500) return;

        var scale = new ScaleTransform(0.35, 0.35);
        var rotate = new RotateTransform((Rnd.Next(2) == 0 ? -1 : 1) * (14 + Rnd.NextDouble() * 16));
        cover.RenderTransform = new TransformGroup { Children = { scale, rotate } };
        var t = TimeSpan.FromMilliseconds(780);
        cover.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        Animate(scale, ScaleTransform.ScaleXProperty, 0.35, 1, t, Pop);
        Animate(scale, ScaleTransform.ScaleYProperty, 0.35, 1, t, Pop);
        Animate(rotate, RotateTransform.AngleProperty, rotate.Angle, 0, t, Pop);

        if ((cover.Background as ImageBrush)?.ImageSource is { } image)
            Dispatcher.BeginInvoke(() => BloomFrom(cover, image), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Destello: la portada, muy difuminada, crece y se desvanece desde su posición.</summary>
    private void BloomFrom(FrameworkElement anchor, ImageSource image)
    {
        if (!anchor.IsVisible || image is not BitmapSource bitmap) return;
        Point center;
        try
        {
            center = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2), Stage);
        }
        catch
        {
            return;
        }
        var blurred = BloomBitmap(bitmap);
        Bloom.Source = blurred;
        Bloom.Margin = new Thickness(center.X - Bloom.Width / 2, center.Y - Bloom.Height / 2, 0, 0);
        var scale = new ScaleTransform(0.4, 0.4);
        Bloom.RenderTransform = scale;
        Bloom.Visibility = Visibility.Visible;

        var t = TimeSpan.FromMilliseconds(1100);
        var glow = new DoubleAnimationUsingKeyFrames { Duration = t };
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromPercent(0.18)) { EasingFunction = Ease });
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)) { EasingFunction = Drift });
        glow.Completed += (s, e) =>
        {
            if (Bloom.Source != blurred) return;
            Bloom.Visibility = Visibility.Collapsed;
            Bloom.Source = null;
        };
        Bloom.BeginAnimation(OpacityProperty, glow);
        Animate(scale, ScaleTransform.ScaleXProperty, 0.4, 3.4, t, Silk);
        Animate(scale, ScaleTransform.ScaleYProperty, 0.4, 3.4, t, Silk);
    }

    // el destello: portada de 320 con un desenfoque de radio 45 y margen para su halo (Bloom mide 320 + 2 × 96)
    private const double BloomSide = 320, BloomPad = 96;
    private BitmapSource? _bloomFrom, _bloomBitmap;

    /// <summary>
    /// La portada ya desenfocada para el destello. Con un BlurEffect en vivo, al crecer ×3,4 WPF acababa desenfocando
    /// más de mil píxeles con radio ~150 en cada fotograma; desenfocada una vez (a media resolución) y ampliada, se ve
    /// igual (0,4 niveles de diferencia media) y apenas cuesta.
    /// </summary>
    private BitmapSource BloomBitmap(BitmapSource cover)
    {
        if (ReferenceEquals(cover, _bloomFrom) && _bloomBitmap != null) return _bloomBitmap;
        const int downscale = 2;
        double total = BloomSide + 2 * BloomPad;
        var host = new Grid { Width = total, Height = total, LayoutTransform = new ScaleTransform(1.0 / downscale, 1.0 / downscale) };
        host.Children.Add(new Image
        {
            Source = cover,
            Width = BloomSide,
            Height = BloomSide,
            Stretch = Stretch.UniformToFill,
            Effect = new BlurEffect { Radius = 45, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        });
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        int px = (int)Math.Ceiling(total / downscale);
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        bmp.Freeze();
        _bloomFrom = cover;
        _bloomBitmap = bmp;
        return bmp;
    }

    /// <summary>Al cambiar de canción, la portada de la barra da la vuelta como una carta y emite un anillo de luz.</summary>
    private void FlipPlayerCover()
    {
        if (!AnimationsEnabled || !PlayerCover.IsVisible) return;
        var flip = new ScaleTransform(0, 1);
        PlayerCover.RenderTransform = flip;
        Animate(flip, ScaleTransform.ScaleXProperty, 0, 1, TimeSpan.FromMilliseconds(520), Pop);

        var ring = new ScaleTransform(1, 1);
        CoverGlow.RenderTransform = ring;
        var t = TimeSpan.FromMilliseconds(800);
        CoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, t) { EasingFunction = Ease });
        Animate(ring, ScaleTransform.ScaleXProperty, 1, 1.7, t, Ease);
        Animate(ring, ScaleTransform.ScaleYProperty, 1, 1.7, t, Ease);
    }

    // ======================================================================
    // Tarjetas
    // ======================================================================

    /// <summary>Las tarjetas entran volando desde posiciones y giros al azar, escalonadas.</summary>
    private void AnimateCardIn(FrameworkElement card)
    {
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var move = new TranslateTransform(0, 0);
        card.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
        // sin vuelo: las que quedan fuera de la vista (nadie lo vería y son decenas de animaciones) y las que solo se
        // recolocan porque ha cambiado el ancho (al redimensionar, cada cambio de columnas las volvía a lanzar todas)
        if (!AnimationsEnabled || Reflowing || IsOutOfView(card)) return;

        // si hace rato que no entra ninguna (p. ej. al desplazar una galería), la cascada vuelve a empezar
        if (_cardClock.ElapsedMilliseconds > 250) _cardStagger = 0;
        _cardClock.Restart();
        var delay = TimeSpan.FromMilliseconds(Math.Min(_cardStagger++, 12) * 32);
        var t = TimeSpan.FromMilliseconds(640);

        double fromX = (Rnd.NextDouble() - 0.5) * 90, fromY = 40 + Rnd.NextDouble() * 50;
        double fromAngle = (Rnd.NextDouble() - 0.5) * 28, fromScale = 0.78 + Rnd.NextDouble() * 0.1;
        card.Opacity = 0;
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay, EasingFunction = Ease });
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, t) { BeginTime = delay, EasingFunction = Silk });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, t) { BeginTime = delay, EasingFunction = Pop });
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(fromAngle, 0, t) { BeginTime = delay, EasingFunction = Pop });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, t) { BeginTime = delay, EasingFunction = Pop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, t) { BeginTime = delay, EasingFunction = Pop });
    }

    // tarjetas creadas por un cambio de ancho (columnas de una galería, tarjetas de más en el inicio): aparecen sin vuelo
    private long _reflowUntil;

    private void MarkReflow() => _reflowUntil = Environment.TickCount64 + 400;

    private bool Reflowing => Environment.TickCount64 < _reflowUntil;

    /// <summary>¿La tarjeta queda fuera de lo que se ve de su lista (filas de reserva de una galería, secciones de abajo del inicio)?</summary>
    private static bool IsOutOfView(FrameworkElement card)
    {
        DependencyObject? d = card;
        while (d != null && d is not ScrollViewer) d = VisualTreeHelper.GetParent(d);
        if (d is not ScrollViewer viewer || viewer.ActualHeight <= 0) return false;
        try
        {
            double top = card.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
            return top >= viewer.ActualHeight || top + card.ActualHeight <= 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Al pasar el ratón, la tarjeta se inclina hacia un lado al azar y crece un poco.</summary>
    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!AnimationsEnabled || CardTransforms(sender) is not var (scale, rotate)) return;
        var t = TimeSpan.FromMilliseconds(340);
        double tilt = (Rnd.Next(2) == 0 ? -1 : 1) * (1.5 + Rnd.NextDouble() * 2.5);
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(tilt, t) { EasingFunction = Pop });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.045, t) { EasingFunction = Pop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.045, t) { EasingFunction = Pop });
    }

    private void Card_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (CardTransforms(sender) is not var (scale, rotate)) return;
        var t = TimeSpan.FromMilliseconds(380);
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, t) { EasingFunction = Ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, t) { EasingFunction = Ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, t) { EasingFunction = Ease });
    }

    private static (ScaleTransform, RotateTransform)? CardTransforms(object sender) =>
        (sender as FrameworkElement)?.RenderTransform is TransformGroup { Children.Count: 3 } g
        && g.Children[0] is ScaleTransform s && g.Children[1] is RotateTransform r ? (s, r) : null;

    // ======================================================================
    // Saludo letra a letra
    // ======================================================================

    private void GreetChar_Loaded(object sender, RoutedEventArgs e)
    {
        if (!AnimationsEnabled || sender is not TextBlock letter || letter.DataContext is not GreetChar ch) return;
        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var move = new TranslateTransform(0, 0);
        letter.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
        var delay = TimeSpan.FromMilliseconds(120 + ch.Index * 38);
        var t = TimeSpan.FromMilliseconds(700);
        double fromY = (Rnd.NextDouble() - 0.5) * 80, fromAngle = (Rnd.NextDouble() - 0.5) * 70, fromScale = 1.5 + Rnd.NextDouble() * 0.6;
        letter.Opacity = 0;
        letter.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = delay });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, t) { BeginTime = delay, EasingFunction = Pop });
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(fromAngle, 0, t) { BeginTime = delay, EasingFunction = Pop });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, t) { BeginTime = delay, EasingFunction = Pop });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, t) { BeginTime = delay, EasingFunction = Pop });
    }

    // ======================================================================
    // Aurora: luces de color de la portada que flotan detrás del contenido
    // ======================================================================

    private void BuildAurora()
    {
        var layout = new (HorizontalAlignment h, VerticalAlignment v, double w, double hgt, Thickness margin, double dx, double dy, double secs)[]
        {
            (HorizontalAlignment.Left, VerticalAlignment.Top, 820, 600, new Thickness(-240, -220, 0, 0), 140, 90, 17),
            (HorizontalAlignment.Right, VerticalAlignment.Top, 720, 560, new Thickness(0, -120, -260, 0), -120, 130, 21),
            (HorizontalAlignment.Center, VerticalAlignment.Bottom, 900, 560, new Thickness(0, 0, 0, -300), 110, -100, 26),
        };
        foreach (var l in layout)
        {
            var stops = new[] { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, 0.45), new GradientStop(Colors.Transparent, 1) };
            var brush = new RadialGradientBrush { GradientStops = new GradientStopCollection(stops) };
            var move = new TranslateTransform();
            var scale = new ScaleTransform(1, 1);
            var blob = new AuroraBlob
            {
                Width = l.w,
                Height = l.hgt,
                HorizontalAlignment = l.h,
                VerticalAlignment = l.v,
                Margin = l.margin,
                Fill = brush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new TransformGroup { Children = { scale, move } },
            };
            Aurora.Children.Add(blob);
            _auroraStops.Add(stops);
            if (!AnimationsEnabled) continue;

            // deriva lenta e infinita (ver Forever)
            var t = TimeSpan.FromSeconds(l.secs);
            Drive(move, TranslateTransform.XProperty, Forever(0, l.dx, t));
            Drive(move, TranslateTransform.YProperty, Forever(0, l.dy, TimeSpan.FromSeconds(l.secs * 0.77)));
            Drive(scale, ScaleTransform.ScaleXProperty, Forever(0.9, 1.18, TimeSpan.FromSeconds(l.secs * 1.3)));
            Drive(scale, ScaleTransform.ScaleYProperty, Forever(1.12, 0.92, TimeSpan.FromSeconds(l.secs * 1.1)));
        }
        _auroraSource = null; // capas nuevas: hay que darles color
        RefreshAurora();
        UpdateAuroraRunning();
    }

    /// <summary>Como BeginAnimation, pero guardando el reloj para poder pausarlo.</summary>
    private void Drive(Animatable target, DependencyProperty property, AnimationTimeline animation)
    {
        var clock = animation.CreateClock();
        target.ApplyAnimationClock(property, clock);
        _auroraClocks.Add(clock);
    }

    /// <summary>
    /// Deriva lenta e infinita. A 20 fotogramas por segundo: se mueve a menos de 15 px/s y son manchas sin bordes, así que
    /// cada paso cambia los píxeles menos de un nivel (no se distingue de 30 o 60), y cada fotograma del fondo obliga a
    /// repintar todo lo que tiene delante: menos fotogramas, menos trabajo con la ventana quieta.
    /// </summary>
    private static DoubleAnimation Forever(double from, double to, TimeSpan t)
    {
        var a = new DoubleAnimation(from, to, t) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Sway };
        Timeline.SetDesiredFrameRate(a, 20);
        return a;
    }

    /// <summary>Colores de la portada de la página (o de lo que suena), con un fundido de color.</summary>
    private void RefreshAurora()
    {
        if (_auroraStops.Count == 0) return;
        var source = (_vm.CurrentPage as SongListPage)?.Cover ?? _vm.NowPlayingCover;
        byte alpha = (byte)(_vm.IsDark ? 92 : 58);
        // misma portada y mismo tema que ya están aplicados: nada que cambiar (antes se fundía de un color al mismo)
        object key = source ?? (object)DBNull.Value; // "sin portada" también cuenta como un estado
        if (_auroraSource != null && ReferenceEquals(key, _auroraSource) && alpha == _auroraAlpha) return;
        _auroraSource = key;
        _auroraAlpha = alpha;
        var colors = Palette.Extract(source);
        for (int i = 0; i < _auroraStops.Count; i++)
        {
            var c = colors[i % colors.Length];
            var stops = _auroraStops[i];
            AnimateStop(stops[0], Color.FromArgb(alpha, c.R, c.G, c.B));
            AnimateStop(stops[1], Color.FromArgb((byte)(alpha * 0.45), c.R, c.G, c.B));
            AnimateStop(stops[2], Color.FromArgb(0, c.R, c.G, c.B));
        }
    }

    private void AnimateStop(GradientStop stop, Color to)
    {
        if (!AnimationsEnabled)
        {
            stop.BeginAnimation(GradientStop.ColorProperty, null);
            stop.Color = to;
            return;
        }
        stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(1400)) { EasingFunction = Drift });
    }

    /// <summary>Cuando la portada de la página llega (se carga después), la aurora cambia a sus colores.</summary>
    private void WatchPageCover()
    {
        if (_watchedPage != null) _watchedPage.PropertyChanged -= OnWatchedPageChanged;
        _watchedPage = _vm.CurrentPage as SongListPage;
        if (_watchedPage != null) _watchedPage.PropertyChanged += OnWatchedPageChanged;
    }

    private void OnWatchedPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        // el aviso puede llegar desde un hilo de fondo: la aurora (y el resto de oyentes) no deben romperse
        if (e.PropertyName == nameof(SongListPage.Cover)) Dispatcher.BeginInvoke(RefreshAurora);
    }

    private static void Animate(Animatable target, DependencyProperty property, double from, double to, TimeSpan t, IEasingFunction easing) =>
        target.BeginAnimation(property, new DoubleAnimation(from, to, t) { EasingFunction = easing });
}

/// <summary>
/// Una mancha de luz de la aurora (y de las luces de la pantalla completa): una elipse que no se recorta al hueco que
/// le da su panel. Las manchas son más grandes que la zona central en ventanas estrechas y WPF recortaba la elipse por
/// el borde de ese hueco; al derivar la mancha, el corte se veía como un borde recto moviéndose por la página.
/// </summary>
public sealed class AuroraBlob : FrameworkElement
{
    public Brush? Fill { get; init; }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        dc.DrawEllipse(Fill, null, new Point(size.Width / 2, size.Height / 2), size.Width / 2, size.Height / 2);
    }

    protected override Geometry GetLayoutClip(Size layoutSlotSize) => null!;
}

/// <summary>Interruptor de animaciones que heredan todos los elementos de la ventana (lo leen los estilos XAML).</summary>
public static class Anim
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Anim), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    /// <summary>Opacidad que llega con un fundido cada vez que cambia (las líneas de la letra al activarse y pasar).</summary>
    public static readonly DependencyProperty SmoothOpacityProperty = DependencyProperty.RegisterAttached(
        "SmoothOpacity", typeof(double), typeof(Anim), new PropertyMetadata(1.0, OnSmoothOpacityChanged));

    public static double GetSmoothOpacity(DependencyObject d) => (double)d.GetValue(SmoothOpacityProperty);
    public static void SetSmoothOpacity(DependencyObject d, double value) => d.SetValue(SmoothOpacityProperty, value);

    private static readonly IEasingFunction Soft = new SineEase { EasingMode = EasingMode.EaseInOut };

    private static void OnSmoothOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        var to = (double)e.NewValue;
        if (!GetEnabled(el) || el is FrameworkElement { IsLoaded: false })
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = to;
            return;
        }
        // sin From: parte del valor que tenga en ese momento, aunque otro fundido esté a medias
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(420)) { EasingFunction = Soft });
    }
}
