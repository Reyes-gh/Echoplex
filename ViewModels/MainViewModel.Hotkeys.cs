using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>Atajos globales (Ajustes → General): la ventana principal los registra y responde.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Atajos globales activos. De serie, apagados (para no quitarle combinaciones a otros programas).</summary>
    public bool GlobalHotkeysOn
    {
        get => Settings.GlobalHotkeys;
        set
        {
            if (Settings.GlobalHotkeys == value) return;
            Settings.GlobalHotkeys = value;
            OnPropertyChanged();
            SettingsStore.Save(Settings);
            HotkeysChanged?.Invoke();
        }
    }

    /// <summary>Con Mayús además de Ctrl+Alt (por si otro programa ya usa Ctrl+Alt con esas teclas).</summary>
    public bool GlobalHotkeyShift
    {
        get => Settings.GlobalHotkeyShift;
        set
        {
            if (Settings.GlobalHotkeyShift == value) return;
            Settings.GlobalHotkeyShift = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HotkeyHelp));
            SettingsStore.Save(Settings);
            HotkeysChanged?.Invoke();
        }
    }

    public string HotkeyHelp => GlobalHotkeys.Help(Settings.GlobalHotkeyShift);

    /// <summary>Las combinaciones que no se pudieron registrar porque otro programa ya las tiene; null si todas van.</summary>
    [ObservableProperty] private string? _hotkeyStatus;

    /// <summary>Se encendieron, apagaron o cambiaron: la ventana los vuelve a registrar.</summary>
    public event Action? HotkeysChanged;

    /// <summary>La ventana cuenta qué combinaciones no pudo registrar.</summary>
    public void SetHotkeysTaken(IReadOnlyList<string> taken)
    {
        HotkeyStatus = taken.Count switch
        {
            0 => null,
            1 => $"{taken[0]} ya lo usa otro programa: ese atajo no funcionará.",
            _ => $"{string.Join(", ", taken)} ya los usa otro programa: esos atajos no funcionarán.",
        };
        if (taken.Count > 0 && !Settings.GlobalHotkeyShift) HotkeyStatus += " Prueba con Ctrl + Alt + Mayús.";
    }
}
