using CommunityToolkit.Mvvm.ComponentModel;

namespace Echoplex.Models;

public sealed partial class Playlist : ObservableObject
{
    public Playlist(string id, string name, List<string> songs, DateTime created)
    {
        Id = id;
        _name = name;
        Songs = songs;
        Created = created;
    }

    public string Id { get; }
    public List<string> Songs { get; }
    public DateTime Created { get; }
    public string ContextId => "playlist:" + Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isSelected;

    public string CountText => Songs.Count == 1 ? "1 canción" : $"{Songs.Count} canciones";

    public void NotifySongsChanged() => OnPropertyChanged(nameof(CountText));
}
