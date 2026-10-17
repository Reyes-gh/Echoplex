using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Echoplex.Services;

/// <summary>
/// Atajos globales: Ctrl+Alt (o Ctrl+Alt+Mayús) con Espacio, flechas y L controlan Echoplex desde cualquier programa,
/// también escondido en la bandeja. Se registran en Windows (RegisterHotKey) con la ventana principal; si otro programa
/// ya usa una combinación, esa no se puede registrar y se avisa.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    public enum Command { PlayPause, Next, Previous, VolumeUp, VolumeDown, Favorite }

    private static readonly (Command command, uint vk, string key, bool repeat)[] Keys =
    {
        (Command.PlayPause, 0x20, "Espacio", false),
        (Command.Next, 0x27, "→", false),
        (Command.Previous, 0x25, "←", false),
        (Command.VolumeUp, 0x26, "↑", true),
        (Command.VolumeDown, 0x28, "↓", true),
        (Command.Favorite, 0x4C, "L", false),
    };

    private const int WM_HOTKEY = 0x0312, BaseId = 0xB100;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly List<int> _registered = new();

    public event Action<Command>? Pressed;

    public GlobalHotkeys(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(Hook);
    }

    /// <summary>Las combinaciones y lo que hacen, para Ajustes.</summary>
    public static string Help(bool withShift)
    {
        string m = withShift ? "Ctrl+Alt+Mayús+" : "Ctrl+Alt+";
        return $"{m}Espacio: reproducir o pausar\n{m}→ / ←: siguiente / anterior\n{m}↑ / ↓: subir / bajar el volumen\n{m}L: me gusta";
    }

    /// <summary>Registra (o quita) los atajos. Devuelve las combinaciones que otro programa ya tenía.</summary>
    public List<string> Apply(bool on, bool withShift)
    {
        Unregister();
        var taken = new List<string>();
        if (!on) return taken;
        uint mods = MOD_CONTROL | MOD_ALT | (withShift ? MOD_SHIFT : 0);
        for (int i = 0; i < Keys.Length; i++)
        {
            var k = Keys[i];
            if (RegisterHotKey(_hwnd, BaseId + i, mods | (k.repeat ? 0 : MOD_NOREPEAT), k.vk)) _registered.Add(BaseId + i);
            else taken.Add((withShift ? "Ctrl+Alt+Mayús+" : "Ctrl+Alt+") + k.key);
        }
        return taken;
    }

    private void Unregister()
    {
        foreach (var id in _registered) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int i = wParam.ToInt32() - BaseId;
            if (i >= 0 && i < Keys.Length && _registered.Contains(BaseId + i))
            {
                handled = true;
                Pressed?.Invoke(Keys[i].command);
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(Hook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
