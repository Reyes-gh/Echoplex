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

/// <summary>Una sección de Ajustes (o un apartado de una sección) en la barra lateral.</summary>
public sealed partial class SettingsNavItem : ObservableObject
{
    public SettingsNavItem(string title, object? icon, FrameworkElement anchor, SettingsNavItem? parent = null)
    {
        Title = title;
        Icon = icon;
        Anchor = anchor;
        Parent = parent;
    }

    public string Title { get; }
    /// <summary>Glifo de la fuente de iconos o un elemento (el micro de las letras, el logo de Discord); los apartados no llevan.</summary>
    public object? Icon { get; }
    /// <summary>Hasta dónde se desplaza Ajustes al elegirlo: el título de la sección o del apartado.</summary>
    public FrameworkElement Anchor { get; }
    public SettingsNavItem? Parent { get; }
    public List<SettingsNavItem> Children { get; } = new();
    /// <summary>Es donde estás: la sección resaltada y, dentro, su apartado.</summary>
    [ObservableProperty] private bool _isActive;
}

/// <summary>
/// Ajustes: a la izquierda las secciones y sus apartados (marcan dónde estás y llevan a cada sitio); a la derecha todo
/// seguido, en tarjetas, para poder recorrerlo también con la rueda.
/// </summary>
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
        else SubReleaseNotes.Visibility = ReleaseNotesGroup.Visibility = Visibility.Collapsed;
        FitToScreen();
        BuildNav();
        AnimationsBox.IsChecked = vm.AnimationsOn;
        (vm.DiscordShow switch
        {
            DiscordPresence.ShowTitle => DiscordShowTitle,
            DiscordPresence.ShowApp => DiscordShowApp,
            _ => DiscordShowArtist,
        }).IsChecked = true;
        (vm.CloseToTray ? CloseTrayChip : CloseExitChip).IsChecked = true;
        (vm.NormalizationMode switch
        {
            Normalization.Track => NormTrack,
            Normalization.Album => NormAlbum,
            Normalization.Smart => NormSmart,
            _ => NormOff,
        }).IsChecked = true;
        UpdateMeasureUi();
        FadeCustomBox.Text = FormatSeconds(vm.CrossfadeSeconds is > 0 and not (3 or 6 or 9) ? vm.CrossfadeSeconds : 5);
        (vm.CrossfadeSeconds switch { 0 => FadeOff, 3 => Fade3, 6 => Fade6, 9 => Fade9, _ => FadeCustom }).IsChecked = true;
        (vm.GlobalHotkeyShift ? HotkeyCtrlAltShift : HotkeyCtrlAlt).IsChecked = true;
        FolderList.ItemsSource = vm.MusicFolders;
        OnlineList.ItemsSource = vm.OnlineFolders;
        BuildColumnSwitches();
        BuildEqPresets();
        vm.PropertyChanged += OnVmChanged;
        // la curva del ecualizador sigue a las bandas al instante, se muevan como se muevan
        foreach (var band in vm.EqBands) band.PropertyChanged += OnEqBandChanged;
        vm.EqPreamp.PropertyChanged += OnEqBandChanged;
        EqBandsList.SizeChanged += (s, e) => DrawEqCurve();
        EqBandsList.Loaded += (s, e) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, DrawEqCurve);
        Closed += (s, e) =>
        {
            vm.PropertyChanged -= OnVmChanged;
            foreach (var band in vm.EqBands) band.PropertyChanged -= OnEqBandChanged;
            vm.EqPreamp.PropertyChanged -= OnEqBandChanged;
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
    // Barra lateral: secciones, apartados y dónde estás
    // ======================================================================

    private readonly List<SettingsNavItem> _nav = new();
    // todas las secciones y apartados en el orden en que salen (para saber cuál se está viendo)
    private readonly List<SettingsNavItem> _navFlat = new();

    /// <summary>Que quepa en la pantalla (en portátiles pequeños, más baja y estrecha).</summary>
    private void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(760, area.Width - 40));
        Height = Math.Min(Height, Math.Max(520, area.Height - 30));
    }

    private void BuildNav()
    {
        SettingsNavItem Section(string title, object icon, FrameworkElement anchor, params (string title, FrameworkElement anchor)[] subs)
        {
            var section = new SettingsNavItem(title, icon, anchor);
            foreach (var (t, a) in subs)
                if (a.Visibility == Visibility.Visible) section.Children.Add(new SettingsNavItem(t, null, a, section));
            _nav.Add(section);
            _navFlat.Add(section);
            _navFlat.AddRange(section.Children);
            return section;
        }

        Section("Apariencia", "", SecAppearance, ("Temas de Echoplex", SubThemes), ("Más temas", SubMoreThemes), ("Barra de progreso", SubSeekBar), ("Animaciones", SubAnimations));
        Section("Biblioteca", "", SecLibrary, ("Carpetas de música", SubFolders), ("Carpetas en línea", SubOnline),
            ("Títulos de las canciones", SubTitles), ("Columnas de las listas", SubColumns), ("Salud de la biblioteca", SubHealth));
        Section("Sonido", "", SecSound, ("Ecualizador", SubEq), ("Fundido entre canciones", SubCrossfade), ("Normalización de volumen", SubNormalization));
        Section("Letras", NavIcon("SingMicIcon", 16, 1.3), SecLyrics, ("Letras en internet", SubOnlineLyrics));
        Section("Discord", NavIcon("DiscordIcon", 17, 1.2), SecDiscord, ("Estado en Discord", SubDiscordStatus),
            ("En la lista de miembros", SubDiscordList), ("Carátulas", SubDiscordCovers));
        Section("Scrobbling", "\uE81C", SecScrobble, ("Last.fm", SubLastFm), ("ListenBrainz", SubListenBrainz));
        Section("General", "", SecGeneral, ("Al pulsar la X", SubCloseAction), ("Atajos globales", SubGlobalKeys), ("Accesos", SubShortcuts));
        Section("Actualizaciones", "", SecUpdates, ("Versión", SubVersion), ("Novedades", SubReleaseNotes));
        NavList.ItemsSource = _nav;

        ContentScroll.PreviewMouseWheel += (s, e) => StopScrollAnimation(); // la rueda manda: se corta el desplazamiento animado
        ContentScroll.PreviewMouseDown += (s, e) => StopScrollAnimation();
        ContentScroll.SizeChanged += (s, e) => UpdateBottomSpacer();
        ContentPanel.SizeChanged += (s, e) => UpdateBottomSpacer();
        Loaded += (s, e) =>
        {
            SetActive(_navFlat[0]);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateBottomSpacer);
        };
    }

    /// <summary>Un icono de trazo (el micro de las letras, el logo de Discord) del mismo color que el texto de su botón.</summary>
    private System.Windows.Shapes.Path NavIcon(string style, double size, double stroke)
    {
        var path = new System.Windows.Shapes.Path { Style = (Style)FindResource(style), Width = size, Height = size, StrokeThickness = stroke };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Foreground))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.ContentControl), 1),
        });
        return path;
    }

    private SettingsNavItem? _active;

    private void SetActive(SettingsNavItem item)
    {
        if (item == _active) return;
        _active = item;
        foreach (var n in _navFlat) n.IsActive = n == item || n == item.Parent;
        Breadcrumb.Text = item.Parent is { } parent ? $"{parent.Title}  ›  {item.Title}" : item.Title;
    }

    /// <summary>Dónde está cada sección o apartado, medido desde arriba del contenido.</summary>
    private double TopOf(FrameworkElement anchor) => anchor.TranslatePoint(new Point(0, 0), ContentPanel).Y + ContentPanel.Margin.Top;

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SettingsNavItem item) return;
        SetActive(item);
        double target = Math.Clamp(TopOf(item.Anchor) - 14, 0, ContentScroll.ScrollableHeight);
        _pinned = item;
        ScrollContentTo(target);
    }

    // lo elegido en la barra lateral se queda marcado mientras no te muevas de ahí (al final del todo, por ejemplo,
    // el último apartado no llega arriba y se marcaría el anterior)
    private SettingsNavItem? _pinned;
    private double _pinnedOffset = double.NaN;

    private void ContentScroll_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (_scrolling || _navFlat.Count == 0) return;
        if (_pinned != null && Math.Abs(ContentScroll.VerticalOffset - _pinnedOffset) < 1) return;
        _pinned = null;
        // la sección o el apartado cuyo título ya ha pasado por arriba (con un poco de margen)
        double line = ContentScroll.VerticalOffset + 60;
        var current = _navFlat[0];
        foreach (var n in _navFlat)
        {
            if (n.Anchor.Visibility != Visibility.Visible) continue;
            if (TopOf(n.Anchor) <= line) current = n;
            else break;
        }
        SetActive(current);
    }

    /// <summary>Hueco al final para que la última sección también pueda quedar arriba del todo.</summary>
    private void UpdateBottomSpacer()
    {
        if (ContentPanel.ActualHeight <= 0 || ContentScroll.ViewportHeight <= 0) return;
        double tail = ContentPanel.ActualHeight - BottomSpacer.Height - (TopOf(SecUpdates) - ContentPanel.Margin.Top);
        double want = Math.Max(0, ContentScroll.ViewportHeight - tail - 4);
        if (Math.Abs(want - BottomSpacer.Height) > 1) BottomSpacer.Height = want;
    }

    // ---------- desplazamiento suave ----------

    private static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        "ScrollOffset", typeof(double), typeof(SettingsWindow),
        new PropertyMetadata(0.0, (d, e) => ((SettingsWindow)d).ContentScroll.ScrollToVerticalOffset((double)e.NewValue)));

    private bool _scrolling;

    private void ScrollContentTo(double target)
    {
        StopScrollAnimation();
        if (!Animated || Math.Abs(target - ContentScroll.VerticalOffset) < 2)
        {
            ContentScroll.ScrollToVerticalOffset(target);
            _pinnedOffset = target;
            return;
        }
        _scrolling = true;
        double distance = Math.Abs(target - ContentScroll.VerticalOffset);
        var anim = new DoubleAnimation(ContentScroll.VerticalOffset, target, TimeSpan.FromMilliseconds(Math.Clamp(180 + distance / 8, 220, 420)))
        {
            EasingFunction = EaseOut,
        };
        anim.Completed += (s, e) =>
        {
            if (!_scrolling) return;
            _scrolling = false;
            _pinnedOffset = ContentScroll.VerticalOffset;
        };
        BeginAnimation(ScrollOffsetProperty, anim);
    }

    private void StopScrollAnimation()
    {
        if (!_scrolling) return;
        _scrolling = false;
        BeginAnimation(ScrollOffsetProperty, null);
        _pinned = null;
    }

    /// <summary>«Powered by Reyes-gh»: su página de GitHub.</summary>
    private void Brand_Click(object sender, RoutedEventArgs e) => OpenUrl("https://github.com/Reyes-gh");

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
        if (e.PropertyName == nameof(MainViewModel.EqAutoHeadroom)) DrawEqResponse();
        if (e.PropertyName is nameof(MainViewModel.MeasureStatus) or nameof(MainViewModel.IsMeasuring)) UpdateMeasureUi();
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
        if (e.PropertyName != nameof(EqBand.Value)) return;
        DrawEqCurve();
        DrawEqResponse();
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

    // ---------- la respuesta real ----------

    private const double ResponseRange = 15; // dB arriba y abajo: ±12 de las bandas y algo de aire
    private const double ResponseSampleRate = 44100;
    private static readonly double[] ResponseFrequencies = EqDesign.CurveFrequencies(ResponseSampleRate, 240);

    private void EqResponse_SizeChanged(object sender, SizeChangedEventArgs e) => DrawEqResponse();

    /// <summary>
    /// Dibuja lo que suena de verdad: la respuesta de los filtros que usa el audio (calculados igual que en la
    /// reproducción, a 44,1 kHz), en escala logarítmica de 20 Hz a 20 kHz, con las líneas de 0, ±6 y ±12 dB.
    /// </summary>
    private void DrawEqResponse()
    {
        var canvas = EqResponseCanvas;
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 10 || h < 10) return;
        canvas.Children.Clear();

        var gains = _vm.EqBands.Select(b => b.Value).ToArray();
        var design = EqDesign.Create(new Equalizer.Snapshot(true, gains, _vm.EqPreamp.Value, _vm.EqAutoHeadroom), ResponseSampleRate);
        var db = design.Response(ResponseFrequencies);

        double lo = Math.Log(ResponseFrequencies[0]), hi = Math.Log(ResponseFrequencies[^1]);
        double X(double f) => (Math.Log(f) - lo) / (hi - lo) * w;
        double Y(double d) => h / 2 - Math.Clamp(d, -ResponseRange, ResponseRange) / ResponseRange * (h / 2 - 2);

        // colores del tema por referencia: si se cambia de tema con Ajustes abierto, la gráfica cambia con él
        const string grid = "CardBorderBrush", label = "Text3Brush";
        void Line(double x1, double y1, double x2, double y2, string brush, double opacity = 1)
        {
            var l = new System.Windows.Shapes.Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = 1, Opacity = opacity, SnapsToDevicePixels = true };
            l.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, brush);
            canvas.Children.Add(l);
        }
        void Label(string text, double x, double y, bool right = false)
        {
            var t = new System.Windows.Controls.TextBlock { Text = text, FontSize = 10 };
            t.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, label);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            System.Windows.Controls.Canvas.SetLeft(t, right ? x - t.DesiredSize.Width - 2 : x + 3);
            System.Windows.Controls.Canvas.SetTop(t, y);
            canvas.Children.Add(t);
        }

        foreach (var d in new[] { 12.0, 6, -6, -12 })
        {
            Line(0, Math.Round(Y(d)) + 0.5, w, Math.Round(Y(d)) + 0.5, grid, 0.6);
            Label(d > 0 ? "+" + d.ToString("0") : d.ToString("0"), w, Y(d) - 13, right: true);
        }
        foreach (var (f, text) in new[] { (100.0, "100"), (1000.0, "1k"), (10000.0, "10k") })
        {
            Line(Math.Round(X(f)) + 0.5, 0, Math.Round(X(f)) + 0.5, h, grid, 0.6);
            Label(text, X(f), h - 14);
        }
        Line(0, Math.Round(Y(0)) + 0.5, w, Math.Round(Y(0)) + 0.5, label, 0.5);

        var line = new PathFigure { StartPoint = new Point(X(ResponseFrequencies[0]), Y(db[0])) };
        var area = new PathFigure { StartPoint = new Point(X(ResponseFrequencies[0]), Y(0)), IsClosed = true };
        area.Segments.Add(new LineSegment(line.StartPoint, false));
        for (int i = 1; i < db.Length; i++)
        {
            var p = new Point(X(ResponseFrequencies[i]), Y(db[i]));
            line.Segments.Add(new LineSegment(p, true));
            area.Segments.Add(new LineSegment(p, false));
        }
        area.Segments.Add(new LineSegment(new Point(X(ResponseFrequencies[^1]), Y(0)), false));
        var fill = new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { area }), Opacity = 0.14 };
        fill.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentBrush");
        var curve = new System.Windows.Shapes.Path { Data = new PathGeometry(new[] { line }), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
        curve.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "AccentBrush");
        canvas.Children.Add(fill);
        canvas.Children.Add(curve);

        // a la derecha del título: cuánto baja el margen o si va a actuar el limitador
        double peak = db.Max() + _vm.EqPreamp.Value;
        EqHeadroomText.Text = design.HeadroomDb < -0.05 ? $"Margen automático: {design.HeadroomDb:0.0} dB"
            : peak > 0.05 ? $"Pico de {peak:+0.0} dB: el limitador frena lo que se pase"
            : "";
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

    private void Normalization_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string mode }) _vm.NormalizationMode = mode;
    }

    private async void LastFmConnect_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ConnectLastFmAsync(LastFmKeyBox.Text, LastFmSecretBox.Password);
        if (_vm.LastFmUser != null) LastFmSecretBox.Clear();
    }

    private void LastFmDisconnect_Click(object sender, RoutedEventArgs e) => _vm.DisconnectLastFm();

    private async void ListenBrainzConnect_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ConnectListenBrainzAsync(ListenBrainzTokenBox.Password);
        if (_vm.ListenBrainzUser != null) ListenBrainzTokenBox.Clear();
    }

    private void ListenBrainzDisconnect_Click(object sender, RoutedEventArgs e) => _vm.DisconnectListenBrainz();

    private void ScrobbleFlush_Click(object sender, RoutedEventArgs e) => _vm.FlushScrobbles();

    private void Health_Click(object sender, RoutedEventArgs e)
    {
        _vm.OpenHealth();
        BeginClose();
    }

    private void HotkeyMods_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string s }) _vm.GlobalHotkeyShift = s == "shift";
    }

    private void Crossfade_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string s }) return;
        if (s == "custom")
        {
            if (!IsLoaded) return; // al abrir Ajustes ya viene puesto
            ApplyCustomFade();
            FadeCustomBox.Focus();
            FadeCustomBox.SelectAll();
        }
        else if (double.TryParse(s, out var seconds))
        {
            _vm.CrossfadeSeconds = seconds;
        }
    }

    private void FadeCustomBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyCustomFade();

    private void FadeCustomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyCustomFade();
        FadeCustomBox.SelectAll();
        e.Handled = true;
    }

    /// <summary>Los segundos escritos, de 1 a 10 en medios segundos; si no se entiende, vuelve a los que había.</summary>
    private void ApplyCustomFade()
    {
        if (FadeCustom.IsChecked != true) return;
        var t = FadeCustomBox.Text.Trim().TrimEnd('s', 'S').Trim();
        if (TryParseDb(t, out var seconds) && seconds > 0)
            _vm.CrossfadeSeconds = Math.Clamp(Math.Round(seconds * 2) / 2, 1, MainViewModel.MaxCrossfade);
        else if (_vm.CrossfadeSeconds <= 0)
            _vm.CrossfadeSeconds = 5;
        FadeCustomBox.Text = FormatSeconds(_vm.CrossfadeSeconds);
    }

    private static string FormatSeconds(double seconds) => seconds.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("es-ES"));

    private void Measure_Click(object sender, RoutedEventArgs e)
    {
        _vm.StartMeasuring();
        UpdateMeasureUi();
    }

    private void UpdateMeasureUi()
    {
        MeasureButtonText.Text = _vm.IsMeasuring ? "Parar" : "Medir la biblioteca";
        NormSummary.Text = _vm.NormalizationSummary;
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

    /// <summary>El interruptor de una carpeta en línea: conectarla o desconectarla.</summary>
    private async void OnlineEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox { DataContext: OnlineFolderItem item } box)
            await _vm.SetOnlineConnectedAsync(item.Id, box.IsChecked == true);
    }

    private async void RemoveOnline_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OnlineFolderItem item) await _vm.RemoveOnlineFolderAsync(item.Id);
    }

    private void OpenOnline_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OnlineFolderItem item) OpenUrl(item.Url);
    }

    private async void RefreshOnline_Click(object sender, RoutedEventArgs e) => await _vm.RefreshOnlineFoldersAsync(announce: true, full: true);

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
