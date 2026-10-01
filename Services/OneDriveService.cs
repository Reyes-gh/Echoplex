using System.Runtime.InteropServices;

namespace Echoplex.Services;

/// <summary>
/// "Descargar" al estilo Spotify sobre OneDrive: marcar archivos como "Mantener siempre en este
/// dispositivo" (anclados) o "Liberar espacio" (solo en la nube). OneDrive hace el trabajo.
/// </summary>
public static class OneDriveService
{
    private const uint Pinned = 0x80000;
    private const uint Unpinned = 0x100000;
    private const uint Invalid = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetFileAttributesW(string path, uint attrs);

    public static bool SetKeepOffline(string path, bool keep)
    {
        var a = GetFileAttributesW(path);
        if (a == Invalid) return false;
        a = keep ? (a | Pinned) & ~Unpinned : (a | Unpinned) & ~Pinned;
        return SetFileAttributesW(path, a);
    }

    public static bool IsCloudOnly(string path)
    {
        var a = GetFileAttributesW(path);
        return a != Invalid && LibraryService.IsCloudOnly((FileAttributes)a);
    }
}
