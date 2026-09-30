using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Shapes;
using Echoplex.Services;
using Echoplex.ViewModels;

namespace Echoplex;

/// <summary>
/// Volumen avanzado (clic derecho en el botón o la barra de volumen): lectura con decimales y en dB, una barra grande
/// que recorre solo el rango elegido (0–5 %, 0–10 %…) para afinar en volúmenes bajos, pasos de 0,1 y 1 puntos,
/// valor exacto, y rueda o flechas con un paso proporcional al rango (Mayús: aún más fino).
/// </summary>
public partial class VolumePanel : UserControl
{
    private static readonly double[] Ranges = { 0.05, 0.10, 0.25, 0.50, 1.0 };
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");

    private PlayerService? _player;
    private double _range = 1;
    private readonly Dictionary<double, RangeOption> _chips = new();

    /// <summary>Pide cerrar el panel (Esc).</summary>
    public event Action? CloseRequested;

    public VolumePanel()
    {
        InitializeComponent();
        // 0–5 | 0–10 | 0–25 | 0–50 | 0–100 %
        foreach (var r in Ranges)
        {
            if (RangePanel.Children.Count > 0) RangePanel.Children.Add(Separator("|"));
            var chip = new RangeOption { Style = (Style)Resources["RangeTab"], GroupName = "VolumeRange", Content = $"0–{Pct(r)}", Tag = r };
            chip.Checked += (s, e) => { _range = (double)((FrameworkElement)s!).Tag; Refresh(); };
            _chips[r] = chip;
            RangePanel.Children.Add(chip);
        }
        RangePanel.Children.Add(Separator("%"));
        DataContextChanged += (s, e) => Attach((DataContext as MainViewModel)?.Player);
    }

    private double Volume
    {
        get => _player?.Volume ?? 0;
        set
        {
            if (_player == null) return;
            if (_player.IsMuted && value > 0) _player.IsMuted = false; // tocar el volumen quita el silencio
            _player.Volume = Math.Round(Math.Clamp(value, 0, 1), 5);
        }
    }

    private void Attach(PlayerService? player)
    {
        if (_player != null) _player.PropertyChanged -= OnPlayerChanged;
        _player = player;
        if (_player != null) _player.PropertyChanged += OnPlayerChanged;
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerService.Volume) or nameof(PlayerService.IsMuted)) Dispatcher.BeginInvoke(Refresh);
    }

    /// <summary>Antes de abrirse: el rango más pequeño en el que el volumen actual queda cómodo (hasta el 80 % de la barra).</summary>
    public void PrepareToShow()
    {
        _range = Ranges.FirstOrDefault(r => Volume <= r * 0.8, 1.0);
        _chips[_range].IsChecked = true;
        Refresh();
    }

    private double Step => _range / 100; // un 1 % de la barra: 0,05 puntos en 0–5 %, 1 punto en 0–100 %

    private void Refresh()
    {
        double v = Volume;
        // si el volumen se sale del rango (pasos, teclado, la barra pequeña), la barra se amplía sola
        if (v > _range + 1e-9 && !Track.IsMouseCaptured)
        {
            _range = Ranges.FirstOrDefault(r => v <= r, 1.0);
            _chips[_range].IsChecked = true;
        }

        // tramos en los que no cabe el volumen actual: no se pueden elegir
        foreach (var (r, chip) in _chips)
        {
            chip.IsAvailable = v <= r + 1e-9;
            chip.ToolTip = chip.IsAvailable ? $"La barra va de 0 a {Pct(r)} %" : $"El volumen ({Pct(v)} %) está por encima de este tramo";
        }

        PercentRun.Text = Pct(v);
        DbText.Text = v <= 0 ? "−∞ dB" : (20 * Math.Log10(v)).ToString("0.0", Es).Replace("-", "−") + " dB";
        if (!ExactBox.IsKeyboardFocused) ExactBox.Text = Pct(v);
        bool muted = _player?.IsMuted == true;
        PercentRun.SetResourceReference(TextElement.ForegroundProperty, muted ? "Text3Brush" : "TextBrush");

        double w = Track.ActualWidth;
        double frac = Math.Clamp(v / _range, 0, 1);
        if (w > 0)
        {
            double x = frac * (w - Thumb.Width);
            Thumb.Margin = new Thickness(x, 0, 0, 0);
            Fill.Width = x + Thumb.Width / 2;
        }
        MidLabel.Text = Pct(_range / 2) + " %";
        MaxLabel.Text = Pct(_range) + " %";
        HintText.Text = $"Rueda o ← →: ±{Pct(Step)} {(Step * 100 == 1 ? "punto" : "puntos")} · con Mayús: ±{Pct(Step / 5)}";
    }

    private static TextBlock Separator(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(9, 0, 9, 3), VerticalAlignment = VerticalAlignment.Center, FontSize = 13.5 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Text3Brush");
        if (text == "%") t.Margin = new Thickness(4, 0, 0, 3);
        return t;
    }

    /// <summary>Porcentaje con los decimales justos: 0,35 · 4,2 · 37.</summary>
    private static string Pct(double v)
    {
        double p = v * 100;
        return p.ToString(p < 10 ? "0.##" : p < 100 ? "0.#" : "0", Es);
    }

    private void Track_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Ticks.Children.Clear();
        double w = Track.ActualWidth - Thumb.Width, h = Track.ActualHeight;
        for (int i = 0; i <= 10; i++)
        {
            double x = Thumb.Width / 2 + w * i / 10;
            bool major = i % 5 == 0;
            var tick = new Rectangle { Width = 1, Height = major ? 8 : 4, Opacity = major ? 0.8 : 0.5 };
            tick.SetResourceReference(Shape.FillProperty, "Text3Brush");
            Canvas.SetLeft(tick, x - 0.5);
            Canvas.SetTop(tick, h / 2 + 7);
            Ticks.Children.Add(tick);
        }
        Refresh();
    }

    private void SetFromMouse(MouseEventArgs e)
    {
        double w = Track.ActualWidth - Thumb.Width;
        if (w <= 0) return;
        double frac = Math.Clamp((e.GetPosition(Track).X - Thumb.Width / 2) / w, 0, 1);
        // en rangos pequeños, redondeo a pasos limpios de la barra (0,01 puntos en 0–5 %)
        double step = _range / 500;
        Volume = Math.Round(frac * _range / step) * step;
    }

    private void Track_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Track.CaptureMouse();
        SetFromMouse(e);
        e.Handled = true;
    }

    private void Track_MouseMove(object sender, MouseEventArgs e)
    {
        if (Track.IsMouseCaptured) SetFromMouse(e);
    }

    private void Track_MouseUp(object sender, MouseButtonEventArgs e) => Track.ReleaseMouseCapture();

    private void Track_LostCapture(object sender, MouseEventArgs e) => Refresh();

    private void Panel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Step / 5 : Step;
        Volume = Math.Round((Volume + Math.Sign(e.Delta) * step) / step) * step;
        e.Handled = true;
    }

    private void Panel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseRequested?.Invoke(); e.Handled = true; return; }
        if (ExactBox.IsKeyboardFocused) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Step / 5 : Step;
        double? delta = e.Key switch
        {
            Key.Left or Key.Down => -step,
            Key.Right or Key.Up => step,
            Key.PageDown => -step * 10,
            Key.PageUp => step * 10,
            _ => null,
        };
        if (e.Key == Key.Home) Volume = 0;
        else if (e.Key == Key.End) Volume = _range;
        else if (delta is { } d) Volume = Math.Round((Volume + d) / step) * step;
        else return;
        e.Handled = true;
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        double points = double.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture);
        Volume = Math.Round(Volume * 100 + points, 2) / 100;
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_player != null) _player.IsMuted = !_player.IsMuted;
    }

    private void ExactBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyExact();
        Focus();
        e.Handled = true;
    }

    private void ExactBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyExact();

    private void ApplyExact()
    {
        var text = ExactBox.Text.Replace('%', ' ').Replace(',', '.').Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) Volume = Math.Clamp(p, 0, 100) / 100;
        else ExactBox.Text = Pct(Volume);
    }
}

/// <summary>Un tramo de la barra de volumen; si el volumen actual no cabe en él, no se puede elegir.</summary>
public sealed class RangeOption : RadioButton
{
    public static readonly DependencyProperty IsAvailableProperty =
        DependencyProperty.Register(nameof(IsAvailable), typeof(bool), typeof(RangeOption), new PropertyMetadata(true));

    public bool IsAvailable
    {
        get => (bool)GetValue(IsAvailableProperty);
        set => SetValue(IsAvailableProperty, value);
    }

    protected override void OnToggle()
    {
        if (IsAvailable) base.OnToggle();
    }
}
