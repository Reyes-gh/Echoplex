using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Echoplex.Services;
using Echoplex.ViewModels;

namespace Echoplex;

public partial class MiniPlayerWindow : Window
{
    private readonly MainViewModel _vm;

    // tamaño del contenido a escala 1 y el margen de la sombra (que no escala)
    private const double BaseWidth = 360, BaseHeight = 112, Shadow = 10, MinScale = 0.75, MaxScale = 2.0;

    private double _scale = 1;

    public MiniPlayerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        SetScale(Effective, anchorRight: true, anchorBottom: true, place: false);
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    public event Action? ExpandRequested;

    /// <summary>Neoplex («mini reproductor de tamaño libre»): el tamaño guardado; si no, el de siempre.</summary>
    private double Effective => _vm.MiniResizable ? Math.Clamp(_vm.Settings.MiniScale, MinScale, MaxScale) : 1;

    /// <summary>Al cambiar Neoplex: el tamaño que toque, sin moverse de su esquina de abajo a la derecha.</summary>
    public void ApplyScale() => SetScale(Effective, anchorRight: true, anchorBottom: true);

    private void SetScale(double scale, bool anchorRight, bool anchorBottom, bool place = true)
    {
        scale = Math.Round(Math.Clamp(scale, MinScale, MaxScale), 2);
        double right = Left + Width, bottom = Top + Height;
        _scale = scale;
        RootScale.ScaleX = RootScale.ScaleY = scale;
        Width = BaseWidth * scale + 2 * Shadow;
        Height = BaseHeight * scale + 2 * Shadow;
        if (!place) return;
        if (anchorRight) Left = right - Width;
        if (anchorBottom) Top = bottom - Height;
    }

    private void Save()
    {
        if (!_vm.MiniResizable) return;
        _vm.Settings.MiniScale = _scale;
        SettingsStore.Save(_vm.Settings);
    }

    // ---------- esquinas ----------

    private Point _dragStart;
    private double _dragScale;

    private Point CursorDip()
    {
        var p = PointToScreen(Mouse.GetPosition(this));
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    private void Grip_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragStart = CursorDip();
        _dragScale = _scale;
    }

    /// <summary>La esquina que se arrastra se va con el ratón y la contraria se queda quieta; nunca se deforma.</summary>
    private void Grip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string corner }) return;
        var now = CursorDip();
        double dx = (now.X - _dragStart.X) * (corner.Contains('r') ? 1 : -1);
        double dy = (now.Y - _dragStart.Y) * (corner.Contains('b') ? 1 : -1);
        double grow = Math.Max(dx / (BaseWidth * _dragScale), dy / (BaseHeight * _dragScale));
        SetScale(_dragScale * (1 + grow), anchorRight: corner.Contains('l'), anchorBottom: corner.Contains('t'));
    }

    private void Grip_DragCompleted(object sender, DragCompletedEventArgs e) => Save();

    private void Grip_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        SetScale(1, anchorRight: true, anchorBottom: true);
        Save();
        e.Handled = true;
    }

    /// <summary>Ctrl + rueda: más grande o más pequeño, sin moverse de la esquina de abajo a la derecha.</summary>
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_vm.MiniResizable || Keyboard.Modifiers != ModifierKeys.Control) return;
        SetScale(_scale + (e.Delta > 0 ? 0.1 : -0.1), anchorRight: true, anchorBottom: true);
        Save();
        e.Handled = true;
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Expand_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();
    private void Close_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();
    private void Play_Click(object sender, RoutedEventArgs e) => _vm.TogglePlay();
    private void Prev_Click(object sender, RoutedEventArgs e) => _vm.Player.Previous();
    private void Next_Click(object sender, RoutedEventArgs e) => _vm.Player.Next();

    private void Fav_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Player.Current is { } s) _vm.ToggleFavorite(s);
    }
}
