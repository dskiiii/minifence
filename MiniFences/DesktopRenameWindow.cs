using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MiniFences;

/// <summary>
/// Hosts desktop-icon renaming in its own top-level HWND. The MiniFences
/// desktop surface is parented into Explorer and must remain non-activating;
/// temporarily activating that child HWND can leave desktop input stuck when
/// focus moves to another application.
/// </summary>
internal sealed class DesktopRenameWindow : Window
{
    private readonly System.Windows.Int32Rect _physicalBounds;
    private readonly string _initialText;
    private readonly int _initialSelectionLength;
    private readonly long _createdAtTicks = Environment.TickCount64;
    private bool _commitPending;
    private bool _closing;
    private int _focusAttempt;

    internal DesktopRenameWindow(
        string text,
        System.Windows.Int32Rect physicalBounds,
        double logicalWidth,
        double logicalHeight,
        int? initialSelectionLength = null)
    {
        _physicalBounds = physicalBounds;
        _initialText = text;
        _initialSelectionLength = Math.Clamp(initialSelectionLength ?? text.Length, 0, text.Length);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        // The Fence surface is parented into Explorer. A normal top-level
        // editor can remain visually composited above it while pointer input
        // is still delivered to the Explorer child underneath. Keep the tiny
        // editor topmost only for its short lifetime so its visible TextBox is
        // also the real mouse target.
        Topmost = true;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Max(InlineRenameAppearance.MinimumWidth, logicalWidth);
        Height = Math.Max(InlineRenameAppearance.EditorHeight, logicalHeight);

        Editor = new System.Windows.Controls.TextBox
        {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        InlineRenameAppearance.Apply(Editor, text);
        Editor.TextWrapping = TextWrapping.Wrap;
        Editor.Width = double.NaN;
        Editor.Height = double.NaN;
        Editor.MinHeight = InlineRenameAppearance.EditorHeight;
        Editor.VerticalAlignment = VerticalAlignment.Stretch;
        Editor.VerticalContentAlignment = VerticalAlignment.Center;
        Editor.KeyDown += Editor_KeyDown;
        Editor.PreviewMouseLeftButtonDown += (_, _) => Dispatcher.BeginInvoke(
            () => FocusEditor(preserveSelection: true), DispatcherPriority.Input);
        Editor.TextChanged += (_, _) =>
        {
            ResizeToContent();
            MiniFences.Services.AppLogger.Log(
                $"{DiagnosticContext} rename text changed. TextLength={Editor.Text.Length}; SelectionStart={Editor.SelectionStart}; SelectionLength={Editor.SelectionLength}.");
        };
        Content = Editor;

        ResizeToContent();

        SourceInitialized += (_, _) => PositionNativeWindow();
        Loaded += (_, _) => BeginFocusHandshake();
        // Do not commit merely because this HWND deactivates. Chinese IME
        // candidate windows temporarily take activation while the user is
        // still composing text; treating that as an outside click closes the
        // rename editor before the composition reaches the TextBox. Enter and
        // the global pointer-outside path remain explicit commit routes.
    }

    internal System.Windows.Controls.TextBox Editor { get; }
    internal Func<string, bool>? TryCommitRequested { get; set; }
    internal Action? CancelRequested { get; set; }
    internal string DiagnosticContext { get; set; } = "Desktop";

    internal bool IsCenteredForTesting =>
        Editor.TextAlignment == TextAlignment.Center &&
        Editor.HorizontalContentAlignment == System.Windows.HorizontalAlignment.Center &&
        Editor.VerticalContentAlignment == VerticalAlignment.Center;

    internal bool CommitsOnWindowDeactivationForTesting => false;

    internal bool ContainsPhysicalScreenPoint(System.Windows.Point point)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
        {
            return point.X >= rect.Left && point.X < rect.Right &&
                   point.Y >= rect.Top && point.Y < rect.Bottom;
        }

        return point.X >= _physicalBounds.X && point.X < _physicalBounds.X + _physicalBounds.Width &&
               point.Y >= _physicalBounds.Y && point.Y < _physicalBounds.Y + _physicalBounds.Height;
    }

    internal bool RequestCommitIfPointerOutside(System.Windows.Point physicalScreenPoint)
    {
        if (_closing || !IsVisible || ContainsPhysicalScreenPoint(physicalScreenPoint)) return false;
        RequestCommit();
        return true;
    }

    internal bool ExistedAtMouseEvent(long mouseEventTicks) =>
        unchecked(mouseEventTicks - _createdAtTicks) >= 0;

    internal void CloseWithoutCommit()
    {
        if (_closing) return;
        MiniFences.Services.AppLogger.Log(
            $"{DiagnosticContext} rename closed without commit by owner. TextLength={Editor.Text.Length}.");
        _closing = true;
        Close();
    }

    private void Editor_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        MiniFences.Services.AppLogger.Log(
            $"{DiagnosticContext} rename key. Key={e.Key}; TextLength={Editor.Text.Length}; SelectionStart={Editor.SelectionStart}; SelectionLength={Editor.SelectionLength}.");
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            RequestCommit();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            _closing = true;
            CancelRequested?.Invoke();
            if (IsVisible) Close();
            e.Handled = true;
        }
    }

    private void RequestCommit()
    {
        if (_closing || _commitPending) return;
        MiniFences.Services.AppLogger.Log($"{DiagnosticContext} rename commit requested. Text='{Editor.Text}'.");
        if (string.Equals(Editor.Text.Trim(), _initialText.Trim(), StringComparison.Ordinal))
        {
            _closing = true;
            MiniFences.Services.AppLogger.Log("Desktop rename dismissed without changes.");
            if (IsVisible) Close();
            CancelRequested?.Invoke();
            return;
        }
        _commitPending = true;
        try
        {
            if (TryCommitRequested?.Invoke(Editor.Text) == true)
            {
                _closing = true;
                if (IsVisible) Close();
            }
            else
            {
                Dispatcher.BeginInvoke(() => FocusEditor(preserveSelection: false), DispatcherPriority.Input);
            }
        }
        finally
        {
            _commitPending = false;
        }
    }

    private void BeginFocusHandshake()
    {
        _focusAttempt = 0;
        FocusEditor(preserveSelection: false);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            if (_closing || !IsVisible || Editor.IsKeyboardFocusWithin || _focusAttempt >= 4)
            {
                timer.Stop();
                return;
            }
            FocusEditor(preserveSelection: false);
        };
        timer.Start();
    }

    private void FocusEditor(bool preserveSelection)
    {
        if (_closing || !IsVisible) return;
        _focusAttempt++;
        var handle = new WindowInteropHelper(this).Handle;
        var foreground = handle != IntPtr.Zero && SetForegroundWindow(handle);
        var activated = Activate();
        var focused = Editor.Focus();
        Keyboard.Focus(Editor);
        if (!preserveSelection && string.Equals(Editor.Text, _initialText, StringComparison.Ordinal))
            Editor.Select(0, _initialSelectionLength);
        MiniFences.Services.AppLogger.Log(
            $"Rename focus handshake. Attempt={_focusAttempt}; Foreground={foreground}; Activated={activated}; " +
            $"Focused={focused}; KeyboardFocusWithin={Editor.IsKeyboardFocusWithin}; ForegroundMatches={GetForegroundWindow() == handle}.");
    }

    private void ResizeToContent()
    {
        var editorWidth = Math.Max(InlineRenameAppearance.MinimumWidth, Width);
        Height = InlineRenameAppearance.MeasureWrappedHeight(Editor, Editor.Text, editorWidth);
    }

    private void PositionNativeWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowPos(
            handle,
            HwndTopmost,
            _physicalBounds.X,
            _physicalBounds.Y,
            0,
            0,
            SwpNoSize | SwpNoActivate);
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("User32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("User32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("User32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("User32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
