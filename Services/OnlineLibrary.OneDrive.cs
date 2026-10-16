using System.Net;
using System.Text.Json;

namespace Echoplex.Services;

public static partial class OnlineLibrary
{
    /// <summary>
    /// Una carpeta (o un archivo) compartida de OneDrive con sus subcarpetas. Las subcarpetas se leen varias a la vez:
    /// cada una tarda medio segundo en contestar, y con muchos álbumes en fila se haría eterno.
    /// </summary>
    private static async Task ListOneDriveAsync(string link, OnlineIndex index, Action<int, string>? progress, CancellationToken ct)
    {
        OneDrive.Register(link);
        progress?.Invoke(0, "");
        var root = await OneDriveJsonAsync($"{OneDrive.Api}/shares/{OneDrive.ShareId(link)}/driveitem", ct, root: true);
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        if (root.TryGetProperty("file", out _))
        {
            if (DownloadUrl(root) is { } url && Wanted.Contains(System.IO.Path.GetExtension(name)))
                index.Files.Add(new OnlineEntry { Path = Clean(name), Url = url, Web = WebOf(root) ?? link });
            return;
        }
        if (!root.TryGetProperty("parentReference", out var parent) || !parent.TryGetProperty("driveId", out var d) || d.GetString() is not { } drive)
            throw new InvalidOperationException("OneDrive no dice dónde está la carpeta: prueba a copiar el enlace otra vez desde «Compartir»");
        index.Folders[""] = WebOf(root) ?? link;

        var files = new List<OnlineEntry>();
        var gate = new SemaphoreSlim(4);
        int songs = 0;

        async Task Walk(string id, string rel, int depth)
        {
            if (depth > 12) return;
            var subfolders = new List<Task>();
            await gate.WaitAsync(ct);
            try
            {
                string? next = $"{OneDrive.Api}/drives/{drive}/items/{id}/children?$top=999";
                while (next != null)
                {
                    var page = await OneDriveJsonAsync(next, ct, root: false);
                    foreach (var item in page.GetProperty("value").EnumerateArray())
                    {
                        var title = item.TryGetProperty("name", out var t) ? t.GetString() ?? "" : "";
                        if (title.Length == 0 || item.GetProperty("id").GetString() is not { } itemId) continue;
                        var path = rel.Length == 0 ? Clean(title) : rel + "\\" + Clean(title);
                        if (item.TryGetProperty("folder", out _))
                        {
                            lock (files) index.Folders[path] = WebOf(item) ?? link;
                            subfolders.Add(Walk(itemId, path, depth + 1));
                        }
                        else if (item.TryGetProperty("file", out _) && Wanted.Contains(System.IO.Path.GetExtension(title)) && DownloadUrl(item) is { } url)
                        {
                            lock (files)
                            {
                                files.Add(new OnlineEntry { Path = path, Url = url, Web = WebOf(item) });
                                if (LibraryService.AudioExtensions.Contains(System.IO.Path.GetExtension(title))) songs++;
                            }
                        }
                    }
                    next = page.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;
                }
            }
            finally
            {
                gate.Release();
            }
            int found;
            lock (files) found = songs;
            progress?.Invoke(found, rel);
            await Task.WhenAll(subfolders);
        }

        await Walk(root.GetProperty("id").GetString()!, "", 0);
        // siempre en el mismo orden (se leen a la vez): así al comprobar cambios no parece que se movió todo
        index.Files.AddRange(files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<JsonElement> OneDriveJsonAsync(string url, CancellationToken ct, bool root)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var resp = await RemoteFile.Client.GetAsync(url, timeout.Token);
        if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            throw new InvalidOperationException(root
                ? "OneDrive no deja ver la carpeta: compártela como «Cualquier persona con el enlace» y pega el enlace de «Copiar vínculo»"
                : "OneDrive dejó de dejar ver una de las subcarpetas (¿se ha dejado de compartir?)");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(timeout.Token));
        return doc.RootElement.Clone();
    }

    // dirección fija de descarga (la otra caduca a la hora); se descarga con el permiso de invitado en la cabecera
    private static string? DownloadUrl(JsonElement item) =>
        item.TryGetProperty("@content.downloadUrlNoAuth", out var u) && u.GetString() is { Length: > 0 } url ? url
        : item.TryGetProperty("parentReference", out var p) && p.TryGetProperty("driveId", out var d) && item.TryGetProperty("id", out var id)
            ? $"{OneDrive.Api}/drives/{d.GetString()}/items/{id.GetString()}/content"
            : null;

    private static string? WebOf(JsonElement item) => item.TryGetProperty("webUrl", out var w) ? w.GetString() : null;
}
