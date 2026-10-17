using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Echoplex.Models;
using Echoplex.Services;
using Echoplex.ViewModels;

namespace Echoplex;

/// <summary>Crear o editar una playlist inteligente: sus reglas, con cuántas canciones saldrían ahora mismo.</summary>
public partial class SmartPlaylistWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _ready;

    public string ResultName { get; private set; } = "";
    public SmartRules? Result { get; private set; }

    private SmartPlaylistWindow(Window owner, MainViewModel vm, string name, SmartRules rules, bool editing)
    {
        InitializeComponent();
        Owner = owner;
        _vm = vm;
        TitleText.Text = editing ? "Editar playlist inteligente" : "Nueva playlist inteligente";
        OkButton.Content = editing ? "Guardar" : "Crear";
        NameBox.Text = name;
        (rules.Scope switch
        {
            SmartRules.ScopeArtist => ScopeArtist,
            SmartRules.ScopeGenre => ScopeGenre,
            SmartRules.ScopeFolder => ScopeFolder,
            _ => ScopeAll,
        }).IsChecked = true;
        ScopeBox.Text = rules.ScopeValue ?? "";
        FavOnly.IsChecked = rules.FavoritesOnly;
        NotPlayedOn.IsChecked = rules.NotPlayedDays > 0;
        if (rules.NotPlayedDays > 0) NotPlayedBox.Text = rules.NotPlayedDays.ToString();
        MinPlaysOn.IsChecked = rules.MinPlays > 0;
        if (rules.MinPlays > 0) MinPlaysBox.Text = rules.MinPlays.ToString();
        AddedOn.IsChecked = rules.AddedDays > 0;
        if (rules.AddedDays > 0) AddedBox.Text = rules.AddedDays.ToString();
        YearsOn.IsChecked = rules.YearFrom > 0 || rules.YearTo > 0;
        if (rules.YearFrom > 0) YearFromBox.Text = rules.YearFrom.ToString();
        if (rules.YearTo > 0) YearToBox.Text = rules.YearTo.ToString();
        (rules.Order switch
        {
            SmartRules.OrderLeast => OrderLeast,
            SmartRules.OrderRecent => OrderRecent,
            SmartRules.OrderAdded => OrderAdded,
            SmartRules.OrderRandom => OrderRandom,
            SmartRules.OrderAlbum => OrderAlbum,
            _ => OrderPlays,
        }).IsChecked = true;
        LimitBox.Text = rules.Limit.ToString();
        _ready = true;
        UpdateScope();
        UpdatePreview();
        Loaded += (s, e) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>Pide las reglas; null si se cancela.</summary>
    public static (string Name, SmartRules Rules)? Show(Window owner, MainViewModel vm, string name, SmartRules? rules = null)
    {
        var w = new SmartPlaylistWindow(owner, vm, name, rules?.Clone() ?? new SmartRules(), rules != null);
        return w.ShowDialog() == true && w.Result != null ? (w.ResultName, w.Result) : null;
    }

    private string Scope =>
        ScopeArtist.IsChecked == true ? SmartRules.ScopeArtist
        : ScopeGenre.IsChecked == true ? SmartRules.ScopeGenre
        : ScopeFolder.IsChecked == true ? SmartRules.ScopeFolder
        : SmartRules.ScopeAll;

    private static int Number(TextBox box) => int.TryParse(box.Text.Trim(), out var n) ? Math.Max(0, n) : 0;

    private SmartRules Read() => new()
    {
        Scope = Scope,
        ScopeValue = Scope == SmartRules.ScopeAll ? null : ResolveScope(),
        FavoritesOnly = FavOnly.IsChecked == true,
        NotPlayedDays = NotPlayedOn.IsChecked == true ? Number(NotPlayedBox) : 0,
        MinPlays = MinPlaysOn.IsChecked == true ? Number(MinPlaysBox) : 0,
        AddedDays = AddedOn.IsChecked == true ? Number(AddedBox) : 0,
        YearFrom = YearsOn.IsChecked == true ? Number(YearFromBox) : 0,
        YearTo = YearsOn.IsChecked == true ? Number(YearToBox) : 0,
        Order = OrderLeast.IsChecked == true ? SmartRules.OrderLeast
            : OrderRecent.IsChecked == true ? SmartRules.OrderRecent
            : OrderAdded.IsChecked == true ? SmartRules.OrderAdded
            : OrderRandom.IsChecked == true ? SmartRules.OrderRandom
            : OrderAlbum.IsChecked == true ? SmartRules.OrderAlbum
            : SmartRules.OrderPlays,
        Limit = Number(LimitBox),
    };

    /// <summary>Lo escrito, tal y como está en la biblioteca (sin importar mayúsculas ni acentos).</summary>
    private string? ResolveScope()
    {
        var text = ScopeBox.Text.Trim();
        if (text.Length == 0) return null;
        var folded = SearchQuery.Fold(text);
        return _vm.SmartScopeChoices(Scope).FirstOrDefault(c => SearchQuery.Fold(c) == folded)
               ?? _vm.SmartScopeChoices(Scope).FirstOrDefault(c => Scope == SmartRules.ScopeFolder && SearchQuery.Fold(System.IO.Path.GetFileName(c)) == folded)
               ?? text;
    }

    private void Scope_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        ScopeBox.Text = "";
        UpdateScope();
        UpdatePreview();
    }

    private void UpdateScope()
    {
        ScopeValuePanel.Visibility = Scope == SmartRules.ScopeAll ? Visibility.Collapsed : Visibility.Visible;
        UpdateSuggestions();
    }

    private void ScopeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateSuggestions();
        UpdatePreview();
    }

    /// <summary>Hasta 8 artistas, géneros o carpetas que encajan con lo escrito (con erratas incluidas).</summary>
    private void UpdateSuggestions()
    {
        Suggestions.Children.Clear();
        if (Scope == SmartRules.ScopeAll) return;
        var choices = _vm.SmartScopeChoices(Scope);
        var q = new SearchQuery(ScopeBox.Text);
        var picked = q.IsEmpty
            ? choices.Take(8)
            : choices.Select(c => (c, s: q.Score((Scope == SmartRules.ScopeFolder ? System.IO.Path.GetFileName(c) : c, 1))))
                .Where(x => x.s > 0).OrderByDescending(x => x.s).Take(8).Select(x => x.c);
        foreach (var c in picked)
        {
            var chip = new Button
            {
                Content = Scope == SmartRules.ScopeFolder ? FolderLabel(c) : c,
                ToolTip = c,
                Style = (Style)FindResource("SecondaryButton"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 3, 10, 3),
            };
            chip.Click += (s, e) =>
            {
                ScopeBox.Text = c;
                ScopeBox.CaretIndex = c.Length;
            };
            Suggestions.Children.Add(chip);
        }
    }

    /// <summary>Nombre de una carpeta para la sugerencia: el de la carpeta, el que le pusiste si es en línea, o la unidad entera.</summary>
    private static string FolderLabel(string path) =>
        OnlineLibrary.RootName(path) ?? (System.IO.Path.GetFileName(path) is { Length: > 0 } n ? n : path);

    private void Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!_ready) return;
        var rules = Read();
        if (rules.Scope != SmartRules.ScopeAll && rules.ScopeValue == null)
        {
            PreviewText.Text = "Elige de dónde.";
            return;
        }
        int n = _vm.SmartSongs(rules).Count;
        PreviewText.Text = n == 0 ? "Ahora mismo no saldría ninguna canción." : n == 1 ? "Ahora mismo saldría 1 canción." : $"Ahora mismo saldrían {n:N0} canciones.";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var rules = Read();
        if (rules.Scope != SmartRules.ScopeAll && rules.ScopeValue == null)
        {
            ScopeBox.Focus();
            return;
        }
        ResultName = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : "Playlist inteligente";
        Result = rules;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
        else if (e.Key == Key.Enter && e.OriginalSource is not TextBox { AcceptsReturn: true }) Ok_Click(sender, e);
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
