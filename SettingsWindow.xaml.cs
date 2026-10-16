using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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
        (vm.CloseToTray ? CloseTrayChip : CloseExitChip).IsChecked = true;
        FolderList.ItemsSource = vm.MusicFolders;
        OnlineList.ItemsSource = vm.OnlineFolders;
        BuildColumnSwitches();
        BuildEqPresets();
        vm.PropertyChanged += OnVmChanged;
        // la curva del ecualizador sigue a las bandas al instante, se muevan como se muevan
        foreach (var band in vm.EqBands) band.PropertyChanged += OnEqBandChanged;
        EqBandsList.SizeChanged += (s, e) => DrawEqCurve();
        EqBandsList.Loaded += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, DrawEqCurve);
        Closed += (s, e) =>
        {
            vm.PropertyChanged -= OnVmChanged;
            foreach (var band in vm.EqBands) band.PropertyChanged -= OnEqBandChanged;
        };
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

    // ======================================================================
    // Ecualizador
    // ======================================================================

    private readonly Dictionary<string, System.Windows.Controls.RadioButton> _eqChips = new();

    /// <summary>Un chip por preajuste y uno de «Personalizado» que se marca solo al mover una banda a mano.</summary>
    private void BuildEqPresets()
    {
        EqPresetChips.Children.Clear();
        _eqChips.Clear();
        var chips = MainViewModel.EqPresets.Select(p => (p.Key, p.Name)).Append((MainViewModel.CustomPreset, "Personalizado"));
        foreach (var (key, name) in chips)
        {
            var chip = new System.Windows.Controls.RadioButton
            {
                Style = (Style)FindResource("ColumnChip"),
                GroupName = "EqPreset",
                FontSize = 13,
                Content = name,
                Tag = key,
            };
            if (key == MainViewModel.CustomPreset)
            {
                chip.IsHitTestVisible = false; // no se elige: aparece al mover una banda
                chip.ToolTip = "Tus propios ajustes";
            }
            else chip.Click += (s, e) => _vm.ApplyEqPreset(key);
            _eqChips[key] = chip;
            EqPresetChips.Children.Add(chip);
        }
        MarkEqPreset();
    }

    private void MarkEqPreset()
    {
        foreach (var (key, chip) in _eqChips)
        {
            chip.IsChecked = key == _vm.EqPresetKey;
            if (key == MainViewModel.CustomPreset) chip.Visibility = key == _vm.EqPresetKey ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.EqPresetKey)) MarkEqPreset();
    }

    /// <summary>Doble clic en una banda: a 0 dB.</summary>
    private void EqSlider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EqBand band) band.Value = 0;
        e.Handled = true;
    }

    /// <summary>Se hizo clic dentro del ecualizador: desde ahora la rueda mueve bandas (hasta que el ratón sale del recuadro).</summary>
    private bool _eqWheelArmed;

    private void EqBox_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _eqWheelArmed = true; // el clic sigue su camino

    private void EqBox_MouseLeave(object sender, MouseEventArgs e) => _eqWheelArmed = false;

    /// <summary>
    /// Rueda sobre una banda: medio dB arriba o abajo, solo con el ecualizador activado y tras un clic dentro. Si no,
    /// la rueda no lo toca y desplaza Ajustes como en cualquier otro sitio.
    /// </summary>
    private void EqSlider_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!_eqWheelArmed || !_vm.EqEnabled) return;
        if ((sender as FrameworkElement)?.DataContext is EqBand band)
            band.Value = Math.Clamp(band.Value + (e.Delta > 0 ? 0.5 : -0.5), -Equalizer.MaxGain, Equalizer.MaxGain);
        e.Handled = true;
    }

    private void EqReset_Click(object sender, RoutedEventArgs e) => _vm.ResetEq();

    // ---------- el valor escrito a mano ----------

    /// <summary>Primer clic en la caja: se selecciona todo para escribir encima.</summary>
    private void EqValue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box || box.IsKeyboardFocusWithin) return;
        box.Focus();
        e.Handled = true;
    }

    private void EqValue_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => (sender as System.Windows.Controls.TextBox)?.SelectAll();

    private void EqValue_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitEqValue(sender as System.Windows.Controls.TextBox);

    private void EqValue_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox box) return;
        if (e.Key == Key.Enter)
        {
            CommitEqValue(box);
            box.SelectAll(); // se puede seguir probando valores sin volver a hacer clic
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // deja el valor como estaba (y no cierra Ajustes)
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down && box.DataContext is EqBand band)
        {
            // flechas: medio dB arriba o abajo, como la rueda
            band.Value = Math.Clamp(Math.Round(band.Value + (e.Key == Key.Up ? 0.5 : -0.5), 1), -Equalizer.MaxGain, Equalizer.MaxGain);
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
            box.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Aplica lo escrito ("3", "+4,5", "-2.5", "−1 dB"…), recortado a ±12 dB y a décimas; si no es un número, vuelve el de antes.</summary>
    private static void CommitEqValue(System.Windows.Controls.TextBox? box)
    {
        if (box?.DataContext is not EqBand band) return;
        if (TryParseDb(box.Text, out var db))
            band.Value = Math.Clamp(Math.Round(db, 1), -Equalizer.MaxGain, Equalizer.MaxGain);
        box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
    }

    public static bool TryParseDb(string text, out double value)
    {
        var t = text.Trim().Replace('−', '-').Replace(',', '.');
        if (t.EndsWith("db", StringComparison.OrdinalIgnoreCase)) t = t[..^2].Trim();
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)
               && double.IsFinite(value);
    }

    // ---------- la curva ----------

    private void OnEqBandChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EqBand.Value)) DrawEqCurve();
    }

    /// <summary>
    /// Une los tiradores de las 15 bandas con una curva suave (la «onda» del ecualizador) y sombrea lo que queda entre
    /// ella y la línea de 0 dB. La posición sale del valor de cada banda, no de dónde esté pintado el tirador: así va a la
    /// vez que el deslizador, sin esperar a que se recoloque.
    /// </summary>
    private void DrawEqCurve()
    {
        if (!EqBandsList.IsLoaded) return;
        var points = new List<Point>();
        double zeroY = double.NaN;
        for (int i = 0; i < _vm.EqBands.Count; i++)
        {
            if (EqBandsList.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container
                || FindChild<System.Windows.Controls.Slider>(container) is not { ActualHeight: > 14 } slider)
                return;
            const double thumb = 14; // alto del tirador (EqSlider)
            double range = slider.Maximum - slider.Minimum;
            double FracY(double v) => thumb / 2 + (1 - (v - slider.Minimum) / range) * (slider.ActualHeight - thumb);
            points.Add(slider.TranslatePoint(new Point(slider.ActualWidth / 2, FracY(_vm.EqBands[i].Value)), EqCurveCanvas));
            if (double.IsNaN(zeroY)) zeroY = slider.TranslatePoint(new Point(0, FracY(0)), EqCurveCanvas).Y;
        }
        if (points.Count < 2) return;

        EqZeroLine.X1 = 0;
        EqZeroLine.X2 = EqCurveCanvas.ActualWidth;
        EqZeroLine.Y1 = EqZeroLine.Y2 = Math.Round(zeroY) + 0.5;

        var curve = new PathFigure { StartPoint = points[0] };
        AddSmoothSegments(curve, points);
        var line = new PathGeometry(new[] { curve });
        line.Freeze();
        EqCurve.Data = line;

        var fill = new PathFigure { StartPoint = new Point(points[0].X, zeroY), IsClosed = true };
        fill.Segments.Add(new LineSegment(points[0], false));
        AddSmoothSegments(fill, points);
        fill.Segments.Add(new LineSegment(new Point(points[^1].X, zeroY), false));
        var area = new PathGeometry(new[] { fill });
        area.Freeze();
        EqCurveFill.Data = area;
    }

    /// <summary>Curva Catmull-Rom (pasa por todos los puntos) convertida a tramos de Bézier.</summary>
    private static void AddSmoothSegments(PathFigure figure, IReadOnlyList<Point> p)
    {
        for (int i = 0; i < p.Count - 1; i++)
        {
            var p0 = p[Math.Max(0, i - 1)];
            var p1 = p[i];
            var p2 = p[i + 1];
            var p3 = p[Math.Min(p.Count - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            figure.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
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

    /// <summary>Qué hace la X: cerrar Echoplex o esconderlo en la bandeja.</summary>
    private void CloseAction_Checked(object sender, RoutedEventArgs e) => _vm.CloseToTray = sender == CloseTrayChip;

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

    // ---------- carpetas en línea ----------

    /// <summary>Pide la URL y el nombre (el de la carpeta en el panel lateral) y la añade.</summary>
    private async void AddOnline_Click(object sender, RoutedEventArgs e)
    {
        var answer = DialogWindow.PromptTwo(this, "Añadir carpeta en línea",
            "Una carpeta compartida de Google Drive u OneDrive («Cualquier persona con el enlace»), el listado de carpetas de un servidor web o un archivo de audio. Se escucha desde la nube, sin copiar nada a tu PC.",
            "Dirección (URL)", "Nombre en el panel lateral", $"Música en línea {_vm.OnlineFolders.Count + 1}", "Añadir");
        if (answer is not { } a) return;
        var error = await _vm.AddOnlineFolderAsync(a.First, a.Second);
        if (error != null) DialogWindow.Info(this, "No se pudo añadir la carpeta en línea", error);
    }

    private async void RemoveOnline_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OnlineFolderItem item) await _vm.RemoveOnlineFolderAsync(item.Id);
    }

    private void OpenOnline_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OnlineFolderItem item) OpenUrl(item.Url);
    }

    private async void RefreshOnline_Click(object sender, RoutedEventArgs e) => await _vm.RefreshOnlineFoldersAsync(announce: true);

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // sin navegador
        }
    }

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
