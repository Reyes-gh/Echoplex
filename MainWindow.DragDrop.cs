using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Echoplex.Models;
using Echoplex.ViewModels;

namespace Echoplex;

/// <summary>
/// Arrastrar y soltar: canciones (las seleccionadas) y carpetas del panel lateral a la cola de «A continuación» (en el
/// punto donde se suelten) o a una playlist del panel lateral; y la cola se reordena arrastrando sus canciones.
/// </summary>
public partial class MainWindow
{
    private const string SongsFormat = "Echoplex.Songs";       // rutas de canciones (string[])
    private const string QueueFormat = "Echoplex.QueueSong";   // una canción de la cola que se está moviendo

    private Point _dragStart;
    private FrameworkElement? _dragSource;
    private bool _deferSelect;

    private bool MovedEnough(MouseEventArgs e)
    {
        var d = e.GetPosition(this) - _dragStart;
        return Math.Abs(d.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(d.Y) >= SystemParameters.MinimumVerticalDragDistance;
    }

    // ---------- filas de canciones ----------

    private void Row_DragDown(object sender, MouseButtonEventArgs e)
    {
        _dragSource = sender as ListViewItem;
        _dragStart = e.GetPosition(this);
        _deferSelect = false;
        // con varias seleccionadas, pulsar una de ellas no deshace la selección hasta soltar (si no, no se podrían arrastrar todas)
        if (e.ClickCount == 1 && sender is ListViewItem { IsSelected: true } item && Keyboard.Modifiers == ModifierKeys.None
            && ItemsControl.ItemsControlFromItemContainer(item) is ListView { SelectedItems.Count: > 1 }
            && !(e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null))
        {
            _deferSelect = true;
            e.Handled = true;
        }
    }

    private void Row_DragUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && sender is ListViewItem item && ItemsControl.ItemsControlFromItemContainer(item) is ListView list)
        {
            list.SelectedItems.Clear();
            item.IsSelected = true;
            item.Focus();
        }
        _deferSelect = false;
        _dragSource = null;
    }

    private void Row_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource != sender || !MovedEnough(e)) return;
        if (sender is not ListViewItem { DataContext: SongRow row } item) return;
        var songs = item.IsSelected && ItemsControl.ItemsControlFromItemContainer(item) is ListView list
            ? list.SelectedItems.OfType<SongRow>().Select(r => r.Song).ToList()
            : new List<Song> { row.Song };
        _dragSource = null;
        _deferSelect = false;
        StartSongsDrag(item, songs);
    }

    // ---------- carpetas del panel lateral ----------

    private void Node_DragDown(object sender, MouseButtonEventArgs e)
    {
        // el evento pasa por todas las carpetas de encima: vale la que está justo debajo del ratón
        if (e.OriginalSource is DependencyObject d && FindAncestor<TreeViewItem>(d) == sender)
        {
            _dragSource = (FrameworkElement)sender;
            _dragStart = e.GetPosition(this);
        }
    }

    private void Node_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource != sender || !MovedEnough(e)) return;
        if (sender is not TreeViewItem { DataContext: FolderNode node } item) return;
        _dragSource = null;
        if (_vm.SongsForContext("folder:" + node.Path) is { Count: > 0 } songs) StartSongsDrag(item, songs);
        e.Handled = true;
    }

    private void StartSongsDrag(DependencyObject source, IReadOnlyList<Song> songs)
    {
        if (songs.Count == 0) return;
        var data = new DataObject(SongsFormat, songs.Select(s => s.Path).ToArray());
        DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
    }

    private List<Song> DraggedSongs(IDataObject data) =>
        data.GetData(SongsFormat) is string[] paths ? _vm.Library.Resolve(paths) : new List<Song>();

    // ---------- la cola ----------

    private void QueueItem_DragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) != null) return; // la ✕ de quitar
        _dragSource = sender as FrameworkElement;
        _dragStart = e.GetPosition(this);
    }

    private void QueueItem_DragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragSource != sender || !MovedEnough(e)) return;
        if (sender is not FrameworkElement { DataContext: Song song } el) return;
        _dragSource = null;
        DragDrop.DoDragDrop(el, new DataObject(QueueFormat, song.Path), DragDropEffects.Move);
    }

    /// <summary>Encima de una canción de la cola: una raya marca dónde quedará lo que se suelte (justo antes de ella).</summary>
    private void QueueItem_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(SongsFormat) && !e.Data.GetDataPresent(QueueFormat)) return;
        e.Effects = e.Data.GetDataPresent(QueueFormat) ? DragDropEffects.Move : DragDropEffects.Copy;
        if (sender is Border b)
        {
            b.BorderBrush = (Brush)FindResource("AccentBrush");
            b.BorderThickness = new Thickness(0, 2, 0, 0);
        }
        e.Handled = true;
    }

    private void QueueItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
    }

    private void QueueItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
        if (sender is not FrameworkElement { DataContext: Song target }) return;
        int at = _vm.Player.QueueNext.IndexOf(target);
        if (at < 0) return;
        if (e.Data.GetData(QueueFormat) is string path && _vm.Library.Find(path) is { } moving)
            _vm.Player.MoveInQueue(moving, at);
        else
            _vm.Player.InsertInQueue(DraggedSongs(e.Data), at);
        e.Handled = true;
    }

    /// <summary>En cualquier otro sitio del panel «A continuación»: al final de la cola.</summary>
    private void QueuePanel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SongsFormat) ? DragDropEffects.Copy
            : e.Data.GetDataPresent(QueueFormat) ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueuePanel_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(QueueFormat) is string path && _vm.Library.Find(path) is { } moving)
            _vm.Player.MoveInQueue(moving, int.MaxValue);
        else if (DraggedSongs(e.Data) is { Count: > 0 } songs)
        {
            _vm.Player.Enqueue(songs);
            _vm.ShowToast(songs.Count == 1 ? "Añadida a la cola" : $"{songs.Count} canciones añadidas a la cola", 3);
        }
        e.Handled = true;
    }

    // ---------- playlists del panel lateral ----------

    private void Playlist_DragOver(object sender, DragEventArgs e)
    {
        bool songs = e.Data.GetDataPresent(SongsFormat);
        e.Effects = songs ? DragDropEffects.Copy : DragDropEffects.None;
        if (songs && sender is FrameworkElement { DataContext: Playlist p }) p.IsDropTarget = true;
        e.Handled = true;
    }

    private void Playlist_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Playlist p }) p.IsDropTarget = false;
    }

    private void Playlist_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Playlist p }) return;
        p.IsDropTarget = false;
        if (DraggedSongs(e.Data) is { Count: > 0 } songs) _vm.AddToPlaylist(p, songs);
        e.Handled = true;
    }
}
