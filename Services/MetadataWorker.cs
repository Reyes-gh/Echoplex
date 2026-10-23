using System.Windows.Threading;
using Echoplex.Models;

namespace Echoplex.Services;

public static class MetadataReader
{
    /// <summary>Abre las etiquetas de una canción: del disco o, si es de una carpeta en línea, de la nube (solo los trozos que mira).</summary>
    public static TagLib.File Open(string path) =>
        OnlineLibrary.IsOnline(path) ? OnlineLibrary.OpenTags(path) ?? throw new FileNotFoundException(path) : TagLib.File.Create(path);

    private static double? Finite(double v) => double.IsFinite(v) ? Math.Round(v, 4) : null;

    /// <summary>
    /// El BPM de las etiquetas. TagLib lo lee de TBPM (MP3) y tmpo (M4A), pero en FLAC/OGG solo mira TEMPO, y la mayoría
    /// de programas (Mp3tag, Picard, Mixed In Key…) escriben BPM; además a veces viene con decimales («127.98»).
    /// </summary>
    private static uint ReadBpm(TagLib.File f)
    {
        uint bpm = f.Tag.BeatsPerMinute;
        if (bpm > 0) return bpm;
        string? text = null;
        if (f.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment x)
            text = x.GetFirstField("BPM") ?? x.GetFirstField("TEMPO");
        else if (f.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            text = id3.GetFrames<TagLib.Id3v2.TextInformationFrame>("TBPM").FirstOrDefault()?.Text.FirstOrDefault();
        return double.TryParse(text?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
               && v is > 0 and < 1000 ? (uint)Math.Round(v) : 0;
    }

    public static CachedMeta Read(string path)
    {
        try
        {
            using var f = Open(path);
            var t = f.Tag;
            return new CachedMeta
            {
                T = t.Title,
                A = t.FirstPerformer ?? t.FirstAlbumArtist,
                B = t.Album,
                N = t.Track,
                Ms = (long)f.Properties.Duration.TotalMilliseconds,
                AA = t.FirstAlbumArtist,
                D = t.Disc,
                Y = t.Year,
                G = t.FirstGenre,
                RT = Finite(t.ReplayGainTrackGain),
                RA = Finite(t.ReplayGainAlbumGain),
                PT = Finite(t.ReplayGainTrackPeak),
                PA = Finite(t.ReplayGainAlbumPeak),
                Bpm = ReadBpm(f),
                V = CachedMeta.Current,
            };
        }
        catch
        {
            // ilegible: se apunta igual (con la versión actual) para no volver a intentarlo en cada arranque
            return new CachedMeta { V = CachedMeta.Current };
        }
    }

    /// <summary>
    /// Como <see cref="ReadPicture"/>, pero distingue «no trae carátula» (true y null) de «no se pudo leer» (false). Un
    /// archivo que TagLib no entiende cuenta como sin carátula: no va a cambiar, y releerlo lo recorre entero cada vez.
    /// </summary>
    public static bool TryReadPicture(string path, out byte[]? picture)
    {
        try
        {
            using var f = Open(path);
            var pics = f.Tag.Pictures;
            var p = pics.FirstOrDefault(x => x.Type == TagLib.PictureType.FrontCover) ?? pics.FirstOrDefault();
            picture = p?.Data?.Data;
            return true;
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException)
        {
            picture = null;
            return true;
        }
        catch
        {
            picture = null;
            return false;
        }
    }

    public static byte[]? ReadPicture(string path)
    {
        try
        {
            using var f = Open(path);
            var pics = f.Tag.Pictures;
            var p = pics.FirstOrDefault(x => x.Type == TagLib.PictureType.FrontCover) ?? pics.FirstOrDefault();
            return p?.Data?.Data;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Lee etiquetas en segundo plano. Primero las canciones de la página visible, luego el resto.
/// Nunca toca archivos que están solo en OneDrive (eso los descargaría).
/// </summary>
public sealed class MetadataWorker
{
    private readonly MetadataCache _cache;
    private readonly Dispatcher _ui;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly DispatcherTimer _notify;
    private Queue<Song> _priority = new();
    private List<Song> _all = new();
    private int _allIndex;

    public event Action? Updated;

    public MetadataWorker(MetadataCache cache, Dispatcher ui)
    {
        _cache = cache;
        _ui = ui;
        _notify = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
            (s, e) => { ((DispatcherTimer)s!).Stop(); Updated?.Invoke(); }, ui);
        _notify.Stop();
    }

    private static bool Needs(Song s) => !s.MetadataLoaded && !s.IsCloud;

    public void Start(List<Song> all)
    {
        _all = all;
        var thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Metadata" };
        thread.Start();
    }

    /// <summary>Cambia a una biblioteca recién cargada (tras cambiar de carpeta o reescanear).</summary>
    public void Reset(List<Song> all)
    {
        lock (_lock)
        {
            _all = all;
            _allIndex = 0;
            _priority = new Queue<Song>();
        }
        _signal.Release();
    }

    public void Prioritize(IEnumerable<Song> songs)
    {
        var q = new Queue<Song>(songs.Where(Needs));
        lock (_lock) _priority = q;
        _signal.Release();
    }

    /// <summary>Relee un archivo concreto (p. ej. al reproducir uno que estaba en la nube).</summary>
    public void ReadNow(Song s, Action? done = null)
    {
        Task.Run(() =>
        {
            var meta = MetadataReader.Read(s.Path);
            bool cloud;
            if (OnlineLibrary.IsOnline(s.Path)) cloud = false;
            else try { cloud = LibraryService.IsCloudOnly(File.GetAttributes(s.Path)); } catch { cloud = s.IsCloud; }
            _cache.Put(s, meta);
            _ui.BeginInvoke(() =>
            {
                var keep = s.Duration;
                LibraryService.ApplyMeta(s, meta);
                if (meta.Ms <= 0) s.Duration = keep;
                s.IsCloud = cloud;
                done?.Invoke();
                ScheduleNotify();
            });
        });
    }

    private void Loop()
    {
        while (true)
        {
            Song? song = null;
            lock (_lock)
            {
                while (_priority.Count > 0)
                {
                    var c = _priority.Dequeue();
                    if (Needs(c)) { song = c; break; }
                }
                if (song == null)
                {
                    while (_allIndex < _all.Count)
                    {
                        var c = _all[_allIndex++];
                        if (Needs(c)) { song = c; break; }
                    }
                }
            }

            if (song == null)
            {
                _signal.Wait();
                continue;
            }

            song.MetadataLoaded = true;
            var meta = MetadataReader.Read(song.Path);
            _cache.Put(song, meta);
            var s = song;
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                LibraryService.ApplyMeta(s, meta);
                ScheduleNotify();
            });
        }
    }

    private void ScheduleNotify()
    {
        if (!_notify.IsEnabled) _notify.Start();
    }
}
