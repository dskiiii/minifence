using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using MiniFences.Models;
using MiniFences.Services;
using Forms = System.Windows.Forms;

namespace MiniFences;

public partial class FenceControl : System.Windows.Controls.UserControl
{
    private const double ExpandedMinHeight = 180;
    private const double CollapsedHeight = 34;
    private readonly FolderItemService _folderItemService = new();
    private readonly ShellContextMenuService _shellContextMenuService = new();
    private readonly AutoOrganizerService _autoOrganizerService = new();
    private readonly DispatcherTimer _refreshTimer;
    private System.IO.FileSystemWatcher? _folderWatcher;
    private string? _watchedFolderPath;
    private bool _isDragging;
    private bool _isTitlePressPending;
    private System.Windows.Point _dragStart;
    private System.Windows.Point _titlePointerOffset;
    private double _leftStart;
    private double _topStart;
    private FolderItem? _pendingDragItem;
    private System.Windows.Point _pendingDragStart;
    private bool _preserveSelectionForPotentialDrag;
    private int _selectionAnchorIndex = -1;
    private LocalizationService _loc = new();
    private string? _lastLoadError;
    private bool _isHoverExpanded;
    private bool _isResizing;
    private bool _isMergeCompactPreview;
    private bool _shiftDetachHeaderDrag;
    private bool _shiftHeaderDrag;
    private bool _wasItemSelectedBeforeLeftDown;
    private DispatcherTimer? _inlineRenameTimer;
    private FolderItem? _inlineRenameItem;
    private TextBlock? _inlineRenameLabel;
    private System.Windows.Controls.TextBox? _inlineRenameTextBox;
    private bool _isCommittingInlineRename;

    public event EventHandler? Changed;
    public event EventHandler? NewFenceRequested;
    public event EventHandler? DeleteRequested;
    public event EventHandler? MoveToPreviousPageRequested;
    public event EventHandler? MoveToNextPageRequested;
    public event EventHandler? MoveToNewPageRequested;
    public event EventHandler? StackWithNearestRequested;
    public event EventHandler? NextTabRequested;
    public event EventHandler? PreviousTabRequested;
    public event Action<int>? TabSelectedRequested;
    public event Action<int, int>? TabReorderRequested;
    public event Action<int>? TabDetachRequested;
    public event EventHandler? UnstackRequested;
    public event EventHandler<DesktopItemsAssignedEventArgs>? DesktopItemsAssigned;
    public event EventHandler<DesktopItemsReleasedEventArgs>? DesktopItemsReleased;
    public event EventHandler? DesktopItemDragStarted;
    public event EventHandler? DesktopItemDragEnded;
    public event EventHandler? ItemsChanged;
    public event EventHandler? HeaderDragCompleted;
    public event EventHandler? HeaderDragMoved;
    public event EventHandler? HeaderDragCanceled;
    public event EventHandler? ItemSelectionRequested;

    public FenceConfig Config { get; }
    public bool SnapToGrid { get; set; }
    public bool RollupEnabled { get; set; } = true;
    public bool DoubleClickRollupEnabled { get; set; } = true;
    public bool ClickTitleToExpandEnabled { get; set; }
    public bool HoverTitleToExpandEnabled { get; set; }
    internal bool IsVisuallyCollapsed => Config.IsCollapsed && !_isHoverExpanded;
    internal bool IsTitleDragging => _isDragging;
    internal bool IsShiftDetachHeaderDrag => _shiftDetachHeaderDrag;
    internal bool IsShiftHeaderDrag => _shiftHeaderDrag;
    internal double HeaderDragDeltaX { get; private set; }
    internal double HeaderDragStartLeft => _leftStart;
    internal double HeaderDragStartTop => _topStart;
    internal Func<System.Drawing.Point, bool>? IsExplorerDesktopPointForDrag { get; set; }
    internal Func<System.Drawing.Point, bool>? IsMiniFencesSurfacePointForDrag { get; set; }

    internal IReadOnlyList<FolderItem> LoadedItemsForTesting =>
        ItemsList.Items.OfType<FolderItem>().ToArray();
    internal string DisplayedTitleForTesting => TitleText.Text;
    internal bool IsCollapsedForTesting => Config.IsCollapsed;
    internal bool IsContentVisibleForTesting => ContentArea.Visibility == Visibility.Visible &&
                                                 FooterPanel.Visibility == Visibility.Visible;
    internal System.Windows.HorizontalAlignment TitleAlignmentForTesting => TitleText.HorizontalAlignment;
    internal bool IsPathVisibleForTesting => StatusText.Visibility == Visibility.Visible;
    internal Thickness BorderThicknessForTesting => OuterBorder.BorderThickness;
    internal bool IsInnerPanelTransparentForTesting => ItemsList.Background == System.Windows.Media.Brushes.Transparent;
    internal bool IsFooterVisibleForTesting => FooterPanel.Visibility == Visibility.Visible;
    internal bool IsResizeHandleVisibleForTesting => ResizeThumb.Visibility == Visibility.Visible;
    internal bool IsManipulationLockedForTesting => Config.IsLocked;
    internal bool IsTabNavigationVisibleForTesting => TabNavigationPanel.Visibility == Visibility.Visible;
    internal bool HasFolderWatcherForTesting => _folderWatcher != null;
    internal IReadOnlyList<GridLength> TabColumnWidthsForTesting =>
        TabStripPanel.ColumnDefinitions.Select(column => column.Width).ToArray();
    internal int SelectedItemCountForTesting => ItemsList.SelectedItems.Count;
    internal void SetHoverExpandedForTesting(bool expanded) => SetHoverExpanded(expanded);
    internal void SetHoverExpandedFromDesktopHost(bool expanded) => SetHoverExpanded(expanded);

    public FenceControl(FenceConfig config)
    {
        Config = config;
        InitializeComponent();
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            LoadFolderItems();
        };
        Width = Config.Width;
        Height = Config.Height;
        TitleText.Text = Config.Title;
        ApplyStyle();
        ApplyCollapsedState();
        Loaded += (_, _) => LoadFolderItems();
        Unloaded += (_, _) => StopFolderWatcher();
    }

    public void SetLocalization(LocalizationService localization)
    {
        _loc.Language = localization.Language;

        NewFenceMenuItem.Header = _loc.T("NewFence");
        HoverExpandMenuItem.Header = _loc.T("ExpandOnHover");
        HoverExpandMenuItem.IsChecked = Config.EnableHoverExpand;
        LockFenceMenuItem.Header = _loc.T("LockFence");
        LockFenceMenuItem.IsChecked = Config.IsLocked;
        UnstackTabMenuItem.Header = _loc.T("RemoveFromTabStack");
        RenameFenceMenuItem.Header = _loc.T("RenameFence");
        ChooseFolderMenuItem.Header = _loc.T("ChooseFolder");
        OpenBoundFolderMenuItem.Header = _loc.T("OpenBoundFolder");
        MoveToPreviousPageMenuItem.Header = _loc.T("MoveToPreviousPage");
        MoveToNextPageMenuItem.Header = _loc.T("MoveToNextPage");
        MoveToNewPageMenuItem.Header = _loc.T("MoveToNewPage");
        SortMenuItem.Header = _loc.T("SortBy");
        SortNoneMenuItem.Header = _loc.T("SortNone");
        SortNameMenuItem.Header = _loc.T("SortName");
        SortSizeMenuItem.Header = _loc.T("SortSize");
        SortTypeMenuItem.Header = _loc.T("SortItemType");
        SortModifiedMenuItem.Header = _loc.T("SortModified");
        SortCreatedMenuItem.Header = _loc.T("SortCreated");
        SortCategoryMenuItem.Header = _loc.T("SortCategory");
        StyleMenuItem.Header = _loc.T("Style");
        CopyColorMenuItem.Header = _loc.T("CopyColor");
        ChooseBackgroundColorMenuItem.Header = _loc.T("EditColor");
        ChooseHeaderColorMenuItem.Header = _loc.T("ChooseHeaderColor");
        OpacityMenuItem.Header = _loc.T("Opacity");
        ResetStyleMenuItem.Header = _loc.T("ResetStyle");
        DeleteFenceMenuItem.Header = _loc.T("DeleteFence");
        OpenItemMenuItem.Header = _loc.T("Open");
        RenameItemMenuItem.Header = _loc.T("RenameItem");
        ShowItemInExplorerMenuItem.Header = _loc.T("ShowInExplorer");
        CopyItemPathMenuItem.Header = _loc.T("CopyPath");
        DeleteItemMenuItem.Header = _loc.T("DeleteItem");
        FooterHintText.Text = _loc.T("RightClickForOptions");

        UpdateStatusText();
    }

    internal void SetTabStatus(int count, int index, IReadOnlyList<string>? titles = null, bool useTabStrip = false,
        bool hoverSwitch = false, bool equalTabWidths = false)
    {
        TabStatusText.Text = count > 1 ? $"{index + 1}/{count}" : string.Empty;
        TabNavigationPanel.Visibility = count > 1 && !useTabStrip ? Visibility.Visible : Visibility.Collapsed;
        TabStripPanel.Children.Clear();
        TabStripPanel.ColumnDefinitions.Clear();
        TabStripPanel.Visibility = count > 1 && useTabStrip ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = count > 1 && useTabStrip ? Visibility.Collapsed : Visibility.Visible;
        if (count <= 1 || !useTabStrip) return;
        for (var column = 0; column < count; column++)
            TabStripPanel.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = equalTabWidths ? new GridLength(1, GridUnitType.Star) : GridLength.Auto
            });
        if (!equalTabWidths)
            TabStripPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var tabIndex = 0; tabIndex < count; tabIndex++)
        {
            var selectedIndex = tabIndex;
            var tab = new Border
            {
                MinWidth = equalTabWidths ? 0 : 72,
                MaxWidth = equalTabWidths ? double.PositiveInfinity : 150,
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(tabIndex == index
                    ? System.Windows.Media.Color.FromArgb(210, 255, 255, 255)
                    : System.Windows.Media.Color.FromArgb(48, 0, 0, 0)),
                Cursor = System.Windows.Input.Cursors.Hand,
                AllowDrop = true
            };
            tab.Child = new TextBlock
            {
                Text = titles != null && tabIndex < titles.Count ? titles[tabIndex] : $"Tab {tabIndex + 1}",
                Foreground = new SolidColorBrush(tabIndex == index ? Colors.Black : Colors.White),
                FontWeight = tabIndex == index ? FontWeights.SemiBold : FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            tab.MouseLeftButtonDown += (_, e) =>
            {
                // A normal drag belongs to the whole Fence. Shift reserves the gesture
                // for tab ordering/detaching, matching browser-style tab handling.
                e.Handled = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            };
            tab.MouseLeftButtonUp += (_, e) =>
            {
                if (!_isDragging) TabSelectedRequested?.Invoke(selectedIndex);
            };
            tab.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || (Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
                var data = new System.Windows.DataObject("MiniFences.TabIndex", selectedIndex);
                System.Windows.DragDropEffects effect;
                try
                {
                    effect = System.Windows.DragDrop.DoDragDrop(tab, data, System.Windows.DragDropEffects.Move);
                }
                finally
                {
                    Mouse.SetCursor(System.Windows.Input.Cursors.Arrow);
                }
                if (effect == System.Windows.DragDropEffects.None)
                    TabDetachRequested?.Invoke(selectedIndex);
            };
            tab.GiveFeedback += (_, e) =>
            {
                e.UseDefaultCursors = false;
                Mouse.SetCursor(System.Windows.Input.Cursors.SizeAll);
                e.Handled = true;
            };
            tab.DragOver += (_, e) =>
            {
                e.Effects = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && e.Data.GetDataPresent("MiniFences.TabIndex")
                    ? System.Windows.DragDropEffects.Move
                    : System.Windows.DragDropEffects.None;
                e.Handled = true;
            };
            tab.Drop += (_, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 &&
                    e.Data.GetData("MiniFences.TabIndex") is int fromIndex && fromIndex != selectedIndex)
                    TabReorderRequested?.Invoke(fromIndex, selectedIndex);
                e.Effects = (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? System.Windows.DragDropEffects.Move
                    : System.Windows.DragDropEffects.None;
                e.Handled = true;
            };
            if (hoverSwitch) tab.MouseEnter += (_, _) => TabSelectedRequested?.Invoke(selectedIndex);
            Grid.SetColumn(tab, selectedIndex);
            TabStripPanel.Children.Add(tab);
        }
    }

    public void LoadFolderItems()
    {
        if (Config.IsDesktopGroup)
        {
            StatusText.Visibility = Config.ShowPath ? Visibility.Visible : Visibility.Collapsed;
            ItemsList.Visibility = Visibility.Visible;
            ContentArea.IsHitTestVisible = true;
            var previousPaths = Config.AssignedPaths.ToArray();
            var existingItems = _folderItemService.LoadAssignedItems(Config.AssignedPaths);
            Config.AssignedPaths = existingItems.Select(item => item.FullPath).ToList();
            // DesktopGroup contents are refreshed by MainWindow's shared personal/public desktop watchers.
            // Creating one watcher per Fence causes duplicate reload storms and handle growth.
            StopFolderWatcher();
            _lastLoadError = null;
            ItemsList.ItemsSource = ApplySort(existingItems);
            AppLogger.Log($"Loading desktop group '{Config.Title}' with {existingItems.Count} assigned item(s).");
            UpdateStatusText();
            if (!previousPaths.SequenceEqual(Config.AssignedPaths, StringComparer.OrdinalIgnoreCase)) Changed?.Invoke(this, EventArgs.Empty);
            return;
        }


        StatusText.Visibility = Config.ShowPath ? Visibility.Visible : Visibility.Collapsed;
        ItemsList.Visibility = Visibility.Visible;
        ContentArea.IsHitTestVisible = true;

        if (!System.IO.Directory.Exists(Config.FolderPath))
        {
            AutoOrganizerService.TryEnsureManagedCategoryFolder(Config.FolderPath, out _, out _);
        }

        if (!System.IO.Directory.Exists(Config.FolderPath))
        {
            AppLogger.Log($"Fence folder missing: {Config.FolderPath}");
            StopFolderWatcher();
            ItemsList.ItemsSource = Array.Empty<FolderItem>();
            _lastLoadError = $"Path does not exist: {Config.FolderPath}";
            UpdateStatusText();
            return;
        }

        EnsureFolderWatcher();
        AppLogger.Log($"Loading Fence '{Config.Title}' from folder: {Config.FolderPath}");
        if (!_folderItemService.TryLoadItems(Config.FolderPath, out var items, out var error))
        {
            _lastLoadError = error;
            ItemsList.ItemsSource = Array.Empty<FolderItem>();
            UpdateStatusText();
            return;
        }

        _lastLoadError = null;
        ItemsList.ItemsSource = ApplySort(items);
        UpdateStatusText();
    }

    public void SyncConfigFromLayout()
    {
        Config.Left = Canvas.GetLeft(this);
        Config.Top = Canvas.GetTop(this);
        Config.Width = Width;
        if (!Config.IsCollapsed)
        {
            Config.Height = Height;
            Config.ExpandedHeight = Height;
        }
        Config.Title = TitleText.Text;
    }

    public void CloseOpenContextMenus(bool force = false, System.Windows.Point? screenPoint = null)
    {
        if (ContextMenu?.IsOpen == true && (force || !screenPoint.HasValue || !IsPointInsideContextMenu(ContextMenu, screenPoint.Value)))
        {
            ContextMenu.IsOpen = false;
        }

        if (ItemsList.ContextMenu?.IsOpen == true &&
            (force || !screenPoint.HasValue || !IsPointInsideContextMenu(ItemsList.ContextMenu, screenPoint.Value)))
        {
            ItemsList.ContextMenu.IsOpen = false;
        }
    }

    private static bool IsPointInsideContextMenu(FrameworkElement menu, System.Windows.Point screenPoint)
    {
        try
        {
            var local = menu.PointFromScreen(screenPoint);
            return new Rect(0, 0, menu.ActualWidth, menu.ActualHeight).Contains(local);
        }
        catch { return false; }
    }

    internal void StopForTesting()
    {
        _refreshTimer.Stop();
        StopFolderWatcher();
    }

    public void ClampToParentBounds()
    {
        if (Parent is not Canvas canvas)
        {
            return;
        }

        var canvasWidth = canvas.ActualWidth;
        var canvasHeight = canvas.ActualHeight;
        if (canvasWidth <= 0 || canvasHeight <= 0)
        {
            return;
        }

        Width = Math.Min(Math.Max(MinWidth, Width), Math.Max(MinWidth, canvasWidth));
        Height = Config.IsCollapsed
            ? CollapsedHeight
            : Math.Min(Math.Max(ExpandedMinHeight, Height), Math.Max(ExpandedMinHeight, canvasHeight));

        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        Canvas.SetLeft(this, Math.Clamp(left, 0, Math.Max(0, canvasWidth - Width)));
        Canvas.SetTop(this, Math.Clamp(top, 0, Math.Max(0, canvasHeight - Height)));
        SyncConfigFromLayout();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Config.IsLocked) return;
        if (e.OriginalSource is DependencyObject source && FindVisualParent<System.Windows.Controls.Button>(source) != null)
        {
            return;
        }

        _isTitlePressPending = true;
        _dragStart = e.GetPosition(Parent as IInputElement);
        _titlePointerOffset = e.GetPosition(this);
        _shiftHeaderDrag = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        _shiftDetachHeaderDrag = _shiftHeaderDrag && !string.IsNullOrWhiteSpace(Config.TabGroupId);
        HeaderDragDeltaX = 0;
        _leftStart = Canvas.GetLeft(this);
        _topStart = Canvas.GetTop(this);
    }

    private void TitleBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindVisualParent<System.Windows.Controls.Button>(source) != null)
        {
            return;
        }

        // WPF supplies the native Windows double-click count. Keeping this out of
        // the drag path prevents a moved title bar from being mistaken for a click.
        if (e.ClickCount == 2 && RollupEnabled && DoubleClickRollupEnabled)
        {
            _isDragging = false;
            _isTitlePressPending = false;
            TitleBar.ReleaseMouseCapture();
            ToggleCollapsed();
            e.Handled = true;
        }
    }

    private void TitleBar_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if ((!_isTitlePressPending && !_isDragging) || e.LeftButton != MouseButtonState.Pressed || Parent is not Canvas canvas)
        {
            return;
        }

        var current = e.GetPosition(canvas);
        if (!_isDragging)
        {
            if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            _isDragging = true;
            TitleBar.Cursor = System.Windows.Input.Cursors.SizeAll;
            if (_isHoverExpanded && Config.IsCollapsed)
            {
                Config.IsCollapsed = false;
                Config.EdgeDock = null;
                _isHoverExpanded = false;
                ApplyCollapsedState();
                SyncConfigFromLayout();
            }
            TitleBar.CaptureMouse();
        }

        var left = _leftStart + current.X - _dragStart.X;
        var top = _topStart + current.Y - _dragStart.Y;
        if (SnapToGrid)
        {
            left = Math.Round(left / 16) * 16;
            top = Math.Round(top / 16) * 16;
        }

        Canvas.SetLeft(this, Math.Clamp(left, 0, Math.Max(0, canvas.ActualWidth - Width)));
        Canvas.SetTop(this, Math.Clamp(top, 0, Math.Max(0, canvas.ActualHeight - Height)));
        HeaderDragDeltaX = Canvas.GetLeft(this) - _leftStart;
        SyncConfigFromLayout();
        HeaderDragMoved?.Invoke(this, EventArgs.Empty);
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isTitlePressPending && !_isDragging)
        {
            return;
        }

        _isTitlePressPending = false;
        if (!_isDragging)
        {
            if (RollupEnabled && ClickTitleToExpandEnabled && Config.IsCollapsed)
            {
                ToggleCollapsed();
            }
            return;
        }

        _isDragging = false;
        TitleBar.Cursor = System.Windows.Input.Cursors.Arrow;
        TitleBar.ReleaseMouseCapture();
        SyncConfigFromLayout();
        Changed?.Invoke(this, EventArgs.Empty);
        HeaderDragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void TitleBar_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        TitleBar.Cursor = System.Windows.Input.Cursors.Arrow;
        _isTitlePressPending = false;
        SyncConfigFromLayout();
        Changed?.Invoke(this, EventArgs.Empty);
        HeaderDragCanceled?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (Config.IsCollapsed || Config.IsLocked)
        {
            return;
        }

        var maxWidth = Parent is Canvas canvas && canvas.ActualWidth > 0
            ? Math.Max(MinWidth, canvas.ActualWidth - Canvas.GetLeft(this))
            : double.PositiveInfinity;
        var maxHeight = Parent is Canvas canvas2 && canvas2.ActualHeight > 0
            ? Math.Max(MinHeight, canvas2.ActualHeight - Canvas.GetTop(this))
            : double.PositiveInfinity;

        Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, maxWidth);
        Height = Math.Clamp(Height + e.VerticalChange, MinHeight, maxHeight);
        SyncConfigFromLayout();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e) => _isResizing = true;

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isResizing = false;
        ResizeThumb.Visibility = IsMouseOver && !IsVisuallyCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AppLogger.Log($"ItemsList left button down. ClickCount={e.ClickCount}, Source={e.OriginalSource?.GetType().FullName}");
        if (e.ClickCount < 2)
        {
            return;
        }

        CancelPendingInlineRename();

        if (e.OriginalSource is not DependencyObject source)
        {
            AppLogger.Log("Double-click ignored because the mouse source was not a DependencyObject.");
            return;
        }

        var container = FindVisualParent<System.Windows.Controls.ListViewItem>(source) ??
                        FindItemContainerAtPoint(e.GetPosition(ItemsList));
        if (container?.DataContext is FolderItem item)
        {
            _pendingDragItem = null;
            AppLogger.Log($"User double-clicked item: {item.FullPath}");
            ItemsList.SelectedItem = item;
            OpenItem(item);
            e.Handled = true;
            return;
        }

        AppLogger.Log($"Double-click ignored because no FolderItem was found under the mouse. Point={e.GetPosition(ItemsList)}; Bounds={DescribeItemBounds()}");
    }

    private void Item_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        CancelPendingInlineRename();
        if (sender is not System.Windows.Controls.ListViewItem { DataContext: FolderItem item })
        {
            return;
        }

        AppLogger.Log($"User double-clicked item: {item.FullPath}");
        _pendingDragItem = null;
        ItemsList.SelectedItem = item;
        OpenItem(item);
        e.Handled = true;
    }

    private void Item_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListViewItem { DataContext: FolderItem item } container)
        {
            _pendingDragItem = null;
            return;
        }

        if (e.OriginalSource is DependencyObject originalSource &&
            FindVisualParent<System.Windows.Controls.TextBox>(originalSource) != null)
        {
            _pendingDragItem = null;
            return;
        }

        ItemSelectionRequested?.Invoke(this, EventArgs.Empty);
        _wasItemSelectedBeforeLeftDown = container.IsSelected;
        _pendingDragItem = item;
        _pendingDragStart = e.GetPosition(this);
        var itemIndex = ItemsList.ItemContainerGenerator.IndexFromContainer(container);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            container.IsSelected = !container.IsSelected;
            _selectionAnchorIndex = itemIndex;
            container.Focus();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _selectionAnchorIndex >= 0)
        {
            var first = Math.Min(_selectionAnchorIndex, itemIndex);
            var last = Math.Max(_selectionAnchorIndex, itemIndex);
            ItemsList.SelectedItems.Clear();
            for (var index = first; index <= last; index++)
            {
                ItemsList.SelectedItems.Add(ItemsList.Items[index]);
            }
            container.Focus();
            e.Handled = true;
            return;
        }

        _preserveSelectionForPotentialDrag = container.IsSelected &&
                                             ItemsList.SelectedItems.Count > 1 &&
                                             Keyboard.Modifiers == ModifierKeys.None;
        if (!_preserveSelectionForPotentialDrag) _selectionAnchorIndex = itemIndex;
        if (_preserveSelectionForPotentialDrag)
        {
            container.Focus();
            e.Handled = true;
        }
    }

    private void Item_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindVisualParent<TextBlock>(source) is { DataContext: FolderItem labelItem } label &&
            _wasItemSelectedBeforeLeftDown &&
            ItemsList.SelectedItems.Contains(labelItem))
        {
            ScheduleInlineRename(labelItem, label);
            e.Handled = true;
        }

        if (_preserveSelectionForPotentialDrag &&
            _pendingDragItem != null &&
            sender is System.Windows.Controls.ListViewItem container)
        {
            ItemsList.SelectedItems.Clear();
            container.IsSelected = true;
        }

        _preserveSelectionForPotentialDrag = false;
        _pendingDragItem = null;
    }

    private void ScheduleInlineRename(FolderItem item, TextBlock label)
    {
        CancelPendingInlineRename();
        _inlineRenameTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 50)
        };
        _inlineRenameTimer.Tick += (_, _) =>
        {
            CancelPendingInlineRename();
            if (ItemsList.SelectedItems.Contains(item) && Mouse.LeftButton == MouseButtonState.Released)
            {
                BeginInlineRename(item, label);
            }
        };
        _inlineRenameTimer.Start();
    }

    private void BeginInlineRename(FolderItem item, TextBlock label)
    {
        CancelPendingInlineRename();
        if (_inlineRenameItem != null) CommitInlineRename();
        if (VisualTreeHelper.GetParent(label) is not Grid grid) return;
        var editor = grid.Children.OfType<System.Windows.Controls.TextBox>().FirstOrDefault();
        if (editor == null) return;

        _inlineRenameItem = item;
        _inlineRenameLabel = label;
        _inlineRenameTextBox = editor;
        editor.Text = item.Name;
        InlineRenameAppearance.Apply(editor, item.Name);
        label.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.FocusInlineRenameEditor(editor);
        }
        else
        {
            editor.Focus();
            editor.SelectAll();
        }
    }

    private void ItemRenameTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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

    private void ItemRenameTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_inlineRenameItem != null) CommitInlineRename();
    }

    internal bool CommitInlineRenameIfPointerOutside(System.Windows.Point screenPoint)
    {
        if (_inlineRenameItem == null || _inlineRenameTextBox == null) return false;

        try
        {
            var localPoint = _inlineRenameTextBox.PointFromScreen(screenPoint);
            if (new Rect(new System.Windows.Point(0, 0), _inlineRenameTextBox.RenderSize).Contains(localPoint))
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
        if (_inlineRenameItem == null || _inlineRenameTextBox == null || _isCommittingInlineRename) return;
        _isCommittingInlineRename = true;
        try
        {
            var item = _inlineRenameItem;
            var editor = _inlineRenameTextBox;
            var newName = editor.Text.Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                System.Windows.MessageBox.Show(_loc.T("ItemNameCannotBeEmpty"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                editor.Focus();
                return;
            }

            if (!_folderItemService.TryRenameItem(item, newName, out var renamedPath, out var error))
            {
                System.Windows.MessageBox.Show(FormatFileOperationError(error, "CouldNotRenameItem"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                editor.Focus();
                editor.SelectAll();
                return;
            }

            if (ReplaceAssignedPathAfterRename(Config, item.FullPath, renamedPath))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            EndInlineRename();
            LoadFolderItems();
        }
        finally
        {
            _isCommittingInlineRename = false;
        }
    }

    private void EndInlineRename()
    {
        var editor = _inlineRenameTextBox;
        if (editor != null) editor.Visibility = Visibility.Collapsed;
        if (_inlineRenameLabel != null) _inlineRenameLabel.Visibility = Visibility.Visible;
        _inlineRenameItem = null;
        _inlineRenameLabel = null;
        _inlineRenameTextBox = null;
        if (editor != null && Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ReleaseInlineRenameEditor(editor);
        }
    }

    private void CancelPendingInlineRename()
    {
        _inlineRenameTimer?.Stop();
        _inlineRenameTimer = null;
    }

    public void ClearItemSelection()
    {
        ItemsList.SelectedItems.Clear();
        _selectionAnchorIndex = -1;
    }

    internal void SelectItemForTesting(int index) => ItemsList.SelectedIndex = index;

    private void Item_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        TryStartItemDrag(e);
    }

    private void Item_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListViewItem { DataContext: FolderItem target } ||
            !System.IO.Directory.Exists(target.FullPath) ||
            !TryGetDroppedFiles(e, out var paths)) return;

        e.Effects = CanMoveIntoFolder(paths, target.FullPath)
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Item_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListViewItem { DataContext: FolderItem target } ||
            !System.IO.Directory.Exists(target.FullPath) ||
            !TryGetDroppedFiles(e, out var paths)) return;

        e.Handled = true;
        if (!CanMoveIntoFolder(paths, target.FullPath))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }

        var result = _folderItemService.MoveIntoFolder(paths, target.FullPath);
        e.Effects = result.Moved > 0 ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        AppLogger.Log($"Folder icon drop completed. Destination={target.FullPath}; Moved={result.Moved}; Skipped={result.Skipped}; Errors={result.Errors.Count}");
        if (result.Errors.Count > 0 || result.Skipped > 0)
        {
            System.Windows.MessageBox.Show(BuildMoveSummary(result), "MiniFences", MessageBoxButton.OK,
                result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        LoadFolderItems();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static bool CanMoveIntoFolder(IEnumerable<string> sourcePaths, string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || !System.IO.Directory.Exists(destinationFolder)) return false;
        var destination = System.IO.Path.GetFullPath(destinationFolder).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return sourcePaths.Any(path =>
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var source = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                return !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) &&
                       (System.IO.File.Exists(source) || System.IO.Directory.Exists(source));
            }
            catch
            {
                return false;
            }
        });
    }

    private void ItemsList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        TryStartItemDrag(e);
    }

    private void TryStartItemDrag(System.Windows.Input.MouseEventArgs e)
    {
        if (_pendingDragItem == null || e.LeftButton != MouseButtonState.Pressed)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _pendingDragItem = null;
            }
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _pendingDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _pendingDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragItems = GetDragItems(_pendingDragItem);
        _preserveSelectionForPotentialDrag = false;
        _pendingDragItem = null;
        StartItemDrag(dragItems);
    }

    private void StartItemDrag(IReadOnlyList<FolderItem> items)
    {
        var paths = items
            .Select(item => item.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           (System.IO.File.Exists(path) || System.IO.Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            AppLogger.Log("Item drag skipped because no selected paths exist.");
            return;
        }

        try
        {
            var data = new System.Windows.DataObject();
            if (Config.IsDesktopGroup)
                DesktopDragData.Set(data, paths, looseIcon: false, paths[0]);
            else
                DesktopDragData.SetFileDropList(data, paths);
            AppLogger.Log($"Item drag started with {paths.Length} item(s): {string.Join("; ", paths)}");
            if (Config.IsDesktopGroup) DesktopItemDragStarted?.Invoke(this, EventArgs.Empty);
            System.Windows.DragDropEffects result;
            System.Windows.QueryContinueDragEventHandler? desktopDropGuard = null;
            var desktopDropCanceled = false;
            if (Config.IsDesktopGroup && IsExplorerDesktopPointForDrag != null)
            {
                desktopDropGuard = (_, e) =>
                {
                    var cursor = Forms.Cursor.Position;
                    var overMiniFencesSurface = IsMiniFencesSurfacePointForDrag?.Invoke(cursor) == true;
                    if (!DesktopDragData.ShouldCancelExplorerDesktopDrop(
                            e.KeyStates,
                            IsExplorerDesktopPointForDrag(cursor),
                            overMiniFencesSurface)) return;
                    desktopDropCanceled = true;
                    e.Action = System.Windows.DragAction.Cancel;
                    e.Handled = true;
                };
                QueryContinueDrag += desktopDropGuard;
            }
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
                if (Config.IsDesktopGroup) DesktopItemDragEnded?.Invoke(this, EventArgs.Empty);
            }
            var cursor = Forms.Cursor.Position;
            var overMiniFencesSurface = IsMiniFencesSurfacePointForDrag?.Invoke(cursor) == true;
            var overExplorerDesktop = Config.IsDesktopGroup &&
                                      !overMiniFencesSurface &&
                                      IsExplorerDesktopPointForDrag?.Invoke(cursor) == true;
            AppLogger.Log($"Item drag completed. Result={result}; ScreenPoint={cursor.X},{cursor.Y}; ExplorerDesktopCanceled={desktopDropCanceled}");
            if (Config.IsDesktopGroup &&
                DesktopDragData.ShouldReleaseDesktopMembershipAfterDrag(result, overExplorerDesktop))
            {
                DesktopItemsReleased?.Invoke(this, new DesktopItemsReleasedEventArgs(paths, cursor));
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Item drag failed", ex);
        }
    }

    private void Item_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListViewItem item)
        {
            return;
        }

        if (!item.IsSelected)
        {
            ItemsList.SelectedItems.Clear();
        }

        ItemSelectionRequested?.Invoke(this, EventArgs.Empty);
        item.IsSelected = true;
        item.Focus();
    }

    private void ItemsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindVisualParent<System.Windows.Controls.ListViewItem>((DependencyObject)e.OriginalSource) ??
                   FindItemContainerAtPoint(e.GetPosition(ItemsList));
        if (item != null)
        {
            ItemSelectionRequested?.Invoke(this, EventArgs.Empty);
            if (!item.IsSelected)
            {
                ItemsList.SelectedItems.Clear();
            }

            item.IsSelected = true;
            item.Focus();
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var selectedPaths = GetSelectedItems().Select(selected => selected.FullPath).ToArray();
            var rightClickedPath = (item.DataContext as FolderItem)?.FullPath;
            if (string.IsNullOrWhiteSpace(rightClickedPath)) return;
            var window = Window.GetWindow(this);
            var owner = window == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var cursor = Forms.Cursor.Position;
            if (_shellContextMenuService.Show(rightClickedPath, selectedPaths, owner, cursor, out var commandInvoked, out var commandVerb, out var error))
            {
                e.Handled = true;
                if (ShellContextMenuService.ShouldHandleCommandInHost(commandVerb) && item.DataContext is FolderItem renameItem)
                {
                    RenameItem(renameItem);
                }
                else if (commandInvoked)
                {
                    LoadFolderItems();
                }
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                AppLogger.Log($"Falling back to MiniFences item menu: {error}");
            }
        }
    }

    private void ItemsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var point = Mouse.GetPosition(ItemsList);
        var item = FindItemContainerAtPoint(point);
        if (item?.DataContext is not FolderItem folderItem)
        {
            e.Handled = true;
            return;
        }

        if (!item.IsSelected)
        {
            ItemsList.SelectedItems.Clear();
        }

        item.IsSelected = true;
        item.Focus();
        ItemsList.SelectedItem = folderItem;
    }

    private void OpenItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetActionItem(sender, out var item))
        {
            AppLogger.Log($"User selected Open from item menu: {item.FullPath}");
            ItemsList.SelectedItem = item;
            OpenItem(item);
        }
    }

    private void ShowItemInExplorerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetActionItem(sender, out var item))
        {
            return;
        }

        if (!_folderItemService.TryShowInExplorer(item, out var error))
        {
            System.Windows.MessageBox.Show(FormatFileOperationError(error, "CouldNotShowItem"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenameItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetActionItem(sender, out var item))
        {
            RenameItem(item);
        }
    }

    private void RenameItem(FolderItem item)
    {
        var dialog = new RenameFenceDialog(
            item.Name,
            _loc,
            "RenameItem",
            "ItemName",
            "ItemNameCannotBeEmpty")
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!_folderItemService.TryRenameItem(item, dialog.InputText, out var renamedPath, out var error))
        {
            System.Windows.MessageBox.Show(FormatFileOperationError(error, "CouldNotRenameItem"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ReplaceAssignedPathAfterRename(Config, item.FullPath, renamedPath))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        LoadFolderItems();
    }

    internal static bool ReplaceAssignedPathAfterRename(FenceConfig config, string oldPath, string? renamedPath)
    {
        if (!config.IsDesktopGroup || string.IsNullOrWhiteSpace(renamedPath)) return false;
        var index = config.AssignedPaths.FindIndex(path => string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || string.Equals(config.AssignedPaths[index], renamedPath, StringComparison.OrdinalIgnoreCase)) return false;

        config.AssignedPaths[index] = renamedPath;
        config.AssignedPaths = config.AssignedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return true;
    }

    private void CopyItemPathMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var items = GetActionItems(sender);
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, items.Select(item => item.FullPath)));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(string.Format(_loc.T("CouldNotCopyPath"), ex.Message), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteItemMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DeleteItems(GetActionItems(sender));
    }

    private void DeleteItem(FolderItem item)
    {
        DeleteItems([item]);
    }

    private void DeleteItems(IReadOnlyList<FolderItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            items.Count == 1
                ? string.Format(_loc.T("DeleteItemQuestion"), items[0].Name)
                : string.Format(_loc.T("DeleteItemsQuestion"), items.Count),
            "MiniFences",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var errors = new List<string>();
        foreach (var item in items)
        {
            if (!_folderItemService.TryDeleteItem(item, out var error))
            {
                errors.Add($"{item.Name}: {FormatFileOperationError(error, "CouldNotDeleteItem")}");
            }
        }

        LoadFolderItems();
        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show(
                string.Join(Environment.NewLine, errors.Take(8)),
                "MiniFences",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ItemsList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ItemsList.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            ItemsList.SelectedItems.Clear();
            e.Handled = true;
            return;
        }

        if (ItemsList.SelectedItem is not FolderItem item)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                OpenItem(item);
                e.Handled = true;
                break;
            case Key.F2:
                RenameItem(item);
                e.Handled = true;
                break;
            case Key.Delete:
                DeleteItems(GetSelectedItems());
                e.Handled = true;
                break;
            case Key.F5:
                LoadFolderItems();
                e.Handled = true;
                break;
        }
    }

    private void RenameFenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameFenceDialog(Config.Title, _loc)
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Config.Title = dialog.FenceTitle;
        TitleText.Text = Config.Title;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NewFenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        NewFenceRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NewFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameFenceDialog(
            _loc.T("DefaultNewFolderName"),
            _loc,
            "NewFolder",
            "FolderName",
            "FolderNameCannotBeEmpty")
        {
            Owner = Window.GetWindow(this)
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!_folderItemService.TryCreateFolder(Config.FolderPath, dialog.InputText, out _, out var error))
        {
            System.Windows.MessageBox.Show(FormatFileOperationError(error, "CouldNotCreateFolder"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        LoadFolderItems();
    }

    private void ChooseFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _loc.T("ChooseThisFenceFolder"),
            SelectedPath = System.IO.Directory.Exists(Config.FolderPath)
                ? Config.FolderPath
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        Config.FolderPath = dialog.SelectedPath;
        Config.Kind = FenceConfig.FolderPortalKind;
        Config.AssignedPaths.Clear();
        if (string.Equals(Config.Title, "Desktop", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Config.Title, "Mini Fence", StringComparison.OrdinalIgnoreCase))
        {
            Config.Title = System.IO.Path.GetFileName(dialog.SelectedPath);
            TitleText.Text = Config.Title;
        }

        LoadFolderItems();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        LoadFolderItems();
    }

    private void ToggleCollapsedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleCollapsed();
    }

    private void HoverExpandMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Config.EnableHoverExpand = HoverExpandMenuItem.IsChecked;
        if (!Config.EnableHoverExpand && _isHoverExpanded)
        {
            _isHoverExpanded = false;
            ApplyCollapsedState();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LockFenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Config.IsLocked = LockFenceMenuItem.IsChecked;
        ResizeThumb.Visibility = Visibility.Collapsed;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void StackWithNearestMenuItem_Click(object sender, RoutedEventArgs e) => StackWithNearestRequested?.Invoke(this, EventArgs.Empty);

    private void NextTabMenuItem_Click(object sender, RoutedEventArgs e) => NextTabRequested?.Invoke(this, EventArgs.Empty);

    private void UnstackTabMenuItem_Click(object sender, RoutedEventArgs e) => UnstackRequested?.Invoke(this, EventArgs.Empty);

    private void PreviousTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(Config.TabGroupId))
        {
            PreviousTabRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void NextTab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Config.TabGroupId)) return;
        NextTabRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void MoveToPreviousPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MoveToPreviousPageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MoveToNextPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MoveToNextPageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MoveToNewPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MoveToNewPageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenBoundFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AutoOrganizerService.TryEnsureManagedCategoryFolder(Config.FolderPath, out _, out _);
        if (!System.IO.Directory.Exists(Config.FolderPath))
        {
            System.Windows.MessageBox.Show(_loc.T("BoundFolderMissing"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(Config.FolderPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(string.Format(_loc.T("CouldNotOpenFolder"), ex.Message), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void EnsureFolderWatcher()
    {
        var normalizedPath = System.IO.Path.GetFullPath(Config.FolderPath);
        if (string.Equals(_watchedFolderPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StopFolderWatcher();
        try
        {
            _folderWatcher = new System.IO.FileSystemWatcher(normalizedPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = System.IO.NotifyFilters.FileName |
                               System.IO.NotifyFilters.DirectoryName |
                               System.IO.NotifyFilters.LastWrite |
                               System.IO.NotifyFilters.Size
            };
            _folderWatcher.Created += FolderWatcher_Changed;
            _folderWatcher.Deleted += FolderWatcher_Changed;
            _folderWatcher.Renamed += FolderWatcher_Renamed;
            _folderWatcher.Changed += FolderWatcher_Changed;
            _folderWatcher.Error += FolderWatcher_Error;
            _folderWatcher.EnableRaisingEvents = true;
            _watchedFolderPath = normalizedPath;
        }
        catch
        {
            StopFolderWatcher();
        }
    }

    private void StopFolderWatcher()
    {
        _refreshTimer.Stop();
        if (_folderWatcher == null)
        {
            _watchedFolderPath = null;
            return;
        }

        _folderWatcher.EnableRaisingEvents = false;
        _folderWatcher.Error -= FolderWatcher_Error;
        _folderWatcher.Dispose();
        _folderWatcher = null;
        _watchedFolderPath = null;
    }

    private void FolderWatcher_Changed(object sender, System.IO.FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private void FolderWatcher_Renamed(object sender, System.IO.RenamedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Config.IsDesktopGroup)
            {
                var index = Config.AssignedPaths.FindIndex(path => string.Equals(path, e.OldFullPath, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    Config.AssignedPaths[index] = e.FullPath;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private void FolderWatcher_Error(object sender, System.IO.ErrorEventArgs e)
    {
        AppLogger.LogException($"Folder watcher failed for '{Config.FolderPath}'", e.GetException());
        Dispatcher.BeginInvoke(() =>
        {
            StopFolderWatcher();
            LoadFolderItems();
        }, DispatcherPriority.Background);
    }

    private void DeleteFenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ChooseBackgroundColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryChooseColor(Config.BackgroundColor, out var color))
        {
            Config.BackgroundColor = color;
            ApplyStyle();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CopyColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(Config.BackgroundColor); }
        catch (Exception ex) { AppLogger.LogException("Could not copy Fence color", ex); }
    }

    private void ChooseHeaderColorMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryChooseColor(Config.HeaderColor, out var color))
        {
            Config.HeaderColor = color;
            ApplyStyle();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OpacityMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || !double.TryParse(item.Tag?.ToString(), out var percent)) return;
        Config.Opacity = Math.Clamp(percent / 100.0, 0.0, 1.0);
        ApplyStyle();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        Config.SortMode = item.Tag?.ToString() ?? "None";
        LoadFolderItems();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void FenceContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        UnstackTabMenuItem.Visibility = string.IsNullOrWhiteSpace(Config.TabGroupId)
            ? Visibility.Collapsed
            : Visibility.Visible;
        foreach (var item in new[] { SortNoneMenuItem, SortNameMenuItem, SortSizeMenuItem, SortTypeMenuItem, SortModifiedMenuItem, SortCreatedMenuItem, SortCategoryMenuItem })
            item.IsChecked = string.Equals(item.Tag?.ToString(), Config.SortMode, StringComparison.OrdinalIgnoreCase);
        var opacity = (int)Math.Round(Config.Opacity * 100);
        foreach (var item in OpacityMenuItem.Items.OfType<MenuItem>())
            item.IsChecked = int.TryParse(item.Tag?.ToString(), out var value) && value == opacity;
    }

    private IReadOnlyList<FolderItem> ApplySort(IEnumerable<FolderItem> items)
    {
        return Config.SortMode switch
        {
            "Name" => items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "Size" => items.OrderBy(item => item.Size).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "ItemType" => items.OrderBy(item => item.Kind, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "Modified" => items.OrderByDescending(item => item.ModifiedAt).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "Created" => items.OrderByDescending(item => item.CreatedAt).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            "Category" => items.OrderBy(item => _autoOrganizerService.GetCategoryForPath(item.FullPath), StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => items.ToList()
        };
    }

    private void ResetStyleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Config.BackgroundColor = "#DD20242A";
        Config.HeaderColor = "#CC3F7FA8";
        Config.Opacity = 1.0;
        ApplyStyle();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void FenceControl_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        UpdateDragState(e);
    }

    private void FenceControl_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        UpdateDragState(e);
    }

    private void FenceControl_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        ClearDragHighlight();
    }

    private void FenceControl_Drop(object sender, System.Windows.DragEventArgs e)
    {
        ClearDragHighlight();
        if (!TryGetDroppedFiles(e, out var paths))
        {
            AppLogger.Log($"Fence drop ignored because item path data was unavailable: {Config.Title}");
            return;
        }
        AppLogger.Log($"Fence drop received {paths.Length} item(s): {Config.Title}");

        if (Config.IsDesktopGroup)
        {
            var desktopPaths = paths.Where(IsDirectChildOfDesktopRoot).ToArray();
            var pathsToRestore = paths.Where(path => !IsDirectChildOfDesktopRoot(path)).ToArray();
            FolderMoveResult? restoreResult = null;
            if (pathsToRestore.Length > 0)
            {
                var desktopRoot = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                restoreResult = _folderItemService.MoveIntoFolder(pathsToRestore, desktopRoot);
                if (restoreResult.Errors.Count > 0 || restoreResult.Skipped > 0)
                {
                    System.Windows.MessageBox.Show(BuildMoveSummary(restoreResult), "MiniFences", MessageBoxButton.OK,
                        restoreResult.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
                }
            }

            var assignablePaths = desktopPaths
                .Concat(restoreResult?.MovedPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (assignablePaths.Length == 0)
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var insertionIndex = GetDropInsertionIndex(e);
            Config.AssignedPaths = ItemsList.Items
                .OfType<FolderItem>()
                .Select(item => item.FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Config.SortMode = "None";
            DesktopItemsAssigned?.Invoke(this, new DesktopItemsAssignedEventArgs(assignablePaths, insertionIndex));
            e.Effects = restoreResult?.Moved > 0
                ? System.Windows.DragDropEffects.Move
                : System.Windows.DragDropEffects.Link;
            e.Handled = true;
            return;
        }

        var result = _folderItemService.MoveIntoFolder(paths, Config.FolderPath);
        if (result.Errors.Count > 0)
        {
            var message = BuildMoveSummary(result);
            System.Windows.MessageBox.Show(message, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadFolderItems();
            return;
        }

        LoadFolderItems();
        if (result.Skipped > 0)
        {
            System.Windows.MessageBox.Show(BuildMoveSummary(result), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private int GetDropInsertionIndex(System.Windows.DragEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return ItemsList.Items.Count;
        var container = FindVisualParent<System.Windows.Controls.ListViewItem>(source);
        if (container == null) return ItemsList.Items.Count;

        var index = ItemsList.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return ItemsList.Items.Count;
        var point = e.GetPosition(container);
        var insertAfter = point.X >= container.ActualWidth / 2;
        return Math.Clamp(index + (insertAfter ? 1 : 0), 0, ItemsList.Items.Count);
    }

    private void UpdateDragState(System.Windows.DragEventArgs e)
    {
        if (!TryGetDroppedFiles(e, out var paths))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            ClearDragHighlight();
            return;
        }

        e.Effects = Config.IsDesktopGroup && paths.All(IsDirectChildOfDesktopRoot)
            ? System.Windows.DragDropEffects.Link
            : System.Windows.DragDropEffects.Move;
        e.Handled = true;
        OuterBorder.BorderBrush = System.Windows.Media.Brushes.DeepSkyBlue;
        OuterBorder.BorderThickness = new Thickness(2);
    }

    private static bool IsDirectChildOf(string path, string folder)
    {
        try
        {
            return string.Equals(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)), System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsDirectChildOfDesktopRoot(string path)
    {
        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }
            .Where(folder => !string.IsNullOrWhiteSpace(folder) && System.IO.Directory.Exists(folder))
            .Any(folder => IsDirectChildOf(path, folder));
    }

    private string BuildMoveSummary(FolderMoveResult result)
    {
        var parts = new List<string>
        {
            string.Format(_loc.T("MovedItems"), result.Moved)
        };
        if (result.Skipped > 0)
        {
            parts.Add(string.Format(_loc.T("SkippedItems"), result.Skipped));
        }

        if (result.Errors.Count > 0)
        {
            parts.Add($"{_loc.T("Errors")}\n{string.Join("\n", result.Errors.Take(8))}");
            if (result.Errors.Count > 8)
            {
                parts.Add(string.Format(_loc.T("MoreErrors"), result.Errors.Count - 8));
            }
        }

        return string.Join("\n", parts);
    }

    private static bool TryGetDroppedFiles(System.Windows.DragEventArgs e, out string[] paths)
    {
        return DesktopDragData.TryGetPaths(e.Data, out paths);
    }

    private void ClearDragHighlight()
    {
        OuterBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        OuterBorder.BorderThickness = Config.UseCleanStyle ? new Thickness(0) : new Thickness(1);
    }

    private void ApplyStyle()
    {
        OuterBorder.Background = System.Windows.Media.Brushes.Transparent;
        OuterBorder.BorderThickness = Config.UseCleanStyle ? new Thickness(0) : new Thickness(1);
        ContentBackground.Background = BrushFromString(Config.BackgroundColor, "#DD20242A");
        ContentBackground.Opacity = Math.Clamp(Config.Opacity, 0.0, 1.0);
        ItemsList.Background = Config.UseCleanStyle
            ? System.Windows.Media.Brushes.Transparent
            : BrushFromString("#16FFFFFF", "#16FFFFFF");
        TitleBar.Background = BrushFromString(Config.HeaderColor, "#CC3F7FA8");
        TitleBar.BorderBrush = Config.UseCleanStyle
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            : System.Windows.Media.Brushes.Transparent;
        TitleBar.BorderThickness = Config.UseCleanStyle ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        TitleText.HorizontalAlignment = Config.TitleAlignment switch
        {
            "Center" => System.Windows.HorizontalAlignment.Center,
            "Right" => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Left
        };
        TitleText.TextAlignment = Config.TitleAlignment switch
        {
            "Center" => TextAlignment.Center,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };
        TitleText.Margin = Config.TitleAlignment == "Center"
            ? new Thickness(48, 0, 48, 0)
            : Config.TitleAlignment == "Right"
                ? new Thickness(48, 0, 12, 0)
                : new Thickness(12, 0, 48, 0);
        StatusText.Visibility = Config.ShowPath ? Visibility.Visible : Visibility.Collapsed;
        Opacity = 1.0;
    }

    private void ToggleCollapsed()
    {
        if (!RollupEnabled) return;
        if (!Config.IsCollapsed)
        {
            Config.ExpandedHeight = Math.Max(ExpandedMinHeight, Height);
        }

        _isHoverExpanded = false;
        Config.IsCollapsed = !Config.IsCollapsed;
        ApplyCollapsedState();
        SyncConfigFromLayout();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void FenceControl_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!IsVisuallyCollapsed && !Config.IsLocked) ResizeThumb.Visibility = Visibility.Visible;
        SetHoverExpanded(true);
    }

    private void FenceControl_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isResizing) ResizeThumb.Visibility = Visibility.Collapsed;
        SetHoverExpanded(false);
    }

    private void SetHoverExpanded(bool expanded)
    {
        if (_isDragging || !RollupEnabled || !Config.IsCollapsed ||
            !ShouldHoverExpand(Config, HoverTitleToExpandEnabled) || _isHoverExpanded == expanded)
        {
            return;
        }

        _isHoverExpanded = expanded;
        ApplyCollapsedState();
        // The desktop host uses a Win32 region for hit testing. Hover expansion
        // changes the visual height without changing the saved collapsed state,
        // so notify the host to resize that region immediately.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static bool ShouldHoverExpand(FenceConfig config, bool dockedHoverEnabled) =>
        config.EnableHoverExpand || (dockedHoverEnabled && !string.IsNullOrWhiteSpace(config.EdgeDock));

    private void ApplyCollapsedState()
    {
        var isVisuallyCollapsed = Config.IsCollapsed && !_isHoverExpanded;
        MinHeight = isVisuallyCollapsed ? CollapsedHeight : ExpandedMinHeight;
        ContentArea.Visibility = isVisuallyCollapsed ? Visibility.Collapsed : Visibility.Visible;
        var showFooter = !isVisuallyCollapsed && !Config.UseCleanStyle;
        FooterPanel.Visibility = showFooter ? Visibility.Visible : Visibility.Collapsed;
        ResizeThumb.Visibility = !isVisuallyCollapsed && !Config.IsLocked && (IsMouseOver || _isResizing)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ContentRow.Height = isVisuallyCollapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FooterRow.Height = showFooter ? new GridLength(22) : new GridLength(0);
        Height = isVisuallyCollapsed
            ? CollapsedHeight
            : Math.Max(ExpandedMinHeight, Config.ExpandedHeight ?? Config.Height);
        if (Parent is Canvas canvas && canvas.ActualHeight > 0)
        {
            if (string.Equals(Config.EdgeDock, "Top", StringComparison.OrdinalIgnoreCase))
                Canvas.SetTop(this, 0);
            else if (string.Equals(Config.EdgeDock, "Bottom", StringComparison.OrdinalIgnoreCase))
                Canvas.SetTop(this, Math.Max(0, canvas.ActualHeight - Height));
        }
        TitleBar.CornerRadius = isVisuallyCollapsed
            ? new CornerRadius(8)
            : new CornerRadius(8, 8, 0, 0);

        // A Canvas does not always immediately remeasure a child after only its
        // row definitions change. Settle the visual tree before a later drag can
        // make a delayed expansion appear.
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
    }

    internal void RefreshAppearance()
    {
        ApplyStyle();
        ApplyCollapsedState();
    }

    internal void DockAndRollUp(string edge)
    {
        if (!RollupEnabled) return;
        if (!Config.IsCollapsed) Config.ExpandedHeight = Math.Max(ExpandedMinHeight, Height);
        Config.EdgeDock = edge;
        Config.IsCollapsed = true;
        _isHoverExpanded = false;
        ApplyCollapsedState();
        SyncConfigFromLayout();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void ClearEdgeDock() => Config.EdgeDock = null;

    internal void SetMergePreview(bool active)
    {
        MergePreview.Opacity = 0;
    }

    internal void SetMergeSourcePreview(bool active, double compactWidth = 0)
    {
        if (!active)
        {
            if (!_isMergeCompactPreview) return;
            OuterBorder.BeginAnimation(WidthProperty, null);
            OuterBorder.BeginAnimation(HeightProperty, null);
            OuterBorder.ClearValue(WidthProperty);
            OuterBorder.ClearValue(HeightProperty);
            OuterBorder.Margin = new Thickness(0);
            OuterBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            OuterBorder.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
            _isMergeCompactPreview = false;
            return;
        }
        if (_isMergeCompactPreview) return;

        _isMergeCompactPreview = true;
        var originalWidth = ActualWidth > 0 ? ActualWidth : Width;
        var originalHeight = ActualHeight > 0 ? ActualHeight : Height;
        OuterBorder.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        OuterBorder.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        var targetWidth = Math.Max(96, compactWidth);
        var previewLeft = Math.Clamp(_titlePointerOffset.X - targetWidth / 2, 0, Math.Max(0, ActualWidth - targetWidth));
        OuterBorder.Margin = new Thickness(previewLeft, 0, 0, 0);
        OuterBorder.BeginAnimation(WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(
            originalWidth, targetWidth, TimeSpan.FromMilliseconds(180))
        { FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd });
        OuterBorder.BeginAnimation(HeightProperty, new System.Windows.Media.Animation.DoubleAnimation(
            originalHeight, CollapsedHeight, TimeSpan.FromMilliseconds(180))
        { FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd });
    }

    internal void ToggleCollapsedForTesting()
    {
        ToggleCollapsed();
    }

    internal void DoubleClickTitleBarForTesting()
    {
        ToggleCollapsed();
    }

    private void OpenItem(FolderItem item)
    {
        if (!_folderItemService.TryOpen(item, out var error))
        {
            System.Windows.MessageBox.Show(FormatFileOperationError(error, "CouldNotOpenItem"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private bool TryGetActionItem(object sender, out FolderItem item)
    {
        if (TryGetMenuItemContext(sender, out var contextItem) && contextItem != null)
        {
            item = contextItem;
            return true;
        }

        if (ItemsList.SelectedItem is FolderItem selectedItem)
        {
            item = selectedItem;
            return true;
        }

        item = null!;
        return false;
    }

    private IReadOnlyList<FolderItem> GetSelectedItems()
    {
        return ItemsList.SelectedItems
            .OfType<FolderItem>()
            .Where(item => !string.IsNullOrWhiteSpace(item.FullPath))
            .ToList();
    }

    private IReadOnlyList<FolderItem> GetActionItems(object sender)
    {
        if (!TryGetActionItem(sender, out var item))
        {
            return [];
        }

        var selectedItems = GetSelectedItems();
        return selectedItems.Any(selected => string.Equals(selected.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
            ? selectedItems
            : [item];
    }

    private IReadOnlyList<FolderItem> GetDragItems(FolderItem pendingItem)
    {
        var selectedItems = GetSelectedItems();
        return selectedItems.Any(item => string.Equals(item.FullPath, pendingItem.FullPath, StringComparison.OrdinalIgnoreCase))
            ? selectedItems
            : [pendingItem];
    }

    private string FormatFileOperationError(string? error, string fallbackKey)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return _loc.T(fallbackKey);
        }

        const string pathMissingPrefix = "Path does not exist:";
        if (error.StartsWith(pathMissingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return $"{_loc.T("PathDoesNotExist")}: {error[pathMissingPrefix.Length..].Trim()}";
        }

        if (string.Equals(error, "The item no longer exists.", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.T("ItemNoLongerExists");
        }

        if (string.Equals(error, "Destination folder does not exist.", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.T("DestinationFolderMissing");
        }

        if (string.Equals(error, "Destination already exists.", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.T("DestinationAlreadyExists");
        }

        if (string.Equals(error, "Invalid item name.", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.T("InvalidItemName");
        }

        return error;
    }

    private void UpdateStatusText()
    {
        if (!System.IO.Directory.Exists(Config.FolderPath))
        {
            StatusText.Text = $"{_loc.T("FolderNotFound")}: {Config.FolderPath}";
            return;
        }

        if (!string.IsNullOrWhiteSpace(_lastLoadError))
        {
            StatusText.Text = $"{_loc.T("CouldNotLoadFolder")}: {FormatFileOperationError(_lastLoadError, "CouldNotLoadFolder")}";
            return;
        }

        StatusText.Text = $"{Config.FolderPath} - {ItemsList.Items.Count} {_loc.T("ItemCount")}";
    }

    private static bool TryGetMenuItemContext(object sender, out FolderItem? item)
    {
        item = null;
        if (sender is not System.Windows.Controls.MenuItem menuItem)
        {
            return false;
        }

        if (menuItem.DataContext is FolderItem dataItem)
        {
            item = dataItem;
            return true;
        }

        if (menuItem.Parent is ContextMenu { PlacementTarget: FrameworkElement { DataContext: FolderItem targetItem } })
        {
            item = targetItem;
            return true;
        }

        return false;
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private System.Windows.Controls.ListViewItem? FindItemContainerAtPoint(System.Windows.Point point)
    {
        for (var index = 0; index < ItemsList.Items.Count; index += 1)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not System.Windows.Controls.ListViewItem item)
            {
                continue;
            }

            var origin = item.TranslatePoint(new System.Windows.Point(0, 0), ItemsList);
            var bounds = new Rect(origin, new System.Windows.Size(item.ActualWidth, item.ActualHeight));
            if (bounds.Contains(point))
            {
                return item;
            }
        }

        return null;
    }

    private string DescribeItemBounds()
    {
        var parts = new List<string>();
        for (var index = 0; index < ItemsList.Items.Count; index += 1)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not System.Windows.Controls.ListViewItem item)
            {
                parts.Add($"{index}:<not-generated>");
                continue;
            }

            var origin = item.TranslatePoint(new System.Windows.Point(0, 0), ItemsList);
            parts.Add($"{index}:{origin.X:0.0},{origin.Y:0.0},{item.ActualWidth:0.0},{item.ActualHeight:0.0}");
        }

        return string.Join("; ", parts);
    }

    private static System.Windows.Media.Brush BrushFromString(string value, string fallback)
    {
        try
        {
            return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(value)!;
        }
        catch
        {
            return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(fallback)!;
        }
    }

    private static bool TryChooseColor(string currentColor, out string selectedColor)
    {
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true
        };

        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(currentColor);
            dialog.Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch
        {
            dialog.Color = System.Drawing.Color.FromArgb(0xDD, 0x20, 0x24, 0x2A);
        }

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            selectedColor = currentColor;
            return false;
        }

        selectedColor = $"#{dialog.Color.A:X2}{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        return true;
    }
}

public sealed class DesktopItemsAssignedEventArgs(IReadOnlyList<string> paths, int? insertionIndex = null) : EventArgs
{
    public IReadOnlyList<string> Paths { get; } = paths;
    public int? InsertionIndex { get; } = insertionIndex;
}

public sealed class DesktopItemsReleasedEventArgs(
    IReadOnlyList<string> paths,
    System.Drawing.Point screenPoint) : EventArgs
{
    public IReadOnlyList<string> Paths { get; } = paths;
    public System.Drawing.Point ScreenPoint { get; } = screenPoint;
}
