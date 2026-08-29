using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace MiniFences;

/// <summary>
/// A single non-activating drag image that is positioned in physical screen
/// pixels. Keeping it outside the Explorer-hosted desktop HWND avoids both
/// DPI virtualization errors and Show/Hide competition between routed drag
/// handlers inside a Fence.
/// </summary>
internal sealed class DragFeedbackWindow : Window
{
    private const double HorizontalPaddingDips = 0;
    private const double IconSizeDips = 48;
    // Desktop labels are wider than the glyph itself. Keeping the drag label at
    // the item-cell width prevents names from being squeezed into an unnatural
    // icon-width column while preserving the 48-DIP image.
    internal const double LabelWidthDips = 86;
    internal const double SourceVisualWidthDips = LabelWidthDips;
    internal const double IconCenterYDips = 1 + IconSizeDips / 2;
    private readonly System.Windows.Controls.Image _icon;
    private readonly TextBlock _text;
    private readonly Border _textBadge;
    private readonly TextBlock _dropTargetText;
    private readonly Border _dropTargetBadge;
    private readonly FrameworkElement _dropArrow;
    private bool _targetTextExpanded;
    private bool _sourceInitialized;
    private IntPtr _cachedMonitor;
    private uint _cachedDpi;
    private int _cachedWidth;
    private int _cachedHeight;
    private volatile int _trackingOffsetX;
    private volatile int _trackingOffsetY;
    private CancellationTokenSource? _trackingCancellation;
    private Task? _trackingTask;

    internal DragFeedbackWindow()
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
        WindowStartupLocation = WindowStartupLocation.Manual;
        // The first native Show may precede SourceInitialized positioning by a
        // paint. Start off-screen so a newly created feedback HWND can never
        // flash at WPF's default coordinates.
        Left = -32000;
        Top = -32000;

        _icon = new System.Windows.Controls.Image
        {
            Width = IconSizeDips,
            Height = IconSizeDips,
            Margin = new Thickness(0),
            Stretch = Stretch.Uniform,
            Opacity = 0.94,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            FontWeight = FontWeights.Normal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
            LineHeight = 15,
            Width = LabelWidthDips,
            Height = 30
        };
        TextOptions.SetTextFormattingMode(_text, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(_text, TextRenderingMode.Grayscale);
        // Match Explorer desktop labels: white text remains readable over
        // white windows because a compact dark shadow outlines every glyph.
        _text.Effect = new DropShadowEffect
        {
            Color = System.Windows.Media.Colors.Black,
            BlurRadius = 2,
            ShadowDepth = 1,
            Direction = 315,
            Opacity = 0.95,
            RenderingBias = RenderingBias.Quality
        };

        _textBadge = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _text
        };
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        row.Children.Add(_icon);
        row.Children.Add(_textBadge);
        _dropTargetText = new TextBlock
        {
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 94, 130)),
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        // A font glyph follows its font baseline and looks lower than adjacent
        // CJK text. Draw around an explicit geometric midline instead.
        _dropArrow = new System.Windows.Shapes.Path
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
        var dropRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        dropRow.Children.Add(_dropArrow);
        dropRow.Children.Add(_dropTargetText);
        _dropTargetBadge = new Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(248, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(174, 181, 190)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 8, 2),
            Margin = new Thickness(5, 20, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Child = dropRow
        };
        var combined = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top
        };
        combined.Children.Add(row);
        combined.Children.Add(_dropTargetBadge);
        Content = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(HorizontalPaddingDips, 1, HorizontalPaddingDips, 1),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = combined
        };

        SourceInitialized += (_, _) =>
        {
            _sourceInitialized = true;
            ApplyNonActivatingStyles();
            UpdatePosition();
        };
    }

    internal ImageSource? IconSource => _icon.Source;
    internal string? TargetText => string.IsNullOrWhiteSpace(_text.Text) ? null : _text.Text;
    internal string? DropTargetText => _dropTargetBadge.Visibility == Visibility.Visible ? _dropTargetText.Text : null;
    internal bool HasSingleCombinedFeedbackVisualForTesting =>
        ReferenceEquals(_dropTargetBadge.Parent, ((Content as Border)?.Child as StackPanel)) &&
        _icon.Visibility == Visibility.Visible && DropTargetText != null;
    internal bool IsSourceNameVisibleWithDropTargetForTesting =>
        DropTargetText != null && TargetText != null && _textBadge.Visibility == Visibility.Visible;
    internal bool UsesGeometricallyCenteredDropArrowForTesting =>
        _dropArrow is System.Windows.Shapes.Path &&
        _dropArrow.Height == 15 &&
        _dropArrow.VerticalAlignment == VerticalAlignment.Center;
    internal bool IsSourceIconVisibleForTesting =>
        _icon.Source != null && _icon.Visibility == Visibility.Visible;
    internal double FeedbackContentWidthForTesting => ActualWidth;
    internal IntPtr NativeHandleForTesting => new WindowInteropHelper(this).Handle;
    internal bool NativeBoundsCoverContentForTesting
    {
        get
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var bounds)) return false;
            var dpi = GetDpiForWindow(handle);
            if (dpi == 0) dpi = 96;
            var scale = dpi / 96d;
            var requiredWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
            var requiredHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
            return bounds.Right - bounds.Left >= requiredWidth &&
                   bounds.Bottom - bounds.Top >= requiredHeight;
        }
    }

    internal void SetIcon(ImageSource? source)
    {
        if (ReferenceEquals(_icon.Source, source)) return;
        _icon.Source = source;
        _icon.Visibility = source == null ? Visibility.Collapsed : Visibility.Visible;
        InvalidateNativeSize();
    }

    internal void SetTargetText(string? text, bool expanded = false, double textWidth = LabelWidthDips)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? null : text;
        if (string.Equals(TargetText, normalized, StringComparison.Ordinal) &&
            _targetTextExpanded == expanded) return;
        _targetTextExpanded = expanded;
        // The source control decides the exact label state. Do not format it a
        // second time here or dragging becomes a third filename layout.
        _text.Text = normalized ?? string.Empty;
        _text.Width = Math.Max(1, textWidth);
        _text.TextWrapping = expanded ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _text.Height = expanded ? double.NaN : 30;
        _text.MaxHeight = expanded ? double.PositiveInfinity : 30;
        _textBadge.Visibility = normalized == null ? Visibility.Collapsed : Visibility.Visible;
        InvalidateNativeSize();
    }

    internal void SetDropTargetText(string? text)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? null : text;
        if (string.Equals(DropTargetText, normalized, StringComparison.Ordinal)) return;
        _dropTargetText.Text = normalized ?? string.Empty;
        _dropTargetBadge.Visibility = normalized == null ? Visibility.Collapsed : Visibility.Visible;
        // A destination is supplementary feedback. It must never replace the
        // dragged item's filename; leaving the target removes only this badge.
        _textBadge.Visibility = TargetText == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateLayout();
        InvalidateNativeSize();
        UpdatePosition();
    }

    internal void EnsureShown()
    {
        if (!IsVisible)
        {
            // A reused HWND remembers the position where the previous drag was
            // cancelled. Move it to the current pointer while it is still
            // hidden, then show it, so that stale position cannot paint for a
            // frame at the beginning of the next drag.
            UpdateLayout();
            if (_sourceInitialized && GetCursorPos(out var cursor))
                UpdatePosition(new System.Drawing.Point(cursor.X, cursor.Y), requireVisible: false, showWindow: false);
            Show();
            UpdateLayout();
            InvalidateNativeSize();
        }
        UpdatePosition();
        StartRealtimeTracking();
    }

    /// <summary>
    /// OLE DoDragDrop runs a modal message loop which can starve WPF input and
    /// Dispatcher callbacks. Track the physical cursor from a dedicated native
    /// thread so feedback motion is independent of the UI thread's cadence.
    /// </summary>
    private void StartRealtimeTracking()
    {
        if (_trackingCancellation is { IsCancellationRequested: false }) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var cancellation = new CancellationTokenSource();
        _trackingCancellation = cancellation;
        _trackingTask = Task.Factory.StartNew(
            () =>
            {
                try { RealtimeTrackingLoop(handle, cancellation.Token); }
                finally { cancellation.Dispose(); }
            },
            cancellation.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    internal void StopRealtimeTracking()
    {
        var cancellation = Interlocked.Exchange(ref _trackingCancellation, null);
        var task = Interlocked.Exchange(ref _trackingTask, null);
        if (cancellation == null) return;
        cancellation.Cancel();
        // The loop waits at most 8ms between cursor samples. Join it before the
        // WPF window is hidden so an in-flight native move cannot race with the
        // next drag session.
        try { task?.Wait(50); }
        catch (AggregateException) { }
    }

    private void RealtimeTrackingLoop(IntPtr handle, CancellationToken cancellationToken)
    {
        timeBeginPeriod(1);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (GetCursorPos(out var cursor))
                {
                    SetWindowPos(
                        handle,
                        HwndTopmost,
                        cursor.X - _trackingOffsetX,
                        cursor.Y - _trackingOffsetY,
                        0,
                        0,
                        RealtimeTrackingWindowFlags);
                }
                if (cancellationToken.WaitHandle.WaitOne(8)) break;
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    internal void UpdatePosition()
    {
        if (!_sourceInitialized || !IsVisible) return;
        if (!GetCursorPos(out var cursor)) return;
        UpdatePosition(new System.Drawing.Point(cursor.X, cursor.Y));
    }

    internal void UpdatePosition(System.Drawing.Point cursor)
    {
        UpdatePosition(cursor, requireVisible: true, showWindow: true);
    }

    private void UpdatePosition(System.Drawing.Point cursor, bool requireVisible, bool showWindow)
    {
        if (!_sourceInitialized || (requireVisible && !IsVisible)) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var nativeCursor = new NativePoint { X = cursor.X, Y = cursor.Y };
        var monitor = MonitorFromPoint(nativeCursor, MonitorDefaultToNearest);
        if (_cachedDpi == 0 || monitor != _cachedMonitor)
        {
            _cachedMonitor = monitor;
            _cachedDpi = GetMonitorDpi(monitor);
            if (_cachedDpi == 0) _cachedDpi = GetDpiForWindow(handle);
            if (_cachedDpi == 0) _cachedDpi = 96;
            InvalidateNativeSize();
        }
        if (_cachedWidth <= 0 || _cachedHeight <= 0)
        {
            var scale = _cachedDpi / 96d;
            _cachedWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth * scale));
            _cachedHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight * scale));
        }
        var placement = CalculatePhysicalPlacement(
            new System.Windows.Point(cursor.X, cursor.Y),
            new System.Windows.Size(_cachedWidth, _cachedHeight),
            _cachedDpi,
            _icon.Visibility == Visibility.Visible);
        _trackingOffsetX = cursor.X - (int)Math.Round(placement.X);
        _trackingOffsetY = cursor.Y - (int)Math.Round(placement.Y);
        SetWindowPos(
            handle,
            HwndTopmost,
            (int)Math.Round(placement.X),
            (int)Math.Round(placement.Y),
            _cachedWidth,
            _cachedHeight,
            SwpNoActivate | (showWindow ? SwpShowWindow : 0));
    }

    internal static bool RealtimeTrackingCanShowWindowForTesting =>
        (RealtimeTrackingWindowFlags & SwpShowWindow) != 0;

    private void InvalidateNativeSize()
    {
        _cachedWidth = 0;
        _cachedHeight = 0;
    }

    internal static System.Windows.Point CalculatePhysicalPlacement(
        System.Windows.Point cursorPhysical,
        System.Windows.Size windowPhysicalSize,
        uint dpi,
        bool iconVisible)
    {
        var scale = Math.Max(1, dpi) / 96d;
        var iconCenterY = iconVisible
            ? IconCenterYDips * scale
            : 0;
        return new System.Windows.Point(
            cursorPhysical.X - SourceVisualWidthDips * scale / 2,
            cursorPhysical.Y - iconCenterY);
    }

    private void ApplyNonActivatingStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        styles |= WsExToolWindow | WsExNoActivate | WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles));
    }

    private static uint GetMonitorDpi(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return 0;
        try
        {
            return GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0
                ? dpiX
                : 0;
        }
        catch (DllNotFoundException)
        {
            return 0;
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : new IntPtr(SetWindowLong32(window, index, value.ToInt32()));

    private static readonly IntPtr HwndTopmost = new(-1);
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint RealtimeTrackingWindowFlags = SwpNoSize | SwpNoActivate;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdtEffectiveDpi = 0;

    [DllImport("User32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("User32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("User32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("User32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("User32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    [DllImport("User32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("User32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("User32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr window, int index, int value);

    [DllImport("User32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
