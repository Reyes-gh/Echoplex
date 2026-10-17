using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>Una carpeta vacía de la revisión.</summary>
public sealed record EmptyFolderItem(string Path, string Name, string Where);

/// <summary>Salud de la biblioteca: lo que se puede arreglar (solo se enseña; Echoplex no cambia nada).</summary>
public sealed partial class HealthPage : PageBase
{
    /// <summary>Sin filtro en esta página (el cuadro de filtro está oculto); evita que su enlace falle.</summary>
    public string Filter { get; set; } = "";

    /// <summary>Por dónde va la revisión, o null al terminar.</summary>
    [ObservableProperty] private string? _status = "Revisando la biblioteca…";
    [ObservableProperty] private List<HealthIssue>? _issues;
    [ObservableProperty] private List<EmptyFolderItem>? _emptyFolders;
    [ObservableProperty] private string? _note;
}

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _healthCts;
    private HealthPage? _healthPage;

    /// <summary>Abre la página y revisa la biblioteca en un hilo de prioridad mínima (no le quita CPU a nada).</summary>
    public void OpenHealth()
    {
        if (IsLoading) return;
        _healthCts?.Cancel();
        var cts = _healthCts = new CancellationTokenSource();
        var page = _healthPage = new HealthPage { Title = "Salud de la biblioteca", Glyph = "", Breadcrumb = "Resumen / Salud de la biblioteca" };
        Navigate(page);
        var entries = Library.Songs.Select(s => new LibraryHealth.Entry(s, _cache.TryGet(s, out var m) ? m : null)).ToList();
        var roots = Settings.MusicRoots.ToList();
        var thread = new Thread(() =>
        {
            try
            {
                var report = LibraryHealth.Analyze(entries, roots, Covers.FindCoverFile,
                    text => _ui.BeginInvoke(() => { if (!cts.IsCancellationRequested) page.Status = text; }), cts.Token);
                _ui.BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    page.Issues = report.Issues;
                    page.EmptyFolders = report.EmptyFolders.Select(p => new EmptyFolderItem(p, System.IO.Path.GetFileName(p), WhereIs(p, roots))).ToList();
                    page.Note = report.Unread > 0
                        ? $"{report.Unread:N0} canciones aún no tienen las etiquetas leídas y no se han revisado (Echoplex las está leyendo en segundo plano)."
                        : null;
                    page.Status = null;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _ui.BeginInvoke(() => page.Status = "No se pudo terminar la revisión: " + ex.Message);
            }
        })
        { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Salud de la biblioteca" };
        thread.Start();
    }

    /// <summary>Dónde está una carpeta: su carpeta de música y el camino hasta ella («Música / Rock / Disco»).</summary>
    private static string WhereIs(string path, List<string> roots)
    {
        var parent = System.IO.Path.GetDirectoryName(path) ?? path;
        var root = roots.Where(r => parent.StartsWith(r, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Length).FirstOrDefault();
        if (root == null) return parent;
        var rel = System.IO.Path.GetRelativePath(root, parent);
        return rel == "." ? LibraryService.RootName(root) : LibraryService.RootName(root) + " / " + rel.Replace("\\", " / ");
    }

    /// <summary>Las canciones de un problema, en una lista normal (para escucharlas, ir a su carpeta…).</summary>
    public void OpenHealthIssue(HealthIssue issue)
    {
        if (!issue.HasSongs) return;
        var songs = issue.GroupByFolder // agrupadas por carpeta, cada una con su portada (o sin ella)
            ? issue.Songs.OrderBy(s => s.Directory, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.DiscNumber).ThenBy(s => s.TrackNumber).ThenBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList()
            : issue.Songs.ToList();
        var extras = issue.Extras?.ToList();
        Navigate(new SongListPage("health:" + issue.Key, ListKind.Health, songs, extras)
        {
            Title = issue.Title,
            Glyph = issue.Glyph,
            Breadcrumb = "Salud de la biblioteca / " + issue.Title,
            ExtraHeader = extras != null ? "Repetida" : null,
            GroupByFolder = issue.GroupByFolder,
            BaseProperties = { new PropertyItem("", "Qué pasa", issue.Description) },
        });
    }
}
