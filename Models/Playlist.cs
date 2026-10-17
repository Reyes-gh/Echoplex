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
    /// <summary>Reglas si es inteligente (sus canciones se rehacen solas); null en las de siempre.</summary>
    public SmartRules? Smart { get; set; }
    public bool IsSmart => Smart != null;
    public DateTime Created { get; }
    public string ContextId => "playlist:" + Id;

    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Se están arrastrando canciones encima: se marca para que se vea dónde caerán.</summary>
    [ObservableProperty] private bool _isDropTarget;

    public string CountText => Songs.Count == 1 ? "1 canción" : $"{Songs.Count} canciones";

    public void NotifySongsChanged() => OnPropertyChanged(nameof(CountText));
}
