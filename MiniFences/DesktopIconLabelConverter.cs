using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MiniFences;

/// <summary>
/// Produces a deterministic two-line desktop label. WPF's TextTrimming and
/// TextWrapping combination is implementation-dependent and often truncates
/// the first line before wrapping, so the line break and final ellipsis are
/// calculated before the TextBlock renders.
/// </summary>
public sealed class DesktopIconLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = value as string ?? string.Empty;
        var width = parameter != null &&
                    double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 78;
        return FormatTwoLines(text, width, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Data.Binding.DoNothing;

    internal static string FormatTwoLines(string text, double width, CultureInfo? culture = null)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0 || Fits(text, width, culture)) return text;

        var firstLength = FittingPrefixLength(text, width, culture);
        var preferredBreak = FindPreferredBreak(text, firstLength);
        if (preferredBreak > 0) firstLength = preferredBreak;
        firstLength = Math.Max(1, firstLength);
        // A hyphen or underscore is part of the filename, not disposable wrap
        // whitespace. Removing it here made e.g. "MiniFences-source-0" render
        // as "MiniFences" / "source-0" even though the file was unchanged.
        var first = text[..firstLength].TrimEnd(' ');
        var remaining = text[firstLength..].TrimStart(' ');
        if (remaining.Length == 0) return first;
        if (Fits(remaining, width, culture)) return first + "\n" + remaining;

        const string ellipsis = "…";
        var secondLength = FittingPrefixLength(remaining + ellipsis, width, culture);
        secondLength = Math.Clamp(secondLength - 1, 1, remaining.Length);
        while (secondLength > 1 && !Fits(remaining[..secondLength].TrimEnd() + ellipsis, width, culture))
            secondLength--;
        return first + "\n" + remaining[..secondLength].TrimEnd() + ellipsis;
    }

    internal static string FormatAllLines(string text, double width, CultureInfo? culture = null)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return text;
        var lines = new List<string>();
        var remaining = text;
        while (remaining.Length > 0)
        {
            if (Fits(remaining, width, culture))
            {
                lines.Add(remaining);
                break;
            }
            var length = FittingPrefixLength(remaining, width, culture);
            var preferredBreak = FindPreferredBreak(remaining, length);
            if (preferredBreak > 0) length = preferredBreak;
            length = Math.Max(1, length);
            var line = remaining[..length].TrimEnd(' ');
            if (line.Length == 0) line = remaining[..length];
            lines.Add(line);
            remaining = remaining[length..].TrimStart(' ');
        }
        return string.Join("\n", lines);
    }

    private static int FindPreferredBreak(string text, int maximum)
    {
        for (var index = Math.Min(maximum, text.Length - 1); index >= Math.Max(1, maximum / 2); index--)
            if (char.IsWhiteSpace(text[index - 1]) || text[index - 1] is '-' or '_') return index;
        return maximum;
    }

    private static int FittingPrefixLength(string text, double width, CultureInfo? culture)
    {
        var low = 1;
        var high = text.Length;
        var best = 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (Fits(text[..middle], width, culture))
            {
                best = middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        return best;
    }

    private static bool Fits(string text, double width, CultureInfo? culture)
    {
        var formatted = new FormattedText(
            text,
            culture ?? CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                System.Windows.FontStyles.Normal,
                System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal),
            12,
            System.Windows.Media.Brushes.White,
            1.0);
        return formatted.WidthIncludingTrailingWhitespace <= width;
    }
}
