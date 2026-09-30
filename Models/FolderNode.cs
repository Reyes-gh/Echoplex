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

    /// <summary>La canción que suena está en esta carpeta o en alguna de sus subcarpetas (se ilumina el camino).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>La canción que suena está justo en esta carpeta (el final del camino).</summary>
    [ObservableProperty] private bool _isPlayingHere;

    /// <summary>
    /// Lleva las barritas de ecualizador: la carpeta más honda del camino que se ve en el árbol (la primera
    /// plegada del camino o, si está todo abierto, la de la canción).
    /// </summary>
    [ObservableProperty] private bool _showsBars;

    public IEnumerable<FolderNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }
}
