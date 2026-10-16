using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Echoplex;

/// <summary>Diálogo minimalista: confirmación, aviso o petición de texto.</summary>
public partial class DialogWindow : Window
{
    private DialogWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Accept();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        if (Input.Visibility == Visibility.Visible && string.IsNullOrWhiteSpace(Input.Text)) return;
        if (Input2.Visibility == Visibility.Visible && string.IsNullOrWhiteSpace(Input2.Text))
        {
            Input2.Focus();
            return;
        }
        DialogResult = true;
    }

    public static string? Prompt(Window owner, string title, string message, string initial = "", string ok = "Aceptar")
    {
        var d = new DialogWindow { Owner = owner };
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        d.Input.Visibility = Visibility.Visible;
        d.Input.Text = initial;
        d.OkButton.Content = ok;
        d.Loaded += (s, e) => { d.Input.Focus(); d.Input.SelectAll(); };
        return d.ShowDialog() == true ? d.Input.Text.Trim() : null;
    }

    /// <summary>Dos campos con su etiqueta (la URL de una carpeta en línea y su nombre). Devuelve null si se cancela.</summary>
    public static (string First, string Second)? PromptTwo(Window owner, string title, string message, string label1, string label2,
        string initial2 = "", string ok = "Aceptar")
    {
        var d = new DialogWindow { Owner = owner };
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        d.InputLabel.Text = label1;
        d.InputLabel.Visibility = Visibility.Visible;
        d.Input.Visibility = Visibility.Visible;
        d.Input.Margin = new Thickness(0, 6, 0, 0);
        d.Input2Label.Text = label2;
        d.Input2Label.Visibility = Visibility.Visible;
        d.Input2.Visibility = Visibility.Visible;
        d.Input2.Text = initial2;
        d.OkButton.Content = ok;
        d.Loaded += (s, e) => d.Input.Focus();
        return d.ShowDialog() == true ? (d.Input.Text.Trim(), d.Input2.Text.Trim()) : null;
    }

    public static bool Confirm(Window owner, string title, string message, string ok = "Aceptar", bool danger = false)
    {
        var d = new DialogWindow { Owner = owner };
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.OkButton.Content = ok;
        if (danger) d.OkButton.Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x3E, 0x3E));
        return d.ShowDialog() == true;
    }

    public static void Info(Window owner, string title, string message)
    {
        var d = new DialogWindow { Owner = owner };
        d.TitleText.Text = title;
        d.MessageText.Text = message;
        d.CancelButton.Visibility = Visibility.Collapsed;
        d.OkButton.Content = "Cerrar";
        d.MessageText.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        d.ShowDialog();
    }
}
