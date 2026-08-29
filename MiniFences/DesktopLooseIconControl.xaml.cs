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
    private bool _isDragging;
    private bool _dragGestureArmed;
    private bool _selectionActiveForRename;
    private bool _expandSelectedName;
    private DesktopRenameWindow? _desktopRenameWindow;

    public DesktopLooseIconControl(FolderItem item, LocalizationService? localization = null)
    {
        Item = item;
        _localization = localization ?? new LocalizationService();
        DataContext = item;
        InitializeComponent();
        Item.PropertyChanged += Item_PropertyChanged;
        UpdateDisplayedName();
        Unloaded += (_, _) =>
        {
            Item.PropertyChanged -= Item_PropertyChanged;
            if (_desktopRenameWindow != null) EndInlineRename();
        };
        if (FolderItemService.IsShellNamespacePath(item.FullPath))
        {
            // The Recycle Bin is a shell namespace item, but it is also a real
            // Shell drop target. Keep dropping enabled for it while retaining
            // the read-only behavior for This PC, Network, and other system icons.
            AllowDrop = FolderItemService.IsRecycleBinNamespacePath(item.FullPath);
            ShowInExplorerMenuItem.Visibility = Visibility.Collapsed;
            RenameItemMenuItem.Visibility = Visibility.Collapsed;
            DeleteItemMenuItem.Visibility = Visibility.Collapsed;
        }
    }

    public FolderItem Item { get; }
    public ActionHistoryService? ActionHistory { get; set; }
    public IReadOnlyList<string> DragPaths { get; set; } = [];
    public bool IsSelected { get; private set; }
    internal bool IsInlineRenamingForTesting =>
        _isInlineRenaming && RenameTextBox.Visibility == Visibility.Visible && NameText.Visibility == Visibility.Collapsed;
    internal bool IsNameExpandedForTesting =>
        IsSelected && double.IsNaN(Height) && NameText.TextWrapping == TextWrapping.Wrap &&
        NameText.TextTrimming == TextTrimming.None;
    internal bool IsCompactNameTwoLinesForTesting =>
        NameText.TextWrapping == TextWrapping.NoWrap &&
        NameText.TextTrimming == TextTrimming.None &&
        Math.Abs(NameText.Height - 30) < 0.01 && Math.Abs(NameText.LineHeight - 15) < 0.01;
    internal bool IsExpandedNameContainedForTesting =>
        Math.Abs(Width - 86) < 0.01 && Math.Abs(Chrome.Width - 86) < 0.01 &&
        Math.Abs(NameText.Width - 76) < 0.01 && NameRow.Height.IsAuto;
    internal void SetDraggingVisualForTesting(bool dragging) => SetDraggingVisual(dragging);
    internal static bool CanStartDragGesture(bool armed, MouseButtonState leftButton) =>
        armed && leftButton == MouseButtonState.Pressed;
    internal bool IsRenameEditorCompactForTesting =>
        RenameTextBox.TextWrapping == TextWrapping.Wrap &&
        !RenameTextBox.AcceptsReturn &&
        Math.Abs(RenameTextBox.Height - InlineRenameAppearance.MeasureWrappedHeight(
            RenameTextBox,
            RenameTextBox.Text,
            RenameTextBox.Width)) < 0.01;
    internal bool HasSelectionChromeForTesting =>
        IsSelected && Chrome.Background is System.Windows.Media.SolidColorBrush { Color.A: > 0 } &&
        Chrome.BorderBrush is System.Windows.Media.SolidColorBrush { Color.A: > 0 };
    internal bool HasIndependentRenameWindow =>
        _desktopRenameWindow is { IsVisible: true };
    internal void CancelActiveRenameForSettings()
    {
        CancelPendingInlineRename();
        if (_isInlineRenaming) EndInlineRename();
    }
    internal bool IsRenameEditorCenteredForTesting
    {
        get
        {
            UpdateLayout();
            var center = RenameTextBox.TranslatePoint(
                new System.Windows.Point(RenameTextBox.ActualWidth / 2, 0),
                this).X;
            return RenameTextBox.TextAlignment == TextAlignment.Center &&
                   RenameTextBox.HorizontalContentAlignment == System.Windows.HorizontalAlignment.Center &&
                   Math.Abs(center - ActualWidth / 2) < 0.75;
        }
    }
    public event Action<DesktopLooseIconControl, ModifierKeys>? SelectionRequested;
    public event Action<DesktopLooseIconControl, string, string>? ItemRenamed;
    public event EventHandler? ItemsChanged;
    public event EventHandler? DesktopItemDragStarted;
    public event EventHandler? DesktopItemDragEnded;
    internal Func<System.Drawing.Point, bool>? IsExplorerDesktopPointForDrag { get; set; }
    internal Func<System.Drawing.Point, bool>? IsMiniFencesSurfacePointForDrag { get; set; }

    public void SetSelected(bool selected, bool expandName = true)
    {
        IsSelected = selected;
        _expandSelectedName = selected && expandName;
        if (!selected) _selectionActiveForRename = false;
        ApplySelectionVisual();
    }

    internal void SetSelectionActiveForRename(bool active) =>
        _selectionActiveForRename = active && IsSelected;

    internal static bool WasActiveSelectionBeforePointerDown(bool selected, bool selectionActive) =>
        selected && selectionActive;

    private void SetDraggingVisual(bool dragging)
    {
        _isDragging = dragging;
        ApplySelectionVisual();
    }

    private void ApplySelectionVisual()
    {
        var showSelection = IsSelected;
        var expandName = showSelection && _expandSelectedName;
        // Expansion is a selection state, never a drag state. DoDragDrop and
        // Escape therefore leave this geometry exactly as it was at drag start.
        Height = expandName ? double.NaN : 92;
        NameText.TextWrapping = expandName ? TextWrapping.Wrap : TextWrapping.NoWrap;
        NameText.TextTrimming = TextTrimming.None;
        NameText.Height = expandName ? double.NaN : 30;
        NameText.MaxHeight = expandName ? double.PositiveInfinity : 30;
        Chrome.Width = 86;
        NameRow.Height = expandName ? GridLength.Auto : new GridLength(30);
        NameText.Width = 76;
        NameText.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        UpdateDisplayedName();
        // Keep selected desktop icons in the same layer as the other loose
        // icons. The label may expand, but it must never float above and mask
        // neighboring icons or a Fence.
        System.Windows.Controls.Panel.SetZIndex(this, 0);
        Chrome.Background = showSelection
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(72, 0, 120, 215))
            : System.Windows.Media.Brushes.Transparent;
        Chrome.BorderBrush = showSelection
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 80, 165, 235))
            : System.Windows.Media.Brushes.Transparent;
        InvalidateMeasure();
    }

    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FolderItem.Name)) UpdateDisplayedName();
    }

    private void UpdateDisplayedName() =>
        NameText.Text = IsSelected && _expandSelectedName
            ? DesktopIconLabelConverter.FormatAllLines(Item.Name, 76)
            : DesktopIconLabelConverter.FormatTwoLines(Item.Name, 76);

    public void CloseContextMenu(bool force = false, System.Windows.Point? screenPoint = null)
    {
        if (ContextMenu?.IsOpen != true) return;
        if (!force && screenPoint.HasValue && IsPointInside(ContextMenu, screenPoint.Value)) return;
        ContextMenu.IsOpen = false;
    }

    internal bool CommitIndependentRenameIfPointerOutside(System.Windows.Point physicalScreenPoint) =>
        _desktopRenameWindow?.RequestCommitIfPointerOutside(physicalScreenPoint) == true;

    private void Control_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isInlineRenaming) return;
        _wasSelectedBeforeLeftDown = WasActiveSelectionBeforePointerDown(IsSelected, _selectionActiveForRename);
        if (string.Equals(Environment.GetEnvironmentVariable("MINIFENCES_RENAME_TRACE"), "1", StringComparison.Ordinal))
            AppLogger.Log($"Rename pointer down. Selected={IsSelected}; Active={_selectionActiveForRename}; WasActive={_wasSelectedBeforeLeftDown}; Path={Item.FullPath}");
        SelectionRequested?.Invoke(this, Keyboard.Modifiers);
        _dragStart = e.GetPosition(this);
        _dragGestureArmed = true;
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
        _dragGestureArmed = false;
        if (!FolderItemService.IsShellNamespacePath(Item.FullPath) &&
            _wasSelectedBeforeLeftDown && IsSelected && !_isInlineRenaming && NameText.Visibility == Visibility.Visible)
        {
            var point = e.GetPosition(NameText);
            if (new Rect(new System.Windows.Point(0, 0), NameText.RenderSize).Contains(point))
            {
                if (string.Equals(Environment.GetEnvironmentVariable("MINIFENCES_RENAME_TRACE"), "1", StringComparison.Ordinal))
                    AppLogger.Log($"Rename scheduled from pointer up. Path={Item.FullPath}");
                ScheduleInlineRename();
                e.Handled = true;
            }
        }
        ReleaseMouseCapture();
    }

    private void Control_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!CanStartDragGesture(_dragGestureArmed, e.LeftButton)) return;
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = DragPaths.Count > 0 ? DragPaths.ToArray() : new[] { Item.FullPath };
        using var data = new ShellCompatibleDataObject();
        DesktopDragData.Set(data, paths, looseIcon: true, Item.FullPath);
        var dragIcon = Item.Icon ?? IconImage.Source ?? FolderItemService.GetGuaranteedPathIcon(Item.FullPath);
        var dragLabel = DesktopIconLabelConverter.FormatTwoLines(Item.Name, 76);
        var host = Window.GetWindow(this) as MainWindow;
        // Use the same stable source HWND as Fence items. Shell's layered drag
        // image is hidden when an embedded folder cell takes over as target on
        // this desktop host, while our independent window stays visible.
        host?.ShowDragSourceHint(
            dragIcon,
            dragLabel,
            expanded: false,
            textWidth: 76,
            pinUntilClear: true);
        AppLogger.Log("Independent MiniFences drag image initialized.");
        SetDraggingVisual(true);
        _dragGestureArmed = false;
        ReleaseMouseCapture();
        AppLogger.Log($"Loose desktop icon drag started: {Item.FullPath}");
        System.Windows.QueryContinueDragEventHandler? desktopDropGuard = null;
        System.Windows.QueryContinueDragEventHandler escapeFeedbackGuard = (_, e) =>
        {
            if (!e.EscapePressed) return;
            e.Action = System.Windows.DragAction.Cancel;
            e.Handled = true;
            host?.ClearDragHint();
        };
        QueryContinueDrag += escapeFeedbackGuard;
        if (IsExplorerDesktopPointForDrag != null)
        {
            desktopDropGuard = (_, e) =>
            {
                var cursor = System.Windows.Forms.Cursor.Position;
                var overMiniFencesSurface = IsMiniFencesSurfacePointForDrag?.Invoke(cursor) == true;
                if (!DesktopDragData.ShouldCancelExplorerDesktopDrop(
                        e.KeyStates,
                        IsExplorerDesktopPointForDrag(cursor),
                        overMiniFencesSurface)) return;
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
            QueryContinueDrag -= escapeFeedbackGuard;
            if (desktopDropGuard != null) QueryContinueDrag -= desktopDropGuard;
            DesktopItemDragEnded?.Invoke(this, EventArgs.Empty);
            SetDraggingVisual(false);
            host?.ClearDragHint();
        }
        AppLogger.Log($"Loose desktop icon drag completed. Result={result}; Path={Item.FullPath}");
    }

    private void Control_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        var host = Window.GetWindow(this) as MainWindow;
        // Recycle Bin is an unambiguous Shell move target. Claim it before
        // asking Explorer for delayed CF_HDROP data; otherwise the event can
        // bubble to the desktop surface and briefly/stickily become "Link".
        if (FolderItemService.IsRecycleBinNamespacePath(Item.FullPath))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            host?.ShowDragHint(Item.Name, e.Data);
            e.Handled = true;
            return;
        }
        if (!DesktopDragData.TryGetPaths(e.Data, out var paths))
        {
            host?.HideDragHint();
            return;
        }

        if (!System.IO.Directory.Exists(Item.FullPath) ||
            !FenceControl.IsStableFolderDropZone(e.GetPosition(this), RenderSize))
        {
            // The workspace remains a valid drop target. Do not collapse the
            // shared drag image while routed DragOver moves through an icon
            // cell; doing so made it alternate with the parent hint.
            host?.ClearDragTargetHint();
            return;
        }
        var canMoveIntoFolder = Item.Kind == "Folder" &&
                                 FenceControl.CanMoveIntoFolder(paths, Item.FullPath, validateFileSystem: false);
        var requestedEffect = DesktopDragData.GetRequestedDropEffect(e.Data, e.KeyStates, e.AllowedEffects);
        e.Effects = canMoveIntoFolder ? requestedEffect : System.Windows.DragDropEffects.None;
        if (canMoveIntoFolder && host != null) host.ShowDragHint(
            Item.Name,
            paths,
            e.Data,
            requestedEffect);
        else if (!canMoveIntoFolder) host?.ShowDragSourceHint(null);
        e.Handled = true;
    }

    internal static bool IsStableFolderDropZone(System.Windows.Point point, System.Windows.Size size) =>
        new Rect(0, 0, size.Width, size.Height).Contains(point);

    private void Control_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // Ignore template-level leave events, but clear our custom image as
        // soon as the pointer really exits MiniFences so Explorer's native
        // drag image can take over without a large+small duplicate.
        var host = Window.GetWindow(this) as MainWindow;
        host?.ClearDragTargetHint();
        if (host != null && !host.IsMiniFencesWindowAtScreenPoint(System.Windows.Forms.Cursor.Position))
            host.HideDragHint();
    }

    private async void Control_Drop(object sender, System.Windows.DragEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.HideDragHint();
        if (!DesktopDragData.TryGetPaths(e.Data, out var paths)) return;

        if (FolderItemService.IsRecycleBinNamespacePath(Item.FullPath))
        {
            DropToRecycleBin(e, paths);
            return;
        }

        if (!System.IO.Directory.Exists(Item.FullPath) ||
            !FenceControl.IsStableFolderDropZone(e.GetPosition(this), RenderSize)) return;
        e.Handled = true;
        if (!FenceControl.CanMoveIntoFolder(paths, Item.FullPath))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }

        var requestedEffect = DesktopDragData.GetRequestedDropEffect(e.Data, e.KeyStates, e.AllowedEffects);
        var operation = FolderItemService.GetTransferOperation(requestedEffect);
        if (operation is null)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }
        // Return the selected effect to OLE before doing potentially long disk
        // work. The global wait cursor makes an in-progress verified transfer
        // visible instead of leaving the desktop apparently frozen.
        e.Effects = requestedEffect;
        await Task.Yield();
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        FolderMoveResult result;
        try
        {
            result = ActionHistory != null
                ? await ActionHistory.ExecuteFileTransferAsync(
                    $"{FolderItemService.GetOperationDisplayName(operation.Value)}到文件夹“{Item.Name}”",
                    _service, paths, Item.FullPath, operation.Value)
                : await Task.Run(() => _service.TransferIntoFolder(paths, Item.FullPath, operation.Value));
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
        AppLogger.Log($"Loose folder icon drop completed. Destination={Item.FullPath}; Effect={requestedEffect}; Transferred={result.Moved}; Skipped={result.Skipped}; Errors={result.Errors.Count}");
        if (result.Errors.Count > 0)
        {
            System.Windows.MessageBox.Show(string.Join(Environment.NewLine, result.Errors), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool CanMoveToRecycleBin(IEnumerable<string> paths) =>
        paths.Any(path => !string.IsNullOrWhiteSpace(path) &&
                          (System.IO.File.Exists(path) || System.IO.Directory.Exists(path)));

    private void DropToRecycleBin(System.Windows.DragEventArgs e, IReadOnlyList<string> paths)
    {
        e.Handled = true;
        if (!CanMoveToRecycleBin(paths))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }

        var items = paths
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           (System.IO.File.Exists(path) || System.IO.Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(CreateDropItem)
            .ToArray();
        IReadOnlyList<string> errors;
        IReadOnlyList<string> deleted;
        if (ActionHistory is null)
        {
            var moved = new List<string>();
            var failures = new List<string>();
            foreach (var item in items)
            {
                if (_service.TryDeleteItem(item, out var error)) moved.Add(item.FullPath);
                else failures.Add($"{item.Name}: {error}");
            }
            deleted = moved;
            errors = failures;
        }
        else
        {
            deleted = ActionHistory.ExecuteRecycleDeleteBatch(_service, items, out errors);
        }

        e.Effects = deleted.Count > 0
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
        AppLogger.Log($"Recycle Bin drop completed. Deleted={deleted.Count}; Errors={errors.Count}");
        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show(
                string.Join(Environment.NewLine, errors.Take(8)),
                "MiniFences",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (deleted.Count > 0) ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static FolderItem CreateDropItem(string path)
    {
        var name = System.IO.Directory.Exists(path)
            ? new System.IO.DirectoryInfo(path).Name
            : System.IO.Path.GetFileName(path);
        return new FolderItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? path : name,
            FullPath = path
        };
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
        ApplyDesktopRenameEditorLayout();
        NameText.Visibility = Visibility.Collapsed;
        RenameTextBox.Visibility = Visibility.Visible;
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            UpdateLayout();
            if (mainWindow.TryGetElementPhysicalScreenBounds(RenameTextBox, out var physicalBounds))
            {
                var renameWindow = new DesktopRenameWindow(
                    Item.Name,
                    physicalBounds,
                    RenameTextBox.ActualWidth,
                    RenameTextBox.ActualHeight,
                    InlineRenameAppearance.GetInitialSelectionLength(Item.FullPath, Item.Name));
                _desktopRenameWindow = renameWindow;
                renameWindow.TryCommitRequested = text => CommitInlineRename(text);
                renameWindow.CancelRequested = EndInlineRename;
                RenameTextBox.Visibility = Visibility.Collapsed;
                renameWindow.Show();
                AppLogger.Log($"Desktop rename opened in independent editor window at {physicalBounds.X},{physicalBounds.Y}.");
            }
            else
            {
                mainWindow.FocusInlineRenameEditor(RenameTextBox);
            }
        }
        else
        {
            RenameTextBox.Focus();
            RenameTextBox.SelectAll();
        }
    }

    internal void BeginInlineRenameForTesting() => BeginInlineRename();
    internal void EndInlineRenameForTesting() => EndInlineRename();
    internal void SetInlineRenameTextForTesting(string text) => RenameTextBox.Text = text;

    private void RenameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isInlineRenaming) ApplyDesktopRenameEditorLayout();
    }

    private void ApplyDesktopRenameEditorLayout()
    {
        RenameTextBox.Width = InlineRenameAppearance.GetEditorWidth(RenameTextBox, RenameTextBox.Text, 76);
        RenameTextBox.MinHeight = InlineRenameAppearance.EditorHeight;
        RenameTextBox.TextWrapping = TextWrapping.Wrap;
        RenameTextBox.AcceptsReturn = false;
        RenameTextBox.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
        RenameTextBox.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden;
        RenameTextBox.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
        RenameTextBox.Height = InlineRenameAppearance.MeasureWrappedHeight(
            RenameTextBox,
            RenameTextBox.Text,
            RenameTextBox.Width);
        AppLogger.Log($"Desktop rename editor measured. TextLength={RenameTextBox.Text.Length}; Height={RenameTextBox.Height:0.##}");
    }

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

        if (_desktopRenameWindow is { IsVisible: true } desktopRenameWindow)
        {
            if (desktopRenameWindow.ContainsPhysicalScreenPoint(screenPoint)) return false;
            return CommitInlineRename(desktopRenameWindow.Editor.Text);
        }

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

        return CommitInlineRename();
    }

    private bool CommitInlineRename(string? requestedName = null)
    {
        if (!_isInlineRenaming || _isCommittingInlineRename) return false;
        _isCommittingInlineRename = true;
        try
        {
            var newName = (requestedName ?? RenameTextBox.Text).Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                System.Windows.MessageBox.Show(_localization.T("ItemNameCannotBeEmpty"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                FocusCurrentRenameEditor();
                return false;
            }

            if (string.Equals(newName, Item.Name.Trim(), StringComparison.Ordinal))
            {
                AppLogger.Log("Desktop rename dismissed because the name was unchanged.");
                EndInlineRename();
                return true;
            }

            var renameSucceeded = ActionHistory is null
                ? _service.TryRenameItem(Item, newName, out var renamedPath, out var error)
                : ActionHistory.ExecuteRename(_service, Item, newName, out renamedPath, out error);
            if (!renameSucceeded)
            {
                System.Windows.MessageBox.Show(error, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                FocusCurrentRenameEditor();
                return false;
            }

            var originalPath = Item.FullPath;
            EndInlineRename();
            if (!string.IsNullOrWhiteSpace(renamedPath) &&
                !string.Equals(originalPath, renamedPath, StringComparison.OrdinalIgnoreCase))
            {
                // Keep the currently rendered desktop icon alive while Explorer
                // finishes broadcasting the rename. Rebuilding the complete
                // desktop layer here could momentarily omit the renamed entry.
                Item.FullPath = renamedPath;
                Item.ToolTip = renamedPath;
                Item.Name = System.IO.Directory.Exists(renamedPath)
                    ? System.IO.Path.GetFileName(renamedPath)
                    : System.IO.Path.GetFileNameWithoutExtension(renamedPath);
                ItemRenamed?.Invoke(this, originalPath, renamedPath);
            }
            return true;
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
        var renameWindow = _desktopRenameWindow;
        _desktopRenameWindow = null;
        renameWindow?.CloseWithoutCommit();
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ReleaseInlineRenameEditor(RenameTextBox);
        }
    }

    private void FocusCurrentRenameEditor()
    {
        if (_desktopRenameWindow is { IsVisible: true } renameWindow)
        {
            renameWindow.Activate();
            renameWindow.Editor.Focus();
            renameWindow.Editor.SelectAll();
            return;
        }

        RenameTextBox.Focus();
        RenameTextBox.SelectAll();
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
        var deleted = ActionHistory is null
            ? _service.TryDeleteItem(Item, out var error)
            : ActionHistory.ExecuteRecycleDelete(_service, Item, out error);
        if (!deleted)
            System.Windows.MessageBox.Show(error, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
