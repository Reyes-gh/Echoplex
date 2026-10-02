using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Echoplex;

/// <summary>
/// Lleva la línea de la letra que suena a una altura fija de su ScrollViewer. El ScrollViewer no anima su
/// desplazamiento, así que el deslizamiento se hace fotograma a fotograma. Si mueves la letra con la rueda,
/// el deslizamiento se aparta y vuelve con la siguiente línea.
/// </summary>
internal sealed class LyricsFollower
{
    private static readonly TimeSpan GlideDuration = TimeSpan.FromMilliseconds(550);

    private readonly ScrollViewer _scroll;
    private readonly ItemsControl _list;
    private readonly double _anchor;
    private readonly Stopwatch _clock = new();
    private double _from, _to;
    private bool _gliding;

    /// <param name="anchor">Altura a la que queda la línea, como fracción de la altura visible (0 = arriba).</param>
    public LyricsFollower(ScrollViewer scroll, ItemsControl list, double anchor)
    {
        _scroll = scroll;
        _list = list;
        _anchor = anchor;
        scroll.PreviewMouseWheel += (o, e) => Stop();
    }

    /// <summary>Deslizándose al cambiar de línea (<paramref name="smooth"/>), o directo al abrir la vista.</summary>
    public void Follow(object? line, bool smooth)
    {
        if (line == null || !_scroll.IsVisible) return;
        if (_list.ItemContainerGenerator.ContainerFromItem(line) is not FrameworkElement el || !el.IsDescendantOf(_scroll)) return;
        var pos = el.TransformToAncestor(_scroll).Transform(new Point(0, 0));
        // desde donde está ahora (si hay un deslizamiento a medias, desde su punto actual)
        var target = Math.Clamp(_scroll.VerticalOffset + pos.Y - _scroll.ViewportHeight * _anchor, 0, _scroll.ScrollableHeight);
        if (smooth) Glide(target);
        else
        {
            Stop();
            _scroll.ScrollToVerticalOffset(target);
        }
    }

    public void Stop()
    {
        if (!_gliding) return;
        CompositionTarget.Rendering -= OnFrame;
        _gliding = false;
    }

    private void Glide(double target)
    {
        if (Math.Abs(target - _scroll.VerticalOffset) < 0.5) return;
        _from = _scroll.VerticalOffset;
        _to = target;
        _clock.Restart();
        if (!_gliding) CompositionTarget.Rendering += OnFrame;
        _gliding = true;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double t = Math.Min(1, _clock.Elapsed.TotalMilliseconds / GlideDuration.TotalMilliseconds);
        double eased = 1 - Math.Pow(1 - t, 3); // ease-out: arranca con decisión y se posa suave
        _scroll.ScrollToVerticalOffset(_from + (_to - _from) * eased);
        if (t >= 1) Stop();
    }
}
