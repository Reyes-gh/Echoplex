using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>La forma de onda en la barra de progreso (Ajustes → Apariencia; apagada de serie).</summary>
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _waveCts;

    /// <summary>La de la canción que suena (null mientras se calcula, si está apagada o si es de la nube).</summary>
    [ObservableProperty] private float[]? _currentWaveform;

    public bool WaveformSeek
    {
        get => Settings.WaveformSeek;
        set
        {
            if (Settings.WaveformSeek == value) return;
            Settings.WaveformSeek = value;
            OnPropertyChanged();
            SettingsStore.Save(Settings);
            LoadWaveform(Player.Current);
        }
    }

    private void InitWaveform() => Player.SongOpened += LoadWaveform;

    private async void LoadWaveform(Song? song)
    {
        _waveCts?.Cancel();
        CurrentWaveform = null;
        if (song == null || !Settings.WaveformSeek || !WaveformService.CanCompute(song)) return;
        if (WaveformService.Cached(song) is { } cached)
        {
            CurrentWaveform = cached;
            return;
        }
        var cts = _waveCts = new CancellationTokenSource();
        var peaks = await WaveformService.ComputeAsync(song, cts.Token);
        if (!cts.IsCancellationRequested && Player.Current == song) CurrentWaveform = peaks;
    }
}
