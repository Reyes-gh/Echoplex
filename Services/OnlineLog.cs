namespace Echoplex.Services;

/// <summary>
/// Registro de lo que pasa con las carpetas en línea (%LocalAppData%\Echoplex\online.log): cada petición a la nube,
/// lo que tarda y cuándo empieza a sonar una canción. Sirve para ver por qué algo tarda en cargar. Solo guarda
/// nombres de archivo y tiempos; si crece de más, empieza de nuevo.
/// </summary>
public static class OnlineLog
{
    private static readonly object Lock = new();
    private const long MaxBytes = 1024 * 1024;

    public static string File => Path.Combine(SettingsStore.DataDirectory, "online.log");

    public static void Write(string line)
    {
        try
        {
            lock (Lock)
            {
                var file = File;
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes) info.Delete();
                System.IO.File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // sin registro
        }
    }
}
