using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Echoplex.Services;
using Echoplex.ViewModels;

namespace Echoplex;

/// <summary>Un tema en la ventana de Ajustes.</summary>
public sealed partial class ThemeOption : ObservableObject
{
    public ThemeOption(ThemeInfo info) => Info = info;
    public ThemeInfo Info { get; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Un grupo de "Más temas" (editores de código, naturaleza…).</summary>
public sealed record ThemeGroup(string Name, List<ThemeOption> Themes);

/// <summary>Ajustes: tema, animaciones, carpetas de música y poco más (lo rápido está en el menú de arriba).</summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private readonly List<ThemeOption> _themes;

    public SettingsWindow(MainViewModel vm, MainWindow owner)
    {
        InitializeComponent();
        _vm = vm;
        _main = owner;
        Owner = owner;
        DataContext = vm;
        _themes = Themes.All.Select(t => new ThemeOption(t) { IsSelected = t.Key == vm.ThemeKey }).ToList();
        ThemeList.ItemsSource = _themes.Where(o => o.Info.File != null).ToList();
        MoreThemes.ItemsSource = _themes.Where(o => o.Info.File == null).GroupBy(o => o.Info.Group)
            .Select(g => new ThemeGroup(g.Key, g.ToList())).ToList();
        MoreThemesToggle.Tag = _themes.Count(o => o.Info.File == null).ToString();
        // si el tema actual es de "Más temas", la lista sale ya desplegada
        MoreThemesToggle.IsChecked = Themes.Get(vm.ThemeKey).File == null;
        // Ver cambios de la versión: solo si las notas vienen en el ejecutable (siempre en las releases)
        var v = UpdateService.CurrentVersion;
        ReleaseNotesToggle.Content = $"Ver cambios de la versión {v.Major}.{v.Minor}.{v.Build}";
        if (ReleaseNotesView.Current() is { } notes) ReleaseNotesBox.Child = ReleaseNotesView.Render(notes);
        else ReleaseNotesToggle.Visibility = Visibility.Collapsed;
        AnimationsBox.IsChecked = vm.AnimationsOn;
        FolderList.ItemsSource = vm.MusicFolders;
        BuildColumnSwitches();
        vm.MusicFolders.CollectionChanged += OnFoldersChanged;
        OnFoldersChanged(null, null);
        Closed += (s, e) => vm.MusicFolders.CollectionChanged -= OnFoldersChanged;
    }

    /// <summary>Icono de cada columna, el mismo que en la cabecera de la tabla (null = texto propio).</summary>
    private static readonly Dictionary<string, (string glyph, bool iconFont)> ColumnIcons = new()
    {
        [SongColumns.NumberKey] = ("#", false),
        ["title"] = ("Aa", false),
        ["artist"] = ("", true),
        ["album"] = ("", true),
        ["format"] = ("", true),
        ["fav"] = ("", true),
        ["duration"] = ("", true),
    };

    /// <summary>Un chip por columna, en el orden de la tabla (el número siempre primero).</summary>
    private void BuildColumnSwitches()
    {
        ColumnSwitches.Children.Clear();
        var names = SongColumns.Toggleable.ToDictionary(t => t.Key, t => t.Name);
        names[SongColumns.NumberKey] = "Número";
        var order = new[] { SongColumns.NumberKey }.Concat(SongColumns.Order).Where(names.ContainsKey);
        foreach (var key in order)
        {
            var (glyph, iconFont) = ColumnIcons[key];
            var icon = new System.Windows.Controls.TextBlock
            {
                Text = glyph,
                FontSize = iconFont ? 12 : 13,
                FontWeight = iconFont ? FontWeights.Normal : FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0),
                Opacity = 0.8,
            };
            if (iconFont) icon.FontFamily = (System.Windows.Media.FontFamily)FindResource("IconFont");
            var content = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            content.Children.Add(icon);
            content.Children.Add(new System.Windows.Controls.TextBlock { Text = names[key], FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var chip = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = (Style)FindResource("ColumnChip"),
                Content = content,
                IsChecked = SongColumns.IsVisible(key),
                ToolTip = "Mostrar u ocultar la columna",
            };
            chip.Click += (s, e) =>
            {
                // no se puede ocultar la última columna visible
                if (!SongColumns.SetVisible(key, chip.IsChecked == true)) chip.IsChecked = true;
            };
            ColumnSwitches.Children.Add(chip);
        }
    }

    private void ResetColumns_Click(object sender, RoutedEventArgs e)
    {
        SongColumns.Reset();
        BuildColumnSwitches();
    }

    private void OnFoldersChanged(object? sender, EventArgs? e) =>
        NoFolders.Visibility = _vm.MusicFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThemeOption option) return;
        _vm.SetTheme(option.Info.Key);
        foreach (var t in _themes) t.IsSelected = t == option;
    }

    private void Animations_Click(object sender, RoutedEventArgs e)
    {
        if (AnimationsBox.IsChecked != _vm.AnimationsOn) _vm.ToggleAnimations();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e) => _main.AddMusicFolder();

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await _vm.RescanAsync(announce: true);

    private async void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MusicFolderItem item) await _vm.RemoveMusicRootAsync(item.Path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MusicFolderItem { Exists: true } item) Explore(item.Path);
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e) => Explore(SettingsStore.DataDirectory);

    private static void Explore(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // sin Explorador
        }
    }

    private void Mini_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _main.OpenMiniPlayer();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await _vm.CheckForUpdatesAsync(manual: true);

    private void RestartUpdate_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _vm.RestartToUpdate();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
