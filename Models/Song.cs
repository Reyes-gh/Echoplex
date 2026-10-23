using CommunityToolkit.Mvvm.ComponentModel;

namespace Echoplex.Models;

public sealed partial class Song : ObservableObject
{
    public Song(string path, string relativePath, long length, long lastWriteTicks, long createdTicks, bool isCloud)
    {
        Path = path;
        RelativePath = relativePath;
        Directory = System.IO.Path.GetDirectoryName(path) ?? "";
        FileName = System.IO.Path.GetFileName(path);
        Format = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var sep = relativePath.IndexOf(System.IO.Path.DirectorySeparatorChar);
        TopFolder = sep > 0 ? relativePath[..sep] : "";
        TopFolderPath = sep > 0 ? path[..(path.Length - relativePath.Length)] + TopFolder : "";
        Length = length;
        LastWriteTicks = lastWriteTicks;
        CreatedTicks = createdTicks;
        _isCloud = isCloud;
    }

    public string Path { get; }
    public string RelativePath { get; }
    public string Directory { get; }
    public string FileName { get; }
    public string Format { get; }
    public string TopFolder { get; }
    /// <summary>Ruta completa de la carpeta de primer nivel (dentro de su carpeta de música).</summary>
    public string TopFolderPath { get; }
    public long Length { get; }
    public long LastWriteTicks { get; }
    public long CreatedTicks { get; }
    public uint TrackNumber { get; set; }
    public bool MetadataLoaded { get; set; }
    /// <summary>Número de disco de las etiquetas (0 si no lo dicen).</summary>
    public uint DiscNumber { get; set; }
    public uint Year { get; set; }
    public string Genre { get; set; } = "";

    /// <summary>ReplayGain de las etiquetas: ganancias en dB y picos lineales (null si no lo traen).</summary>
    public double? TrackGain { get; set; }
    public double? AlbumGain { get; set; }
    public double? TrackPeak { get; set; }
    public double? AlbumPeak { get; set; }

    /// <summary>Pulsaciones por minuto de las etiquetas (0 si no lo dicen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Bpm), nameof(BpmText), nameof(BpmDoubtful), nameof(BpmTip), nameof(BpmLoading))]
    private uint _tagBpm;

    /// <summary>El de Deezer (si está activado en Ajustes y las etiquetas no lo traen; 0 si no).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Bpm), nameof(BpmText), nameof(BpmDoubtful), nameof(BpmTip), nameof(BpmLoading))]
    private uint _onlineBpm;

    /// <summary>El que calculó Echoplex escuchando el audio (si está activado en Ajustes; 0 si no).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Bpm), nameof(BpmText), nameof(BpmDoubtful), nameof(BpmTip), nameof(BpmLoading))]
    private uint _localBpm;

    /// <summary>¿El calculado salió seguro? Si no, se enseña con un aviso.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmDoubtful), nameof(BpmTip))]
    private bool _localBpmSure;

    /// <summary>¿Se está calculando ahora? (la de una carpeta en línea que acaba de empezar a sonar, según se baja)</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmTip), nameof(BpmLoading))]
    private bool _bpmCalculating;

    /// <summary>El BPM: el de las etiquetas manda; si no lo traen, el de Deezer, y si tampoco, el calculado.</summary>
    public uint Bpm => TagBpm > 0 ? TagBpm : OnlineBpm > 0 ? OnlineBpm : LocalBpm;

    /// <summary>Aún no hay BPM que enseñar y se está calculando (en la tabla, un círculo que gira).</summary>
    public bool BpmLoading => BpmCalculating && Bpm == 0;

    /// <summary>El que se enseña es el calculado y el cálculo no salió seguro (en la tabla, con un aviso).</summary>
    public bool BpmDoubtful => TagBpm == 0 && OnlineBpm == 0 && LocalBpm > 0 && !LocalBpmSure;

    /// <summary>De dónde sale el BPM (y, si es dudoso, por qué), para la descripción emergente de la tabla.</summary>
    public string BpmTip =>
        BpmLoading ? "Calculando el BPM con el audio que se está bajando para que suene"
        : TagBpm > 0 ? "Pulsaciones por minuto, de las etiquetas del archivo"
        : OnlineBpm > 0 ? "Pulsaciones por minuto, de Deezer"
        : LocalBpm == 0 ? "Pulsaciones por minuto"
        : LocalBpmSure ? "Pulsaciones por minuto, calculadas por Echoplex escuchando el audio"
        : "Aproximado: Echoplex lo calculó escuchando el audio, pero el ritmo admite más de una lectura y el cálculo no está seguro. " +
          "El de verdad puede ser otro: a menudo el doble, la mitad o 2/3 de este.";

    /// <summary>El BPM para la tabla (vacío si no se sabe).</summary>
    public string BpmText => Bpm > 0 ? Bpm.ToString() : "";

    /// <summary>Artista del álbum de las etiquetas (vacío si no lo dicen).</summary>
    public string AlbumArtist { get; set; } = "";

    private static readonly HashSet<string> VariousNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "various artists", "various", "va", "v.a.", "v/a", "varios artistas", "varios", "artistas varios", "compilation", "recopilatorio",
    };

    /// <summary>Recopilatorio (artista del álbum «Various Artists» y parecidos): no cuenta como artista propio.</summary>
    public bool IsCompilation => VariousNames.Contains(AlbumArtist.Trim());

    /// <summary>
    /// A qué artista pertenece en Artistas: el artista del álbum si lo dicen las etiquetas (así un disco con invitados no
    /// reparte sus canciones entre diez artistas) y, si no, el artista principal de la canción.
    /// </summary>
    public string ArtistKey => AlbumArtist.Length > 0 && !IsCompilation ? AlbumArtist.Trim() : PrimaryArtist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryArtist))]
    private string _artist = "";

    private string _title = "";

    /// <summary>
    /// Mostrar (y ordenar por) el nombre del archivo en lugar del título de los metadatos. Lo decide su carpeta
    /// (o la más cercana hacia arriba con ajuste propio) y, si ninguna lo tiene, el ajuste general.
    /// </summary>
    public bool UseFileName { get; private set; }

    /// <summary>Título de los metadatos (o el deducido del nombre), aunque se muestre el nombre del archivo.</summary>
    public string MetaTitle => _title;

    /// <summary>Nombre del archivo sin extensión.</summary>
    public string FileTitle => _fileTitle ??= System.IO.Path.GetFileNameWithoutExtension(FileName);
    private string? _fileTitle;

    /// <summary>Título que se muestra: el de los metadatos o, con <see cref="UseFileName"/>, el nombre del archivo.</summary>
    public string Title
    {
        get => UseFileName ? FileTitle : _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>Cambia el modo de título y avisa a la interfaz si cambió.</summary>
    public void SetUseFileName(bool value)
    {
        if (UseFileName == value) return;
        UseFileName = value;
        OnPropertyChanged(nameof(Title));
    }
    [ObservableProperty] private string _album = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private TimeSpan? _duration;

    /// <summary>El archivo es un marcador de OneDrive: leerlo lo descarga.</summary>
    [ObservableProperty] private bool _isCloud;

    /// <summary>
    /// Alguna canción pasó a estar solo en la nube o en el dispositivo (se cambia siempre desde el hilo de la interfaz):
    /// el árbol vuelve a mirar qué carpetas están enteras en la nube.
    /// </summary>
    public static event Action? CloudStateChanged;

    partial void OnIsCloudChanged(bool value) => CloudStateChanged?.Invoke();

    /// <summary>Es la canción cargada en el reproductor.</summary>
    [ObservableProperty] private bool _isCurrent;

    [ObservableProperty] private bool _isFavorite;

    public string DurationText => Duration is { } d ? FormatTime(d) : "";

    private static readonly string[] FeatSeparators = { " feat.", " feat ", " ft.", " ft ", " featuring ", " Feat.", " Ft.", " FEAT." };
    private static readonly char[] OpenBrackets = { '(', '[' };
    private string? _primaryArtist;

    partial void OnArtistChanged(string value) => _primaryArtist = null;

    /// <summary>Artista principal, sin colaboraciones ("feat.", "ft."). Se calcula una vez por cada artista.</summary>
    public string PrimaryArtist => _primaryArtist ??= ComputePrimaryArtist(Artist);

    private static string ComputePrimaryArtist(string a)
    {
        foreach (var sep in FeatSeparators)
        {
            int i = a.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) a = a[..i];
        }
        return a.Trim().TrimEnd(OpenBrackets).Trim();
    }

    public static string FormatTime(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";
}
