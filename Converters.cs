using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Echoplex.ViewModels;

namespace Echoplex;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null / "" / colección vacía → Collapsed. Con parámetro "invert" al revés.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool visible = value switch
        {
            null => false,
            string s => s.Length > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Portada → la misma en blanco y negro (para el efecto al pasar el ratón por la cabecera).</summary>
public sealed class GrayscaleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not System.Windows.Media.Imaging.BitmapSource src) return null;
        try
        {
            var gray = new System.Windows.Media.Imaging.FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Gray8, null, 0);
            gray.Freeze();
            return gray;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>value.ToString() == parámetro → true.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Sangría de un TreeViewItem según su profundidad.</summary>
public sealed class TreeIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int depth = 0;
        DependencyObject? cur = value as DependencyObject;
        while (cur != null && (cur = ItemsControl.ItemsControlFromItemContainer(cur)) is TreeViewItem) depth++;
        return new Thickness(depth * 14, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RowStyleSelector : StyleSelector
{
    public Style? HeaderStyle { get; set; }
    public Style? RowStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container) =>
        item is ListHeader or GalleryHeader or GroupHeader or IEnumerable<CardVm> ? HeaderStyle : RowStyle;
}

public sealed class HeartGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
/// <summary>Cabecera de galería o fila de tarjetas.</summary>
public sealed class GalleryTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? RowTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is GalleryHeader ? HeaderTemplate : RowTemplate;
}