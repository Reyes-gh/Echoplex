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
    private static readonly IEasingFunction Ease = new SineEase { EasingMode = EasingMode.EaseInOut };

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
        Loaded += (s, e) => Update();
        Unloaded += (s, e) => Stop();
        IsVisibleChanged += (s, e) => Update();
    }

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
        if (!(IsAnimating && IsVisible && IsLoaded && Anim.GetEnabled(this)))
        {
            Stop();
            return;
        }
        for (int i = 0; i < _bars.Count; i++)
        {
            var b = Bars[i];
            var a = new DoubleAnimation(b.Low, b.High, TimeSpan.FromMilliseconds(b.Ms))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Ease,
                BeginTime = TimeSpan.FromMilliseconds(b.Delay),
            };
            Timeline.SetDesiredFrameRate(a, 30); // barritas: 30 fps de sobra
            _bars[i].Scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    private void Stop()
    {
        for (int i = 0; i < _bars.Count; i++)
        {
            _bars[i].Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _bars[i].Scale.ScaleY = Bars[i].Idle;
        }
    }
}
