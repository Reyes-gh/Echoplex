namespace Echoplex.ViewModels;

/// <summary>Una carpeta de música en Ajustes.</summary>
public sealed record MusicFolderItem(string Path, bool Exists, int Count)
{
    public string Name => System.IO.Path.GetFileName(Path) is { Length: > 0 } n ? n : Path;
    public string Detail => !Exists ? "No se encuentra: " + Path : $"{Path} · {(Count == 1 ? "1 canción" : $"{Count:N0} canciones")}";
}
