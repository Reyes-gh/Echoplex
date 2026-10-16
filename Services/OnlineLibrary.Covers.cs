using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace Echoplex.Services;

public static partial class OnlineLibrary
{
    private const int CoverMaxWidth = 640;      // basta para la cabecera grande (500 px) y el reproductor
    private const int CoverKeepAsIs = 300 * 1024; // las pequeñas se guardan tal cual
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CoverLocks = new();

    private static string CoverDir(OnlineSource s) => System.IO.Path.Combine(BaseDir, "covers", s.Id);

    /// <summary>
    /// Carátula de un archivo en línea: una imagen de la carpeta o la incrustada en una canción. La primera vez se baja
    /// (las incrustadas en un FLAC pueden pesar varios MB) y se guarda en pequeño en el disco; las siguientes, también al
    /// volver a abrir Echoplex, salen de ahí al momento. Solo carátulas: la música nunca se guarda.
    /// </summary>
    public static byte[]? CoverBytes(string path, bool embedded)
    {
        if (Find(path) is not { } e || SourceOf(path) is not { } source) return null;
        var name = Hash((embedded ? "incrustada|" : "imagen|") + e.Url);
        var dir = CoverDir(source);
        var file = System.IO.Path.Combine(dir, name + ".jpg");
        var none = System.IO.Path.Combine(dir, name + ".none"); // la canción no trae carátula: no se vuelve a mirar
        // la misma carátula pedida a la vez (a dos tamaños, desde dos sitios): se baja una vez y la otra la lee del disco
        var gate = CoverLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (File.Exists(file))
            {
                try
                {
                    return File.ReadAllBytes(file);
                }
                catch
                {
                    // ilegible: se vuelve a bajar
                }
            }
            if (File.Exists(none)) return null;

            byte[]? bytes;
            if (embedded)
            {
                if (!MetadataReader.TryReadPicture(path, out bytes)) return null; // no se pudo leer (red): se probará otra vez
            }
            else if ((bytes = ReadAllBytes(path)) == null) return null;

            try
            {
                Directory.CreateDirectory(dir);
                if (bytes == null) File.WriteAllBytes(none, Array.Empty<byte>());
                else
                {
                    var tmp = file + ".tmp";
                    File.WriteAllBytes(tmp, Shrink(bytes));
                    File.Move(tmp, file, overwrite: true);
                }
            }
            catch
            {
                // sin guardar: la próxima vez se baja otra vez
            }
            return bytes;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Las grandes, a JPEG de 640 px de ancho; si no se puede decodificar, tal cual.</summary>
    private static byte[] Shrink(byte[] bytes)
    {
        if (bytes.Length <= CoverKeepAsIs) return bytes;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = new MemoryStream(bytes);
            bi.DecodePixelWidth = CoverMaxWidth;
            bi.EndInit();
            bi.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = 90 };
            enc.Frames.Add(BitmapFrame.Create(bi));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.Length > 0 && ms.Length < bytes.Length ? ms.ToArray() : bytes;
        }
        catch
        {
            return bytes;
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();

    /// <summary>Al quitar una carpeta en línea, sus carátulas guardadas también se van.</summary>
    private static void ForgetCovers(OnlineSource s)
    {
        try
        {
            if (Directory.Exists(CoverDir(s))) Directory.Delete(CoverDir(s), recursive: true);
        }
        catch
        {
            // se quedan: no molestan
        }
    }
}
