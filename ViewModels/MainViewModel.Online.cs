using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Models;
using Echoplex.Services;

namespace Echoplex.ViewModels;

/// <summary>
/// Una carpeta en línea en Ajustes: su nombre, su URL y cuántas canciones tiene, o qué está pasando (buscando
/// canciones, leyendo sus datos…) con el icono de carga girando. Se actualiza en el sitio, sin rehacer la fila.
/// </summary>
public sealed partial class OnlineFolderItem : ObservableObject
{
    public OnlineFolderItem(string id, string name, string url)
    {
        Id = id;
        Name = name;
        Url = url;
    }

    public string Id { get; }
    public string Name { get; }
    public string Url { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private int _count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _status;

    /// <summary>Algo en marcha: se ve el icono de carga.</summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>Conectada (su interruptor en Ajustes); desconectada lleva el mundo tachado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private bool _enabled = true;

    public string Detail => !Enabled ? "Desconectada" : Status ?? (Count == 1 ? "1 canción" : $"{Count:N0} canciones");
}

public sealed partial class MainViewModel
{
    /// <summary>Carpetas en línea configuradas (para Ajustes), más la que se esté añadiendo.</summary>
    public ObservableCollection<OnlineFolderItem> OnlineFolders { get; } = new();

    // lo que está pasando con cada carpeta en línea ("Buscando canciones… 37", un error), por su id
    private readonly Dictionary<string, (string Text, bool Busy)> _onlineStatus = new();
    private bool _refreshingOnline;
    private OnlineFolderItem? _adding;
    private DispatcherTimer? _onlineMetaTimer;

    /// <summary>Las carpetas de música del disco más las raíces de las carpetas en línea.</summary>
    private static List<string> WithOnline(IEnumerable<string> local) =>
        local.Where(r => !OnlineLibrary.IsOnline(r)).Concat(OnlineLibrary.RootPaths).ToList();

    /// <summary>Pone la lista de Ajustes al día sin rehacer las filas que ya estaban.</summary>
    private void RefreshOnlineFolders()
    {
        var sources = OnlineLibrary.Sources;
        for (int i = OnlineFolders.Count - 1; i >= 0; i--)
            if (OnlineFolders[i] != _adding && sources.All(s => s.Id != OnlineFolders[i].Id)) OnlineFolders.RemoveAt(i);
        foreach (var s in sources)
        {
            var item = OnlineFolders.FirstOrDefault(o => o.Id == s.Id);
            if (item == null)
            {
                item = new OnlineFolderItem(s.Id, s.Name, s.Url);
                OnlineFolders.Insert(_adding != null ? OnlineFolders.IndexOf(_adding) : OnlineFolders.Count, item);
            }
            item.Count = Library.GetCount(OnlineLibrary.RootPath(s));
            item.Enabled = s.Enabled;
            var status = _onlineStatus.GetValueOrDefault(s.Id);
            item.Status = status.Text;
            item.IsBusy = status.Busy;
        }
        UpdateOnlineMetadataProgress();
    }

    private void SetOnlineStatus(string id, string? status, bool busy = false)
    {
        if (status == null) _onlineStatus.Remove(id); else _onlineStatus[id] = (status, busy);
        RefreshOnlineFolders();
    }

    private static string Songs(int n) => n == 1 ? "1 canción" : $"{n:N0} canciones";

    /// <summary>
    /// Mientras se leen los datos (título, artista, duración) de las canciones en línea, cada fila lo cuenta:
    /// «Leyendo datos de las canciones: 54 de 324». Se mira cada poco y se para solo al acabar.
    /// </summary>
    private void UpdateOnlineMetadataProgress()
    {
        bool pending = false;
        foreach (var item in OnlineFolders)
        {
            if (item == _adding || _onlineStatus.ContainsKey(item.Id)) { pending |= item.IsBusy; continue; }
            if (OnlineLibrary.Sources.FirstOrDefault(s => s.Id == item.Id) is not { } source) continue;
            var songs = Library.SongsUnder(OnlineLibrary.RootPath(source));
            int done = songs.Count(s => s.MetadataLoaded);
            if (done < songs.Count)
            {
                item.Status = $"Leyendo datos de las canciones: {done:N0} de {songs.Count:N0}";
                item.IsBusy = true;
                pending = true;
            }
            else if (item.IsBusy)
            {
                item.Status = null;
                item.IsBusy = false;
            }
        }
        if (pending && OnlineLibrary.Sources.Count > 0)
        {
            _onlineMetaTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                (s, e) => UpdateOnlineMetadataProgress(), _ui);
            if (!_onlineMetaTimer.IsEnabled) _onlineMetaTimer.Start();
        }
        else _onlineMetaTimer?.Stop();
    }

    /// <summary>
    /// Conecta o desconecta una carpeta en línea (desde su página o en Ajustes). Desconectada sigue en el panel lateral,
    /// atenuada y con el mundo tachado, pero sus canciones no se ven ni suenan y no se le pide nada a la nube. Al
    /// conectarla vuelven sus canciones (del índice guardado) y se mira si ha cambiado algo.
    /// </summary>
    public async Task SetOnlineConnectedAsync(string id, bool connected)
    {
        if (OnlineLibrary.Sources.FirstOrDefault(s => s.Id == id) is not { } source || source.Enabled == connected) return;
        source.Enabled = connected;
        SaveOnlineSources();
        RefreshOnlineFolders();
        await RescanAsync(announce: false);
        var root = OnlineLibrary.RootPath(source);
        if (CurrentPage is SongListPage { FolderPath: { } open } && Eq(open, root)) OpenFolder(root, replace: true);
        ShowToast(connected ? $"«{source.Name}» conectada" : $"«{source.Name}» desconectada", 3);
        if (connected) await RefreshOnlineFoldersAsync(announce: false);
    }

    private void SaveOnlineSources()
    {
        Settings.OnlineSources = OnlineLibrary.Sources.ToList();
        SettingsStore.Save(Settings);
    }

    /// <summary>
    /// Añade una carpeta en línea con el nombre que eligió el usuario (el de su raíz en el árbol). Primero lee qué hay
    /// en la URL (en Ajustes se ve cómo va subiendo el número de canciones); devuelve el motivo si no se puede (no es
    /// pública, no hay música…), o null si quedó añadida.
    /// </summary>
    public async Task<string?> AddOnlineFolderAsync(string url, string name)
    {
        url = url.Trim();
        name = name.Trim();
        if (name.Length == 0) name = "Carpeta en línea";
        if (OnlineLibrary.Sources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
            return "Esa carpeta en línea ya está en tu biblioteca";
        if (_adding != null) return "Ya se está añadiendo otra carpeta en línea: espera a que termine";

        // fila provisional con el icono de carga mientras se buscan sus canciones
        _adding = new OnlineFolderItem("", name, url) { Status = "Buscando canciones…", IsBusy = true };
        OnlineFolders.Add(_adding);
        var adding = _adding;
        OnlineIndex index;
        try
        {
            index = await Task.Run(() => OnlineLibrary.ListAsync(url, (found, folder) => _ui.BeginInvoke(() =>
                adding.Status = $"Buscando canciones… {found:N0}" + (folder.Length > 0 ? $" · {folder}" : ""))));
        }
        catch (Exception ex)
        {
            OnlineFolders.Remove(adding);
            _adding = null;
            return ex is InvalidOperationException ? ex.Message : "No se pudo leer la dirección: " + ex.Message;
        }

        int found = index.Files.Count(f => LibraryService.AudioExtensions.Contains(Path.GetExtension(f.Path)));
        adding.Status = $"{Songs(found)} encontradas · añadiéndolas a la biblioteca…";
        var source = new OnlineSource { Id = Guid.NewGuid().ToString("N")[..12], Name = name, Url = url };
        OnlineLibrary.Add(source);
        OnlineLibrary.SetIndex(source, index);
        SaveOnlineSources();
        await ApplyMusicRootsAsync(Library.Roots.Append(OnlineLibrary.RootPath(source)).ToList());
        OnlineFolders.Remove(adding);
        _adding = null;
        RefreshOnlineFolders();
        ShowToast($"«{name}» añadida: {Songs(Library.GetCount(OnlineLibrary.RootPath(source)))} que se escuchan desde la nube", 5);
        return null;
    }

    /// <summary>Quita una carpeta en línea de la biblioteca (en la nube no se toca nada).</summary>
    public async Task RemoveOnlineFolderAsync(string id)
    {
        if (OnlineLibrary.Sources.FirstOrDefault(s => s.Id == id) is not { } source) return;
        var root = OnlineLibrary.RootPath(source);
        OnlineLibrary.Forget(source);
        _onlineStatus.Remove(id);
        SaveOnlineSources();
        await ApplyMusicRootsAsync(Library.Roots.Where(r => !string.Equals(r, root, StringComparison.OrdinalIgnoreCase)).ToList());
        ShowToast($"Carpeta en línea quitada: {source.Name}", 4);
    }

    /// <summary>
    /// Vuelve a mirar qué hay en cada carpeta en línea (al abrir Echoplex y con «Comprobar cambios»). Si cambió algo,
    /// la biblioteca se pone al día; si no hay conexión, se queda con lo que ya sabía.
    /// </summary>
    /// <param name="full">Recorrerlas enteras aunque OneDrive diga que no ha cambiado nada («Comprobar cambios» en Ajustes).</param>
    public async Task RefreshOnlineFoldersAsync(bool announce, bool full = false)
    {
        if (_refreshingOnline) return;
        _refreshingOnline = true;
        bool changed = false;
        try
        {
            foreach (var source in OnlineLibrary.Sources.Where(s => s.Enabled)) // a las desconectadas no se les pide nada
            {
                var id = source.Id;
                SetOnlineStatus(id, "Comprobando si hay cambios…", busy: true);
                try
                {
                    // OneDrive dice en una petición si algo cambió: si no, no se recorre nada
                    if (!full && await Task.Run(() => OnlineLibrary.UnchangedAsync(source)))
                    {
                        SetOnlineStatus(id, null);
                        continue;
                    }
                    var index = await Task.Run(() => OnlineLibrary.ListAsync(source.Url, (found, folder) => _ui.BeginInvoke(() =>
                        SetOnlineStatus(id, $"Comprobando si hay cambios… {found:N0} canciones", busy: true))));
                    changed |= OnlineLibrary.SetIndex(source, index);
                    SetOnlineStatus(id, null);
                }
                catch (Exception ex)
                {
                    SetOnlineStatus(id, "No se pudo comprobar: " + ex.Message);
                }
            }
        }
        finally
        {
            _refreshingOnline = false;
        }
        if (changed) await RescanAsync(announce);
        else if (announce && OnlineLibrary.Sources.Count > 0) ShowToast("Las carpetas en línea no han cambiado", 3);
        RefreshOnlineFolders();
    }
}
