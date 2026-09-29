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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryArtist))]
    private string _artist = "";

    private string _title = "";

    /// <summary>Ajuste global: mostrar (y ordenar por) el nombre del archivo en lugar del título de los metadatos.</summary>
    public static bool PreferFileName { get; set; }

    /// <summary>Nombre del archivo sin extensión.</summary>
    public string FileTitle => System.IO.Path.GetFileNameWithoutExtension(FileName);

    /// <summary>Título que se muestra: el de los metadatos o, con <see cref="PreferFileName"/>, el nombre del archivo.</summary>
    public string Title
    {
        get => PreferFileName ? FileTitle : _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>Avisa a la interfaz de que el título mostrado cambió (al activar o desactivar la opción).</summary>
    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
    [ObservableProperty] private string _album = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private TimeSpan? _duration;

    /// <summary>El archivo es un marcador de OneDrive: leerlo lo descarga.</summary>
    [ObservableProperty] private bool _isCloud;

    /// <summary>Es la canción cargada en el reproductor.</summary>
    [ObservableProperty] private bool _isCurrent;

    [ObservableProperty] private bool _isFavorite;

    public string DurationText => Duration is { } d ? FormatTime(d) : "";

    /// <summary>Artista principal, sin colaboraciones ("feat.", "ft.").</summary>
    public string PrimaryArtist
    {
        get
        {
            var a = Artist;
            foreach (var sep in new[] { " feat.", " feat ", " ft.", " ft ", " featuring ", " Feat.", " Ft.", " FEAT." })
            {
                int i = a.IndexOf(sep, StringComparison.Ordinal);
                if (i > 0) a = a[..i];
            }
            return a.Trim().TrimEnd('(', '[').Trim();
        }
    }

    public static string FormatTime(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";
}
