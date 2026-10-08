using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>Una banda del ecualizador (o el preamplificador) en Ajustes.</summary>
public sealed partial class EqBand : ObservableObject
{
    private readonly Action<EqBand> _changed;
    private bool _silent;

    public EqBand(int index, string label, string tip, Action<EqBand> changed)
    {
        Index = index;
        Label = label;
        Tip = tip;
        _changed = changed;
    }

    /// <summary>Número de banda; −1 = preamplificador.</summary>
    public int Index { get; }
    public string Label { get; }
    public string Tip { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ValueText))]
    private double _value;

    public string ValueText => Math.Abs(Value) < 0.05 ? "0" : Value.ToString("+0.#;−0.#", System.Globalization.CultureInfo.GetCultureInfo("es-ES"));

    partial void OnValueChanged(double value)
    {
        if (!_silent) _changed(this);
    }

    /// <summary>Cambia el valor sin avisar (al aplicar un preajuste se avisa una sola vez al final).</summary>
    public void SetSilently(double value)
    {
        _silent = true;
        Value = value;
        _silent = false;
    }
}

public sealed record EqPreset(string Key, string Name, double[] Gains);

public sealed partial class MainViewModel
{
    /// <summary>Preajustes (dB por banda, de 25 Hz a 16 kHz).</summary>
    public static readonly IReadOnlyList<EqPreset> EqPresets = new EqPreset[]
    {
        new("flat", "Plano", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
        new("bass", "Graves", new double[] { 6, 6, 5.5, 5, 4, 2.5, 1, 0, 0, 0, 0, 0, 0, 0, 0 }),
        new("treble", "Agudos", new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0.5, 1.5, 3, 4, 5, 5.5, 6 }),
        new("vocal", "Voces", new double[] { -2, -2, -1.5, -1, 0, 1, 2.5, 3.5, 4, 3.5, 2.5, 1.5, 0.5, 0, -0.5 }),
        new("rock", "Rock", new double[] { 4.5, 4, 3.5, 2.5, 1, -0.5, -1.5, -1.5, -0.5, 1, 2.5, 3.5, 4, 4.5, 4.5 }),
        new("pop", "Pop", new double[] { -1, -0.5, 0, 1, 2.5, 3.5, 3.5, 2.5, 1, 0, -0.5, -0.5, 0, 0.5, 1 }),
        new("electronic", "Electrónica", new double[] { 5.5, 5, 4.5, 3, 1, 0, -1, -1, 0, 1, 2, 3, 4, 4.5, 5 }),
        new("acoustic", "Acústica", new double[] { 3, 3, 2.5, 2, 1.5, 1, 1.5, 2, 2, 2.5, 2.5, 2.5, 2, 1.5, 1 }),
        new("classical", "Clásica", new double[] { 3, 3, 2.5, 2, 1, 0, 0, 0, 0, 0, 0.5, 1.5, 2.5, 3, 3.5 }),
        new("loudness", "Sonoridad", new double[] { 6, 5, 4, 2.5, 1, 0, -1, -1, -1, 0, 1, 2.5, 3.5, 4.5, 5 }),
    };

    public const string CustomPreset = "custom";

    private IReadOnlyList<EqBand>? _eqBands;
    private EqBand? _eqPreamp;
    private DispatcherTimer? _eqSaveTimer;

    /// <summary>Las 15 bandas, de graves a agudos.</summary>
    public IReadOnlyList<EqBand> EqBands => _eqBands ??= CreateEqBands();

    public EqBand EqPreamp => _eqPreamp ??= CreatePreamp();

    private IReadOnlyList<EqBand> CreateEqBands()
    {
        var saved = Settings.EqGains;
        return Equalizer.Frequencies.Select((f, i) =>
        {
            var band = new EqBand(i, f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}", f >= 1000 ? $"{f / 1000:0.#} kHz" : $"{f:0} Hz", OnEqBandChanged);
            band.SetSilently(saved != null && i < saved.Length ? saved[i] : 0);
            return band;
        }).ToList();
    }

    private EqBand CreatePreamp()
    {
        var pre = new EqBand(-1, "Pre", "Preamplificador: baja el volumen antes de ecualizar si al subir bandas satura", OnEqBandChanged);
        pre.SetSilently(Settings.EqPreamp);
        return pre;
    }

    public bool EqEnabled
    {
        get => Settings.EqEnabled;
        set
        {
            if (Settings.EqEnabled == value) return;
            Settings.EqEnabled = value;
            OnPropertyChanged();
            ApplyEq();
            SettingsStore.Save(Settings);
        }
    }

    /// <summary>Clave del preajuste elegido, o <see cref="CustomPreset"/>.</summary>
    public string EqPresetKey => Settings.EqPreset;

    /// <summary>Aplica un preajuste: todas las bandas a la vez y en el acto.</summary>
    public void ApplyEqPreset(string key)
    {
        if (EqPresets.FirstOrDefault(p => p.Key == key) is not { } preset) return;
        for (int i = 0; i < EqBands.Count; i++) EqBands[i].SetSilently(preset.Gains[i]);
        Settings.EqPreset = key;
        OnPropertyChanged(nameof(EqPresetKey));
        // elegir un preajuste es querer oírlo: se enciende
        if (!Settings.EqEnabled)
        {
            Settings.EqEnabled = true;
            OnPropertyChanged(nameof(EqEnabled));
        }
        StoreEq();
        ApplyEq();
        SettingsStore.Save(Settings);
    }

    /// <summary>Todo a 0 dB, preamplificador incluido.</summary>
    public void ResetEq()
    {
        foreach (var b in EqBands) b.SetSilently(0);
        EqPreamp.SetSilently(0);
        Settings.EqPreset = "flat";
        OnPropertyChanged(nameof(EqPresetKey));
        StoreEq();
        ApplyEq();
        SettingsStore.Save(Settings);
    }

    private void OnEqBandChanged(EqBand band)
    {
        // cada movimiento se aplica ya; el preajuste pasa a «personalizado» si ya no coincide con ninguno
        StoreEq();
        if (band.Index >= 0)
        {
            var gains = EqBands.Select(b => b.Value).ToArray();
            var match = EqPresets.FirstOrDefault(p => p.Gains.SequenceEqual(gains));
            var key = match?.Key ?? CustomPreset;
            if (key != Settings.EqPreset)
            {
                Settings.EqPreset = key;
                OnPropertyChanged(nameof(EqPresetKey));
            }
        }
        ApplyEq();
        // guardar en disco, cuando deje de arrastrar
        _eqSaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background,
            (s, e) => { ((DispatcherTimer)s!).Stop(); SettingsStore.Save(Settings); }, _ui);
        _eqSaveTimer.Stop();
        _eqSaveTimer.Start();
    }

    private void StoreEq()
    {
        Settings.EqGains = EqBands.Select(b => Math.Round(b.Value, 1)).ToArray();
        Settings.EqPreamp = Math.Round(EqPreamp.Value, 1);
    }

    /// <summary>Pasa los ajustes al audio (lo usa en el siguiente bloque que procesa).</summary>
    private void ApplyEq() => Equalizer.Set(Settings.EqEnabled, EqBands.Select(b => b.Value).ToArray(), EqPreamp.Value);
}
