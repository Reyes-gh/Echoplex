using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Echoplex.Services;

/// <summary>Colores dominantes de una portada, para la aurora de fondo.</summary>
public static class Palette
{
    public static readonly Color[] Default =
    {
        Color.FromRgb(0x23, 0x83, 0xE2), Color.FromRgb(0x9B, 0x5D, 0xE5), Color.FromRgb(0x1F, 0xB5, 0x9A),
    };

    /// <summary>Hasta tres colores vivos y distintos entre sí (agrupados por tono).</summary>
    public static Color[] Extract(BitmapSource? image)
    {
        if (image == null) return Default;
        try
        {
            const int n = 32;
            var scaled = new TransformedBitmap(image, new ScaleTransform((double)n / image.PixelWidth, (double)n / image.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            // 12 cubetas de tono ponderadas por saturación y brillo
            var weight = new double[12];
            var sumR = new double[12];
            var sumG = new double[12];
            var sumB = new double[12];
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max <= 0 ? 0 : (max - min) / max;
                if (max < 0.12 || sat < 0.12) continue;
                double hue = Hue(r, g, b, max, min);
                int bin = (int)(hue / 30) % 12;
                double wgt = sat * sat * max;
                weight[bin] += wgt;
                sumR[bin] += r * wgt;
                sumG[bin] += g * wgt;
                sumB[bin] += b * wgt;
            }

            var picked = Enumerable.Range(0, 12).Where(i => weight[i] > 0).OrderByDescending(i => weight[i]).Take(3)
                .Select(i => Vivid(sumR[i] / weight[i], sumG[i] / weight[i], sumB[i] / weight[i])).ToList();
            if (picked.Count == 0) return Default;
            while (picked.Count < 3) picked.Add(Shift(picked[picked.Count - 1]));
            return picked.ToArray();
        }
        catch
        {
            return Default;
        }
    }

    private static double Hue(double r, double g, double b, double max, double min)
    {
        double d = max - min;
        if (d <= 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>Lleva el color a un brillo agradable como luz de fondo.</summary>
    private static Color Vivid(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double k = max > 0 ? 0.85 / max : 1;
        return Color.FromRgb((byte)(Math.Clamp(r * k, 0, 1) * 255), (byte)(Math.Clamp(g * k, 0, 1) * 255), (byte)(Math.Clamp(b * k, 0, 1) * 255));
    }

    private static Color Shift(Color c) => Color.FromRgb(c.B, c.R, c.G);
}
