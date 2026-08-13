using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MiniFences;

/// <summary>
/// Destination feedback is deliberately isolated from DragFeedbackWindow.
/// Folder hit-testing may show/hide this HWND without ever touching the HWND
/// that owns the dragged icon and filename.
/// </summary>
internal sealed class DragTargetHintWindow : Window
{
    private readonly TextBlock _text;
    private CancellationTokenSource? _trackingCancellation;
    private volatile int _offsetX = -50;
    private volatile int _offsetY = 12;

    internal DragTargetHintWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        IsHitTestVisible = false;
        Focusable = false;

        var arrow = new Path
        {
            Data = Geometry.Parse("M 1,7.5 L 12,7.5 M 8,3.5 L 12,7.5 L 8,11.5"),
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(61, 129, 196)),
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Square,
            StrokeEndLineCap = PenLineCap.Square,
            StrokeLineJoin = PenLineJoin.Miter,
            Width = 13,
            Height = 15,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true
        };
        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 94, 130)),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 190,
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(arrow);
        row.Children.Add(_text);
        Content = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(248, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 181, 190)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 8, 2),
            Child = row,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        SourceInitialized += (_, _) => ApplyNonActivatingStyles();
    }

    internal string? TargetText => string.IsNullOrWhiteSpace(_text.Text) ? null : _text.Text;
    internal IntPtr NativeHandleForTesting => new WindowInteropHelper(this).Handle;

    internal void ShowTarget(string text)
    {
        _text.Text = text;
        if (!IsVisible) Show();
        UpdateLayout();
        UpdatePosition();
        StartTracking();
    }

    internal void HideTarget()
    {
        StopTracking();
        if (IsVisible) Hide();
        _text.Text = string.Empty;
    }

    internal void UpdatePosition()
    {
        if (!IsVisible || !GetCursorPos(out var cursor)) return;
        UpdatePosition(new System.Drawing.Point(cursor.X, cursor.Y));
    }

    internal void UpdatePosition(System.Drawing.Point cursor)
    {
        if (!IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var dpi = GetDpiForWindow(handle);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96d;
        var x = cursor.X + (int)Math.Round(50 * scale);
        var y = cursor.Y - (int)Math.Round(Math.Max(12, ActualHeight * scale / 2));
        _offsetX = cursor.X - x;
        _offsetY = cursor.Y - y;
        SetWindowPos(handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void StartTracking()
    {
        if (_trackingCancellation is { IsCancellationRequested: false }) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var cancellation = new CancellationTokenSource();
        _trackingCancellation = cancellation;
        _ = Task.Factory.StartNew(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (GetCursorPos(out var cursor))
                    SetWindowPos(handle, HwndTopmost, cursor.X - _offsetX, cursor.Y - _offsetY,
                        0, 0, SwpNoSize | SwpNoActivate | SwpShowWindow);
                if (cancellation.Token.WaitHandle.WaitOne(8)) break;
            }
        }, cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void StopTracking()
    {
        var cancellation = Interlocked.Exchange(ref _trackingCancellation, null);
        cancellation?.Cancel();
    }

    private void ApplyNonActivatingStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles | WsExToolWindow | WsExNoActivate | WsExTransparent));
    }

    protected override void OnClosed(EventArgs e)
    {
        StopTracking();
        base.OnClosed(e);
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80L;
    private const long WsExTransparent = 0x20L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));
    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    [DllImport("User32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("User32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("User32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("User32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong32(IntPtr window, int index);
    [DllImport("User32.dll", EntryPoint = "GetWindowLongPtr")] private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    [DllImport("User32.dll", EntryPoint = "SetWindowLong")] private static extern int SetWindowLong32(IntPtr window, int index, int value);
    [DllImport("User32.dll", EntryPoint = "SetWindowLongPtr")] private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
}
