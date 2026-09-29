using System.Collections.ObjectModel;
using System.Text.Json;
using Echoplex.Models;

namespace Echoplex.Services;

public sealed class PlaylistData
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> Songs { get; set; } = new();
    public DateTime Created { get; set; } = DateTime.Now;
}

public sealed class HistoryEntry
{
    public string P { get; set; } = "";
    public DateTime At { get; set; }
}

public sealed class PlayStat
{
    public int Count { get; set; }
    public double Seconds { get; set; }
    public DateTime Last { get; set; }
}

public sealed class UserData
{
    public List<string> Favorites { get; set; } = new();
    public List<PlaylistData> Playlists { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public Dictionary<string, PlayStat> Stats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Segundos escuchados por día (yyyy-MM-dd).</summary>
    public Dictionary<string, double> Daily { get; set; } = new();
}

/// <summary>Favoritas, playlists, historial y estadísticas de escucha.</summary>
public sealed class UserDataService
{
    private const int MaxHistory = 3000;
    private readonly string _file = Path.Combine(SettingsStore.DataDirectory, "userdata.json");
    private UserData _data = new();
    private HashSet<string> _favorites = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public ObservableCollection<Playlist> Playlists { get; } = new();

    public event Action? FavoritesChanged;
    public event Action? PlaylistsChanged;

    public IReadOnlyList<string> Favorites => _data.Favorites;
    public IReadOnlyList<HistoryEntry> History => _data.History;
    public IReadOnlyDictionary<string, PlayStat> Stats => _data.Stats;
    public IReadOnlyDictionary<string, double> Daily => _data.Daily;

    public void Load()
    {
        try
        {
            if (File.Exists(_file))
                _data = JsonSerializer.Deserialize<UserData>(File.ReadAllText(_file)) ?? new UserData();
        }
        catch
        {
            _data = new UserData();
        }
        _data.Stats = new Dictionary<string, PlayStat>(_data.Stats, StringComparer.OrdinalIgnoreCase);
        _favorites = new HashSet<string>(_data.Favorites, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Rellena la colección observable (debe llamarse desde el hilo de la interfaz).</summary>
    public void PopulatePlaylists()
    {
        Playlists.Clear();
        foreach (var p in _data.Playlists) Playlists.Add(new Playlist(p.Id, p.Name, p.Songs, p.Created));
    }

    public void Save(bool force = false)
    {
        if (!_dirty && !force) return;
        try
        {
            _data.Playlists = Playlists.Select(p => new PlaylistData { Id = p.Id, Name = p.Name, Songs = p.Songs, Created = p.Created }).ToList();
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
            File.Move(tmp, _file, true);
            _dirty = false;
        }
        catch
        {
            // se reintenta en el siguiente guardado
        }
    }

    // ---------- favoritas ----------

    public bool IsFavorite(string path) => _favorites.Contains(path);

    public void SetFavorite(Song song, bool value)
    {
        if (value == _favorites.Contains(song.Path)) { song.IsFavorite = value; return; }
        if (value)
        {
            _favorites.Add(song.Path);
            _data.Favorites.Insert(0, song.Path);
        }
        else
        {
            _favorites.Remove(song.Path);
            _data.Favorites.RemoveAll(p => string.Equals(p, song.Path, StringComparison.OrdinalIgnoreCase));
        }
        song.IsFavorite = value;
        _dirty = true;
        FavoritesChanged?.Invoke();
    }

    // ---------- playlists ----------

    public Playlist CreatePlaylist(string name, IEnumerable<Song>? songs = null)
    {
        var p = new Playlist(Guid.NewGuid().ToString("N"), name, songs?.Select(s => s.Path).ToList() ?? new(), DateTime.Now);
        Playlists.Add(p);
        Touch();
        return p;
    }

    public void RenamePlaylist(Playlist p, string name)
    {
        p.Name = name;
        Touch();
    }

    public void DeletePlaylist(Playlist p)
    {
        Playlists.Remove(p);
        Touch();
    }

    public int AddToPlaylist(Playlist p, IEnumerable<Song> songs)
    {
        int added = 0;
        foreach (var s in songs)
        {
            if (p.Songs.Contains(s.Path, StringComparer.OrdinalIgnoreCase)) continue;
            p.Songs.Add(s.Path);
            added++;
        }
        Touch(p);
        return added;
    }

    public void RemoveFromPlaylist(Playlist p, IEnumerable<Song> songs)
    {
        var set = new HashSet<string>(songs.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        p.Songs.RemoveAll(set.Contains);
        Touch(p);
    }

    public void MoveInPlaylist(Playlist p, Song song, int delta)
    {
        int i = p.Songs.FindIndex(x => string.Equals(x, song.Path, StringComparison.OrdinalIgnoreCase));
        int j = i + delta;
        if (i < 0 || j < 0 || j >= p.Songs.Count) return;
        (p.Songs[i], p.Songs[j]) = (p.Songs[j], p.Songs[i]);
        Touch(p);
    }

    private void Touch(Playlist? p = null)
    {
        p?.NotifySongsChanged();
        _dirty = true;
        PlaylistsChanged?.Invoke();
    }

    // ---------- escucha ----------

    public void RecordListen(Song song, double seconds)
    {
        if (seconds <= 0) return;
        if (!_data.Stats.TryGetValue(song.Path, out var st)) _data.Stats[song.Path] = st = new PlayStat();
        st.Seconds += seconds;
        var day = DateTime.Now.ToString("yyyy-MM-dd");
        _data.Daily[day] = _data.Daily.GetValueOrDefault(day) + seconds;
        _dirty = true;
    }

    public void RecordPlay(Song song)
    {
        if (!_data.Stats.TryGetValue(song.Path, out var st)) _data.Stats[song.Path] = st = new PlayStat();
        st.Count++;
        st.Last = DateTime.Now;
        _data.History.Insert(0, new HistoryEntry { P = song.Path, At = DateTime.Now });
        if (_data.History.Count > MaxHistory) _data.History.RemoveRange(MaxHistory, _data.History.Count - MaxHistory);
        _dirty = true;
    }

    public PlayStat? GetStat(string path) => _data.Stats.GetValueOrDefault(path);
}
