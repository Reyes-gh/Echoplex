using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Echoplex.Services;
using Echoplex.ViewModels;

namespace Echoplex;

/// <summary>
/// Modo ligero: con la ventana sin ver (minimizada, oculta en la bandeja o tras el mini reproductor, en otro escritorio
/// virtual, con la sesión bloqueada o la pantalla apagada) no se anima ni se repinta nada de ella, y los análisis en segundo
/// plano ceden el paso (<see cref="LightMode"/>). La reproducción no se toca. Al volver, todo se pone al día antes del primer
/// fotograma: la barra, el tiempo y la letra están donde estarían si no hubiera parado. Tapada por otras ventanas no
/// cuenta: Windows no avisa cuando deja de estarlo, y se vería un instante desfasada.
/// </summary>
public partial class MainWindow
{
    private IntPtr _awayHwnd;
    private bool _cloaked, _locked, _displayOff;
    private IntPtr _cloakHook;
    private WinEventProc? _cloakProc; // vivo mientras dure el gancho (si no, el recolector se lo llevaría)
    private IntPtr _displayNotify;

    private void InitAway(IntPtr hwnd)
    {
        _awayHwnd = hwnd;
        HwndSource.FromHwnd(hwnd)?.AddHook(AwayHook);
        // sesión bloqueada o desconectada, y pantalla apagada (Windows manda el estado actual nada más apuntarse)
        try { WTSRegisterSessionNotification(hwnd, NotifyForThisSession); } catch { }
        var display = SessionDisplayStatus;
        try { _displayNotify = RegisterPowerSettingNotification(hwnd, ref display, DeviceNotifyWindowHandle); } catch { }
        // otro escritorio virtual: DWM oculta (cloak) la ventana sin minimizarla
        _cloakProc = OnCloakEvent;
        _cloakHook = SetWinEventHook(EventObjectCloaked, EventObjectUncloaked, IntPtr.Zero, _cloakProc, (uint)Environment.ProcessId, 0, WinEventOutOfContext);
        StateChanged += (s, e) => UpdateAway();
        // al volver a mostrarse (bandeja, mini reproductor), WPF avisa a la ventana antes de que sus elementos se vean: la
        // puesta al día va justo después (prioridad normal, antes de pintar), o la letra no podría colocarse
        IsVisibleChanged += (s, e) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(UpdateAway));
            else UpdateAway();
        };
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LightWhenAway)) UpdateAway();
        };
        Closed += (s, e) =>
        {
            if (_cloakHook != IntPtr.Zero) UnhookWinEvent(_cloakHook);
            if (_displayNotify != IntPtr.Zero) UnregisterPowerSettingNotification(_displayNotify);
            try { WTSUnRegisterSessionNotification(hwnd); } catch { }
        };
    }

    private void UpdateAway()
    {
        bool away = _vm.LightWhenAway && (!IsVisible || WindowState == WindowState.Minimized || _cloaked || _locked || _displayOff);
        if (away == Anim.Away) return;
        if (away) EnterAway();
        else LeaveAway();
    }

    private void EnterAway()
    {
        Anim.Away = true;  // barras de la carpeta, fundidos de la letra, transiciones
        _vm.Away = true;   // barras de la fila que suena, círculo del BPM, línea de la letra
        LightMode.Set(true);
        _panelLyrics.Stop();
        _fullLyrics.Stop();
        UpdateAuroraRunning();
        UpdateFullRunning();
    }

    private void LeaveAway()
    {
        // todavía con Anim.Away puesto: lo que se pone al día llega de golpe, sin fundidos ni deslizamientos
        LightMode.Set(false);
        _vm.Away = false; // la línea de la letra que suena
        _shownSecond = -1;
        _fullShownSecond = -1;
        ShowDuration();
        ShowPosition();
        UpdateLayout(); // las líneas de una letra nueva ya existen y tienen su sitio
        ScrollToActiveLyric(smooth: false);
        Anim.Away = false;
        UpdateAuroraRunning();
        UpdateFullRunning();
    }

    private IntPtr AwayHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmWtsSessionChange)
        {
            switch ((int)wParam)
            {
                case WtsSessionLock or WtsConsoleDisconnect or WtsRemoteDisconnect:
                    _locked = true;
                    UpdateAway();
                    break;
                case WtsSessionUnlock or WtsConsoleConnect or WtsRemoteConnect:
                    _locked = false;
                    if ((int)wParam != WtsSessionUnlock) _displayOff = false; // otra pantalla: la de antes ya no cuenta
                    UpdateAway();
                    break;
            }
        }
        else if (msg == WmPowerBroadcast && (int)wParam == PbtPowerSettingChange && lParam != IntPtr.Zero)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting == SessionDisplayStatus)
            {
                _displayOff = setting.Data == 0; // 0 apagada, 1 encendida, 2 atenuada (se sigue viendo)
                UpdateAway();
            }
        }
        return IntPtr.Zero;
    }

    private void OnCloakEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != _awayHwnd || idObject != 0 || idChild != 0) return;
        _cloaked = eventType == EventObjectCloaked;
        UpdateAway();
    }

    private const int WmWtsSessionChange = 0x02B1, WmPowerBroadcast = 0x0218, PbtPowerSettingChange = 0x8013;
    private const int WtsConsoleConnect = 1, WtsConsoleDisconnect = 2, WtsRemoteConnect = 3, WtsRemoteDisconnect = 4;
    private const int WtsSessionLock = 7, WtsSessionUnlock = 8, NotifyForThisSession = 0, DeviceNotifyWindowHandle = 0;
    private const uint EventObjectCloaked = 0x8017, EventObjectUncloaked = 0x8018, WinEventOutOfContext = 0;

    /// <summary>
    /// GUID_SESSION_DISPLAY_STATUS: la pantalla de esta sesión apagada, encendida o atenuada (la de la consola no vale: por
    /// Escritorio remoto se apaga el monitor del PC mientras Echoplex se está viendo).
    /// </summary>
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
