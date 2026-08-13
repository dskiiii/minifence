using System.Windows.Media;
using MiniFences.Models;

namespace MiniFences;

internal static class FenceAppearanceBrush
{
    internal static System.Windows.Media.Brush CreateHeaderBrush(FenceConfig config)
    {
        var start = ParseColor(config.HeaderColor, "#CC3F7FA8");
        if (!config.HeaderGradientEnabled)
        {
            return new SolidColorBrush(start);
        }

        var end = ParseColor(config.HeaderGradientColor, "#CC8E5BB7");
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new(start, 0),
                new(end, 1)
            },
            new System.Windows.Point(0, 0.5),
            new System.Windows.Point(1, 0.5));
    }

    internal static System.Windows.Media.Color ParseColor(string? value, string fallback)
    {
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value ?? fallback);
        }
        catch
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback);
        }
    }
}
