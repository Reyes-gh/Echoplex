using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Echoplex;

/// <summary>
/// Tres barritas de ecualizador que suben y bajan mientras suena la música (en el árbol, en las carpetas del camino
/// hasta la canción). En pausa o con las animaciones desactivadas se quedan quietas a distintas alturas.
/// </summary>
public sealed class EqualizerBars : StackPanel
{
    // cada barra con su ritmo, para que no vayan a la par
    private static readonly (double Low, double High, double Ms, double Delay, double Idle)[] Bars =
    {
        (0.25, 1.0, 420, 0, 0.55),
        (0.35, 0.9, 560, 140, 0.9),
        (0.2, 0.8, 480, 60, 0.4),
    };
    // las tres animaciones se crean una vez, congeladas, y las comparten todas las barritas
    private static readonly DoubleAnimation[] Motions = Bars.Select(b =>
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var a = new DoubleAnimation(b.Low, b.High, TimeSpan.FromMilliseconds(b.Ms))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(b.Delay),
        };
        Timeline.SetDesiredFrameRate(a, 30); // barritas: 30 fps de sobra
        a.Freeze();
        return a;
    }).ToArray();

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(EqualizerBars), new PropertyMetadata(Brushes.Gray, (d, e) => ((EqualizerBars)d).ApplyFill()));

    public static readonly DependencyProperty IsAnimatingProperty = DependencyProperty.Register(
        nameof(IsAnimating), typeof(bool), typeof(EqualizerBars), new PropertyMetadata(false, (d, e) => ((EqualizerBars)d).Update()));

    static EqualizerBars()
    {
        // también se paran si se desactivan las animaciones (Ctrl+E)
        Anim.EnabledProperty.OverrideMetadata(typeof(EqualizerBars),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits, (d, e) => ((EqualizerBars)d).Update()));
    }

    private readonly List<(Rectangle Bar, ScaleTransform Scale)> _bars = new();

    public EqualizerBars()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Height = 12;
        IsHitTestVisible = false;
        for (int i = 0; i < Bars.Length; i++)
        {
            var scale = new ScaleTransform(1, Bars[i].Idle);
            var bar = new Rectangle
            {
                Width = 2.5,
                Height = 12,
                RadiusX = 1,
                RadiusY = 1,
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = scale,
            };
            _bars.Add((bar, scale));
            Children.Add(bar);
        }
        ApplyFill();
        Loaded += (s, e) =>
        {
            // con la ventana minimizada no se ven: se paran y vuelven a moverse al restaurarla
            _window = Window.GetWindow(this);
            if (_window != null) _window.StateChanged += OnWindowStateChanged;
            Update();
        };
        Unloaded += (s, e) =>
        {
            if (_window != null) _window.StateChanged -= OnWindowStateChanged;
            _window = null;
            Stop();
        };
        IsVisibleChanged += (s, e) => Update();
    }

    private Window? _window;
    private bool _running;

    private void OnWindowStateChanged(object? sender, EventArgs e) => Update();

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    private void ApplyFill()
    {
        foreach (var (bar, _) in _bars) bar.Fill = Fill;
    }

    private void Update()
    {
        bool minimized = _window?.WindowState == WindowState.Minimized;
        if (!(IsAnimating && IsVisible && IsLoaded && !minimized && Anim.GetEnabled(this)))
        {
            Stop();
            return;
        }
        if (_running) return; // ya se mueven: no se reinician
        _running = true;
        for (int i = 0; i < _bars.Count; i++) _bars[i].Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motions[i]);
    }

    private void Stop()
    {
        if (!_running) return; // quietas ya, a su altura de reposo
        _running = false;
        for (int i = 0; i < _bars.Count; i++)
        {
            _bars[i].Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _bars[i].Scale.ScaleY = Bars[i].Idle;
        }
    }
}
