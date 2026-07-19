using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniFences.Models;
using MiniFences.Services;

namespace MiniFences;

public partial class DesktopLooseIconControl : System.Windows.Controls.UserControl
{
    private readonly FolderItemService _service = new();
    private readonly ShellContextMenuService _shellContextMenu = new();
    private readonly LocalizationService _localization;
    private System.Windows.Point _dragStart;
    private DispatcherTimer? _renameTimer;
    private bool _wasSelectedBeforeLeftDown;
    private bool _isInlineRenaming;
    private bool _isCommittingInlineRename;

    public DesktopLooseIconControl(FolderItem item, LocalizationService? localization = null)
    {
        Item = item;
        _localization = localization ?? new LocalizationService();
        DataContext = item;
        InitializeComponent();
    }

    public FolderItem Item { get; }
    public IReadOnlyList<string> DragPaths { get; set; } = [];
    public bool IsSelected { get; private set; }
    internal bool IsInlineRenamingForTesting =>
        _isInlineRenaming && RenameTextBox.Visibility == Visibility.Visible && NameText.Visibility == Visibility.Collapsed;
    public event Action<DesktopLooseIconControl, ModifierKeys>? SelectionRequested;
    public event EventHandler? ItemsChanged;
    public event EventHandler? DesktopItemDragStarted;
    public event EventHandler? DesktopItemDragEnded;
    internal Func<System.Drawing.Point, bool>? IsExplorerDesktopPointForDrag { get; set; }

    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        Chrome.Background = selected
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(110, 49, 130, 206))
            : System.Windows.Media.Brushes.Transparent;
        Chrome.BorderBrush = selected ? System.Windows.Media.Brushes.LightSkyBlue : System.Windows.Media.Brushes.Transparent;
    }

    public void CloseContextMenu(bool force = false, System.Windows.Point? screenPoint = null)
    {
        if (ContextMenu?.IsOpen != true) return;
        if (!force && screenPoint.HasValue && IsPointInside(ContextMenu, screenPoint.Value)) return;
        ContextMenu.IsOpen = false;
    }

    private void Control_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isInlineRenaming) return;
        _wasSelectedBeforeLeftDown = IsSelected;
        SelectionRequested?.Invoke(this, Keyboard.Modifiers);
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    private void Control_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsSelected) SelectionRequested?.Invoke(this, Keyboard.Modifiers);
        var window = Window.GetWindow(this);
        var owner = window == null ? IntPtr.Zero : new WindowInteropHelper(window).Handle;
        var cursor = System.Windows.Forms.Cursor.Position;
        var paths = DragPaths.Count > 0 ? DragPaths : [Item.FullPath];
        if (_shellContextMenu.Show(Item.FullPath, paths, owner, cursor, out var commandInvoked, out var commandVerb, out var error))
        {
            e.Handled = true;
            if (ShellContextMenuService.ShouldHandleCommandInHost(commandVerb))
            {
                RenameItem();
            }
            else if (commandInvoked)
            {
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            AppLogger.Log($"Falling back to MiniFences item menu: {error}");
        }
    }

    private void Control_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_wasSelectedBeforeLeftDown && IsSelected && !_isInlineRenaming && NameText.Visibility == Visibility.Visible)
        {
            var point = e.GetPosition(NameText);
            if (new Rect(new System.Windows.Point(0, 0), NameText.RenderSize).Contains(point))
            {
                ScheduleInlineRename();
                e.Handled = true;
            }
        }
        ReleaseMouseCapture();
    }

    private void Control_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = DragPaths.Count > 0 ? DragPaths.ToArray() : new[] { Item.FullPath };
        var data = new System.Windows.DataObject();
        DesktopDragData.Set(data, paths, looseIcon: true, Item.FullPath);
        ReleaseMouseCapture();
        AppLogger.Log($"Loose desktop icon drag started: {Item.FullPath}");
        System.Windows.QueryContinueDragEventHandler? desktopDropGuard = null;
        if (IsExplorerDesktopPointForDrag != null)
        {
            desktopDropGuard = (_, e) =>
            {
                var cursor = System.Windows.Forms.Cursor.Position;
                if (!DesktopDragData.ShouldCancelExplorerDesktopDrop(
                        e.KeyStates,
                        IsExplorerDesktopPointForDrag(cursor))) return;
                e.Action = System.Windows.DragAction.Cancel;
                e.Handled = true;
            };
            QueryContinueDrag += desktopDropGuard;
        }
        System.Windows.DragDropEffects result;
        DesktopItemDragStarted?.Invoke(this, EventArgs.Empty);
        try
        {
            result = System.Windows.DragDrop.DoDragDrop(
                this,
                data,
                System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Link);
        }
        finally
        {
            if (desktopDropGuard != null) QueryContinueDrag -= desktopDropGuard;
            DesktopItemDragEnded?.Invoke(this, EventArgs.Empty);
        }
        AppLogger.Log($"Loose desktop icon drag completed. Result={result}; Path={Item.FullPath}");
    }

    private static bool IsPointInside(FrameworkElement element, System.Windows.Point screenPoint)
    {
        try
        {
            var local = element.PointFromScreen(screenPoint);
            return new Rect(0, 0, element.ActualWidth, element.ActualHeight).Contains(local);
        }
        catch { return false; }
    }

    private void Control_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CancelPendingInlineRename();
        if (_isInlineRenaming) return;
        Open();
        e.Handled = true;
    }

    private void OpenMenuItem_Click(object sender, RoutedEventArgs e) => Open();

    private void Open()
    {
        if (!_service.TryOpen(Item, out var error))
            System.Windows.MessageBox.Show(error, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_service.TryShowInExplorer(Item, out var error))
            System.Windows.MessageBox.Show(error, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RenameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RenameItem();
    }

    private void RenameItem()
    {
        BeginInlineRename();
    }

    private void ScheduleInlineRename()
    {
        CancelPendingInlineRename();
        _renameTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 50)
        };
        _renameTimer.Tick += (_, _) =>
        {
            CancelPendingInlineRename();
            if (IsSelected && Mouse.LeftButton == MouseButtonState.Released) BeginInlineRename();
        };
        _renameTimer.Start();
    }

    private void BeginInlineRename()
    {
        CancelPendingInlineRename();
        if (_isInlineRenaming) return;
        _isInlineRenaming = true;
        RenameTextBox.Text = Item.Name;
        InlineRenameAppearance.Apply(RenameTextBox, Item.Name);
        NameText.Visibility = Visibility.Collapsed;
        RenameTextBox.Visibility = Visibility.Visible;
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.FocusInlineRenameEditor(RenameTextBox);
        }
        else
        {
            RenameTextBox.Focus();
            RenameTextBox.SelectAll();
        }
    }

    internal void BeginInlineRenameForTesting() => BeginInlineRename();
    internal void EndInlineRenameForTesting() => EndInlineRename();

    private void RenameTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitInlineRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndInlineRename();
            e.Handled = true;
        }
    }

    private void RenameTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_isInlineRenaming) CommitInlineRename();
    }

    internal bool CommitInlineRenameIfPointerOutside(System.Windows.Point screenPoint)
    {
        if (!_isInlineRenaming) return false;

        try
        {
            var localPoint = RenameTextBox.PointFromScreen(screenPoint);
            if (new Rect(new System.Windows.Point(0, 0), RenameTextBox.RenderSize).Contains(localPoint))
            {
                return false;
            }
        }
        catch
        {
            // A detached editor cannot contain the current pointer.
        }

        CommitInlineRename();
        return true;
    }

    private void CommitInlineRename()
    {
        if (!_isInlineRenaming || _isCommittingInlineRename) return;
        _isCommittingInlineRename = true;
        try
        {
            var newName = RenameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                System.Windows.MessageBox.Show(_localization.T("ItemNameCannotBeEmpty"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                RenameTextBox.Focus();
                return;
            }

            if (!_service.TryRenameItem(Item, newName, out _, out var error))
            {
                System.Windows.MessageBox.Show(error, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                RenameTextBox.Focus();
                RenameTextBox.SelectAll();
                return;
            }

            EndInlineRename();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _isCommittingInlineRename = false;
        }
    }

    private void EndInlineRename()
    {
        _isInlineRenaming = false;
        RenameTextBox.Visibility = Visibility.Collapsed;
        NameText.Visibility = Visibility.Visible;
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ReleaseInlineRenameEditor(RenameTextBox);
        }
    }

    private void CancelPendingInlineRename()
    {
        _renameTimer?.Stop();
        _renameTimer = null;
    }

    private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                $"Move '{Item.Name}' to the Recycle Bin?",
                "MiniFences",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_service.TryDeleteItem(Item, out var error))
            System.Windows.MessageBox.Show(error, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
