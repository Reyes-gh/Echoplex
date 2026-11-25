using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Echoplex;

/// <summary>
/// El vinilo de Inicio (Neoplex), plano: la portada de lo que suena es la funda y el disco asoma por la derecha hasta
/// la mitad, girando a 33⅓ rpm con la portada en la etiqueta. En pausa frena y se mete en la funda (asoma solo un filo);
/// al volver a sonar sale y arranca. Todo va pintado de antemano en mapas de bits: al girar solo se rota una imagen.
/// Sin verse (otra página, el modo ligero) o con las animaciones apagadas no se mueve nada: sin relojes, WPF no dibuja
/// ni un fotograma por él.
/// </summary>
public sealed class Vinyl : Viewbox
{
    public static readonly DependencyProperty CoverProperty = DependencyProperty.Register(
        nameof(Cover), typeof(ImageSource), typeof(Vinyl), new PropertyMetadata(null, (d, e) => ((Vinyl)d).ShowCover()));

    public static readonly DependencyProperty IsSpinningProperty = DependencyProperty.Register(
        nameof(IsSpinning), typeof(bool), typeof(Vinyl), new PropertyMetadata(false, (d, e) => ((Vinyl)d).Update(smooth: true)));

    public ImageSource? Cover
    {
        get => (ImageSource?)GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }

    public bool IsSpinning
    {
        get => (bool)GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    // la funda (S × S), el disco (radio R; la etiqueta, un tercio como en un LP) y el escenario, con sitio para las sombras
    private const double S = 200, Corner = 3, R = 94, LabelR = 33, HoleR = 2.6, Left = 2, Top = 4;
    private const double StageW = Left + S + R + 8, StageH = Top + S + 10;
    /// <summary>Sonando, el centro del disco en el borde de la funda (fuera la mitad); en pausa, dentro salvo un filo.</summary>
    private const double SlideIn = -78;

    // 33⅓ rpm: una vuelta cada 1,8 s; arranca y frena en 0,8 s (aceleración constante, 80° de camino)
    private static readonly TimeSpan Revolution = TimeSpan.FromSeconds(1.8), Ramp = TimeSpan.FromSeconds(0.8);
    private const double RampDegrees = 80, FrameRate = 30;

    private readonly RotateTransform _spin = new(0, R, R);
    private readonly TranslateTransform _slide = new(SlideIn, 0);
    private readonly Image _disc = new() { Width = 2 * R, Height = 2 * R };
    private readonly Image _sleeve = new() { Width = S, Height = S };
    // los relojes de cada animación, a mano: uno sustituido con BeginAnimation sigue vivo en el árbol de tiempos (el de
    // vueltas sin fin seguía pidiendo fotogramas en pausa), así que al cambiar se para el de antes
    private AnimationClock? _spinClock, _slideClock;
    private bool _spinning;
    private int _generation;

    public Vinyl()
    {
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        var stage = new Canvas { Width = StageW, Height = StageH, ClipToBounds = false };

        // el disco con su sombra y su brillo (que no gira: la luz viene de fuera); va por detrás de la funda
        var disc = new Canvas { Width = 2 * R, Height = 2 * R, RenderTransform = _slide };
        Canvas.SetLeft(disc, Left + S - R);
        Canvas.SetTop(disc, Top + S / 2 - R);
        var discShadow = new Image { Source = DiscShadow(), Width = 2 * R + 24, Height = 2 * R + 24 };
        Canvas.SetLeft(discShadow, -12 + 2);
        Canvas.SetTop(discShadow, -12 + 5);
        disc.Children.Add(discShadow);
        _disc.RenderTransform = _spin;
        RenderOptions.SetBitmapScalingMode(_disc, BitmapScalingMode.HighQuality);
        disc.Children.Add(_disc);
        disc.Children.Add(new Image { Source = Sheen(), Width = 2 * R, Height = 2 * R });
        stage.Children.Add(disc);

        // la sombra de la funda: cae en la mesa y en el disco, junto al borde por donde sale
        var sleeveShadow = new Image { Source = SleeveShadow(), Width = S + 2 * ShadowPad, Height = S + 2 * ShadowPad };
        Canvas.SetLeft(sleeveShadow, Left - ShadowPad + 3);
        Canvas.SetTop(sleeveShadow, Top - ShadowPad + 5);
        stage.Children.Add(sleeveShadow);

        RenderOptions.SetBitmapScalingMode(_sleeve, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(_sleeve, Left);
        Canvas.SetTop(_sleeve, Top);
        stage.Children.Add(_sleeve);

        Child = stage;
        ShowCover();
        Update(smooth: false);
        Loaded += (s, e) =>
        {
            Anim.AwayChanged += OnAwayChanged;
            ShowCover(); // con el color del tema ya a mano (sin portada)
            Update(smooth: false);
        };
        Unloaded += (s, e) =>
        {
            Anim.AwayChanged -= OnAwayChanged;
            Update(smooth: false);
        };
        IsVisibleChanged += (s, e) => Update(smooth: false);
    }

    private void OnAwayChanged() => Update(smooth: false);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == Anim.EnabledProperty) Update(smooth: false); // Ctrl+E
    }

    /// <summary>Se puede mover: a la vista, sin el modo ligero y con las animaciones puestas.</summary>
    private bool CanAnimate => IsLoaded && IsVisible && !Anim.Away && Anim.GetEnabled(this);

    private void Update(bool smooth)
    {
        bool play = IsSpinning;
        bool animate = CanAnimate;
        smooth &= animate;

        // sale de la funda al sonar (con un pequeño rebote) y se mete en pausa, cuando ya va frenando
        double slideTo = play ? 0 : SlideIn;
        if (smooth)
        {
            var slide = play
                ? new DoubleAnimation(slideTo, TimeSpan.FromMilliseconds(620)) { EasingFunction = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut } }
                : new DoubleAnimation(slideTo, TimeSpan.FromMilliseconds(650)) { BeginTime = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
            Animate(_slide, TranslateTransform.XProperty, ref _slideClock, Smooth(slide));
        }
        else
        {
            Animate(_slide, TranslateTransform.XProperty, ref _slideClock, null);
            _slide.X = slideTo;
        }

        bool spin = play && animate;
        if (spin == _spinning) return;
        _spinning = spin;
        int gen = ++_generation;
        double from = _spin.Angle % 360; // el ángulo de ahora (el animado, si está girando)
        if (spin)
        {
            if (!smooth)
            {
                Loop(from);
                return;
            }
            // arranca: aceleración constante hasta la velocidad de vuelta y, desde ahí, vueltas sin fin
            var start = Smooth(new DoubleAnimation(from, from + RampDegrees, Ramp) { AccelerationRatio = 1 });
            start.Completed += (s, e) =>
            {
                if (gen == _generation) Loop(from + RampDegrees);
            };
            Animate(_spin, RotateTransform.AngleProperty, ref _spinClock, start);
        }
        else if (smooth)
        {
            // frena hasta pararse y se queda donde paró (sin reloj)
            var stop = Smooth(new DoubleAnimation(from, from + RampDegrees, Ramp) { DecelerationRatio = 1 });
            stop.Completed += (s, e) =>
            {
                if (gen != _generation) return;
                Animate(_spin, RotateTransform.AngleProperty, ref _spinClock, null);
                _spin.Angle = (from + RampDegrees) % 360;
            };
            Animate(_spin, RotateTransform.AngleProperty, ref _spinClock, stop);
        }
        else
        {
            Animate(_spin, RotateTransform.AngleProperty, ref _spinClock, null);
            _spin.Angle = from;
        }
    }

    // en el sentido de las agujas del reloj, como un disco de verdad visto desde arriba
    private void Loop(double from) =>
        Animate(_spin, RotateTransform.AngleProperty, ref _spinClock,
            Smooth(new DoubleAnimation(from, from + 360, Revolution) { RepeatBehavior = RepeatBehavior.Forever }));

    /// <summary>Pone la animación (o ninguna) y para del todo la de antes, para que no siga pidiendo fotogramas.</summary>
    private static void Animate(Animatable target, DependencyProperty property, ref AnimationClock? current, AnimationTimeline? next)
    {
        var old = current;
        current = next?.CreateClock();
        target.ApplyAnimationClock(property, current); // sin «From», la nueva sale del valor de ahora (el animado)
        old?.Controller?.Remove();
    }

    /// <summary>A 30 fotogramas: a esta velocidad se ve igual de suave y cuesta la mitad.</summary>
    private static DoubleAnimation Smooth(DoubleAnimation a)
    {
        Timeline.SetDesiredFrameRate(a, (int)FrameRate);
        return a;
    }

    private void ShowCover()
    {
        var accent = (TryFindResource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.DeepSkyBlue;
        var cover = Cover as BitmapSource;
        _sleeve.Source = SleeveImage(cover, accent);
        _disc.Source = DiscImage(cover, accent);
    }

    // ======================================================================
    // Lo pintado (una vez por portada; las sombras y el brillo, una vez y para todos)
    // ======================================================================

    /// <summary>Transparente en negro: Colors.Transparent es blanco transparente y, degradado con negro, deja un halo gris.</summary>
    private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);

    private const double Px = 3; // píxeles por unidad: nítido también ampliado y en pantallas de más resolución
    private const double ShadowPad = 16;

    private static BitmapSource Render(double w, double h, Action<DrawingContext> paint)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Px, Px));
            paint(dc);
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w * Px), (int)Math.Ceiling(h * Px), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>La portada recortada a un cuadrado (la parte central) dentro de <paramref name="rect"/>.</summary>
    private static void DrawSquare(DrawingContext dc, BitmapSource cover, Rect rect)
    {
        double ratio = cover.PixelWidth / (double)Math.Max(1, cover.PixelHeight);
        double w = ratio >= 1 ? rect.Height * ratio : rect.Width, h = ratio >= 1 ? rect.Height : rect.Width / ratio;
        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawImage(cover, new Rect(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h));
        dc.Pop();
    }

    /// <summary>La funda: la portada con un filo de cartón (claro arriba, oscuro abajo) y la boca por la derecha.</summary>
    private static BitmapSource SleeveImage(BitmapSource? cover, Color accent) => Render(S, S, dc =>
    {
        var rect = new Rect(0, 0, S, S);
        var shape = new RectangleGeometry(rect, Corner, Corner);
        dc.PushClip(shape);
        if (cover != null) DrawSquare(dc, cover, rect);
        else dc.DrawRectangle(new LinearGradientBrush(accent, Color.FromRgb((byte)(accent.R * 0.35), (byte)(accent.G * 0.35), (byte)(accent.B * 0.35)), 45), null, rect);
        // un poco de luz arriba a la izquierda y la boca de la funda, algo más oscura, a la derecha
        dc.DrawRectangle(new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0), new(Colors.Transparent, 0.45),
        }, new Point(0, 0), new Point(1, 1)), null, rect);
        dc.DrawRectangle(new LinearGradientBrush(Clear, Color.FromArgb(0x38, 0, 0, 0), 0) { StartPoint = new Point(0.9, 0), EndPoint = new Point(1, 0) }, null, rect);
        dc.Pop();
        dc.DrawRoundedRectangle(null, new Pen(new LinearGradientBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0, 0, 0), 90), 0.8),
            new Rect(0.4, 0.4, S - 0.8, S - 0.8), Corner, Corner);
    });

    /// <summary>El disco: negro, con sus surcos (y los huecos entre canciones), la portada en la etiqueta y el agujero.</summary>
    private static BitmapSource DiscImage(BitmapSource? cover, Color accent) => Render(2 * R, 2 * R, dc =>
    {
        var c = new Point(R, R);
        dc.DrawEllipse(new RadialGradientBrush(Color.FromRgb(0x1B, 0x1C, 0x21), Color.FromRgb(0x0D, 0x0E, 0x11)), null, c, R, R);
        // los surcos: anillos finos, alternando un poco el brillo; cada tanto, un hueco liso entre canciones
        var grooveA = new Pen(new SolidColorBrush(Color.FromArgb(0x13, 0xFF, 0xFF, 0xFF)), 0.32);
        var grooveB = new Pen(new SolidColorBrush(Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF)), 0.32);
        var gap = new Pen(new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)), 0.9);
        double[] gaps = { LabelR + 21, LabelR + 34, LabelR + 46 };
        int i = 0;
        for (double r = LabelR + 7; r < R - 3; r += 1.05, i++)
        {
            if (gaps.Any(g => Math.Abs(r - g) < 0.9)) continue;
            dc.DrawEllipse(null, i % 2 == 0 ? grooveA : grooveB, c, r, r);
        }
        foreach (var g in gaps) dc.DrawEllipse(null, gap, c, g, g);
        // el canto, un pelo más claro
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x5A, 0x5C, 0x66)), 1.2), c, R - 0.7, R - 0.7);
        // la etiqueta: la portada en un círculo, con su filo
        dc.PushClip(new EllipseGeometry(c, LabelR, LabelR));
        if (cover != null) DrawSquare(dc, cover, new Rect(c.X - LabelR, c.Y - LabelR, 2 * LabelR, 2 * LabelR));
        else dc.DrawEllipse(new SolidColorBrush(accent), null, c, LabelR, LabelR);
        dc.Pop();
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 0.8), c, LabelR, LabelR);
        // el agujero del eje
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)), new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 0.4), c, HoleR, HoleR);
    });

    private static BitmapSource? _sheen, _discShadow, _sleeveShadow;

    /// <summary>El reflejo de los surcos: dos cuñas de luz enfrentadas, quietas mientras el disco gira debajo.</summary>
    private static BitmapSource Sheen() => _sheen ??= Render(2 * R, 2 * R, dc =>
    {
        var c = new Point(R, R);
        dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(c, R - 1, R - 1), new EllipseGeometry(c, LabelR + 4, LabelR + 4)));
        foreach (var (angle, alpha) in new[] { (40.0, (byte)0x34), (220.0, (byte)0x24) })
        {
            var wedge = new StreamGeometry();
            using (var g = wedge.Open())
            {
                double a0 = (angle - 16) * Math.PI / 180, a1 = (angle + 16) * Math.PI / 180, r = R * 1.3;
                g.BeginFigure(c, true, true);
                g.LineTo(new Point(c.X + r * Math.Cos(a0), c.Y - r * Math.Sin(a0)), false, false);
                g.LineTo(new Point(c.X + r * Math.Cos(a1), c.Y - r * Math.Sin(a1)), false, false);
            }
            // más fuerte en el centro de la cuña que en sus lados
            double mid = angle * Math.PI / 180;
            var along = new Point(0.5 + 0.5 * Math.Cos(mid), 0.5 - 0.5 * Math.Sin(mid));
            dc.DrawGeometry(new RadialGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb(alpha, 0xE8, 0xEC, 0xF4), 0), new(Colors.Transparent, 1),
            }) { Center = along, GradientOrigin = along, RadiusX = 0.55, RadiusY = 0.55 }, null, wedge);
        }
        dc.Pop();
    });

    private static BitmapSource DiscShadow() => _discShadow ??= Render(2 * R + 24, 2 * R + 24, dc =>
        dc.DrawEllipse(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x80, 0, 0, 0), 0.82), new(Clear, 1),
        }), null, new Point(R + 12, R + 12), R + 12, R + 12));

    /// <summary>La sombra de la funda, difuminada (pintada una vez: un efecto en vivo se recalcularía con cada vuelta).</summary>
    private static BitmapSource SleeveShadow()
    {
        if (_sleeveShadow != null) return _sleeveShadow;
        double side = S + 2 * ShadowPad;
        var shape = new Rectangle
        {
            Width = S * Px, Height = S * Px, RadiusX = Corner * Px, RadiusY = Corner * Px,
            Fill = new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)),
            Effect = new BlurEffect { Radius = 10 * Px, KernelType = KernelType.Gaussian },
            Margin = new Thickness(ShadowPad * Px),
        };
        var host = new Grid { Width = side * Px, Height = side * Px, Children = { shape } };
        host.Measure(new Size(side * Px, side * Px));
        host.Arrange(new Rect(0, 0, side * Px, side * Px));
        var bmp = new RenderTargetBitmap((int)(side * Px), (int)(side * Px), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        bmp.Freeze();
        return _sleeveShadow = bmp;
    }
}
