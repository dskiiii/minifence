using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiniFences.Services;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace MiniFences;

public partial class ColorPickerDialog : Window
{
    private bool _updating;
    private bool _draggingColorField;
    private double _hue;
    private double _saturation;
    private double _value;
    private byte _alpha = 255;
    private readonly string _previousColor;

    public string SelectedColor { get; private set; }

    public ColorPickerDialog(string currentColor, LocalizationService localization, bool chooseHeader)
    {
        InitializeComponent();
        var chinese = localization.Language == LocalizationService.Chinese;
        Title = chinese ? "选择颜色" : "Choose color";
        WindowTitleText.Text = Title;
        PromptText.Text = localization.T(chooseHeader ? "ChooseHeaderColor" : "ChooseBackgroundColor").TrimEnd('.');
        HueLabel.Text = chinese ? "色相" : "Hue";
        HexLabel.Text = "HEX";
        OpacityLabel.Text = chinese ? "透明度" : "Opacity";
        PreviousLabel.Text = chinese ? "原颜色" : "Previous";
        CurrentLabel.Text = chinese ? "新颜色" : "New";
        OkButton.Content = chinese ? "应用" : "Apply";
        CancelButton.Content = chinese ? "取消" : "Cancel";

        SelectedColor = NormalizeColor(currentColor) ?? (chooseHeader ? "#CC3F7FA8" : "#DD20242A");
        _previousColor = SelectedColor;
        PreviousSwatch.Background = BrushFromColor(_previousColor);
        SetColor(SelectedColor);
        Loaded += ColorPickerDialog_Loaded;
    }

    private void ColorPickerDialog_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateColorFieldVisuals();
        Activate();
        HexTextBox.Focus();
        HexTextBox.SelectAll();
    }

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string color }) return;
        var rgb = (MediaColor)MediaColorConverter.ConvertFromString(color);
        SetColor($"#{_alpha:X2}{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}");
    }

    private void ColorField_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingColorField = true;
        ColorField.CaptureMouse();
        UpdateSaturationAndValue(e.GetPosition(ColorField));
        e.Handled = true;
    }

    private void ColorField_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_draggingColorField || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateSaturationAndValue(e.GetPosition(ColorField));
    }

    private void ColorField_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingColorField) return;
        UpdateSaturationAndValue(e.GetPosition(ColorField));
        _draggingColorField = false;
        ColorField.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateSaturationAndValue(System.Windows.Point point)
    {
        if (ColorField.ActualWidth <= 0 || ColorField.ActualHeight <= 0) return;
        _saturation = Math.Clamp(point.X / ColorField.ActualWidth, 0, 1);
        _value = 1 - Math.Clamp(point.Y / ColorField.ActualHeight, 0, 1);
        CommitHsvColor();
    }

    private void HueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || ColorField == null) return;
        _hue = e.NewValue;
        CommitHsvColor();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText == null) return;
        _alpha = (byte)Math.Round(e.NewValue);
        OpacityValueText.Text = $"{Math.Round(_alpha / 255.0 * 100)}%";
        if (!_updating) CommitHsvColor();
    }

    private void HexTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        var normalized = NormalizeColor(HexTextBox.Text);
        OkButton.IsEnabled = normalized != null;
        HexTextBox.BorderBrush = normalized == null
            ? MediaBrushes.IndianRed
            : new SolidColorBrush(MediaColor.FromRgb(70, 85, 100));
        if (normalized != null) SetColor(normalized);
    }

    private void RgbTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (!TryReadByte(RedTextBox.Text, out var red) ||
            !TryReadByte(GreenTextBox.Text, out var green) ||
            !TryReadByte(BlueTextBox.Text, out var blue))
        {
            OkButton.IsEnabled = false;
            return;
        }

        SetColor($"#{_alpha:X2}{red:X2}{green:X2}{blue:X2}");
    }

    private void CommitHsvColor()
    {
        var rgb = ColorFromHsv(_hue, _saturation, _value, _alpha);
        ApplyColor(rgb, updateHsv: false);
    }

    private void SetColor(string color)
    {
        var normalized = NormalizeColor(color);
        if (normalized == null) return;
        var parsed = (MediaColor)MediaColorConverter.ConvertFromString(normalized);
        ApplyColor(parsed, updateHsv: true);
    }

    private void ApplyColor(MediaColor color, bool updateHsv)
    {
        SelectedColor = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        _alpha = color.A;
        if (updateHsv) (_hue, _saturation, _value) = HsvFromColor(color);

        _updating = true;
        HexTextBox.Text = SelectedColor;
        RedTextBox.Text = color.R.ToString();
        GreenTextBox.Text = color.G.ToString();
        BlueTextBox.Text = color.B.ToString();
        HueSlider.Value = _hue;
        OpacitySlider.Value = _alpha;
        OpacityValueText.Text = $"{Math.Round(_alpha / 255.0 * 100)}%";
        PreviewSwatch.Background = new SolidColorBrush(color);
        OkButton.IsEnabled = true;
        _updating = false;
        UpdateColorFieldVisuals();
    }

    private void UpdateColorFieldVisuals()
    {
        if (ColorField == null || ColorFieldThumb == null) return;
        var hueColor = ColorFromHsv(_hue, 1, 1, 255);
        ColorField.Background = new SolidColorBrush(hueColor);
        var width = ColorField.ActualWidth;
        var height = ColorField.ActualHeight;
        if (width <= 0 || height <= 0) return;
        Canvas.SetLeft(ColorFieldThumb, _saturation * width - ColorFieldThumb.Width / 2);
        Canvas.SetTop(ColorFieldThumb, (1 - _value) * height - ColorFieldThumb.Height / 2);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var normalized = NormalizeColor(HexTextBox.Text);
        if (normalized == null) return;
        SelectedColor = normalized;
        DialogResult = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    internal static MediaColor ColorFromHsvForTesting(double hue, double saturation, double value, byte alpha = 255) =>
        ColorFromHsv(hue, saturation, value, alpha);

    internal static (double Hue, double Saturation, double Value) HsvFromColorForTesting(MediaColor color) =>
        HsvFromColor(color);

    private static MediaColor ColorFromHsv(double hue, double saturation, double value, byte alpha)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var match = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x)
        };
        return MediaColor.FromArgb(
            alpha,
            (byte)Math.Round((r + match) * 255),
            (byte)Math.Round((g + match) * 255),
            (byte)Math.Round((b + match) * 255));
    }

    private static (double Hue, double Saturation, double Value) HsvFromColor(MediaColor color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? 0 :
            max == r ? 60 * (((g - b) / delta) % 6) :
            max == g ? 60 * ((b - r) / delta + 2) :
            60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static bool TryReadByte(string? text, out byte value) =>
        byte.TryParse(text?.Trim(), out value);

    private static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length == 7) text = "#FF" + text[1..];
        if (text.Length != 9) return null;
        try
        {
            var color = (MediaColor)MediaColorConverter.ConvertFromString(text);
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch
        {
            return null;
        }
    }

    private static SolidColorBrush BrushFromColor(string color) =>
        new((MediaColor)MediaColorConverter.ConvertFromString(color));
}
