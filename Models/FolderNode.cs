using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Echoplex.Models;

public sealed partial class FolderNode : ObservableObject
{
    public FolderNode(string name, string path, int count, FolderNode? parent)
    {
        Name = name;
        Path = path;
        Count = count;
        Parent = parent;
    }

    public string Name { get; }
    public string Path { get; }
    public int Count { get; }
    public FolderNode? Parent { get; }
    public bool IsRoot => Parent == null;
    public ObservableCollection<FolderNode> Children { get; } = new();

    /// <summary>Miniatura: la primera imagen que haya dentro de la carpeta (o de sus subcarpetas).</summary>
    [ObservableProperty] private BitmapSource? _cover;
    public bool CoverRequested { get; set; }

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isSelected;

    /// <summary>La cola de reproducción actual proviene de esta carpeta.</summary>
    [ObservableProperty] private bool _isActive;

    public IEnumerable<FolderNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }
}
