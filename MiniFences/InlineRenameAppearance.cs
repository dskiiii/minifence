using System.Globalization;
using System.IO;
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
    internal const double WrappedEditorWidth = 82;
    internal const double MaximumWidth = 96;
    internal const double EditorHeight = 20;
    internal const double SelectionOpacity = 0.45;

    internal static void Apply(TextBox editor, string text)
    {
        // Match the desktop label exactly. Falling back to Segoe UI changed
        // glyph widths for Chinese text, so a name that fitted on one line in
        // its icon wrapped differently as soon as rename started.
        editor.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        editor.FontSize = 12;
        editor.FontStyle = FontStyles.Normal;
        editor.FontWeight = FontWeights.Normal;
        editor.FontStretch = FontStretches.Normal;
        editor.MinWidth = MinimumWidth;
        editor.MaxWidth = MaximumWidth;
        editor.Height = EditorHeight;
        // Border + padding leave the same 76-DIP text column used by the
        // normal desktop label when the editor reaches MaximumWidth.
        editor.Padding = new Thickness(1, 0, 1, 0);
        editor.Background = WpfBrushes.White;
        editor.Foreground = new SolidColorBrush(WpfColor.FromRgb(17, 17, 17));
        editor.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(0, 120, 215));
        editor.BorderThickness = new Thickness(1);
        editor.CaretBrush = editor.Foreground;
        editor.SelectionBrush = new SolidColorBrush(WpfColor.FromRgb(0, 120, 215));
        editor.SelectionOpacity = SelectionOpacity;
        editor.TextAlignment = TextAlignment.Center;
        editor.TextWrapping = TextWrapping.NoWrap;
        editor.HorizontalAlignment = WpfHorizontalAlignment.Center;
        editor.VerticalAlignment = VerticalAlignment.Top;
        editor.HorizontalContentAlignment = WpfHorizontalAlignment.Center;
        editor.VerticalContentAlignment = VerticalAlignment.Center;
        editor.FocusVisualStyle = null;
        editor.Width = GetEditorWidth(editor, text, 76);
    }

    internal static double GetEditorWidth(TextBox editor, string text, double normalLabelWidth)
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
        var textWidth = formatted.WidthIncludingTrailingWhitespace;
        if (textWidth > normalLabelWidth)
        {
            // The normal label already wraps; preserve its existing column.
            return WrappedEditorWidth;
        }

        // Compensate for the themed TextBox border and internal ScrollViewer
        // only when the normal label displays this name on one line.
        return Math.Clamp(Math.Ceiling(textWidth) + 14, MinimumWidth, MaximumWidth);
    }

    internal static double MeasureWrappedHeight(TextBox editor, string text, double width)
    {
        var typeface = new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(editor).PixelsPerDip;
        var availableWidth = Math.Max(1, width - 6);
        var totalHeight = 0d;
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
            var unwrappedWidth = formatted.WidthIncludingTrailingWhitespace;
            var singleLineHeight = formatted.Height;
            formatted.MaxTextWidth = availableWidth;
            var widthBasedHeight = Math.Max(1, Math.Ceiling(unwrappedWidth / availableWidth)) * singleLineHeight;
            totalHeight += Math.Max(formatted.Height, widthBasedHeight);
        }
        return Math.Max(EditorHeight, Math.Ceiling(totalHeight) + 5);
    }

    internal static int GetInitialSelectionLength(string path, string displayedName)
    {
        if (string.IsNullOrEmpty(displayedName) || Directory.Exists(path)) return displayedName?.Length ?? 0;
        var extension = Path.GetExtension(path);
        return !string.IsNullOrEmpty(extension) && displayedName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? Math.Max(0, displayedName.Length - extension.Length)
            : displayedName.Length;
    }
}
