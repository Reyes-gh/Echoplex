using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Echoplex.Services;

namespace Echoplex;

public partial class App : Application
{
    private static DateTime _lastErrorShown = DateTime.MinValue;
    private static Mutex? _instanceMutex;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
        foreach (var p in Process.GetProcessesByName(current.ProcessName))
        {
            if (p.Id == current.Id || p.MainWindowHandle == IntPtr.Zero) continue;
            if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
            SetForegroundWindow(p.MainWindowHandle);
            break;
        }
    }

    /// <summary>Cambia la paleta en caliente (todas las brochas son DynamicResource).</summary>
    public static void ApplyTheme(string key)
    {
        var dicts = Current.Resources.MergedDictionaries;
        var uri = new Uri($"pack://application:,,,/Echoplex;component/Themes/Colors.{Themes.Get(key).File}.xaml", UriKind.Absolute);
        dicts[0] = new ResourceDictionary { Source = uri };
    }
}
