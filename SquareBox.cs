using System.Windows;
using System.Windows.Controls;

namespace Echoplex;

/// <summary>
/// Contenedor cuadrado: tan alto como ancho le den. Lo calcula en la misma pasada de maquetación; atar el alto a
/// ActualWidth con un enlace lo hacía una pasada después y, dentro de un ScrollViewer, en ciertos anchos la barra
/// de desplazamiento entraba en bucle (aparecía, la portada encogía, sobraba, desaparecía, la portada crecía…).
/// </summary>
public sealed class SquareBox : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        double side = double.IsInfinity(constraint.Width) ? 0 : constraint.Width;
        var size = new Size(side, side);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = new Size(arrangeSize.Width, arrangeSize.Width);
        Child?.Arrange(new Rect(size));
        return size;
    }
}
