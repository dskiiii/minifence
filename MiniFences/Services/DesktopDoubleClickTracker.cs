namespace MiniFences.Services;

internal sealed class DesktopDoubleClickTracker
{
    private const int DoubleClickMilliseconds = 500;
    private const int DoubleClickMaxDistance = 6;

    private long _lastClickTicks;
    private int _lastX;
    private int _lastY;

    public bool RegisterClick(bool isEligibleDesktopBlank, int x, int y, long ticks)
    {
        if (!isEligibleDesktopBlank)
        {
            Reset();
            return false;
        }

        var elapsed = ticks - _lastClickTicks;
        var dx = Math.Abs(x - _lastX);
        var dy = Math.Abs(y - _lastY);
        if (_lastClickTicks > 0 &&
            elapsed >= 0 &&
            elapsed <= DoubleClickMilliseconds &&
            dx <= DoubleClickMaxDistance &&
            dy <= DoubleClickMaxDistance)
        {
            Reset();
            return true;
        }

        _lastClickTicks = ticks;
        _lastX = x;
        _lastY = y;
        return false;
    }

    public void Reset()
    {
        _lastClickTicks = 0;
        _lastX = 0;
        _lastY = 0;
    }
}
