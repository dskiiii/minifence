using MiniFences.Models;

namespace MiniFences.Services;

public static class FenceLayoutService
{
    public static (double Left, double Top) FindAvailablePosition(
        IEnumerable<FenceConfig> existingFences,
        FenceConfig newFence,
        double workspaceWidth,
        double workspaceHeight)
    {
        const double margin = 24;
        const double gap = 16;
        const double step = 32;

        var width = Math.Max(240, newFence.Width);
        var height = Math.Max(180, newFence.Height);
        var maxLeft = Math.Max(margin, workspaceWidth - width - margin);
        var maxTop = Math.Max(margin, workspaceHeight - height - margin);
        var occupied = existingFences
            .Select(ToRect)
            .ToList();

        for (var top = margin; top <= maxTop; top += step)
        {
            for (var left = margin; left <= maxLeft; left += step)
            {
                var candidate = new Rect(left, top, width, height);
                if (occupied.All(rect => !Inflate(rect, gap).IntersectsWith(candidate)))
                {
                    return (left, top);
                }
            }
        }

        return (
            Math.Clamp(newFence.Left, 0, Math.Max(0, workspaceWidth - width)),
            Math.Clamp(newFence.Top, 0, Math.Max(0, workspaceHeight - height)));
    }

    private static Rect ToRect(FenceConfig fence)
    {
        var width = Math.Max(240, fence.Width);
        var height = Math.Max(180, fence.Height);
        return new Rect(fence.Left, fence.Top, width, height);
    }

    private static Rect Inflate(Rect rect, double amount)
    {
        return new Rect(rect.Left - amount, rect.Top - amount, rect.Width + amount * 2, rect.Height + amount * 2);
    }

    private readonly record struct Rect(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;

        public bool IntersectsWith(Rect other)
        {
            return other.Left < Right &&
                   other.Right > Left &&
                   other.Top < Bottom &&
                   other.Bottom > Top;
        }
    }
}
