using System.Windows;
using System.Windows.Controls;

namespace Echoplex;

/// <summary>Una columna de la tabla de canciones: proporcional (★, reparte el sobrante) o de ancho fijo en px.</summary>
public sealed class SongColumn
{
    public SongColumn(string key, bool star, double value, double min)
    {
        Key = key;
        Star = star;
        Value = value;
        Min = min;
    }

    public string Key { get; }
    public bool Star { get; }
    /// <summary>Peso (★) o píxeles.</summary>
    public double Value { get; set; }
    public double Min { get; }
}

/// <summary>
/// Orden y ancho de las columnas de la tabla de canciones, compartidos por la cabecera y todas las filas.
/// Cada Grid con <c>SongColumns.Host="True"</c> se construye con este diseño y cada celda con
/// <c>SongColumns.Key</c> se coloca en su columna; al cambiar el diseño se actualizan todas a la vez.
/// La columna 0 (el número) es fija.
/// </summary>
public static class SongColumns
{
    public const double NumberWidth = 44;

    private static readonly SongColumn[] Defaults =
    {
        new("title", true, 4, 120),
        new("artist", true, 3, 80),
        new("album", true, 3, 80),
        new("extra", false, 130, 70),   // solo en Historial / Añadidas recientemente
        new("format", false, 76, 56),
        new("fav", false, 36, 30),
        new("duration", false, 62, 50),
    };

    /// <summary>Clave de la columna fija del número (#), para poder ocultarla como las demás.</summary>
    public const string NumberKey = "number";

    /// <summary>Columnas que el usuario puede mostrar u ocultar desde Ajustes → Estructura, con su nombre.</summary>
    public static readonly IReadOnlyList<(string Key, string Name)> Toggleable = new[]
    {
        (NumberKey, "#  Número"), ("title", "Título"), ("artist", "Artista"), ("album", "Álbum"),
        ("format", "Tipo"), ("fav", "Favorita"), ("duration", "Duración"),
    };

    private static List<SongColumn> _columns = Clone(Defaults);
    private static HashSet<string> _hidden = new();
    private static readonly List<WeakReference<Grid>> Hosts = new();

    /// <summary>Tras terminar de arrastrar un borde o de mover una columna: hay que guardar.</summary>
    public static event Action? Committed;

    public static IReadOnlyList<SongColumn> Columns => _columns;

    private static List<SongColumn> Clone(IEnumerable<SongColumn> src) => src.Select(c => new SongColumn(c.Key, c.Star, c.Value, c.Min)).ToList();

    // ---------- guardar / cargar (ajustes del usuario) ----------

    public static void Load(IList<string>? order, IDictionary<string, double>? sizes, IEnumerable<string>? hidden = null)
    {
        _hidden = new HashSet<string>((hidden ?? Enumerable.Empty<string>()).Where(k => Toggleable.Any(t => t.Key == k)));
        var cols = Clone(Defaults);
        if (order is { Count: > 0 })
        {
            // las que el usuario ordenó, y detrás cualquier columna nueva que no conociera
            cols = order.Select(k => cols.FirstOrDefault(c => c.Key == k)).Where(c => c != null).Cast<SongColumn>()
                .Concat(cols.Where(c => !order.Contains(c.Key))).ToList();
        }
        if (sizes != null)
            foreach (var c in cols)
                if (sizes.TryGetValue(c.Key, out var v) && v > 0 && double.IsFinite(v)) c.Value = c.Star ? v : Math.Max(c.Min, v);
        _columns = cols;
        ApplyAll();
    }

    public static List<string> Order => _columns.Select(c => c.Key).ToList();

    public static Dictionary<string, double> Sizes => _columns.ToDictionary(c => c.Key, c => Math.Round(c.Value, 3));

    public static List<string> Hidden => _hidden.ToList();

    public static bool IsVisible(string key) => !_hidden.Contains(key);

    /// <summary>Muestra u oculta una columna. No deja ocultar la última que queda visible.</summary>
    public static bool SetVisible(string key, bool visible)
    {
        if (visible == IsVisible(key)) return true;
        if (!visible && Toggleable.Count(t => IsVisible(t.Key)) <= 1) return false;
        if (visible) _hidden.Remove(key); else _hidden.Add(key);
        ApplyAll();
        Committed?.Invoke();
        return true;
    }

    /// <summary>Orden, anchos y columnas visibles de fábrica.</summary>
    public static void Reset()
    {
        _columns = Clone(Defaults);
        _hidden.Clear();
        ApplyAll();
        Committed?.Invoke();
    }

    // ---------- propiedades adjuntas ----------

    public static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
        "Host", typeof(bool), typeof(SongColumns), new PropertyMetadata(false, OnHostChanged));

    public static bool GetHost(DependencyObject d) => (bool)d.GetValue(HostProperty);
    public static void SetHost(DependencyObject d, bool value) => d.SetValue(HostProperty, value);

    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(SongColumns), new PropertyMetadata(null));

    public static string? GetKey(DependencyObject d) => (string?)d.GetValue(KeyProperty);
    public static void SetKey(DependencyObject d, string? value) => d.SetValue(KeyProperty, value);

    private static void OnHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid || e.NewValue is not true) return;
        lock (Hosts)
        {
            Hosts.RemoveAll(w => !w.TryGetTarget(out _));
            Hosts.Add(new WeakReference<Grid>(grid));
        }
        Apply(grid);
        // al fijar la propiedad en XAML las celdas aún no están dentro: se recolocan al cargar
        grid.Loaded += (s, _) => Apply((Grid)s);
    }

    private static void ApplyAll()
    {
        List<Grid> live;
        lock (Hosts)
        {
            Hosts.RemoveAll(w => !w.TryGetTarget(out _));
            live = Hosts.Select(w => w.TryGetTarget(out var g) ? g : null).Where(g => g != null).Cast<Grid>().ToList();
        }
        foreach (var g in live) Apply(g);
    }

    /// <summary>Columnas del Grid según el diseño actual, y cada celda en la suya.</summary>
    public static void Apply(Grid grid)
    {
        var defs = grid.ColumnDefinitions;
        int needed = 1 + _columns.Count;
        while (defs.Count < needed) defs.Add(new ColumnDefinition());
        while (defs.Count > needed) defs.RemoveAt(defs.Count - 1);
        defs[0].Width = new GridLength(IsVisible(NumberKey) ? NumberWidth : 0);
        for (int i = 0; i < _columns.Count; i++)
        {
            var c = _columns[i];
            // "extra" puede estar oculta: columna Auto y el ancho en la propia celda, así oculta no ocupa nada
            defs[i + 1].Width = !IsVisible(c.Key) ? new GridLength(0)
                : c.Key == "extra" ? GridLength.Auto
                : c.Star ? new GridLength(c.Value, GridUnitType.Star) : new GridLength(c.Value);
        }
        var extra = _columns.First(c => c.Key == "extra");
        foreach (UIElement child in grid.Children)
        {
            if (GetKey(child) is not { } key) continue;
            if (key != NumberKey)
            {
                int idx = _columns.FindIndex(c => c.Key == key);
                if (idx < 0) continue;
                Grid.SetColumn(child, idx + 1);
                if (key == "extra" && child is FrameworkElement fe && child is not System.Windows.Controls.Primitives.Thumb) fe.Width = extra.Value;
            }
            // "extra" tiene su propia visibilidad (según la página): no se toca
            if (key != "extra") child.Visibility = IsVisible(key) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------- cambiar tamaño: el borde entre una columna y la siguiente ----------

    private static bool IsShown(Grid header, int col) => header.ColumnDefinitions[col].ActualWidth > 0.5;

    /// <summary>Mueve el borde derecho de la columna <paramref name="key"/>: gana ella y pierde la siguiente visible (o al revés).</summary>
    public static void Resize(Grid header, string key, double delta)
    {
        int li = _columns.FindIndex(c => c.Key == key);
        if (li < 0) return;
        int ri = li + 1;
        while (ri < _columns.Count && !IsShown(header, ri + 1)) ri++;
        if (ri >= _columns.Count) return; // la última no tiene vecina a la derecha
        var l = _columns[li];
        var r = _columns[ri];
        double lw = header.ColumnDefinitions[li + 1].ActualWidth, rw = header.ColumnDefinitions[ri + 1].ActualWidth;
        if (lw <= 0 || rw <= 0) return;
        delta = Math.Clamp(delta, l.Min - lw, rw - r.Min);
        if (Math.Abs(delta) < 0.1) return;
        double nl = lw + delta, nr = rw - delta;
        if (l.Star && r.Star)
        {
            // entre dos proporcionales: se reparten su peso conjunto, el resto no se mueve
            double w = l.Value + r.Value;
            l.Value = w * nl / (nl + nr);
            r.Value = w - l.Value;
        }
        else
        {
            l.Value = l.Star ? l.Value * nl / lw : nl;
            r.Value = r.Star ? r.Value * nr / rw : nr;
        }
        ApplyAll();
    }

    public static void CommitResize() => Committed?.Invoke();

    // ---------- cambiar el orden ----------

    /// <summary>Coloca la columna <paramref name="key"/> en la posición <paramref name="index"/> (0 = primera tras el número).</summary>
    public static void Move(string key, int index)
    {
        int from = _columns.FindIndex(c => c.Key == key);
        if (from < 0) return;
        var col = _columns[from];
        _columns.RemoveAt(from);
        if (index > from) index--;
        index = Math.Clamp(index, 0, _columns.Count);
        _columns.Insert(index, col);
        if (index == from) return;
        ApplyAll();
        Committed?.Invoke();
    }

    /// <summary>Posición de inserción (0..n) bajo la coordenada X de la cabecera, y la X de la línea que la marca.</summary>
    public static (int index, double x) DropTarget(Grid header, double x)
    {
        double left = header.ColumnDefinitions[0].ActualWidth;
        int index = 0;
        double lineX = left;
        for (int i = 0; i < _columns.Count; i++)
        {
            double w = header.ColumnDefinitions[i + 1].ActualWidth;
            if (w <= 0.5) continue;
            if (x < left + w / 2) return (i, left);
            left += w;
            index = i + 1;
            lineX = left;
        }
        return (index, lineX);
    }
}
