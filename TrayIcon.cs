using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace Echoplex;

/// <summary>
/// Icono de Echoplex en la bandeja, junto al reloj (Windows lo deja en la flecha de los iconos ocultos hasta que lo
/// saques). Está mientras Echoplex esté abierto: la X de la ventana la esconde aquí y la música sigue. Clic: vuelve la
/// ventana; botón derecho: el menú. Con Shell_NotifyIcon directamente, sin cargar Windows Forms.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 0x2A; // WM_APP + 42: lo que manda la bandeja a la ventana
    private const int IconId = 1;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const int NOTIFYICON_VERSION_4 = 4;
    private const int WM_CONTEXTMENU = 0x007B, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    private const int SM_CXSMICON = 49;

    // el Explorador lo manda a todas las ventanas al arrancar (o al reiniciarse): la bandeja empieza vacía
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly HwndSource _source;
    private IntPtr _icon;
    private bool _ownsIcon; // el del exe hay que liberarlo; el genérico de Windows, no
    private bool _disposed;
    private string _tip = "Echoplex";

    /// <summary>Clic (o Intro con el teclado) en el icono.</summary>
    public event Action? Clicked;

    /// <summary>Botón derecho (o la tecla de menú) en el icono.</summary>
    public event Action? MenuRequested;

    public TrayIcon(Window owner)
    {
        _source = HwndSource.FromHwnd(new WindowInteropHelper(owner).Handle);
        _source.AddHook(WndProc);
        LoadIcon();
        IsShown = Add();
    }

    /// <summary>
    /// El icono está en la bandeja. Si no se pudo poner (el Explorador aún no ha arrancado, p. ej.), la X cierra como
    /// siempre: sin icono no habría forma de volver a la ventana. Se vuelve a intentar cuando arranque el Explorador.
    /// </summary>
    public bool IsShown { get; private set; }

    /// <summary>Lo que sale al pasar el ratón (como el título de la ventana: canción · artista — Echoplex).</summary>
    public void SetToolTip(string text)
    {
        _tip = text.Length > 127 ? text[..126] + "…" : text; // el sistema admite 127 caracteres
        if (!IsShown) return;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Abre el menú junto al cursor (encima del icono). Se activa su ventana: si no, al hacer clic fuera no se cerraría
    /// (Echoplex no es la aplicación activa mientras está en la bandeja).
    /// </summary>
    public static void ShowMenu(ContextMenu menu)
    {
        GetCursorPos(out var cursor);
        double scale = GetDpiForSystem() / 96.0; // la bandeja está en la pantalla principal
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = cursor.X / scale;
        menu.VerticalOffset = cursor.Y / scale;
        menu.IsOpen = true;
        if (PresentationSource.FromVisual(menu) is HwndSource popup) SetForegroundWindow(popup.Handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsShown)
        {
            var data = Data(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            IsShown = false;
        }
        if (!_source.IsDisposed) _source.RemoveHook(WndProc);
        if (_ownsIcon) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
        _ownsIcon = false;
    }

    private bool Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIcon(NIM_ADD, ref data)) return false;
        // versión 4: un clic llega como NIN_SELECT y el botón derecho como WM_CONTEXTMENU
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF)) // versión 4: el evento va en la palabra baja
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    Clicked?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    MenuRequested?.Invoke();
                    break;
            }
            handled = true;
        }
        else if (msg == TaskbarCreated && TaskbarCreated != 0)
        {
            IsShown = Add(); // el Explorador se reinició: hay que volver a poner el icono
        }
        return IntPtr.Zero;
    }

    private NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _tip,
    };

    /// <summary>El icono del propio Echoplex.exe, al tamaño de la bandeja con el escalado de Windows (16 px al 100 %, 24 al 150 %…).</summary>
    private void LoadIcon()
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        if (Environment.ProcessPath is { } exe && SHDefExtractIcon(exe, 0, 0, out var icon, IntPtr.Zero, (uint)size) == 0 && icon != IntPtr.Zero)
        {
            _icon = icon;
            _ownsIcon = true;
        }
        else
        {
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion; // unión con uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHDefExtractIconW")]
    private static extern int SHDefExtractIcon(string file, int index, uint flags, out IntPtr large, IntPtr small, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
