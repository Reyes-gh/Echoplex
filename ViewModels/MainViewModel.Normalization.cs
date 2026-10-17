using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>Normalización de volumen: que todas las canciones suenen igual de fuertes (Ajustes → Sonido).</summary>
public sealed partial class MainViewModel
{
    private readonly LoudnessAnalyzer _analyzer = new();

    public const double MaxCrossfade = 10;

    /// <summary>Apagada, por canción, por álbum o inteligente (por álbum si escuchas un álbum o una carpeta en orden).</summary>
    public string NormalizationMode
    {
        get => Settings.Normalization;
        set
        {
            if (Settings.Normalization == value) return;
            Settings.Normalization = value;
            OnPropertyChanged();
            SettingsStore.Save(Settings);
            Player.RefreshGain();
        }
    }

    /// <summary>Fundido entre canciones, en segundos (0 = apagado; como mucho 10).</summary>
    public double CrossfadeSeconds
    {
        get => Settings.Crossfade;
        set
        {
            value = Math.Clamp(value, 0, MaxCrossfade);
            if (Settings.Crossfade == value) return;
            Settings.Crossfade = value;
            Player.CrossfadeSeconds = value;
            OnPropertyChanged();
            SettingsStore.Save(Settings);
            Player.RefreshGain(); // la siguiente ya preparada se vuelve a preparar con el fundido nuevo
        }
    }

    /// <summary>Fundir también las pistas seguidas de un mismo disco.</summary>
    public bool CrossfadeAlbums
    {
        get => Settings.CrossfadeSameAlbum;
        set
        {
            if (Settings.CrossfadeSameAlbum == value) return;
            Settings.CrossfadeSameAlbum = value;
            Player.CrossfadeAlbums = value;
            OnPropertyChanged();
            SettingsStore.Save(Settings);
            Player.RefreshGain(); // la siguiente ya preparada se vuelve a preparar
        }
    }

    /// <summary>«Medir la biblioteca»: por dónde va, o null si no está midiendo.</summary>
    [ObservableProperty] private string? _measureStatus;

    public bool IsMeasuring => _analyzer.IsRunning;

    private void InitNormalization()
    {
        Player.GainFor = GainFor;
        Settings.Crossfade = Math.Clamp(Settings.Crossfade, 0, MaxCrossfade);
        Player.CrossfadeSeconds = Settings.Crossfade;
        Player.CrossfadeAlbums = Settings.CrossfadeSameAlbum;
        Player.Measured += OnMeasured;
        _analyzer.Progress += (done, total) => _ui.BeginInvoke(() => MeasureStatus = $"Midiendo el volumen: {done:N0} de {total:N0} canciones…");
        _analyzer.Finished += done => _ui.BeginInvoke(() =>
        {
            MeasureStatus = done == 0 ? "Ya estaba todo medido." : $"Listo: {done:N0} canciones medidas.";
            OnPropertyChanged(nameof(IsMeasuring));
            Player.RefreshGain();
        });
    }

    private double GainFor(Song song)
    {
        var mode = Settings.Normalization;
        if (mode == Normalization.Off) return 0;
        bool album = mode == Normalization.Album
                     || (mode == Normalization.Smart && !Player.Shuffle && Player.ContextId is { } ctx
                         && (ctx.StartsWith("album:", StringComparison.Ordinal) || ctx.StartsWith("folder:", StringComparison.Ordinal)));
        var albumSongs = album ? Library.GetAlbum(LibraryService.AlbumKey(song))?.Songs ?? new List<Song> { song } : new List<Song>();
        return Normalization.GainDb(song, album, albumSongs);
    }

    /// <summary>Una canción sonó entera: se guarda cuánto suena (la próxima vez ya sale normalizada).</summary>
    private void OnMeasured(string path, double lufs, double peak, double seconds)
    {
        if (Library.Find(path) is not { } song || song.TrackGain != null) return; // las que traen ReplayGain ya lo dicen
        bool first = LoudnessStore.Get(song) == null;
        LoudnessStore.Put(song, lufs, peak, seconds);
        LoudnessStore.Save();
        if (first) Player.RefreshGain(); // si es la siguiente ya preparada, que entre con su volumen
    }

    /// <summary>Mide en segundo plano las canciones del PC que aún no tienen medida ni etiquetas ReplayGain.</summary>
    public void StartMeasuring()
    {
        if (_analyzer.IsRunning)
        {
            _analyzer.Stop();
            MeasureStatus = "Parado.";
            return;
        }
        int pending = Library.Songs.Count(LoudnessAnalyzer.NeedsMeasure);
        if (pending == 0)
        {
            MeasureStatus = "Ya estaba todo medido.";
            return;
        }
        MeasureStatus = $"Midiendo el volumen: 0 de {pending:N0} canciones…";
        _analyzer.Start(Library.Songs);
        OnPropertyChanged(nameof(IsMeasuring));
    }

    /// <summary>Cuántas canciones tienen ya su volumen (por etiquetas o medido), para Ajustes.</summary>
    public string NormalizationSummary
    {
        get
        {
            var songs = Library.Songs;
            int tagged = songs.Count(s => s.TrackGain != null);
            int measured = songs.Count(s => s.TrackGain == null && LoudnessStore.Get(s) != null);
            return $"{tagged + measured:N0} de {songs.Count:N0} canciones con su volumen conocido ({tagged:N0} por etiquetas ReplayGain, {measured:N0} medidas por Echoplex).";
        }
    }
}
