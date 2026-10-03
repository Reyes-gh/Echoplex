using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Echoplex;

/// <summary>
/// La portada muy difuminada de la franja de arriba de las páginas. Se ve igual que un rectángulo con la portada
/// (UniformToFill) y un BlurEffect de radio 45, pero ese efecto en vivo se recalculaba entero en cada fotograma:
/// al redimensionar, en las transiciones y cada vez que la aurora se movía detrás. Aquí se difumina una vez, a un
/// cuarto de resolución (en algo tan borroso no se distingue: 0,1 niveles de diferencia media) y se pinta como imagen.
/// </summary>
public sealed class BlurredCover : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(BitmapSource), typeof(BlurredCover), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public BitmapSource? Source
    {
        get => (BitmapSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Radio del desenfoque, en las mismas unidades que el BlurEffect al que sustituye.</summary>
    public double Radius { get; set; } = 45;

    private const int Downscale = 4;
    /// <summary>Mientras cambia el tamaño, la imagen se estira hasta un 6 % antes de rehacerla; al pararse, se rehace exacta.</summary>
    private const double StretchTolerance = 0.06;

    private BitmapSource? _blurred;
    private BitmapSource? _blurredFrom;
    private Size _blurredSize;
    private bool _exact;
    private readonly DispatcherTimer _settle;

    public BlurredCover()
    {
        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _settle.Tick += (s, e) =>
        {
            _settle.Stop();
            if (_blurredSize == RenderSize) return;
            _exact = true;
            InvalidateVisual();
        };
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var source = Source;
        var size = RenderSize;
        if (source == null || size.Width < 1 || size.Height < 1) return;

        if (_blurred == null || !ReferenceEquals(source, _blurredFrom) || (_exact && size != _blurredSize))
        {
            Rebuild(source, size);
        }
        else if (size != _blurredSize)
        {
            bool near = Math.Abs(size.Width - _blurredSize.Width) <= size.Width * StretchTolerance
                        && Math.Abs(size.Height - _blurredSize.Height) <= size.Height * StretchTolerance;
            if (near)
            {
                // redimensionando: de momento la que hay, estirada; exacta cuando el tamaño se quede quieto
                _settle.Stop();
                _settle.Start();
            }
            else
            {
                Rebuild(source, size);
            }
        }
        dc.DrawImage(_blurred, new Rect(size));
    }

    private void Rebuild(BitmapSource source, Size size)
    {
        _blurred = Blur(source, size, Radius);
        _blurredFrom = source;
        _blurredSize = size;
        _exact = false;
    }

    /// <summary>La portada en w×h (UniformToFill) con un desenfoque gaussiano de radio r, calculada a 1/4 de resolución.</summary>
    private static BitmapSource Blur(BitmapSource cover, Size size, double radius)
    {
        var rect = new Rectangle
        {
            Width = size.Width,
            Height = size.Height,
            Fill = new ImageBrush(cover) { Stretch = Stretch.UniformToFill },
            Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian },
        };
        var host = new Grid { LayoutTransform = new ScaleTransform(1.0 / Downscale, 1.0 / Downscale) };
        host.Children.Add(rect);
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(size.Width / Downscale), (int)Math.Ceiling(size.Height / Downscale), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        bmp.Freeze();
        return bmp;
    }
}
