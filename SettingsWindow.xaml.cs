using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

/// <summary>
/// Un grupo plegable de Más temas: Claros u Oscuros (con sus familias en <see cref="Groups"/>) o una familia
/// (editores de código, naturaleza…) con sus temas en <see cref="Themes"/>.
/// </summary>
public sealed partial class ThemeGroup : ObservableObject
{
    public ThemeGroup(string name, string? glyph, IReadOnlyList<ThemeGroup> groups, IReadOnlyList<ThemeOption> themes)
    {
        Name = name;
        Glyph = glyph;
        Groups = groups;
        Themes = themes;
        UpdateSummary();
    }

    public string Name { get; }
    /// <summary>Icono (sol o luna) de Claros y Oscuros; las familias no llevan.</summary>
    public string? Glyph { get; }
    public IReadOnlyList<ThemeGroup> Groups { get; }
    public IReadOnlyList<ThemeOption> Themes { get; }
    [ObservableProperty] private bool _isOpen;
    /// <summary>Al lado del nombre: el número de temas y, si dentro está el que se usa, su nombre (se ve aun plegado).</summary>
    [ObservableProperty] private string _summary = "";

    public void UpdateSummary()
    {
        var all = Themes.Concat(Groups.SelectMany(g => g.Themes)).ToList();
        var selected = all.FirstOrDefault(o => o.IsSelected);
        Summary = selected != null ? $"{all.Count} · {selected.Info.Name}" : all.Count.ToString();
        foreach (var g in Groups) g.UpdateSummary();
    }
}

/// <summary>Ajustes: tema, animaciones, carpetas de música y poco más (lo rápido está en el menú de arriba).</summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly MainWindow _main;
    private readonly List<ThemeOption> _themes;
    private readonly ThemeGroup[] _categories;

    public SettingsWindow(MainViewModel vm, MainWindow owner)
    {
        InitializeComponent();
        _vm = vm;
        _main = owner;
        Owner = owner;
        DataContext = vm;
        _themes = Themes.All.Select(t => new ThemeOption(t) { IsSelected = t.Key == vm.ThemeKey }).ToList();
        // arriba, los de Echoplex; el resto en Más temas, por Claros y Oscuros y por familias
        ThemeList.ItemsSource = _themes.Where(o => o.Info.File != null).ToList();
        MoreThemesToggle.Tag = _themes.Count(o => o.Info.File == null).ToString();
        _categories = new[] { Category("Claros", "", dark: false), Category("Oscuros", "", dark: true) };
        ThemeCategories.ItemsSource = _categories;
        // Más temas y Ver cambios de la versión salen plegados al abrir Ajustes; dentro de Más temas, todo desplegado
        // Ver cambios de la versión: solo si las notas vienen en el ejecutable (siempre en las releases)
        var v = UpdateService.CurrentVersion;
        ReleaseNotesToggle.Content = $"Ver cambios de la versión {v.Major}.{v.Minor}.{v.Build}";
        if (ReleaseNotesView.Current() is { } notes) ReleaseNotesBox.Child = ReleaseNotesView.Render(notes);
        else ReleaseNotesToggle.Visibility = Visibility.Collapsed;
        AnimationsBox.IsChecked = vm.AnimationsOn;
        (vm.DiscordShow switch
        {
            DiscordPresence.ShowTitle => DiscordShowTitle,
            DiscordPresence.ShowApp => DiscordShowApp,
            _ => DiscordShowArtist,
        }).IsChecked = true;
        FolderList.ItemsSource = vm.MusicFolders;
        BuildColumnSwitches();
        vm.MusicFolders.CollectionChanged += OnFoldersChanged;
        OnFoldersChanged(null, null);
        Closed += (s, e) => vm.MusicFolders.CollectionChanged -= OnFoldersChanged;
        // la entrada empieza cuando la ventana ya está pintada: montarla lleva su tiempo y la animación se perdería
        if (Animated) Card.Opacity = 0;
        ContentRendered += (s, e) =>
        {
            if (_closing) return; // cerrada antes de llegar a pintarse: ni oscurecer el fondo ni animar la entrada
            Opened?.Invoke();
            AnimateIn();
        };
    }

    // ======================================================================
    // Entrada y salida animadas
    // ======================================================================

    private bool _closing;
    private bool _allowClose;

    /// <summary>Ya se ve y empieza a entrar: la ventana principal oscurece el fondo a la vez.</summary>
    public event Action? Opened;

    /// <summary>Empieza a cerrarse (con o sin animación): la ventana principal quita el oscurecido a la vez.</summary>
    public event Action? ClosingStarted;

    private bool Animated => _vm.AnimationsOn && SystemParameters.ClientAreaAnimation;

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction Settle = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };

    private void AnimateIn()
    {
        if (!Animated)
        {
            Card.Opacity = 1;
            return;
        }
        var t = TimeSpan.FromMilliseconds(320);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = EaseOut });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, t) { EasingFunction = Settle });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, t) { EasingFunction = Settle });
        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, t) { EasingFunction = EaseOut });
    }

    /// <summary>Cierra con un fundido suave (todas las vías acaban aquí: ✕, Esc, Ctrl+, o clic fuera).</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CommitUploadCommand(); // cerrar con lo escrito sin salir de la caja también lo guarda
        if (!_allowClose)
        {
            e.Cancel = true;
            BeginClose();
            return;
        }
        base.OnClosing(e);
    }

    private void BeginClose()
    {
        if (_closing) return;
        _closing = true;
        ClosingStarted?.Invoke();
        if (!Animated)
        {
            CloseNow();
            return;
        }
        IsHitTestVisible = false;
        var t = TimeSpan.FromMilliseconds(180);
        var fade = new DoubleAnimation(0, t) { EasingFunction = EaseIn };
        fade.Completed += (s, e) => CloseNow();
        Card.BeginAnimation(OpacityProperty, fade);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, t) { EasingFunction = EaseIn });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, t) { EasingFunction = EaseIn });
        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, t) { EasingFunction = EaseIn });
    }

    /// <summary>Cierra ya, sin animación (al cerrar Echoplex con Ajustes abierto).</summary>
    public void CloseNow()
    {
        if (!_closing)
        {
            _closing = true;
            ClosingStarted?.Invoke();
        }
        _allowClose = true;
        Close();
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

    /// <summary>Claros u Oscuros de Más temas (sin los de Echoplex, que van arriba) con sus familias, desplegados.</summary>
    private ThemeGroup Category(string name, string glyph, bool dark)
    {
        var families = _themes.Where(o => o.Info.File == null && o.Info.IsDark == dark)
            .GroupBy(o => o.Info.Group)
            .OrderBy(g => FamilyOrder(g.Key))
            .Select(g => new ThemeGroup(g.Key, null, Array.Empty<ThemeGroup>(), g.ToList()) { IsOpen = true })
            .ToList();
        return new ThemeGroup(name, glyph, families, Array.Empty<ThemeOption>()) { IsOpen = true };
    }

    private static int FamilyOrder(string family)
    {
        for (int i = 0; i < Themes.Families.Count; i++)
            if (Themes.Families[i] == family) return i;
        return int.MaxValue;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThemeOption option) return;
        _vm.SetTheme(option.Info.Key);
        foreach (var t in _themes) t.IsSelected = t == option;
        foreach (var c in _categories) c.UpdateSummary();
    }

    private void Animations_Click(object sender, RoutedEventArgs e)
    {
        if (AnimationsBox.IsChecked != _vm.AnimationsOn) _vm.ToggleAnimations();
    }

    private void DiscordShow_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string show }) _vm.DiscordShow = show;
    }

    private void DiscordUploadBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitUploadCommand();
        e.Handled = true;
    }

    private void CommitUploadCommand() =>
        DiscordUploadBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();

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

    /// <summary>La página de Echoplex en GitHub, en el navegador.</summary>
    private void GitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{UpdateService.Repository}") { UseShellExecute = true });
        }
        catch
        {
            // sin navegador
        }
    }

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

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e) => await _vm.InstallUpdateAsync();

    private void SkipUpdate_Click(object sender, RoutedEventArgs e) => _vm.SkipUpdate();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Esc o el mismo atajo que la abre (Ctrl+,)
        if (e.Key == Key.Escape || (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemComma))
        {
            Close();
            e.Handled = true;
        }
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
