using System.Runtime;
using System.Runtime.InteropServices;

namespace Echoplex.Services;

/// <summary>
/// Modo ligero: Echoplex no se ve (minimizado, en la bandeja o el mini reproductor, en otro escritorio, con la sesión
/// bloqueada o la pantalla apagada). La ventana deja de animar y de pintar lo que nadie ve (eso lo lleva MainWindow); aquí,
/// lo que no es de la ventana: los hilos de análisis (etiquetas, BPM, forma de onda, sonoridad) pasan al modo de fondo de
/// Windows (CPU, disco y memoria con la prioridad más baja) y el recolector evita las pausas largas, para que nada le
/// quite tiempo a la reproducción. Nada se para: solo cede el paso.
/// </summary>
public static class LightMode
{
    private static volatile bool _active;
    private static GCLatencyMode _latency = GCLatencyMode.Interactive;

    public static void Set(bool active)
    {
        if (_active == active) return;
        _active = active;
        try
        {
            // sin recolecciones completas que bloqueen el proceso (salvo con poca memoria)
            if (active)
            {
                _latency = GCSettings.LatencyMode;
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
            else GCSettings.LatencyMode = _latency;
        }
        catch { /* no todos los modos de recolección lo admiten */ }
    }

    [ThreadStatic] private static bool _background;

    /// <summary>
    /// Desde un hilo de análisis (propio, no del pool), entre tarea y tarea: entra o sale del modo de fondo de Windows
    /// según esté o no el modo ligero. Solo cambia algo cuando cambia el modo.
    /// </summary>
    public static void Sync()
    {
        bool want = _active;
        if (_background == want || Thread.CurrentThread.IsThreadPoolThread) return;
        if (SetThreadPriority(GetCurrentThread(), want ? ThreadModeBackgroundBegin : ThreadModeBackgroundEnd)) _background = want;
    }

    private const int ThreadModeBackgroundBegin = 0x00010000, ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr thread, int priority);
}
