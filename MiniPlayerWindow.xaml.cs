using System.Windows;
using System.Windows.Input;
using Echoplex.ViewModels;

namespace Echoplex;

public partial class MiniPlayerWindow : Window
{
    private readonly MainViewModel _vm;

    public MiniPlayerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    public event Action? ExpandRequested;

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
