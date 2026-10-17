using System.Windows;
using System.Windows.Media;

namespace Echoplex;

/// <summary>
/// La forma de onda de la canción en la barra de progreso: barritas de la altura de su volumen, la parte ya escuchada en
/// el color del tema y el resto apagada. Se pinta detrás de la barra de siempre (que se vuelve transparente).
/// </summary>
public sealed class WaveformBar : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(nameof(Peaks), typeof(float[]), typeof(WaveformBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(nameof(PlayedBrush), typeof(Brush), typeof(WaveformBar),
        new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RestBrushProperty = DependencyProperty.Register(nameof(RestBrush), typeof(Brush), typeof(WaveformBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Volumen máximo de cada trozo de la canción, de 0 a 1.</summary>
    public float[]? Peaks
    {
        get => (float[]?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    /// <summary>Por dónde va, de 0 a 1.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush PlayedBrush
    {
        get => (Brush)GetValue(PlayedBrushProperty);
        set => SetValue(PlayedBrushProperty, value);
    }

    public Brush RestBrush
    {
        get => (Brush)GetValue(RestBrushProperty);
        set => SetValue(RestBrushProperty, value);
    }

    private const double BarWidth = 2, Gap = 1;

    protected override void OnRender(DrawingContext dc)
    {
        var peaks = Peaks;
        double w = ActualWidth, h = ActualHeight;
        if (peaks is not { Length: > 0 } || w < 4 || h < 4) return;
        int bars = (int)(w / (BarWidth + Gap));
        if (bars < 1) return;
        float max = peaks.Max();
        if (max <= 0) max = 1;
        double played = Math.Clamp(Progress, 0, 1) * w;
        var playedBrush = PlayedBrush;
        var restBrush = RestBrush;
        for (int b = 0; b < bars; b++)
        {
            // lo más fuerte del trozo de canción que cae en esta barrita
            int from = b * peaks.Length / bars, to = Math.Max(from + 1, (b + 1) * peaks.Length / bars);
            float v = 0;
            for (int i = from; i < to && i < peaks.Length; i++) v = Math.Max(v, peaks[i]);
            double bh = Math.Max(2, Math.Pow(v / max, 1.6) * h); // con contraste: lo flojo se ve bajo y lo fuerte alto
            double x = b * (BarWidth + Gap);
            dc.DrawRoundedRectangle(x + BarWidth / 2 <= played ? playedBrush : restBrush, null,
                new Rect(x, (h - bh) / 2, BarWidth, bh), 1, 1);
        }
    }
}
