using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Echoplex.Services;

namespace Echoplex;

public partial class App : Application
{
    private static DateTime _lastErrorShown = DateTime.MinValue;
    private static Mutex? _instanceMutex;

    /// <summary>
    /// Aviso de otro Echoplex que se acaba de abrir: el que ya estaba enseña su ventana. Lleva el nombre del ejecutable,
    /// como la búsqueda de la ventana de antes: solo se avisan entre sí los Echoplex.exe.
    /// </summary>
    private static readonly string ShowRequestName = "Echoplex.Show." + Process.GetCurrentProcess().ProcessName;

    /// <summary>
    /// Petición de salir desde fuera (p. ej. para sustituir los archivos al actualizar a mano): se cierra como con Salir,
    /// guardando todo. Cerrar la ventana ya no sirve: la esconde en la bandeja.
    /// </summary>
    private static readonly string ExitRequestName = "Echoplex.Exit." + Process.GetCurrentProcess().ProcessName;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Una sola instancia: dos Echoplex abiertos se pisarían los ajustes y los datos al cerrarse.
        _instanceMutex = new Mutex(true, "Echoplex.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            ActivateRunningInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (s, ex) =>
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DataDirectory);
                File.AppendAllText(Path.Combine(SettingsStore.DataDirectory, "error.log"), $"[{DateTime.Now:u}] {ex.Exception}\n\n");
            }
            catch
            {
                // sin log
            }
            // un solo aviso aunque el mismo error se repita muchas veces seguidas
            if ((DateTime.Now - _lastErrorShown).TotalSeconds > 10)
            {
                _lastErrorShown = DateTime.Now;
                MessageBox.Show(ex.Exception.Message, "Echoplex", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            ex.Handled = true;
        };
        base.OnStartup(e);
        UpdateService.CleanupOldFiles(); // restos de la actualización anterior
        new MainWindow().Show();
        ListenForRequests();
    }

    /// <summary>
    /// Avisos de fuera. Abrir Echoplex otra vez con este abierto: el nuevo avisa y se cierra, y este enseña su ventana
    /// (escondida en la bandeja no hay ventana visible que el nuevo pueda activar). Y la petición de salir.
    /// </summary>
    private void ListenForRequests()
    {
        var requests = new WaitHandle[]
        {
            new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestName),
            new EventWaitHandle(false, EventResetMode.AutoReset, ExitRequestName),
        };
        new Thread(() =>
        {
            while (true)
            {
                bool exit = WaitHandle.WaitAny(requests) == 1;
                Dispatcher.BeginInvoke(() =>
                {
                    if (Current.MainWindow is not Echoplex.MainWindow main) return;
                    if (exit) main.ExitApp(); else main.ShowFromTray();
                });
            }
        })
        { IsBackground = true, Name = "Requests" }.Start();
    }

    /// <summary>Libera el candado de instancia única (antes de reiniciar la aplicación).</summary>
    public static void ReleaseSingleInstance()
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }
        catch
        {
            // ya liberado
        }
        _instanceMutex = null;
    }

    private static void ActivateRunningInstance()
    {
        var current = Process.GetCurrentProcess();
        var others = Process.GetProcessesByName(current.ProcessName).Where(p => p.Id != current.Id).ToList();
        // a este lo acaba de abrir el usuario: le cede al otro el permiso de ponerse delante
        foreach (var p in others) AllowSetForegroundWindow(p.Id);
        if (EventWaitHandle.TryOpenExisting(ShowRequestName, out var requests))
        {
            using (requests) requests.Set();
            return;
        }
        // una versión anterior (sin bandeja): se activa su ventana, como antes
        foreach (var p in others)
        {
            if (p.MainWindowHandle == IntPtr.Zero) continue;
            if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
            SetForegroundWindow(p.MainWindowHandle);
            break;
        }
    }

    /// <summary>Cambia la paleta en caliente (todas las brochas son DynamicResource).</summary>
    public static void ApplyTheme(string key)
    {
        var dicts = Current.Resources.MergedDictionaries;
        var theme = Themes.Get(key);
        dicts[0] = theme.File is { } file
            ? new ResourceDictionary { Source = new Uri($"pack://application:,,,/Echoplex;component/Themes/Colors.{file}.xaml", UriKind.Absolute) }
            : ThemeFactory.Build(theme);
    }
}
