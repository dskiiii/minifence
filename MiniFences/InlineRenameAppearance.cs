using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TextBox = System.Windows.Controls.TextBox;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfFlowDirection = System.Windows.FlowDirection;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace MiniFences;

internal static class InlineRenameAppearance
{
    internal const double MinimumWidth = 36;
    internal const double MaximumWidth = 82;
    internal const double EditorHeight = 20;
    internal const double SelectionOpacity = 0.45;

    internal static void Apply(TextBox editor, string text)
    {
        editor.MinWidth = MinimumWidth;
        editor.MaxWidth = MaximumWidth;
        editor.Height = EditorHeight;
        editor.Padding = new Thickness(1, 0, 1, 0);
        editor.Background = WpfBrushes.White;
        editor.Foreground = new SolidColorBrush(WpfColor.FromRgb(17, 17, 17));
        editor.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0, 120, 215));
        editor.BorderThickness = new Thickness(1);
        editor.CaretBrush = editor.Foreground;
        editor.SelectionBrush = new SolidColorBrush(WpfColor.FromRgb(0, 120, 215));
        editor.SelectionOpacity = SelectionOpacity;
        editor.TextAlignment = TextAlignment.Left;
        editor.TextWrapping = TextWrapping.NoWrap;
        editor.HorizontalAlignment = WpfHorizontalAlignment.Center;
        editor.VerticalAlignment = VerticalAlignment.Top;
        editor.HorizontalContentAlignment = WpfHorizontalAlignment.Stretch;
        editor.VerticalContentAlignment = VerticalAlignment.Center;
        editor.FocusVisualStyle = null;
        editor.Width = MeasureWidth(editor, text);
    }

    internal static double MeasureWidth(TextBox editor, string text)
    {
        var typeface = new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
        var formatted = new FormattedText(
            text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            WpfFlowDirection.LeftToRight,
            typeface,
            editor.FontSize,
            WpfBrushes.Black,
            VisualTreeHelper.GetDpi(editor).PixelsPerDip);
        return Math.Clamp(Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 6, MinimumWidth, MaximumWidth);
    }

    internal static double MeasureWrappedHeight(TextBox editor, string text, double width)
    {
        var typeface = new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(editor).PixelsPerDip;
        var availableWidth = Math.Max(1, width - 6);
        var totalLines = 0;
        var lineHeight = 0d;
        foreach (var logicalLine in (text ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var formatted = new FormattedText(
                string.IsNullOrEmpty(logicalLine) ? " " : logicalLine,
                CultureInfo.CurrentUICulture,
                WpfFlowDirection.LeftToRight,
                typeface,
                editor.FontSize,
                WpfBrushes.Black,
                pixelsPerDip);
            lineHeight = Math.Max(lineHeight, formatted.Height);
            totalLines += Math.Max(1, (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace / availableWidth));
        }
        return Math.Max(EditorHeight, Math.Ceiling(totalLines * lineHeight) + 5);
    }
}
