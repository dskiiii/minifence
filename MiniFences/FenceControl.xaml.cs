using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using MiniFences.Models;
using MiniFences.Services;
using Forms = System.Windows.Forms;

namespace MiniFences;

public partial class FenceControl : System.Windows.Controls.UserControl
{
    private const string TabFenceIdFormat = "MiniFences.TabFenceId";
    private const string TabPreviewContextFormat = "MiniFences.TabDragPreviewContext";
    private const string TabIndexFormat = "MiniFences.TabIndex";
    private const string TabExternalMoveAllowedFormat = "MiniFences.TabExternalMoveAllowed";
    private const double ExpandedMinHeight = 180;
    internal const double CollapsedHeight = 34;
    private const double ResizeHandleRevealDistance = 32;
    private const int TabReorderAnimationDurationMs = 80;
    private static FenceAppearance? _copiedStyle;
    private readonly FolderItemService _folderItemService = new();
    private readonly ShellContextMenuService _shellContextMenuService = new();
    private readonly AutoOrganizerService _autoOrganizerService = new();
    public ActionHistoryService? ActionHistory { get; set; }
    public AppConfig? ActionHistoryConfig { get; set; }
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
    private bool _resizeFromTop;
    private double _resizeBottomAnchor;
    private bool _isMergeCompactPreview;
    private double _mergePreviewLeft;
    private double _mergePreviewWidth;
    private bool _shiftDetachHeaderDrag;
    private bool _shiftHeaderDrag;
    private string? _headerDragDockEdge;
    private bool _wasItemSelectedBeforeLeftDown;
    private ModifierKeys _itemModifiersOnLeftDown;
    private ModifierKeys _expandedLabelModifiersOnLeftDown;
    private DispatcherTimer? _inlineRenameTimer;
    private FolderItem? _inlineRenameItem;
    private TextBlock? _inlineRenameLabel;
    private System.Windows.Controls.TextBox? _inlineRenameTextBox;
    private bool _isCommittingInlineRename;
    private bool _ignoreExpandedLabelMouseUp;
    private DesktopRenameWindow? _inlineRenameWindow;
    private bool _dropOperationInProgress;
    private bool _dragHighlightActive;
    private CancellationTokenSource? _iconLoadCancellation;
    private readonly Stack<string> _portalBackHistory = new();
    private IReadOnlyList<FenceConfig>? _tabConfigs;
    private bool _tabStripEqualWidths;
    private bool _tabStripAdaptiveWidths;
    private string _tabSelectionStyle = "Fill";
    private string _tabSelectionLineColor = "#FF73D7FF";
    private string _tabSelectionLineThickness = "Medium";
    private IReadOnlyList<string> _tabStripTitles = [];
    private int? _tabReorderPreviewFromIndex;
    private int? _tabReorderPreviewToIndex;
    private int? _fileDragActivationRequestedTabIndex;
    private int? _activeTabDragHiddenIndex;
    private bool _activeTabDragSlotCollapsed;
    private DataTemplate? _iconItemTemplate;

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
    public event Action<int, System.Drawing.Point>? TabDetachRequested;
    public event Action<int>? TabDragStarted;
    public event Action<int>? TabDragCanceled;
    public event Action<string, int>? TabMergeRequested;
    public event EventHandler? UnstackRequested;
    public event EventHandler<DesktopItemsAssignedEventArgs>? DesktopItemsAssigned;
    public event EventHandler<DesktopItemsReleasedEventArgs>? DesktopItemsReleased;
    public event EventHandler? DesktopItemDragStarted;
    public event EventHandler? DesktopItemDragEnded;
    public event EventHandler? ItemsChanged;
    public event EventHandler? ContentRefreshed;
    public event EventHandler? HeaderDragCompleted;
    public event EventHandler? HeaderDragMoved;
    public event EventHandler? HeaderDragCanceled;
    public event EventHandler? ItemSelectionRequested;
    public event Action<string, string>? ItemRenamed;

    public FenceConfig Config { get; }
    public bool SnapToGrid { get; set; }
    public int GridSize { get; set; } = 16;
    public bool SnapWhileDragging { get; set; }
    public bool RollupEnabled { get; set; } = true;
    public bool DoubleClickRollupEnabled { get; set; } = true;
    public bool ClickTitleToExpandEnabled { get; set; }
    public bool HoverTitleToExpandEnabled { get; set; }
    public bool BottomDockTitleAtBottom { get; set; } = true;
    public bool TopDockTitleAtBottomOnExpand { get; set; }
    internal bool IsVisuallyCollapsed => Config.IsCollapsed && !_isHoverExpanded;
    internal bool IsTitleDragging => _isDragging;
    internal bool IsShiftDetachHeaderDrag => _shiftDetachHeaderDrag;
    internal bool IsShiftHeaderDrag => _shiftHeaderDrag;
    internal double HeaderDragDeltaX { get; private set; }
    internal double HeaderDragStartLeft => _leftStart;
    internal double HeaderDragStartTop => _topStart;
    internal Func<System.Drawing.Point, bool>? IsExplorerDesktopPointForDrag { get; set; }
    internal Func<System.Drawing.Point, bool>? IsMiniFencesSurfacePointForDrag { get; set; }
    internal Func<System.Drawing.Point, Rect>? DragWorkAreaProvider { get; set; }
    internal IReadOnlyList<FolderItem> LoadedItemsForTesting =>
        ItemsList.Items.OfType<FolderItem>().ToArray();
    internal int RealizedItemCountForTesting =>
        Enumerable.Range(0, ItemsList.Items.Count)
            .Count(index => ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not null);
    internal string PortalPathForTesting => GetPortalPath();
    internal bool IsPortalNavigationVisibleForTesting => PortalNavigationBar.Visibility == Visibility.Visible;
    internal void NavigatePortalForTesting(string path) => NavigatePortal(path, true);
    internal void NavigatePortalUpForTesting() => PortalUpButton_Click(this, new RoutedEventArgs());
    internal string DisplayedTitleForTesting => TitleText.Text;
    internal bool IsCollapsedForTesting => Config.IsCollapsed;
    internal bool IsTitleAtBottomForTesting => Grid.GetRow(TitleBar) == 2;
    internal bool IsMergePreviewVisibleForTesting => MergePreview.Opacity > 0;
    internal bool IsContentVisibleForTesting => ContentArea.Visibility == Visibility.Visible &&
                                                 FooterPanel.Visibility == Visibility.Visible;
    internal System.Windows.HorizontalAlignment TitleAlignmentForTesting => TitleText.HorizontalAlignment;
    internal bool IsPathVisibleForTesting => StatusText.Visibility == Visibility.Visible;
    internal Thickness BorderThicknessForTesting => OuterBorder.BorderThickness;
    internal bool IsInnerPanelTransparentForTesting => ItemsList.Background == System.Windows.Media.Brushes.Transparent;
    internal bool IsFooterVisibleForTesting => FooterPanel.Visibility == Visibility.Visible;
    internal ScrollBarVisibility VerticalScrollBarVisibilityForTesting =>
        ScrollViewer.GetVerticalScrollBarVisibility(ItemsList);
    internal bool IsResizeHandleVisibleForTesting => ResizeThumb.Visibility == Visibility.Visible;
    internal bool IsResizeHandleAtTopForTesting => ResizeThumb.VerticalAlignment == VerticalAlignment.Top;
    internal string ResizeGripOrientationForTesting => ResizeThumb.Tag?.ToString() ?? "";
    internal bool IsManipulationLockedForTesting => Config.IsLocked;
    internal static bool IsNearResizeHandle(System.Windows.Point pointer, double width, double height, bool handleAtTop) =>
        pointer.X >= Math.Max(0, width - ResizeHandleRevealDistance) &&
        (handleAtTop
            ? pointer.Y <= ResizeHandleRevealDistance
            : pointer.Y >= Math.Max(0, height - ResizeHandleRevealDistance));
    internal bool IsTabNavigationVisibleForTesting => TabNavigationPanel.Visibility == Visibility.Visible;
    internal bool CompactTabNavigationExcludesRollupForTesting =>
        TabNavigationPanel.Children.OfType<DependencyObject>()
            .All(IsTitleBarInteractiveSource);
    internal bool TabStripHandlesRollupForTesting =>
        TabStripPanel.Children.OfType<DependencyObject>()
            .All(ShouldHandleTitleBarDoubleClick);
    internal int TabPickerItemCountForTesting => BuildTabPickerMenu().Items.OfType<MenuItem>().Count(item => item.IsCheckable);
    internal ContextMenu BuildTabPickerMenuForTesting() => BuildTabPickerMenu();
    internal Window CreateTabDragPreviewForTesting() => CreateTabDragPreview(0);
    internal bool HasFolderWatcherForTesting => _folderWatcher != null;
    internal IReadOnlyList<GridLength> TabColumnWidthsForTesting =>
        TabStripPanel.ColumnDefinitions.Select(column => column.Width).ToArray();
    internal int VisibleTabCountForTesting =>
        TabStripPanel.Children.OfType<UIElement>().Count(child => child.Visibility == Visibility.Visible);
    internal bool IsTabDragSlotPreservedForTesting(int tabIndex) =>
        TabStripPanel.Children.OfType<Border>().FirstOrDefault(tab => tab.Tag is int index && index == tabIndex) is
            { Visibility: Visibility.Visible, IsHitTestVisible: false, Opacity: < 1 };
    internal void CollapseActiveTabDragSlotForTesting(bool collapsed) =>
        SetActiveTabDragSlotCollapsed(collapsed);
    internal bool AreTabReorderTransformsResetForTesting =>
        TabStripPanel.Children.OfType<Border>().All(tab =>
            tab.RenderTransform is not System.Windows.Media.TranslateTransform transform ||
            Math.Abs(transform.X) < 0.01);
    internal bool AreRemainingTabSlotsContiguousForTesting =>
        TabStripPanel.Children.OfType<Border>()
            .Where(tab => tab.Visibility == Visibility.Visible)
            .OrderBy(Grid.GetColumn)
            .Select((tab, index) => Grid.GetColumn(tab) == index)
            .All(matches => matches);
    internal void PreviewTabReorderForTesting(int fromIndex, int toIndex) =>
        PreviewTabReorder(fromIndex, toIndex);
    internal IReadOnlyList<int> TabGridColumnsForTesting =>
        TabStripPanel.Children.OfType<Border>()
            .Where(tab => tab.Tag is int)
            .OrderBy(tab => (int)tab.Tag)
            .Select(Grid.GetColumn)
            .ToArray();
    internal bool AreTabTitlesCenteredForTesting =>
        TabStripPanel.Children.OfType<Border>()
            .Select(border => border.Child)
            .OfType<TextBlock>()
            .All(text => text.HorizontalAlignment == System.Windows.HorizontalAlignment.Stretch &&
                         text.TextAlignment == TextAlignment.Center);
    internal Thickness SelectedTabBorderThicknessForTesting =>
        TabStripPanel.Children.OfType<Border>().FirstOrDefault(tab =>
            tab.Tag is int index && index == _activeTabIndexForTesting)?.BorderThickness ?? new Thickness();
    internal System.Windows.Media.Color SelectedTabBackgroundForTesting =>
        (TabStripPanel.Children.OfType<Border>().FirstOrDefault(tab =>
            tab.Tag is int index && index == _activeTabIndexForTesting)?.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
    internal System.Windows.Media.Color SelectedTabBorderColorForTesting =>
        (TabStripPanel.Children.OfType<Border>().FirstOrDefault(tab =>
            tab.Tag is int index && index == _activeTabIndexForTesting)?.BorderBrush as SolidColorBrush)?.Color ?? Colors.Transparent;
    private int _activeTabIndexForTesting;
    internal CornerRadius FirstTabCornerRadiusForTesting =>
        TabStripPanel.Children.OfType<Border>()
            .FirstOrDefault(tab => tab.Visibility == Visibility.Visible)?.CornerRadius ?? new CornerRadius();
    internal int SelectedItemCountForTesting => ItemsList.SelectedItems.Count;
    internal bool IsDragHighlightedForTesting => _dragHighlightActive;
    internal double VerticalScrollOffsetForTesting =>
        FindVisualChild<ScrollViewer>(ItemsList)?.VerticalOffset ?? 0;
    internal bool HasIndependentRenameWindow => _inlineRenameWindow is { IsVisible: true };
    internal void CancelActiveRenameForSettings()
    {
        CancelPendingInlineRename();
        if (_inlineRenameItem != null) EndInlineRename();
    }
    internal bool IsListViewModeForTesting =>
        string.Equals(Config.PortalViewMode, "List", StringComparison.OrdinalIgnoreCase) &&
        ReferenceEquals(ItemsList.ItemTemplate, ItemsList.Resources["PortalListItemTemplate"]);
    internal bool ListTemplateContainsIconsForTesting =>
        ((DataTemplate)ItemsList.Resources["PortalListItemTemplate"]).LoadContent() is DependencyObject root &&
        FindVisualChild<System.Windows.Controls.Image>(root) != null;
    internal void ApplyPortalViewForTesting() => ApplyPortalView();
    internal static (double PanelWidth, double PanelHeight, double ContainerWidth, double ContainerHeight)
        GetPortalItemLayout(bool listMode, double availableWidth, double iconSize, double spacing)
    {
        spacing = Math.Clamp(spacing, 0, 16);
        var containerWidth = listMode ? Math.Max(120, availableWidth - 12 - spacing * 2) : iconSize + 44;
        var containerHeight = listMode ? 30 : iconSize + 58;
        return (
            listMode ? Math.Max(120, availableWidth - 8) : containerWidth + spacing * 2,
            containerHeight + spacing * 2,
            containerWidth,
            containerHeight);
    }
    internal void SelectViewModeForTesting(string mode)
    {
        SetPortalViewMode(mode);
    }
    internal bool IsRenameSelectionChromeHiddenForTesting =>
        _inlineRenameItem != null && ExpandedItemLabelOverlay.Background == System.Windows.Media.Brushes.Transparent;
    internal bool IsExpandedOverlayHiddenForTesting => ExpandedItemLabelOverlay.Visibility != Visibility.Visible;
    internal bool IsEmbeddedRenameEditorVisibleForTesting => ExpandedItemRenameTextBox.Visibility == Visibility.Visible;
    internal void SetDragHighlightedForTesting() => SetDragHighlight();
    internal void SetHoverExpandedForTesting(bool expanded) => SetHoverExpanded(expanded);
    internal void SetHoverExpandedFromDesktopHost(bool expanded) => SetHoverExpanded(expanded);

    public FenceControl(FenceConfig config)
    {
        Config = config;
        InitializeComponent();
        TabStripPanel.SizeChanged += (_, _) => RefreshAdaptiveTabWidths();
        _iconItemTemplate = ItemsList.ItemTemplate;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            LoadFolderItems();
            ContentRefreshed?.Invoke(this, EventArgs.Empty);
        };
        Width = Config.Width;
        Height = Config.Height;
        TitleText.Text = Config.Title;
        ApplyStyle();
        ApplyCollapsedState();
        ItemsList.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ItemsList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
                ApplyPortalView();
        };
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
        MovePageMenuItem.Header = _loc.T("MovePage");
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
        var chinese = string.Equals(_loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
        PortalSmallIconsMenuItem.Header = chinese ? "小图标" : "Small icons";
        PortalMediumIconsMenuItem.Header = chinese ? "中等图标" : "Medium icons";
        PortalLargeIconsMenuItem.Header = chinese ? "大图标" : "Large icons";
        PortalCompactSpacingMenuItem.Header = chinese ? "紧凑间距" : "Compact spacing";
        PortalComfortableSpacingMenuItem.Header = chinese ? "舒适间距" : "Comfortable spacing";
        PortalSpaciousSpacingMenuItem.Header = chinese ? "宽松间距" : "Spacious spacing";
        PortalViewMenuItem.Header = chinese ? "视图" : "View";
        PortalIconsViewMenuItem.Header = chinese ? "图标" : "Icons";
        PortalListViewMenuItem.Header = chinese ? "列表" : "List";
        PortalBackButton.ToolTip = chinese ? "返回" : "Back";
        PortalUpButton.ToolTip = chinese ? "上级" : "Up";
        StyleMenuItem.Header = _loc.T("Style");
        CopyStyleMenuItem.Header = _loc.T("CopyStyle");
        PasteStyleMenuItem.Header = _loc.T("PasteStyle");
        ChooseBackgroundColorMenuItem.Header = _loc.T("ChooseBackgroundColor");
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
        bool hoverSwitch = false, bool equalTabWidths = false, IReadOnlyList<FenceConfig>? tabConfigs = null,
        bool adaptiveTabWidths = false, string selectionStyle = "Fill",
        string selectionLineColor = "#FF73D7FF", string selectionLineThickness = "Medium")
    {
        _activeTabIndexForTesting = index;
        _tabSelectionStyle = NormalizeTabSelectionStyle(selectionStyle);
        _tabSelectionLineColor = NormalizeTabSelectionLineColor(selectionLineColor);
        _tabSelectionLineThickness = NormalizeTabSelectionLineThickness(selectionLineThickness);
        _tabConfigs = tabConfigs;
        _tabStripEqualWidths = equalTabWidths;
        _tabStripAdaptiveWidths = adaptiveTabWidths;
        _tabStripTitles = Enumerable.Range(0, Math.Max(0, count))
            .Select(tabIndex => titles != null && tabIndex < titles.Count ? titles[tabIndex] : $"Tab {tabIndex + 1}")
            .ToArray();
        _tabReorderPreviewFromIndex = null;
        _tabReorderPreviewToIndex = null;
        _activeTabDragHiddenIndex = null;
        _activeTabDragSlotCollapsed = false;
        TabStatusText.Text = count > 1 ? $"{index + 1}/{count}" : string.Empty;
        TabNavigationPanel.Visibility = count > 1 && !useTabStrip ? Visibility.Visible : Visibility.Collapsed;
        TabStripPanel.Children.Clear();
        TabStripPanel.ColumnDefinitions.Clear();
        TabStripPanel.Visibility = count > 1 && useTabStrip ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = count > 1 && useTabStrip ? Visibility.Collapsed : Visibility.Visible;
        if (count <= 1 || !useTabStrip) return;
        for (var column = 0; column < count; column++)
            TabStripPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GetTabColumnWidth(column) });
        if (!equalTabWidths && !adaptiveTabWidths)
            TabStripPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var tabIndex = 0; tabIndex < count; tabIndex++)
        {
            var selectedIndex = tabIndex;
            System.Windows.Point? tabDragStart = null;
            var selected = tabIndex == index;
            var fillSelection = selected && _tabSelectionStyle == "Fill";
            var tab = new Border
            {
                Tag = selectedIndex,
                MinWidth = equalTabWidths || adaptiveTabWidths ? 0 : 72,
                MaxWidth = equalTabWidths || adaptiveTabWidths ? double.PositiveInfinity : 150,
                Padding = new Thickness(12, 0, 12, 0),
                Background = new SolidColorBrush(fillSelection
                    ? System.Windows.Media.Color.FromArgb(210, 255, 255, 255)
                    : System.Windows.Media.Color.FromArgb(48, 0, 0, 0)),
                BorderBrush = selected && !fillSelection
                    ? new SolidColorBrush(GetSelectionLineColor(_tabSelectionLineColor))
                    : System.Windows.Media.Brushes.Transparent,
                BorderThickness = selected
                    ? GetSelectionBorderThickness(_tabSelectionStyle, _tabSelectionLineThickness)
                    : new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                AllowDrop = true
            };
            tab.Child = new TextBlock
            {
                Text = _tabStripTitles[tabIndex],
                Foreground = new SolidColorBrush(fillSelection ? Colors.Black : Colors.White),
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            tab.MouseLeftButtonDown += (_, e) =>
            {
                if (!ShouldStartTabOperation(Keyboard.Modifiers))
                {
                    // Let the event bubble to TitleBar so an ordinary drag moves
                    // the whole combined Fence. Only Shift turns this into a tab
                    // reorder/detach gesture.
                    tabDragStart = null;
                    return;
                }
                tabDragStart = e.GetPosition(tab);
                e.Handled = true;
            };
            tab.MouseLeftButtonUp += (_, e) =>
            {
                tabDragStart = null;
                if (!_isDragging) TabSelectedRequested?.Invoke(selectedIndex);
            };
            tab.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || tabDragStart is not { } start) return;
                var current = e.GetPosition(tab);
                if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                tabDragStart = null;
                var data = new System.Windows.DataObject(TabIndexFormat, selectedIndex);
                var sourceFenceId = _tabConfigs != null && selectedIndex < _tabConfigs.Count
                    ? _tabConfigs[selectedIndex].Id
                    : Config.Id;
                data.SetData(TabFenceIdFormat, sourceFenceId);
                var externalMoveAllowed = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                data.SetData(TabExternalMoveAllowedFormat, externalMoveAllowed);
                AppLogger.Log($"Tab drag initiated. Source={sourceFenceId}; ExternalMoveAllowed={externalMoveAllowed}");
                var sourceFenceBounds = GetScreenBoundsForTabDrag();
                var sourceReorderBounds = GetScreenHeaderReorderBoundsForTabDrag();
                var dragPreview = CreateTabDragPreview(selectedIndex);
                var dragPreviewRoot = (Grid)dragPreview.Content;
                var dragPreviewFence = dragPreviewRoot.Children.OfType<FenceControl>().Single();
                var compactTabPreview = dragPreviewRoot.Children.OfType<Border>().Single();
                var sourceTabWidth = Math.Max(72, tab.ActualWidth > 0 ? tab.ActualWidth : 96);
                var sourceTabs = TabStripPanel.Children.OfType<Border>()
                    .Where(candidate => candidate.Tag is int)
                    .OrderBy(candidate => (int)candidate.Tag)
                    .ToArray();
                var sourceTabLefts = sourceTabs
                    .Select(candidate => candidate.TranslatePoint(new System.Windows.Point(), TabStripPanel).X)
                    .ToArray();
                var sourceTabWidths = sourceTabs
                    .Select(candidate => Math.Max(1, candidate.ActualWidth))
                    .ToArray();
                var dragPreviewContext = new TabDragPreviewContext(
                    dragPreview, dragPreviewFence, compactTabPreview, ResetTabReorderPreview,
                    SetActiveTabDragSlotCollapsed, sourceTabWidth, externalMoveAllowed,
                    sourceTabLefts, sourceTabWidths);
                data.SetData(TabPreviewContextFormat, dragPreviewContext);
                var dropPoint = Forms.Cursor.Position;
                void RefreshTabDragPreviewPosition(bool? shiftOverride = null)
                {
                    var shiftHeld = shiftOverride ?? dragPreviewContext.ExternalMoveAllowed;
                    dragPreviewContext.SetExternalMoveAllowed(shiftHeld);
                    var cursor = Forms.Cursor.Position;
                    var collapseSourceSlot = ShouldCollapseDraggedTabSlot(
                        shiftHeld, sourceReorderBounds,
                        new System.Windows.Point(cursor.X, cursor.Y));
                    dragPreviewContext.SetSourceSlotCollapsed(collapseSourceSlot);
                    if (shiftHeld && !collapseSourceSlot && !sourceReorderBounds.IsEmpty)
                    {
                        var localPointer = TabStripPanel.PointFromScreen(
                            new System.Windows.Point(cursor.X, cursor.Y));
                        PreviewTabReorder(selectedIndex,
                            dragPreviewContext.GetReorderTargetIndex(localPointer.X, selectedIndex));
                    }
                }
                System.Windows.GiveFeedbackEventHandler followPreview = (_, _) =>
                    RefreshTabDragPreviewPosition(dragPreviewContext.ExternalMoveAllowed);
                System.Windows.QueryContinueDragEventHandler trackShift = (_, args) =>
                    RefreshTabDragPreviewPosition(
                        (args.KeyStates & System.Windows.DragDropKeyStates.ShiftKey) != 0);
                var previewFollowTimer = new DispatcherTimer(DispatcherPriority.Send)
                {
                    Interval = TimeSpan.FromMilliseconds(8)
                };
                previewFollowTimer.Tick += (_, _) =>
                    RefreshTabDragPreviewPosition(dragPreviewContext.ExternalMoveAllowed);
                System.Threading.Timer? nativePreviewFollowTimer = null;
                System.Windows.DragDropEffects effect;
                tab.Opacity = 0;
                tab.IsHitTestVisible = false;
                try
                {
                    tab.GiveFeedback += followPreview;
                    tab.QueryContinueDrag += trackShift;
                    // Start as a tab-sized preview. It expands to the full Fence
                    // only after leaving every tab/merge target.
                    dragPreviewContext.SetMergePreview(true, sourceTabWidth);
                    RefreshTabDragPreviewPosition();
                    dragPreview.Show();
                    RefreshTabDragPreviewPosition();
                    PositionTabDragPreview(dragPreview,
                        ShouldConstrainTabDragToHeader(dragPreviewContext.ExternalMoveAllowed),
                        sourceFenceBounds);
                    var previewHandle = new WindowInteropHelper(dragPreview).Handle;
                    var previewScale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                                       ?? System.Windows.Media.Matrix.Identity;
                    nativePreviewFollowTimer = new System.Threading.Timer(_ =>
                    {
                        var (previewWidth, previewHeight) = dragPreviewContext.GetCurrentSize();
                        PositionTabDragPreviewHandle(
                            previewHandle,
                            previewWidth,
                            previewHeight,
                            previewScale.M11,
                            previewScale.M22,
                            ShouldConstrainTabDragToHeader(IsShiftKeyDown()),
                            sourceFenceBounds);
                    }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(4));
                    previewFollowTimer.Start();
                    TabDragStarted?.Invoke(selectedIndex);
                    effect = System.Windows.DragDrop.DoDragDrop(tab, data, System.Windows.DragDropEffects.Move);
                    dropPoint = Forms.Cursor.Position;
                }
                finally
                {
                    previewFollowTimer.Stop();
                    nativePreviewFollowTimer?.Dispose();
                    tab.GiveFeedback -= followPreview;
                    tab.QueryContinueDrag -= trackShift;
                    // Never expand the compact drag window as part of cleanup.
                    // A Drop handler used to restore the full Fence one frame
                    // before DoDragDrop returned, producing a large release flash.
                    dragPreview.Hide();
                    dragPreviewContext.SetSourceSlotCollapsed(false);
                    dragPreview.Close();
                    tab.Visibility = Visibility.Visible;
                    tab.Opacity = 1;
                    tab.IsHitTestVisible = true;
                    Mouse.SetCursor(System.Windows.Input.Cursors.Arrow);
                }
                if (effect == System.Windows.DragDropEffects.None)
                {
                    if (dragPreviewContext.ExternalMoveAllowed && ShouldDetachTabDrop(sourceFenceBounds, dropPoint))
                        TabDetachRequested?.Invoke(selectedIndex, dropPoint);
                    else
                        TabDragCanceled?.Invoke(selectedIndex);
                }
            };
            tab.GiveFeedback += (_, e) =>
            {
                e.UseDefaultCursors = false;
                Mouse.SetCursor(System.Windows.Input.Cursors.SizeAll);
                e.Handled = true;
            };
            tab.DragOver += (_, e) =>
            {
                var sourceFenceId = DesktopDragData.GetCachedData(e.Data, TabFenceIdFormat) as string;
                if (sourceFenceId == null && TryGetDroppedFiles(e, out var itemPaths))
                {
                    var targetConfig = _tabConfigs != null && selectedIndex < _tabConfigs.Count
                        ? _tabConfigs[selectedIndex]
                        : null;
                    var canDropItems = targetConfig != null && CanDropPathsOnTab(targetConfig, itemPaths);
                    var requestedEffect = GetRequestedDropEffect(e);
                    e.Effects = canDropItems
                        ? requestedEffect
                        : System.Windows.DragDropEffects.None;
                    e.Handled = true;
                    ClearDragHighlight();
                    if (canDropItems)
                    {
                        ActivateTabForFileDrop(selectedIndex, targetConfig!, deferUntilDragEventCompletes: true);
                        (Window.GetWindow(this) as MainWindow)?.ShowDragHint(targetConfig!.Title, itemPaths, e.Data, requestedEffect);
                    }
                    return;
                }
                var belongsToThisGroup = sourceFenceId != null && _tabConfigs?.Any(config =>
                    string.Equals(config.Id, sourceFenceId, StringComparison.OrdinalIgnoreCase)) == true;
                var externalMoveAllowed = IsExternalTabMoveAllowed(e.Data);
                var inMergeZone = IsTabMergeTargetPoint(e.GetPosition(this));
                var canMergeOrReturn = sourceFenceId != null && inMergeZone &&
                                       (belongsToThisGroup ||
                                        externalMoveAllowed && CanAcceptTabMerge(sourceFenceId));
                var canReorder = belongsToThisGroup &&
                                 DesktopDragData.GetCachedData(e.Data, TabIndexFormat) is int fromIndex && fromIndex != selectedIndex;
                if (belongsToThisGroup &&
                    DesktopDragData.GetCachedData(e.Data, TabIndexFormat) is int previewFromIndex)
                {
                    var reorderPreviewContext = DesktopDragData.GetCachedData(
                        e.Data, TabPreviewContextFormat) as TabDragPreviewContext;
                    PreviewTabReorder(previewFromIndex,
                        reorderPreviewContext?.GetReorderTargetIndex(
                            e.GetPosition(TabStripPanel).X, previewFromIndex)
                        ?? GetTabReorderTargetIndex(e.GetPosition(TabStripPanel).X, previewFromIndex));
                }
                else if (DesktopDragData.GetCachedData(e.Data, TabPreviewContextFormat) is TabDragPreviewContext externalPreviewContext)
                    externalPreviewContext.ClearReorderPreview();
                e.Effects = canMergeOrReturn || canReorder
                    ? System.Windows.DragDropEffects.Move
                    : System.Windows.DragDropEffects.None;
                if (DesktopDragData.GetCachedData(e.Data, TabPreviewContextFormat) is TabDragPreviewContext previewContext)
                    previewContext.SetMergePreview(
                        belongsToThisGroup || canMergeOrReturn,
                        belongsToThisGroup ? Math.Max(72, tab.ActualWidth) : ActualWidth / 3);
                e.Handled = true;
            };
            tab.Drop += (_, e) =>
            {
                var sourceFenceId = DesktopDragData.GetCachedData(e.Data, TabFenceIdFormat) as string;
                if (sourceFenceId == null && TryGetDroppedFiles(e, out var itemPaths))
                {
                    var targetConfig = _tabConfigs != null && selectedIndex < _tabConfigs.Count
                        ? _tabConfigs[selectedIndex]
                        : null;
                    var accepted = targetConfig != null && CanDropPathsOnTab(targetConfig, itemPaths);
                    (Window.GetWindow(this) as MainWindow)?.HideDragHint();
                    ClearDragHighlight();
                    if (accepted)
                    {
                        // A tab-header drop is only used to open the target tab.
                        // The actual file drop must happen over the content area,
                        // where GetDropInsertionIndex can preserve the pointer
                        // position instead of always appending to the end.
                        ActivateTabForFileDrop(selectedIndex, targetConfig!, deferUntilDragEventCompletes: false);
                        AppLogger.Log($"File drag hovered tab '{targetConfig!.Title}'; waiting for content-area drop to preserve insertion position.");
                    }
                    e.Effects = System.Windows.DragDropEffects.None;
                    e.Handled = true;
                    return;
                }
                var belongsToThisGroup = sourceFenceId != null && _tabConfigs?.Any(config =>
                    string.Equals(config.Id, sourceFenceId, StringComparison.OrdinalIgnoreCase)) == true;
                if (sourceFenceId != null && !belongsToThisGroup &&
                    IsExternalTabMoveAllowed(e.Data) && CanAcceptTabMerge(sourceFenceId) &&
                    IsTabMergeTargetPoint(e.GetPosition(this)))
                {
                    TabMergeRequested?.Invoke(sourceFenceId,
                        GetTabInsertionIndex(e.GetPosition(TabStripPanel).X));
                    e.Effects = System.Windows.DragDropEffects.Move;
                }
                else if (belongsToThisGroup &&
                         DesktopDragData.GetCachedData(e.Data, TabIndexFormat) is int fromIndex &&
                         (fromIndex != selectedIndex || IsTabMergeDropPoint(e.GetPosition(this))))
                {
                    var targetIndex = _tabReorderPreviewFromIndex == fromIndex &&
                                      _tabReorderPreviewToIndex is int previewTarget
                        ? previewTarget
                        : selectedIndex;
                    if (fromIndex != targetIndex) TabReorderRequested?.Invoke(fromIndex, targetIndex);
                    else TabSelectedRequested?.Invoke(fromIndex);
                    e.Effects = System.Windows.DragDropEffects.Move;
                }
                else
                {
                    e.Effects = System.Windows.DragDropEffects.None;
                }
                e.Handled = true;
            };
            if (hoverSwitch) tab.MouseEnter += (_, _) => TabSelectedRequested?.Invoke(selectedIndex);
            Grid.SetColumn(tab, selectedIndex);
            TabStripPanel.Children.Add(tab);
        }
        UpdateTabStripCornerRadii(UsesBottomTitleLayout());
        Dispatcher.BeginInvoke(RefreshAdaptiveTabWidths, DispatcherPriority.Loaded);
    }

    internal static string NormalizeTabSelectionStyle(string? style) => style switch
    {
        "Underline" => "Underline",
        "Overline" => "Overline",
        "DoubleLine" => "DoubleLine",
        _ => "Fill"
    };

    internal static Thickness GetSelectionBorderThickness(string? style) =>
        GetSelectionBorderThickness(style, "Medium");

    internal static Thickness GetSelectionBorderThickness(string? style, string? thickness)
    {
        var pixels = NormalizeTabSelectionLineThickness(thickness) switch
        {
            "Thin" => 1.0,
            "Thick" => 3.0,
            _ => 2.0
        };
        return NormalizeTabSelectionStyle(style) switch
        {
            "Underline" => new Thickness(0, 0, 0, pixels),
            "Overline" => new Thickness(0, pixels, 0, 0),
            "DoubleLine" => new Thickness(0, pixels, 0, pixels),
            _ => new Thickness(0)
        };
    }

    internal static string NormalizeTabSelectionLineColor(string? color)
    {
        var value = color switch
        {
            "Cyan" => "#FF73D7FF",
            "White" => "#FFFFFFFF",
            "Gold" => "#FFFFD166",
            "Green" => "#FF6EE7A8",
            "Pink" => "#FFFF8CCB",
            _ => color
        };
        try
        {
            if (string.IsNullOrWhiteSpace(value)) return "#FF73D7FF";
            var parsed = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            return $"#{parsed.A:X2}{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}";
        }
        catch { return "#FF73D7FF"; }
    }

    internal static string NormalizeTabSelectionLineThickness(string? thickness) => thickness switch
    {
        "Thin" => "Thin",
        "Thick" => "Thick",
        _ => "Medium"
    };

    internal static System.Windows.Media.Color GetSelectionLineColor(string? color) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
            NormalizeTabSelectionLineColor(color));

    private GridLength GetTabColumnWidth(int tabIndex)
    {
        if (_tabStripEqualWidths) return new GridLength(1, GridUnitType.Star);
        if (_tabStripAdaptiveWidths)
        {
            var title = tabIndex >= 0 && tabIndex < _tabStripTitles.Count ? _tabStripTitles[tabIndex] : string.Empty;
            var widths = GetAdaptiveTabWidths(_tabStripTitles, TabStripPanel.ActualWidth);
            return widths is not null && tabIndex >= 0 && tabIndex < widths.Count
                ? new GridLength(widths[tabIndex], GridUnitType.Pixel)
                : new GridLength(GetAdaptiveTabWeight(title), GridUnitType.Star);
        }
        return GridLength.Auto;
    }

    internal static double GetAdaptiveTabWeight(string? title)
    {
        var visualUnits = string.IsNullOrEmpty(title)
            ? 0
            : title.Sum(character => character > 0xFF ? 2 : 1);
        return Math.Clamp(visualUnits + 14, 16, 30);
    }

    internal static IReadOnlyList<double>? GetAdaptiveTabWidths(IReadOnlyList<string> titles, double availableWidth)
    {
        if (titles.Count == 0 || !double.IsFinite(availableWidth) || availableWidth <= 0) return null;
        var contentWidths = titles.Select(title =>
        {
            var units = string.IsNullOrEmpty(title) ? 0 : title.Sum(character => character > 0xFF ? 2 : 1);
            return Math.Clamp(32 + units * 7.0, 52, 220);
        }).ToArray();
        var contentTotal = contentWidths.Sum();
        if (contentTotal <= availableWidth)
        {
            var sharedRemainder = (availableWidth - contentTotal) / titles.Count;
            return contentWidths.Select(width => width + sharedRemainder).ToArray();
        }
        var scale = availableWidth / contentTotal;
        return contentWidths.Select(width => width * scale).ToArray();
    }

    private void RefreshAdaptiveTabWidths()
    {
        if (!_tabStripAdaptiveWidths || TabStripPanel.ColumnDefinitions.Count == 0) return;
        var widths = GetAdaptiveTabWidths(_tabStripTitles, TabStripPanel.ActualWidth);
        if (widths is null) return;
        for (var column = 0; column < TabStripPanel.ColumnDefinitions.Count; column++)
        {
            var originalIndex = TabStripPanel.Children.OfType<Border>()
                .FirstOrDefault(tab => Grid.GetColumn(tab) == column)?.Tag as int? ?? column;
            if (originalIndex >= 0 && originalIndex < widths.Count)
                TabStripPanel.ColumnDefinitions[column].Width = new GridLength(widths[originalIndex], GridUnitType.Pixel);
        }
    }

    private void PreviewTabReorder(int fromIndex, int toIndex)
    {
        var count = _tabConfigs?.Count ?? 0;
        if (count < 2 || fromIndex < 0 || fromIndex >= count || toIndex < 0 || toIndex >= count) return;
        if (_tabReorderPreviewFromIndex == fromIndex && _tabReorderPreviewToIndex == toIndex) return;
        var previousLeft = TabStripPanel.Children.OfType<Border>()
            .ToDictionary(tab => tab, tab => tab.TranslatePoint(new System.Windows.Point(), TabStripPanel).X);
        ClearTabReorderTransforms();
        _tabReorderPreviewFromIndex = fromIndex;
        _tabReorderPreviewToIndex = toIndex;
        AppLogger.Log($"Tab reorder preview changed. From={fromIndex}; To={toIndex}");
        var order = BuildTabReorderPreviewOrder(count, fromIndex, toIndex);
        for (var column = 0; column < count && column < TabStripPanel.ColumnDefinitions.Count; column++)
            TabStripPanel.ColumnDefinitions[column].Width = GetTabColumnWidth(order[column]);
        foreach (var tab in TabStripPanel.Children.OfType<Border>())
        {
            if (tab.Tag is not int originalIndex) continue;
            var previewIndex = order.IndexOf(originalIndex);
            if (previewIndex >= 0) Grid.SetColumn(tab, previewIndex);
        }
        TabStripPanel.UpdateLayout();
        foreach (var tab in TabStripPanel.Children.OfType<Border>())
        {
            if (!previousLeft.TryGetValue(tab, out var oldLeft) ||
                tab.Tag is int originalIndex && originalIndex == fromIndex) continue;
            var newLeft = tab.TranslatePoint(new System.Windows.Point(), TabStripPanel).X;
            var offset = oldLeft - newLeft;
            if (Math.Abs(offset) < 0.5) continue;
            var transform = tab.RenderTransform as System.Windows.Media.TranslateTransform
                            ?? new System.Windows.Media.TranslateTransform();
            tab.RenderTransform = transform;
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(offset, 0,
                    TimeSpan.FromMilliseconds(TabReorderAnimationDurationMs))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                });
        }
        UpdateTabStripCornerRadii(UsesBottomTitleLayout());
    }

    private void ResetTabReorderPreview() => ResetTabReorderPreview(animate: true);

    private void ResetTabReorderPreview(bool animate)
    {
        if (_tabReorderPreviewFromIndex == null && _tabReorderPreviewToIndex == null)
        {
            if (!animate) ClearTabReorderTransforms();
            return;
        }
        var previousLeft = animate
            ? TabStripPanel.Children.OfType<Border>()
                .ToDictionary(tab => tab, tab => tab.TranslatePoint(new System.Windows.Point(), TabStripPanel).X)
            : null;
        ClearTabReorderTransforms();
        _tabReorderPreviewFromIndex = null;
        _tabReorderPreviewToIndex = null;
        foreach (var tab in TabStripPanel.Children.OfType<Border>())
        {
            if (tab.Tag is int originalIndex) Grid.SetColumn(tab, originalIndex);
        }
        for (var column = 0; column < (_tabConfigs?.Count ?? 0) && column < TabStripPanel.ColumnDefinitions.Count; column++)
            TabStripPanel.ColumnDefinitions[column].Width = GetTabColumnWidth(column);
        TabStripPanel.UpdateLayout();
        if (!animate)
        {
            UpdateTabStripCornerRadii(UsesBottomTitleLayout());
            return;
        }
        foreach (var tab in TabStripPanel.Children.OfType<Border>())
        {
            if (previousLeft == null || !previousLeft.TryGetValue(tab, out var oldLeft)) continue;
            var newLeft = tab.TranslatePoint(new System.Windows.Point(), TabStripPanel).X;
            var offset = oldLeft - newLeft;
            if (Math.Abs(offset) < 0.5) continue;
            var transform = tab.RenderTransform as System.Windows.Media.TranslateTransform
                            ?? new System.Windows.Media.TranslateTransform();
            tab.RenderTransform = transform;
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(offset, 0,
                    TimeSpan.FromMilliseconds(TabReorderAnimationDurationMs))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                    {
                        EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                    }
                });
        }
        UpdateTabStripCornerRadii(UsesBottomTitleLayout());
    }

    private void ClearTabReorderTransforms()
    {
        foreach (var tab in TabStripPanel.Children.OfType<Border>())
        {
            if (tab.RenderTransform is not System.Windows.Media.TranslateTransform transform) continue;
            transform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            transform.X = 0;
        }
    }

    internal static List<int> BuildTabReorderPreviewOrder(int count, int fromIndex, int toIndex)
    {
        var order = Enumerable.Range(0, Math.Max(0, count)).ToList();
        if (fromIndex < 0 || fromIndex >= order.Count || toIndex < 0 || toIndex >= order.Count) return order;
        var moved = order[fromIndex];
        order.RemoveAt(fromIndex);
        order.Insert(toIndex, moved);
        return order;
    }

    private int GetTabReorderTargetIndex(double localX, int fromIndex)
    {
        var tabs = TabStripPanel.Children.OfType<Border>()
            .Where(tab => tab.Tag is int)
            .OrderBy(tab => (tab.Tag as int?) ?? 0)
            .ToArray();
        var lefts = tabs.Select(tab => tab.TranslatePoint(new System.Windows.Point(), TabStripPanel).X).ToArray();
        var widths = tabs.Select(tab => Math.Max(1, tab.ActualWidth)).ToArray();
        return CalculateTabReorderTargetIndex(localX, fromIndex, lefts, widths);
    }

    internal static int CalculateTabReorderTargetIndex(double localX, int fromIndex,
        IReadOnlyList<double> tabLefts, IReadOnlyList<double> tabWidths)
    {
        var count = Math.Min(tabLefts.Count, tabWidths.Count);
        if (count < 2 || fromIndex < 0 || fromIndex >= count) return Math.Clamp(fromIndex, 0, Math.Max(0, count - 1));
        var visibleTabs = Enumerable.Range(0, count)
            .Where(index => index != fromIndex)
            .Select(index => (Left: tabLefts[index], Width: Math.Max(1, tabWidths[index])))
            .OrderBy(tab => tab.Left)
            .ToArray();
        for (var insertionIndex = 0; insertionIndex < visibleTabs.Length; insertionIndex++)
        {
            if (localX < visibleTabs[insertionIndex].Left + visibleTabs[insertionIndex].Width / 2)
                return insertionIndex;
        }
        return visibleTabs.Length;
    }

    internal static bool ShouldAllowExternalTabMove(bool shiftHeldAtDragStart) => shiftHeldAtDragStart;

    internal static bool ShouldStartTabOperation(ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Shift) != 0;

    internal static bool ShouldConstrainTabDragToHeader(bool externalMoveAllowed) => !externalMoveAllowed;

    private static bool IsExternalTabMoveAllowed(System.Windows.IDataObject data) =>
        DesktopDragData.GetCachedData(data, TabPreviewContextFormat) is TabDragPreviewContext context
            ? context.ExternalMoveAllowed
            : DesktopDragData.GetCachedData(data, TabExternalMoveAllowedFormat) is true;

    internal static bool ShouldCompactTabDragPreview(bool inMergeZone, bool belongsToTargetGroup,
        bool canMergeIntoTarget) =>
        belongsToTargetGroup || (inMergeZone && canMergeIntoTarget);

    private void UpdateTabStripCornerRadii(bool bottomDocked)
    {
        var allTabs = TabStripPanel.Children.OfType<Border>().OrderBy(Grid.GetColumn).ToArray();
        foreach (var tab in allTabs) tab.CornerRadius = new CornerRadius();
        var tabs = allTabs.Where(tab => tab.Visibility == Visibility.Visible).ToArray();
        if (tabs.Length == 0) return;

        tabs[0].CornerRadius = bottomDocked
            ? new CornerRadius(0, 0, 0, 8)
            : new CornerRadius(8, 0, 0, 0);
        var visibleTabsFillStrip = TabStripPanel.ColumnDefinitions.Count == allTabs.Length ||
                                   TabStripPanel.ColumnDefinitions.LastOrDefault()?.Width.Value == 0;
        if (visibleTabsFillStrip)
        {
            if (tabs.Length == 1)
            {
                tabs[0].CornerRadius = bottomDocked
                    ? new CornerRadius(0, 0, 8, 8)
                    : new CornerRadius(8, 8, 0, 0);
                return;
            }
            tabs[^1].CornerRadius = bottomDocked
                ? new CornerRadius(0, 0, 8, 0)
                : new CornerRadius(0, 8, 0, 0);
        }
    }

    private Rect GetScreenBoundsForTabDrag()
    {
        try
        {
            var topLeft = PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = PointToScreen(new System.Windows.Point(Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private Rect GetScreenHeaderReorderBoundsForTabDrag()
    {
        try
        {
            var topLeft = PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = PointToScreen(new System.Windows.Point(
                Math.Max(0, ActualWidth), CollapsedHeight));
            var bounds = new Rect(topLeft, bottomRight);
            // A little vertical forgiveness prevents a one-pixel wobble along
            // the header edge. Keep the horizontal bounds exact: once the pointer
            // leaves the large title bar, reordering must stop.
            return new Rect(bounds.Left, bounds.Top - 6, bounds.Width, bounds.Height + 12);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    internal static bool ShouldDetachTabDrop(Rect sourceFenceBounds, System.Drawing.Point dropPoint,
        double tolerance = 12)
    {
        if (sourceFenceBounds.IsEmpty) return true;
        sourceFenceBounds.Inflate(Math.Max(0, tolerance), Math.Max(0, tolerance));
        return !sourceFenceBounds.Contains(new System.Windows.Point(dropPoint.X, dropPoint.Y));
    }

    internal static bool ShouldCollapseDraggedTabSlot(bool externalMoveAllowed,
        Rect sourceHeaderBounds, System.Windows.Point cursor) =>
        externalMoveAllowed && (sourceHeaderBounds.IsEmpty || !sourceHeaderBounds.Contains(cursor));

    internal void HideTabForActiveDrag(string fenceId)
    {
        if (_tabConfigs == null) return;
        var hiddenIndex = _tabConfigs.ToList().FindIndex(config =>
            string.Equals(config.Id, fenceId, StringComparison.OrdinalIgnoreCase));
        if (hiddenIndex < 0) return;
        _activeTabDragHiddenIndex = hiddenIndex;
        _activeTabDragSlotCollapsed = false;

        foreach (UIElement child in TabStripPanel.Children)
        {
            if (child is Border tab && tab.Tag is int originalIndex && originalIndex == hiddenIndex)
            {
                // Keep the column as a browser-style insertion slot. Collapsing it
                // shifts every target while the pointer is moving and makes release
                // land on the Fence background instead of the intended tab position.
                tab.Opacity = 0;
                tab.IsHitTestVisible = false;
            }
        }
        UpdateTabStripCornerRadii(UsesBottomTitleLayout());
    }

    private void SetActiveTabDragSlotCollapsed(bool collapsed)
    {
        if (_activeTabDragHiddenIndex is not int hiddenIndex ||
            _activeTabDragSlotCollapsed == collapsed) return;
        // Slot collapse/restore is a layout state change, not another reorder.
        // Reset synchronously so the old horizontal animation cannot flash in the
        // opposite direction or accumulate into an overshoot.
        ResetTabReorderPreview(animate: false);
        var tab = TabStripPanel.Children.OfType<Border>()
            .FirstOrDefault(candidate => candidate.Tag is int originalIndex && originalIndex == hiddenIndex);
        if (tab == null) return;

        tab.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        tab.Opacity = 0;
        tab.IsHitTestVisible = false;
        var allTabs = TabStripPanel.Children.OfType<Border>()
            .Where(candidate => candidate.Tag is int)
            .OrderBy(candidate => (int)candidate.Tag)
            .ToArray();
        if (collapsed)
        {
            var nextColumn = 0;
            foreach (var remaining in allTabs.Where(candidate => !ReferenceEquals(candidate, tab)))
            {
                Grid.SetColumn(remaining, nextColumn);
                TabStripPanel.ColumnDefinitions[nextColumn].Width =
                    GetTabColumnWidth(remaining.Tag is int originalIndex ? originalIndex : nextColumn);
                nextColumn++;
            }
            Grid.SetColumn(tab, Math.Max(0, allTabs.Length - 1));
            if (allTabs.Length - 1 < TabStripPanel.ColumnDefinitions.Count)
                TabStripPanel.ColumnDefinitions[allTabs.Length - 1].Width = new GridLength(0);
        }
        else
        {
            foreach (var candidate in allTabs)
                Grid.SetColumn(candidate, (int)candidate.Tag);
            for (var column = 0; column < allTabs.Length && column < TabStripPanel.ColumnDefinitions.Count; column++)
                TabStripPanel.ColumnDefinitions[column].Width = GetTabColumnWidth(column);
        }
        _activeTabDragSlotCollapsed = collapsed;
        TabStripPanel.UpdateLayout();
        UpdateTabStripCornerRadii(UsesBottomTitleLayout());
        AppLogger.Log($"Tab drag source slot state changed. Collapsed={collapsed}; HiddenIndex={hiddenIndex}");
    }

    private Window CreateTabDragPreview(int tabIndex)
    {
        var sourceConfig = _tabConfigs != null && tabIndex >= 0 && tabIndex < _tabConfigs.Count
            ? _tabConfigs[tabIndex]
            : Config;
        var previewConfig = System.Text.Json.JsonSerializer.Deserialize<FenceConfig>(
            System.Text.Json.JsonSerializer.Serialize(sourceConfig)) ?? sourceConfig;
        previewConfig.TabGroupId = null;
        previewConfig.Width = sourceConfig.PreTabWidth is > 0 ? sourceConfig.PreTabWidth.Value : ActualWidth;
        previewConfig.Height = sourceConfig.PreTabHeight is > 0 ? sourceConfig.PreTabHeight.Value : ActualHeight;
        previewConfig.IsCollapsed = false;
        previewConfig.EdgeDock = null;

        var previewFence = new FenceControl(previewConfig)
        {
            Width = Math.Max(180, previewConfig.Width),
            Height = Math.Max(120, previewConfig.Height),
            IsHitTestVisible = false,
            Opacity = 0.9
        };
        previewFence.SetLocalization(_loc);
        previewFence.SetTabStatus(1, 0);
        previewFence.LoadFolderItems();

        var compactTabPreview = new Border
        {
            Height = CollapsedHeight,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = BrushFromString(previewConfig.HeaderColor, "#F03F7FA8"),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xBB, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 10, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = sourceConfig.Title,
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var previewRoot = new Grid { IsHitTestVisible = false };
        previewRoot.Children.Add(previewFence);
        previewRoot.Children.Add(compactTabPreview);

        var preview = new Window
        {
            Width = previewFence.Width,
            Height = previewFence.Height,
            Content = previewRoot,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowActivated = false,
            ShowInTaskbar = false,
            Topmost = true,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.Manual
        };
        preview.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(preview).Handle;
            var style = GetWindowLong(handle, GwlExStyle);
            SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);
        };
        return preview;
    }

    internal static FenceControl? GetTabDragPreviewFenceForTesting(Window preview) =>
        preview.Content switch
        {
            FenceControl fence => fence,
            Grid root => root.Children.OfType<FenceControl>().FirstOrDefault(),
            _ => null
        };

    private void PositionTabDragPreview(Window preview, bool constrainToSourceHeader, Rect sourceFenceBounds)
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(preview).Handle;
        if (handle == IntPtr.Zero) return;
        var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                       ?? System.Windows.Media.Matrix.Identity;
        PositionTabDragPreviewHandle(handle, preview.Width, preview.Height,
            toDevice.M11, toDevice.M22, constrainToSourceHeader, sourceFenceBounds);
    }

    private static void PositionTabDragPreviewHandle(IntPtr handle, double width, double height,
        double scaleX, double scaleY, bool constrainToSourceHeader, Rect sourceFenceBounds)
    {
        if (handle == IntPtr.Zero) return;
        var cursor = Forms.Cursor.Position;
        var widthPixels = Math.Max(1, (int)Math.Round(width * scaleX));
        var heightPixels = Math.Max(1, (int)Math.Round(height * scaleY));
        var workArea = Forms.Screen.FromPoint(cursor).WorkingArea;
        var requestedLeft = cursor.X - widthPixels / 2;
        var requestedTop = cursor.Y - Math.Max(1, (int)Math.Round(17 * scaleY));
        var horizontalLeft = constrainToSourceHeader && !sourceFenceBounds.IsEmpty
            ? (int)Math.Round(sourceFenceBounds.Left)
            : workArea.Left;
        var horizontalRight = constrainToSourceHeader && !sourceFenceBounds.IsEmpty
            ? (int)Math.Round(sourceFenceBounds.Right)
            : workArea.Right;
        var left = Math.Clamp(requestedLeft, horizontalLeft, Math.Max(horizontalLeft, horizontalRight - widthPixels));
        var top = constrainToSourceHeader && !sourceFenceBounds.IsEmpty
            ? (int)Math.Round(sourceFenceBounds.Top)
            : Math.Clamp(requestedTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - heightPixels));
        SetWindowPos(
            handle,
            HwndTopmost,
            left,
            top,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private sealed class TabDragPreviewContext
    {
        private readonly Window _window;
        private readonly FenceControl _fence;
        private readonly Border _compactTab;
        private readonly Action _clearReorderPreview;
        private readonly Action<bool> _setSourceSlotCollapsed;
        private readonly double _fullWidth;
        private readonly double _fullHeight;
        private readonly IReadOnlyList<double> _sourceTabLefts;
        private readonly IReadOnlyList<double> _sourceTabWidths;
        private bool _isCompact;
        private double _compactWidth;
        private double _currentWidth;
        private double _currentHeight;

        public double SourceTabWidth { get; }
        public bool ExternalMoveAllowed { get; private set; }

        public TabDragPreviewContext(Window window, FenceControl fence, Border compactTab,
            Action clearReorderPreview, Action<bool> setSourceSlotCollapsed,
            double sourceTabWidth, bool externalMoveAllowed,
            IReadOnlyList<double> sourceTabLefts, IReadOnlyList<double> sourceTabWidths)
        {
            _window = window;
            _fence = fence;
            _compactTab = compactTab;
            _clearReorderPreview = clearReorderPreview;
            _setSourceSlotCollapsed = setSourceSlotCollapsed;
            SourceTabWidth = Math.Max(72, sourceTabWidth);
            ExternalMoveAllowed = externalMoveAllowed;
            _sourceTabLefts = sourceTabLefts.ToArray();
            _sourceTabWidths = sourceTabWidths.ToArray();
            _fullWidth = window.Width;
            _fullHeight = window.Height;
            _currentWidth = _fullWidth;
            _currentHeight = _fullHeight;
        }

        public void SetExternalMoveAllowed(bool allowed)
        {
            if (ExternalMoveAllowed == allowed) return;
            ExternalMoveAllowed = allowed;
            AppLogger.Log($"Tab drag Shift state changed. ExternalMoveAllowed={allowed}");
            if (!allowed) SetMergePreview(true, SourceTabWidth);
        }

        public void SetMergePreview(bool active, double compactWidth)
        {
            // Without Shift this is a browser-style reorder only: never turn the
            // floating tab into a detachable full Fence while it is being sorted.
            if (!ExternalMoveAllowed) active = true;
            // Never resize the floating tab to the hovered target or to a fixed
            // fallback. Its width must match the hidden source slot, otherwise a
            // middle tab overlaps both neighbours and edge tabs spill outward.
            var requestedCompactWidth = SourceTabWidth;
            if (_isCompact == active && (!active || Math.Abs(_compactWidth - requestedCompactWidth) < 0.5)) return;
            _isCompact = active;
            _compactWidth = requestedCompactWidth;
            var targetWidth = active ? requestedCompactWidth : _fullWidth;
            var targetHeight = active ? CollapsedHeight : _fullHeight;
            Volatile.Write(ref _currentWidth, targetWidth);
            Volatile.Write(ref _currentHeight, targetHeight);
            _window.BeginAnimation(Window.WidthProperty, null);
            _window.BeginAnimation(Window.HeightProperty, null);
            _window.Width = targetWidth;
            _window.Height = targetHeight;
            _fence.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            _compactTab.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active)
            {
                _compactTab.BeginAnimation(OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.55, 0.96, TimeSpan.FromMilliseconds(90)));
            }
            else
            {
                _compactTab.BeginAnimation(OpacityProperty, null);
            }
            _window.UpdateLayout();
        }

        public void ClearReorderPreview() => _clearReorderPreview();
        public void SetSourceSlotCollapsed(bool collapsed) => _setSourceSlotCollapsed(collapsed);
        public int GetReorderTargetIndex(double localX, int fromIndex) =>
            CalculateTabReorderTargetIndex(localX, fromIndex, _sourceTabLefts, _sourceTabWidths);
        public (double Width, double Height) GetCurrentSize() =>
            (Volatile.Read(ref _currentWidth), Volatile.Read(ref _currentHeight));
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int VkShift = 0x10;

    private static bool IsShiftKeyDown() => (GetAsyncKeyState(VkShift) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

    private string? _lastScrollContentPath;

    public void LoadFolderItems()
    {
        var contentPath = GetPortalPath();
        var viewer = FindVisualChild<ScrollViewer>(ItemsList);
        var preserveScroll = string.Equals(_lastScrollContentPath, contentPath, StringComparison.OrdinalIgnoreCase);
        var vertical = preserveScroll ? viewer?.VerticalOffset ?? 0 : 0;
        var horizontal = preserveScroll ? viewer?.HorizontalOffset ?? 0 : 0;
        LoadFolderItemsCore();
        _lastScrollContentPath = GetPortalPath();
        if (!string.Equals(contentPath, _lastScrollContentPath, StringComparison.OrdinalIgnoreCase))
            vertical = horizontal = 0;
        if (IsLoaded)
        {
            ItemsList.UpdateLayout();
            viewer = FindVisualChild<ScrollViewer>(ItemsList);
            viewer?.ScrollToVerticalOffset(vertical);
            viewer?.ScrollToHorizontalOffset(horizontal);
            ItemsList.UpdateLayout();
            UpdateExpandedItemLabelOverlay();
        }
    }

    private void LoadFolderItemsCore()
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
            if (!IsListMode()) BeginAsyncIconLoad(ItemsList.ItemsSource.Cast<FolderItem>().ToArray());
            ApplyPortalView();
            AppLogger.Log($"Loading desktop group '{Config.Title}' with {existingItems.Count} assigned item(s).");
            UpdateStatusText();
            if (!previousPaths.SequenceEqual(Config.AssignedPaths, StringComparer.OrdinalIgnoreCase)) Changed?.Invoke(this, EventArgs.Empty);
            return;
        }


        StatusText.Visibility = Visibility.Collapsed;
        PortalNavigationBar.Visibility = Visibility.Visible;
        ItemsList.Visibility = Visibility.Visible;
        ContentArea.IsHitTestVisible = true;

        var portalPath = GetPortalPath();
        if (!System.IO.Directory.Exists(portalPath))
        {
            Config.PortalCurrentPath = Config.FolderPath;
            portalPath = Config.FolderPath;
            AutoOrganizerService.TryEnsureManagedCategoryFolder(portalPath, out _, out _);
        }

        if (!System.IO.Directory.Exists(portalPath))
        {
            AppLogger.Log($"Fence folder missing: {portalPath}");
            StopFolderWatcher();
            ItemsList.ItemsSource = Array.Empty<FolderItem>();
            _lastLoadError = $"Path does not exist: {portalPath}";
            UpdateStatusText();
            UpdatePortalNavigation();
            return;
        }

        EnsureFolderWatcher();
        AppLogger.Log($"Loading Fence '{Config.Title}' from folder: {portalPath}");
        if (!_folderItemService.TryLoadItems(portalPath, out var items, out var error))
        {
            _lastLoadError = error;
            ItemsList.ItemsSource = Array.Empty<FolderItem>();
            UpdateStatusText();
            return;
        }

        _lastLoadError = null;
        ItemsList.ItemsSource = ApplySort(items);
        if (!IsListMode()) BeginAsyncIconLoad(ItemsList.ItemsSource.Cast<FolderItem>().ToArray());
        ApplyPortalView();
        UpdatePortalNavigation();
        UpdateStatusText();
    }

    private string GetPortalPath()
    {
        if (Config.IsDesktopGroup || string.IsNullOrWhiteSpace(Config.PortalCurrentPath)) return Config.FolderPath;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Config.FolderPath));
            var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Config.PortalCurrentPath));
            var relative = Path.GetRelativePath(root, current);
            return relative != ".." &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? current
                : root;
        }
        catch { return Config.FolderPath; }
    }

    private void NavigatePortal(string path, bool addToHistory)
    {
        if (Config.IsDesktopGroup || !Directory.Exists(path)) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Config.FolderPath));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var relative = Path.GetRelativePath(root, target);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
        var current = GetPortalPath();
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return;
        if (addToHistory) _portalBackHistory.Push(current);
        Config.PortalCurrentPath = target;
        LoadFolderItems();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PortalBackButton_Click(object sender, RoutedEventArgs e)
    {
        while (_portalBackHistory.Count > 0)
        {
            var path = _portalBackHistory.Pop();
            if (!Directory.Exists(path)) continue;
            NavigatePortal(path, false);
            break;
        }
    }

    private void PortalUpButton_Click(object sender, RoutedEventArgs e)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Config.FolderPath));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetPortalPath()));
        if (string.Equals(root, current, StringComparison.OrdinalIgnoreCase)) return;
        var parent = Directory.GetParent(current)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent)) NavigatePortal(parent, true);
    }

    private void UpdatePortalNavigation()
    {
        if (Config.IsDesktopGroup) { PortalNavigationBar.Visibility = Visibility.Collapsed; return; }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Config.FolderPath));
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetPortalPath()));
        var isAtRoot = string.Equals(root, current, StringComparison.OrdinalIgnoreCase);
        PortalNavigationBar.Visibility = isAtRoot && _portalBackHistory.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusText.Visibility = PortalNavigationBar.Visibility == Visibility.Collapsed
            ? Visibility.Visible
            : Visibility.Collapsed;
        PortalBackButton.IsEnabled = _portalBackHistory.Count > 0;
        PortalUpButton.IsEnabled = !isAtRoot;
        PortalBreadcrumbPanel.Children.Clear();
        AddBreadcrumb(Path.GetFileName(root) is { Length: > 0 } rootName ? rootName : root, root);
        var relative = Path.GetRelativePath(root, current);
        if (relative != ".")
        {
            var accumulated = root;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                accumulated = Path.Combine(accumulated, segment);
                PortalBreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "›", Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 3, 0)
                });
                AddBreadcrumb(segment, accumulated);
            }
        }
    }

    private void AddBreadcrumb(string title, string path)
    {
        var button = new System.Windows.Controls.Button
        {
            Content = title, Tag = path, Padding = new Thickness(6, 2, 6, 2),
            Background = System.Windows.Media.Brushes.Transparent, Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0), MaxWidth = 130
        };
        button.Click += (_, _) => NavigatePortal((string)button.Tag, true);
        PortalBreadcrumbPanel.Children.Add(button);
    }

    private void PortalViewMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode }) return;
        SetPortalViewMode(mode);
    }

    private void PortalViewMenuItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode }) return;
        SetPortalViewMode(mode);
        FenceContextMenu.IsOpen = false;
        e.Handled = true;
    }

    private void SetPortalViewMode(string mode)
    {
        var wasListMode = IsListMode();
        Config.PortalViewMode = mode;
        AppLogger.Log($"Fence '{Config.Title}' view mode changed to {Config.PortalViewMode}.");
        ApplyPortalView();
        if (wasListMode && !IsListMode() && ItemsList.ItemsSource is not null)
        {
            // List mode intentionally skips shell icon extraction. Switching
            // back must populate the existing items immediately instead of
            // leaving every file with the generic placeholder.
            BeginAsyncIconLoad(ItemsList.ItemsSource.Cast<FolderItem>().ToArray());
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetListColumnVisibility(bool showType, bool showSize, bool showTime)
    {
        Config.ListShowType = showType;
        Config.ListShowSize = showSize;
        Config.ListShowTime = showTime;
        AppLogger.Log($"Fence '{Config.Title}' list column visibility changed: type={Config.ListShowType}, size={Config.ListShowSize}, time={Config.ListShowTime}.");
        if (!IsListMode()) return;
        ApplyListColumnVisibility();
        Dispatcher.BeginInvoke(ApplyListColumnVisibility, DispatcherPriority.Loaded);
        ItemsList.InvalidateMeasure();
    }

    private void PortalIconSizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !double.TryParse(value, out var size)) return;
        Config.PortalIconSize = Math.Clamp(size, 24, 72);
        ApplyPortalView();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PortalSpacingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string value } || !double.TryParse(value, out var spacing)) return;
        Config.PortalItemSpacing = Math.Clamp(spacing, 0, 16);
        ApplyPortalView();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyPortalView()
    {
        var listMode = IsListMode();
        ItemsList.ItemTemplate = listMode
            ? (DataTemplate)ItemsList.Resources["PortalListItemTemplate"]
            : _iconItemTemplate;
        PortalIconsViewMenuItem.IsChecked = !listMode;
        PortalListViewMenuItem.IsChecked = listMode;
        Dispatcher.BeginInvoke(() =>
        {
            var layout = GetPortalItemLayout(listMode, ItemsList.ActualWidth, Config.PortalIconSize, Config.PortalItemSpacing);
            if (FindVisualChild<Controls.VirtualizingWrapPanel>(ItemsList) is { } panel)
            {
                panel.ItemWidth = layout.PanelWidth;
                panel.ItemHeight = layout.PanelHeight;
            }
            for (var index = 0; index < ItemsList.Items.Count; index++)
            {
                if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not System.Windows.Controls.ListViewItem container) continue;
                container.Width = layout.ContainerWidth;
                container.Height = layout.ContainerHeight;
                container.Margin = new Thickness(Config.PortalItemSpacing);
                container.Tag = GetItemPresentationTag(
                    listMode,
                    container.IsSelected,
                    ItemsList.SelectedItems.Count);
            }
            if (!listMode)
                foreach (var image in FindVisualChildren<System.Windows.Controls.Image>(ItemsList))
                    image.Width = image.Height = Config.PortalIconSize;
            else
                ApplyListColumnVisibility();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyListColumnVisibility()
    {
        var widths = new Dictionary<string, GridLength>(StringComparer.Ordinal)
        {
            ["ListTypeColumn"] = Config.ListShowType ? new GridLength(52) : new GridLength(0),
            ["ListSizeColumn"] = Config.ListShowSize ? new GridLength(58) : new GridLength(0),
            ["ListTimeColumn"] = Config.ListShowTime ? new GridLength(118) : new GridLength(0)
        };

        foreach (var text in FindVisualChildren<TextBlock>(ItemsList))
        {
            if (text.Tag is not string tag || !widths.TryGetValue(tag, out var width)) continue;
            text.Visibility = width.Value > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (text.Parent is Grid row)
            {
                var column = Grid.GetColumn(text);
                if (column >= 0 && column < row.ColumnDefinitions.Count)
                    row.ColumnDefinitions[column].Width = width;
            }
        }
    }

    private bool IsListMode() =>
        string.Equals(Config.PortalViewMode, "List", StringComparison.OrdinalIgnoreCase);

    private void BeginAsyncIconLoad(IReadOnlyList<FolderItem> items)
    {
        _iconLoadCancellation?.Cancel();
        _iconLoadCancellation?.Dispose();
        _iconLoadCancellation = new CancellationTokenSource();
        var token = _iconLoadCancellation.Token;
        _ = _folderItemService.LoadIconsAsync(items, (item, icon) =>
        {
            if (icon is null || token.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested) item.Icon = icon;
            }, DispatcherPriority.Background);
        }, token).ContinueWith(task =>
        {
            if (task.Exception is not null && !token.IsCancellationRequested)
                AppLogger.LogException("Asynchronous icon loading failed.", task.Exception.GetBaseException());
        }, TaskScheduler.Default);
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
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // WPF supplies the native Windows double-click count. Keeping this out of
        // the drag path prevents a moved title bar from being mistaken for a click.
        // Full-width title tabs remain selectable on a single click, but a double-click
        // on their labels follows the same roll-up behavior as the rest of the title bar.
        if (e.ClickCount == 2 && RollupEnabled && DoubleClickRollupEnabled &&
            ShouldHandleTitleBarDoubleClick(source))
        {
            _isDragging = false;
            _isTitlePressPending = false;
            TitleBar.ReleaseMouseCapture();
            ToggleCollapsed();
            e.Handled = true;
            return;
        }

        if (IsTitleBarInteractiveSource(source))
        {
            return;
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
            _headerDragDockEdge = Config.EdgeDock;
            if (Config.IsCollapsed && !string.IsNullOrWhiteSpace(_headerDragDockEdge))
            {
                Config.IsCollapsed = false;
                _isHoverExpanded = false;
                ApplyCollapsedState();
                SyncConfigFromLayout();
                _leftStart = Canvas.GetLeft(this);
                _topStart = Canvas.GetTop(this);
                _dragStart = current;
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else if (_isHoverExpanded && Config.IsCollapsed)
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
        if (SnapToGrid && SnapWhileDragging)
        {
            var snapped = SnapPositionToGrid(new System.Windows.Point(left, top), GridSize);
            left = snapped.X;
            top = snapped.Y;
        }
        var dragArea = DragWorkAreaProvider?.Invoke(Forms.Cursor.Position) ??
                       new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
        var position = ClampHeaderDragPosition(
            new System.Windows.Point(left, top),
            dragArea,
            new System.Windows.Size(Width, Height),
            _isMergeCompactPreview
                ? new Rect(_mergePreviewLeft, 0, _mergePreviewWidth, CollapsedHeight)
                : null);
        Canvas.SetLeft(this, position.X);
        Canvas.SetTop(this, position.Y);
        if (ShouldUndockDuringHeaderDrag(_headerDragDockEdge,
                new Rect(position.X, position.Y, Width, Height), dragArea))
        {
            Config.EdgeDock = null;
            _headerDragDockEdge = null;
            ApplyCollapsedState();
        }
        HeaderDragDeltaX = Canvas.GetLeft(this) - _leftStart;
        SyncConfigFromLayout();
        HeaderDragMoved?.Invoke(this, EventArgs.Empty);
    }

    internal static System.Windows.Point SnapPositionToGrid(System.Windows.Point position, int gridSize)
    {
        var size = Math.Max(1, gridSize);
        return new System.Windows.Point(
            Math.Round(position.X / size) * size,
            Math.Round(position.Y / size) * size);
    }

    internal static System.Windows.Point ClampHeaderDragPosition(
        System.Windows.Point requested,
        System.Windows.Size canvas,
        System.Windows.Size fenceSize,
        System.Windows.Rect? visibleBounds = null)
        => ClampHeaderDragPosition(requested, new Rect(0, 0, canvas.Width, canvas.Height), fenceSize, visibleBounds);

    internal static System.Windows.Point ClampHeaderDragPosition(
        System.Windows.Point requested,
        System.Windows.Rect dragArea,
        System.Windows.Size fenceSize,
        System.Windows.Rect? visibleBounds = null)
    {
        var visible = visibleBounds ?? new Rect(0, 0, fenceSize.Width, fenceSize.Height);
        var minimumLeft = dragArea.Left - visible.Left;
        var maximumLeft = Math.Max(minimumLeft, dragArea.Right - visible.Right);
        var minimumTop = dragArea.Top - visible.Top;
        var maximumTop = Math.Max(minimumTop, dragArea.Bottom - visible.Bottom);
        return new System.Windows.Point(
            Math.Clamp(requested.X, minimumLeft, maximumLeft),
            Math.Clamp(requested.Y, minimumTop, maximumTop));
    }

    internal static bool ShouldUndockDuringHeaderDrag(
        string? dockEdge,
        Rect fenceBounds,
        Rect usableArea,
        double threshold = 12)
    {
        if (string.Equals(dockEdge, "Top", StringComparison.OrdinalIgnoreCase))
            return fenceBounds.Top > usableArea.Top + threshold;
        if (string.Equals(dockEdge, "Bottom", StringComparison.OrdinalIgnoreCase))
            return fenceBounds.Bottom < usableArea.Bottom - threshold;
        return false;
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
        _headerDragDockEdge = null;
        TitleBar.Cursor = System.Windows.Input.Cursors.Arrow;
        TitleBar.ReleaseMouseCapture();
        if (SnapToGrid)
        {
            var snapped = SnapPositionToGrid(
                new System.Windows.Point(Canvas.GetLeft(this), Canvas.GetTop(this)), GridSize);
            Canvas.SetLeft(this, snapped.X);
            Canvas.SetTop(this, snapped.Y);
        }
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
        _headerDragDockEdge = null;
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
        Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, maxWidth);
        if (_resizeFromTop)
        {
            var minimumTop = Parent is Canvas ? 0 : double.NegativeInfinity;
            var maxHeight = double.IsNegativeInfinity(minimumTop)
                ? double.PositiveInfinity
                : Math.Max(MinHeight, _resizeBottomAnchor - minimumTop);
            Height = Math.Clamp(Height - e.VerticalChange, MinHeight, maxHeight);
            Canvas.SetTop(this, _resizeBottomAnchor - Height);
        }
        else
        {
            var maxHeight = Parent is Canvas canvas2 && canvas2.ActualHeight > 0
                ? Math.Max(MinHeight, canvas2.ActualHeight - Canvas.GetTop(this))
                : double.PositiveInfinity;
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, maxHeight);
        }
        SyncConfigFromLayout();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isResizing = true;
        _resizeFromTop = ResizeThumb.VerticalAlignment == VerticalAlignment.Top;
        _resizeBottomAnchor = Canvas.GetTop(this) + Height;
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isResizing = false;
        _resizeFromTop = false;
        UpdateResizeHandleVisibility(Mouse.GetPosition(this));
    }

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AppLogger.Log($"ItemsList left button down. ClickCount={e.ClickCount}, Source={e.OriginalSource?.GetType().FullName}");
        if (e.ClickCount < 2)
        {
            if (e.OriginalSource is DependencyObject clickSource &&
                FindVisualParent<System.Windows.Controls.ListViewItem>(clickSource) == null &&
                FindVisualParent<System.Windows.Controls.Primitives.ScrollBar>(clickSource) == null)
            {
                CancelPendingInlineRename();
                if (_inlineRenameItem != null) CommitInlineRename();
                ClearItemSelection();
                AppLogger.Log("Cleared Fence item selection after clicking content blank space.");
            }
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

    private void FenceControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindVisualParent<System.Windows.Controls.ListViewItem>(source) != null ||
            ReferenceEquals(source, ExpandedItemLabelOverlay) ||
            ExpandedItemLabelOverlay.IsAncestorOf(source) ||
            FindVisualParent<System.Windows.Controls.TextBox>(source) != null)
        {
            return;
        }

        if (ItemsList.SelectedItems.Count > 0)
        {
            CancelPendingInlineRename();
            if (_inlineRenameItem != null) CommitInlineRename();
            ClearItemSelection();
            AppLogger.Log("Cleared Fence item selection after clicking outside an item.");
        }
    }

    private void ItemsList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_inlineRenameItem != null &&
            !ItemsList.SelectedItems.OfType<FolderItem>().Contains(_inlineRenameItem) &&
            !_isCommittingInlineRename)
        {
            CommitInlineRename();
        }
        UpdateItemSelectionPresentationTags();
        Dispatcher.BeginInvoke(UpdateExpandedItemLabelOverlay, DispatcherPriority.Render);
    }

    private void UpdateItemSelectionPresentationTags()
    {
        var listMode = IsListMode();
        var selectedCount = ItemsList.SelectedItems.Count;
        for (var index = 0; index < ItemsList.Items.Count; index++)
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromIndex(index) is not System.Windows.Controls.ListViewItem container) continue;
            container.Tag = GetItemPresentationTag(listMode, container.IsSelected, selectedCount);
        }
    }

    private bool IsTitleBarInteractiveSource(DependencyObject source) =>
        FindVisualParent<System.Windows.Controls.Button>(source) != null ||
        IsVisualDescendantOf(source, TabNavigationPanel) ||
        IsVisualDescendantOf(source, TabStripPanel);

    private bool ShouldHandleTitleBarDoubleClick(DependencyObject source) =>
        !IsTitleBarInteractiveSource(source) ||
        IsVisualDescendantOf(source, TabStripPanel);

    internal static string GetItemPresentationTag(bool listMode, bool isSelected, int selectedCount) =>
        listMode ? "List" : isSelected && selectedCount == 1 ? "SingleSelectedIcon" : "Icons";

    private void ItemsList_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateExpandedItemLabelOverlay, DispatcherPriority.Render);

    private void ItemLabelOverlayCanvas_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateExpandedItemLabelOverlay();

    private void UpdateExpandedItemLabelOverlay()
    {
        if (IsListMode())
        {
            SetCompactItemNameVisibility(null);
            ExpandedItemLabelOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var selectedItems = ItemsList.SelectedItems.OfType<FolderItem>().ToArray();
        if (!ShouldShowExpandedSelectionLabel(selectedItems.Length) ||
            selectedItems[0] is not { } item ||
            ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.ListViewItem container)
        {
            SetCompactItemNameVisibility(null);
            ExpandedItemLabelOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var point = container.TranslatePoint(new System.Windows.Point(0, 0), ItemLabelOverlayCanvas);
            var centeredLeft = point.X - (ExpandedItemLabelOverlay.Width - container.ActualWidth) / 2;
            var maximumLeft = Math.Max(0, ItemLabelOverlayCanvas.ActualWidth - ExpandedItemLabelOverlay.Width);
            Canvas.SetLeft(ExpandedItemLabelOverlay, Math.Clamp(centeredLeft, 0, maximumLeft));
            Canvas.SetTop(ExpandedItemLabelOverlay, point.Y);
            ExpandedItemLabelOverlay.DataContext = item;
            ExpandedItemLabelText.Text = DesktopIconLabelConverter.FormatAllLines(item.Name, 78);
            ExpandedItemLabelText.Measure(new System.Windows.Size(78, double.PositiveInfinity));
            ExpandedItemLabelOverlay.Height = Math.Max(92, 60 + ExpandedItemLabelText.DesiredSize.Height);
            var isRenamingThisItem = ReferenceEquals(_inlineRenameItem, item);
            // The expanded tile is visual-only. Let pointer input pass through
            // to the real virtualized item cells so a tall selected label can
            // never block selecting or dragging the icon below it. Re-enable
            // input only while its rename editor is open.
            ExpandedItemLabelOverlay.IsHitTestVisible = isRenamingThisItem;
            ExpandedItemLabelText.Visibility = isRenamingThisItem ? Visibility.Collapsed : Visibility.Visible;
            // When the independent top-level editor exists, the embedded
            // TextBox must remain hidden. Re-showing it here produces the
            // smaller blue rectangle seen inside the real rename editor.
            ExpandedItemRenameTextBox.Visibility = isRenamingThisItem && _inlineRenameWindow == null
                ? Visibility.Visible
                : Visibility.Collapsed;
            SetCompactItemNameVisibility(item);
            ExpandedItemLabelOverlay.Visibility = Visibility.Visible;
        }
        catch
        {
            SetCompactItemNameVisibility(null);
            ExpandedItemLabelOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SetCompactItemNameVisibility(FolderItem? expandedItem)
    {
        foreach (var text in FindVisualChildren<TextBlock>(ItemsList)
                     .Where(candidate => string.Equals(candidate.Name, "CompactItemName", StringComparison.Ordinal)))
        {
            text.Visibility = expandedItem != null && ReferenceEquals(text.DataContext, expandedItem)
                ? Visibility.Hidden
                : Visibility.Visible;
        }
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
        _itemModifiersOnLeftDown = Keyboard.Modifiers;
        _pendingDragItem = item;
        _pendingDragStart = e.GetPosition(this);
        var itemIndex = ItemsList.ItemContainerGenerator.IndexFromContainer(container);
        if ((_itemModifiersOnLeftDown & ModifierKeys.Control) != 0)
        {
            container.IsSelected = !container.IsSelected;
            _selectionAnchorIndex = itemIndex;
            container.Focus();
            e.Handled = true;
            return;
        }

        if ((_itemModifiersOnLeftDown & ModifierKeys.Shift) != 0 && _selectionAnchorIndex >= 0)
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
        if (sender is System.Windows.Controls.ListViewItem { DataContext: FolderItem labelItem } labelContainer &&
            e.GetPosition(labelContainer).Y >= 56 &&
            ShouldScheduleInlineRename(
                _wasItemSelectedBeforeLeftDown,
                ItemsList.SelectedItems.Contains(labelItem),
                _itemModifiersOnLeftDown))
        {
            ScheduleInlineRename(labelItem);
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
        _itemModifiersOnLeftDown = ModifierKeys.None;
    }

    private void ExpandedItemLabel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelPendingInlineRename();
        _pendingDragItem = null;
        _wasItemSelectedBeforeLeftDown = true;
        _expandedLabelModifiersOnLeftDown = Keyboard.Modifiers;
        _ignoreExpandedLabelMouseUp = e.ClickCount >= 2;
        if (sender is TextBlock { DataContext: FolderItem item } &&
            (_expandedLabelModifiersOnLeftDown & ModifierKeys.Control) != 0)
        {
            ItemsList.SelectedItems.Remove(item);
            _selectionAnchorIndex = ItemsList.Items.IndexOf(item);
            _ignoreExpandedLabelMouseUp = true;
            UpdateExpandedItemLabelOverlay();
        }
        else if ((_expandedLabelModifiersOnLeftDown & ModifierKeys.Shift) != 0)
        {
            _ignoreExpandedLabelMouseUp = true;
        }
        else if (_ignoreExpandedLabelMouseUp && sender is TextBlock { DataContext: FolderItem doubleClickedItem })
        {
            AppLogger.Log($"User double-clicked expanded item label: {doubleClickedItem.FullPath}");
            ItemsList.SelectedItem = doubleClickedItem;
            OpenItem(doubleClickedItem);
        }
        e.Handled = true;
    }

    private void ExpandedItemLabel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_ignoreExpandedLabelMouseUp &&
            sender is TextBlock { DataContext: FolderItem item } label &&
            ShouldScheduleInlineRename(
                _wasItemSelectedBeforeLeftDown,
                ItemsList.SelectedItems.Contains(item),
                _expandedLabelModifiersOnLeftDown))
        {
            ScheduleInlineRename(item);
        }
        _ignoreExpandedLabelMouseUp = false;
        _expandedLabelModifiersOnLeftDown = ModifierKeys.None;
        e.Handled = true;
    }

    internal static bool ShouldShowExpandedSelectionLabel(int selectedItemCount) => selectedItemCount == 1;

    internal static bool ShouldScheduleInlineRename(
        bool wasSelectedBeforeMouseDown,
        bool isStillSelected,
        ModifierKeys modifiers) =>
        wasSelectedBeforeMouseDown &&
        isStillSelected &&
        modifiers == ModifierKeys.None;

    private void ScheduleInlineRename(FolderItem item)
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
                BeginInlineRename(item);
            }
        };
        _inlineRenameTimer.Start();
    }

    private void BeginInlineRename(FolderItem item)
    {
        CancelPendingInlineRename();
        if (_inlineRenameItem != null) CommitInlineRename();
        ItemsList.SelectedItem = item;
        UpdateExpandedItemLabelOverlay();
        var label = ExpandedItemLabelText;
        var editor = ExpandedItemRenameTextBox;

        _inlineRenameItem = item;
        _inlineRenameLabel = label;
        _inlineRenameTextBox = editor;
        editor.Text = item.Name;
        ExpandedItemLabelOverlay.IsHitTestVisible = true;
        InlineRenameAppearance.Apply(editor, item.Name);
        ApplyFenceRenameEditorLayout(editor);
        label.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        ExpandedItemLabelOverlay.Background = System.Windows.Media.Brushes.Transparent;
        if (Window.GetWindow(this) is MainWindow mainWindow)
        {
            UpdateLayout();
            if (mainWindow.TryGetElementPhysicalScreenBounds(editor, out var physicalBounds))
            {
                var renameWindow = new DesktopRenameWindow(
                    item.Name,
                    physicalBounds,
                    editor.ActualWidth,
                    editor.ActualHeight,
                    InlineRenameAppearance.GetInitialSelectionLength(item.FullPath, item.Name));
                renameWindow.DiagnosticContext = "Fence";
                _inlineRenameWindow = renameWindow;
                renameWindow.TryCommitRequested = text => CommitInlineRename(text);
                renameWindow.CancelRequested = EndInlineRename;
                editor.Visibility = Visibility.Collapsed;
                renameWindow.Show();
                AppLogger.Log($"Fence rename opened in independent editor window at {physicalBounds.X},{physicalBounds.Y}.");
            }
            else
            {
                mainWindow.FocusInlineRenameEditor(editor);
                editor.Select(0, InlineRenameAppearance.GetInitialSelectionLength(item.FullPath, item.Name));
            }
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

    private void ItemRenameTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_inlineRenameItem != null && sender is System.Windows.Controls.TextBox editor)
        {
            ApplyFenceRenameEditorLayout(editor);
        }
    }

    private static void ApplyFenceRenameEditorLayout(System.Windows.Controls.TextBox editor)
    {
        editor.Width = InlineRenameAppearance.GetEditorWidth(editor, editor.Text, 78);
        editor.MinHeight = InlineRenameAppearance.EditorHeight;
        editor.TextWrapping = TextWrapping.Wrap;
        editor.TextAlignment = TextAlignment.Center;
        editor.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
        editor.AcceptsReturn = true;
        editor.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
        editor.VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Hidden;
        editor.Height = InlineRenameAppearance.MeasureWrappedHeight(editor, editor.Text, editor.Width);
        AppLogger.Log($"Fence rename editor measured. TextLength={editor.Text.Length}; Height={editor.Height:0.##}");
    }

    private void ItemRenameTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_inlineRenameItem != null) CommitInlineRename();
    }

    internal bool CommitInlineRenameIfPointerOutside(System.Windows.Point screenPoint, long mouseEventTicks = long.MaxValue)
    {
        if (_inlineRenameItem == null || _inlineRenameTextBox == null) return false;

        if (_inlineRenameWindow is { IsVisible: true } renameWindow)
        {
            if (!renameWindow.ExistedAtMouseEvent(mouseEventTicks) ||
                renameWindow.ContainsPhysicalScreenPoint(screenPoint)) return false;
            return renameWindow.RequestCommitIfPointerOutside(screenPoint);
        }

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

        return CommitInlineRename();
    }

    internal bool IsPointerInsideIndependentRename(
        System.Windows.Point screenPoint,
        long mouseEventTicks) =>
        _inlineRenameWindow is { IsVisible: true } renameWindow &&
        renameWindow.ExistedAtMouseEvent(mouseEventTicks) &&
        renameWindow.ContainsPhysicalScreenPoint(screenPoint);

    private bool CommitInlineRename(string? requestedName = null)
    {
        if (_inlineRenameItem == null || _inlineRenameTextBox == null || _isCommittingInlineRename) return false;
        _isCommittingInlineRename = true;
        try
        {
            var item = _inlineRenameItem;
            var editor = _inlineRenameTextBox;
            var newName = (requestedName ?? editor.Text).Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                System.Windows.MessageBox.Show(_loc.T("ItemNameCannotBeEmpty"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                editor.Focus();
                return false;
            }

            var originalPath = item.FullPath;
            string? renamedPath;
            string? error;
            var renameSucceeded = ActionHistory is null
                ? _folderItemService.TryRenameItem(item, newName, out renamedPath, out error)
                : ActionHistory.ExecuteRename(_folderItemService, item, newName, out renamedPath, out error);
            if (!renameSucceeded)
            {
                System.Windows.MessageBox.Show(FormatFileOperationError(error, "CouldNotRenameItem"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                editor.Focus();
                editor.SelectAll();
                return false;
            }

            if (ReplaceAssignedPathAfterRename(Config, originalPath, renamedPath))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            if (!string.IsNullOrWhiteSpace(renamedPath) &&
                !string.Equals(originalPath, renamedPath, StringComparison.OrdinalIgnoreCase))
                ItemRenamed?.Invoke(originalPath, renamedPath);
            EndInlineRename();
            LoadFolderItems();
            return true;
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
        var renameWindow = _inlineRenameWindow;
        _inlineRenameWindow = null;
        renameWindow?.CloseWithoutCommit();
        ExpandedItemLabelOverlay.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x48, 0x00, 0x78, 0xD7));
        if (editor != null && Window.GetWindow(this) is MainWindow mainWindow)
        {
            mainWindow.ReleaseInlineRenameEditor(editor);
        }
        UpdateExpandedItemLabelOverlay();
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
        UpdateExpandedItemLabelOverlay();
    }

    internal void SelectItemForTesting(int index) => ItemsList.SelectedIndex = index;
    internal void SelectItemsForTesting(params int[] indices)
    {
        ItemsList.SelectedItems.Clear();
        foreach (var index in indices.Where(index => index >= 0 && index < ItemsList.Items.Count))
            ItemsList.SelectedItems.Add(ItemsList.Items[index]);
        ItemsList.UpdateLayout();
        ApplyPortalView();
    }

    internal void ScrollItemsForTesting(double offset)
    {
        ItemsList.UpdateLayout();
        var scrollViewer = FindVisualChild<ScrollViewer>(ItemsList);
        scrollViewer?.ScrollToVerticalOffset(offset);
        ItemsList.UpdateLayout();
        UpdateExpandedItemLabelOverlay();
    }

    internal bool ExpandedOverlayTracksSelectedItemForTesting()
    {
        if (ItemsList.SelectedItem is not FolderItem item ||
            ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not System.Windows.Controls.ListViewItem container)
            return false;
        var expected = container.TranslatePoint(new System.Windows.Point(0, 0), ItemLabelOverlayCanvas);
        var centeredLeft = expected.X - (ExpandedItemLabelOverlay.Width - container.ActualWidth) / 2;
        var maximumLeft = Math.Max(0, ItemLabelOverlayCanvas.ActualWidth - ExpandedItemLabelOverlay.Width);
        var expectedLeft = Math.Clamp(centeredLeft, 0, maximumLeft);
        return Math.Abs(Canvas.GetLeft(ExpandedItemLabelOverlay) - expectedLeft) < 0.5 &&
               Math.Abs(Canvas.GetTop(ExpandedItemLabelOverlay) - expected.Y) < 0.5;
    }

    internal bool ExpandedOverlayAllowsUnderlyingItemSelectionForTesting()
    {
        UpdateExpandedItemLabelOverlay();
        return ExpandedItemLabelOverlay.Visibility == Visibility.Visible &&
               !ExpandedItemLabelOverlay.IsHitTestVisible;
    }

    internal void RaiseBlankAreaLeftClickForTesting()
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = ItemsList
        };
        ItemsList.RaiseEvent(args);
    }

    internal bool BeginItemInlineRenameForTesting(int index)
    {
        if (index < 0 || index >= ItemsList.Items.Count) return false;
        ItemsList.SelectedIndex = index;
        ItemsList.UpdateLayout();
        if (ItemsList.Items[index] is not FolderItem item) return false;
        BeginInlineRename(item);
        return true;
    }

    private void Item_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        TryStartItemDrag(e);
    }

    private void Item_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        var host = Window.GetWindow(this) as MainWindow;
        if (sender is System.Windows.Controls.ListViewItem { DataContext: FolderItem recycleTarget } recycleContainer &&
            FolderItemService.IsRecycleBinNamespacePath(recycleTarget.FullPath))
        {
            if (!TryGetDroppedFiles(e, out var recyclePaths) || !CanMoveToRecycleBin(recyclePaths))
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                host?.ClearDragTargetHint();
                return;
            }

            e.Effects = System.Windows.DragDropEffects.Move;
            host?.ShowDragHint(recycleTarget.Name, e.Data);
            e.Handled = true;
            return;
        }

        if (sender is not System.Windows.Controls.ListViewItem { DataContext: FolderItem target } container ||
            target.Kind != "Folder" ||
            !TryGetDroppedFiles(e, out var paths) ||
            !IsPointerOverFolderIcon(container, e))
        {
            host?.ClearDragTargetHint();
            return;
        }

        var canMove = CanMoveIntoFolder(paths, target.FullPath, validateFileSystem: false);
        e.Effects = canMove ? GetRequestedDropEffect(e) : System.Windows.DragDropEffects.None;
        if (canMove && host != null) host.ShowDragHint(
            target.Name,
            paths,
            e.Data,
            e.Effects);
        else if (!canMove) host?.ShowDragSourceHint(null);
        e.Handled = true;
    }

    private async void Item_Drop(object sender, System.Windows.DragEventArgs e)
    {
        (Window.GetWindow(this) as MainWindow)?.HideDragHint();
        if (sender is not System.Windows.Controls.ListViewItem { DataContext: FolderItem target } container ||
            !TryGetDroppedFiles(e, out var paths) ||
            !IsPointerOverFolderIcon(container, e)) return;

        if (FolderItemService.IsRecycleBinNamespacePath(target.FullPath))
        {
            DropToRecycleBin(e, paths);
            return;
        }

        if (!System.IO.Directory.Exists(target.FullPath)) return;

        e.Handled = true;
        if (_dropOperationInProgress || !CanMoveIntoFolder(paths, target.FullPath))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }

        var requestedEffect = GetRequestedDropEffect(e);
        if (requestedEffect == System.Windows.DragDropEffects.None)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }
        e.Effects = requestedEffect;
        AppLogger.Log($"Folder icon drop accepted asynchronously. Destination={target.FullPath}; Items={paths.Length}; Effect={requestedEffect}");
        var operation = FolderItemService.GetTransferOperation(requestedEffect);
        var result = await RunDropOperationAsync(() => operation is not null && ActionHistory != null
            ? ActionHistory.ExecuteFileTransferAsync(
                $"{FolderItemService.GetOperationDisplayName(operation.Value)}到文件夹“{target.Name}”",
                _folderItemService, paths, target.FullPath, operation.Value)
            : Task.Run(() => TransferItems(paths, target.FullPath, requestedEffect)));
        AppLogger.Log($"Folder icon drop completed. Destination={target.FullPath}; Moved={result.Moved}; Skipped={result.Skipped}; Errors={result.Errors.Count}");
        if (result.Errors.Count > 0 || result.Skipped > 0)
        {
            System.Windows.MessageBox.Show(BuildMoveSummary(result), "MiniFences", MessageBoxButton.OK,
                result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        LoadFolderItems();
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
                if (_folderItemService.TryDeleteItem(item, out var error)) moved.Add(item.FullPath);
                else failures.Add($"{item.Name}: {error}");
            }
            deleted = moved;
            errors = failures;
        }
        else
        {
            deleted = ActionHistory.ExecuteRecycleDeleteBatch(_folderItemService, items, out errors);
        }

        e.Effects = deleted.Count > 0
            ? System.Windows.DragDropEffects.Move
            : System.Windows.DragDropEffects.None;
        AppLogger.Log($"Fence Recycle Bin drop completed. Deleted={deleted.Count}; Errors={errors.Count}");
        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show(
                string.Join(Environment.NewLine, errors.Take(8)),
                "MiniFences",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (deleted.Count > 0)
        {
            LoadFolderItems();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static FolderItem CreateDropItem(string path)
    {
        var name = System.IO.Directory.Exists(path)
            ? new System.IO.DirectoryInfo(path).Name
            : System.IO.Path.GetFileName(path);
        return new FolderItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? path : name,
            FullPath = path,
            ToolTip = path
        };
    }

    private static bool IsPointerOverFolderIcon(System.Windows.Controls.ListViewItem container, System.Windows.DragEventArgs e)
    {
        return IsStableFolderDropZone(e.GetPosition(container), container.RenderSize);
    }

    private async Task<T> RunDropOperationAsync<T>(Func<Task<T>> operation)
    {
        _dropOperationInProgress = true;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            // Yield once before starting file-system work so OLE can end the
            // drag immediately and remove Explorer's drag image.
            await Task.Yield();
            return await operation();
        }
        finally
        {
            _dropOperationInProgress = false;
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    internal static bool IsFolderIconHotZone(System.Windows.Point point, System.Windows.Size iconSize)
    {
        const double tolerance = 6;
        return new Rect(
            -tolerance,
            -tolerance,
            iconSize.Width + tolerance * 2,
            iconSize.Height + tolerance * 2).Contains(point);
    }

    internal static bool IsStableFolderDropZone(System.Windows.Point point, System.Windows.Size size) =>
        new Rect(0, 0, size.Width, size.Height).Contains(point);

    internal static bool IsFolderCellGlyphDropZone(System.Windows.Point point, System.Windows.Size cellSize)
    {
        const double glyphWidth = 42;
        const double glyphHeight = 42;
        const double tolerance = 6;
        var left = (cellSize.Width - glyphWidth) / 2 - tolerance;
        var top = 4 - tolerance;
        return new Rect(left, top, glyphWidth + tolerance * 2, glyphHeight + tolerance * 2).Contains(point);
    }

    internal static bool CanMoveIntoFolder(
        IEnumerable<string> sourcePaths,
        string destinationFolder,
        bool validateFileSystem = true)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) ||
            (validateFileSystem && !System.IO.Directory.Exists(destinationFolder))) return false;
        var destination = System.IO.Path.GetFullPath(destinationFolder).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        return sourcePaths.Any(path =>
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                var source = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                return !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) &&
                       (!validateFileSystem || System.IO.File.Exists(source) || System.IO.Directory.Exists(source));
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
                           (Config.IsDesktopGroup && FolderItemService.IsShellNamespacePath(path) ||
                            System.IO.File.Exists(path) || System.IO.Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            AppLogger.Log("Item drag skipped because no selected paths exist.");
            return;
        }

        try
        {
            using var data = new ShellCompatibleDataObject();
            if (Config.IsDesktopGroup)
                DesktopDragData.Set(data, paths, looseIcon: false, paths[0]);
            else
            {
                DesktopDragData.MarkMiniFencesSource(data);
                DesktopDragData.SetFileDropList(data, paths);
            }
            var dragIcon = items.FirstOrDefault()?.Icon ?? FolderItemService.GetGuaranteedPathIcon(paths[0]);
            var host = Window.GetWindow(this) as MainWindow;
            var dragName = items.FirstOrDefault()?.Name;
            var dragLabel = string.IsNullOrWhiteSpace(dragName)
                ? dragName
                : DesktopIconLabelConverter.FormatTwoLines(dragName, 78);
            // Keep MiniFences' source image in its own topmost HWND for the
            // whole drag. Windows' Shell drag image disappears when a folder
            // cell inside our Explorer-hosted WPF desktop becomes the target.
            // The separate destination HWND can now show/hide independently
            // without rebuilding, resizing, or obscuring this source image.
            host?.ShowDragSourceHint(
                dragIcon,
                dragLabel,
                expanded: false,
                textWidth: 78,
                pinUntilClear: true);
            AppLogger.Log("Independent MiniFences drag image initialized.");
            AppLogger.Log($"Item drag started with {paths.Length} item(s): {string.Join("; ", paths)}");
            if (Config.IsDesktopGroup) DesktopItemDragStarted?.Invoke(this, EventArgs.Empty);
            System.Windows.DragDropEffects result;
            System.Windows.QueryContinueDragEventHandler? desktopDropGuard = null;
            var dragCanceledByEscape = false;
            System.Windows.QueryContinueDragEventHandler escapeFeedbackGuard = (_, e) =>
            {
                if (!e.EscapePressed) return;
                dragCanceledByEscape = true;
                e.Action = System.Windows.DragAction.Cancel;
                e.Handled = true;
                host?.ClearDragHint();
            };
            QueryContinueDrag += escapeFeedbackGuard;
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
                QueryContinueDrag -= escapeFeedbackGuard;
                if (desktopDropGuard != null) QueryContinueDrag -= desktopDropGuard;
                if (Config.IsDesktopGroup) DesktopItemDragEnded?.Invoke(this, EventArgs.Empty);
                host?.ClearDragHint();
                UpdateExpandedItemLabelOverlay();
            }
            var cursor = Forms.Cursor.Position;
            var overMiniFencesSurface = IsMiniFencesSurfacePointForDrag?.Invoke(cursor) == true;
            var overExplorerDesktop = Config.IsDesktopGroup &&
                                      !overMiniFencesSurface &&
                                      IsExplorerDesktopPointForDrag?.Invoke(cursor) == true;
            AppLogger.Log($"Item drag completed. Result={result}; ScreenPoint={cursor.X},{cursor.Y}; ExplorerDesktopCanceled={desktopDropCanceled}; EscapeCanceled={dragCanceledByEscape}");
            if (Config.IsDesktopGroup &&
                DesktopDragData.ShouldReleaseDesktopMembershipAfterDrag(result, overExplorerDesktop, dragCanceledByEscape))
            {
                DesktopItemsReleased?.Invoke(this, new DesktopItemsReleasedEventArgs(paths, cursor));
            }
        }
        catch (Exception ex)
        {
            UpdateExpandedItemLabelOverlay();
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

        var originalPath = item.FullPath;
        string? renamedPath;
        string? error;
        var renameSucceeded = ActionHistory is null
            ? _folderItemService.TryRenameItem(item, dialog.InputText, out renamedPath, out error)
            : ActionHistory.ExecuteRename(_folderItemService, item, dialog.InputText, out renamedPath, out error);
        if (!renameSucceeded)
        {
            System.Windows.MessageBox.Show(FormatFileOperationError(error, "CouldNotRenameItem"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ReplaceAssignedPathAfterRename(Config, originalPath, renamedPath))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        if (!string.IsNullOrWhiteSpace(renamedPath) &&
            !string.Equals(originalPath, renamedPath, StringComparison.OrdinalIgnoreCase))
            ItemRenamed?.Invoke(originalPath, renamedPath);
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

        List<string> errors;
        if (ActionHistory is not null)
        {
            ActionHistory.ExecuteRecycleDeleteBatch(_folderItemService, items, out var failures);
            errors = failures.ToList();
        }
        else
        {
            errors = [];
            foreach (var item in items)
            {
                if (!_folderItemService.TryDeleteItem(item, out var error))
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

        if (!_folderItemService.TryCreateFolder(GetPortalPath(), dialog.InputText, out _, out var error))
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

        bool ApplyFolderChange()
        {
            Config.FolderPath = dialog.SelectedPath;
            var desktopGroup = FolderItemService.IsDesktopRootPath(dialog.SelectedPath);
            Config.PortalCurrentPath = desktopGroup ? null : dialog.SelectedPath;
            _portalBackHistory.Clear();
            Config.Kind = desktopGroup ? FenceConfig.DesktopGroupKind : FenceConfig.FolderPortalKind;
            Config.AssignedPaths.Clear();
            if (string.Equals(Config.Title, "Desktop", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Config.Title, "Mini Fence", StringComparison.OrdinalIgnoreCase))
            {
                Config.Title = System.IO.Path.GetFileName(dialog.SelectedPath);
                TitleText.Text = Config.Title;
            }
            return true;
        }

        if (ActionHistory is not null && ActionHistoryConfig is not null)
            ActionHistory.ExecuteFenceChange($"更改 Fence“{Config.Title}”的文件夹", ActionHistoryConfig, Config, ApplyFolderChange);
        else
            ApplyFolderChange();
        LoadFolderItems();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
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
        var normalizedPath = System.IO.Path.GetFullPath(GetPortalPath());
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

    private void ChooseBackgroundColorMenuItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AppLogger.Log($"Background color menu pressed for '{Config.Title}'.");
        e.Handled = true;
        FenceContextMenu.IsOpen = false;
        BeginChooseColor(chooseHeader: false);
    }

    private void ChooseHeaderColorMenuItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AppLogger.Log($"Title color menu pressed for '{Config.Title}'.");
        e.Handled = true;
        FenceContextMenu.IsOpen = false;
        BeginChooseColor(chooseHeader: true);
    }

    private void CopyStyleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _copiedStyle = FenceAppearance.From(Config);
        PasteStyleMenuItem.IsEnabled = true;
        AppLogger.Log($"Fence style copied from '{Config.Title}'.");
    }

    private void PasteStyleMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_copiedStyle is not { } style) return;
        style.ApplyTo(Config);
        ApplyStyle();
        Changed?.Invoke(this, EventArgs.Empty);
        AppLogger.Log($"Fence style pasted to '{Config.Title}'.");
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
        PasteStyleMenuItem.IsEnabled = _copiedStyle != null;
        UnstackTabMenuItem.Visibility = string.IsNullOrWhiteSpace(Config.TabGroupId)
            ? Visibility.Collapsed
            : Visibility.Visible;
        foreach (var item in new[] { SortNoneMenuItem, SortNameMenuItem, SortSizeMenuItem, SortTypeMenuItem, SortModifiedMenuItem, SortCreatedMenuItem, SortCategoryMenuItem })
            item.IsChecked = string.Equals(item.Tag?.ToString(), Config.SortMode, StringComparison.OrdinalIgnoreCase);
        var opacity = (int)Math.Round(Config.Opacity * 100);
        foreach (var item in OpacityMenuItem.Items.OfType<MenuItem>())
            item.IsChecked = int.TryParse(item.Tag?.ToString(), out var value) && value == opacity;
    }

    private void FenceControl_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var bottomDocked = UsesBottomTitleLayout();
        FenceContextMenu.PlacementTarget = bottomDocked ? TitleBar : null;
        FenceContextMenu.Placement = GetFenceContextMenuPlacement(Config.EdgeDock, bottomDocked);
        FenceContextMenu.VerticalOffset = bottomDocked ? -4 : 0;
    }

    internal static PlacementMode GetFenceContextMenuPlacement(string? dockEdge, bool bottomTitleLayout = true) =>
        string.Equals(dockEdge, "Bottom", StringComparison.OrdinalIgnoreCase) && bottomTitleLayout
            ? PlacementMode.Top
            : PlacementMode.MousePoint;

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
        if (DesktopDragData.GetCachedData(e.Data, TabFenceIdFormat) is string sourceFenceId)
        {
            (Window.GetWindow(this) as MainWindow)?.HideDragHint();
            UpdateTabMergeDragState(e, sourceFenceId);
            return;
        }
        UpdateDragState(e);
    }

    private void FenceControl_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        // ListView/ScrollViewer may consume the bubbling OLE drag event before
        // it reaches the Fence. Handle ordinary Fence drops on the tunneling
        // route, while preserving the dedicated folder-icon hot zone below.
        if (ShouldRouteDropToFolderIcon(e)) return;
        if (TryGetTabStripIndex(e.OriginalSource as DependencyObject, out var fileDropTabIndex) &&
            TryGetDroppedFiles(e, out var fileDropPaths))
        {
            HandleFileDragOverTab(e, fileDropTabIndex, fileDropPaths);
            return;
        }
        if (DesktopDragData.GetCachedData(e.Data, TabFenceIdFormat) is string sourceFenceId)
        {
            if (IsVisualDescendantOf(e.OriginalSource as DependencyObject, TabStripPanel)) return;
            (Window.GetWindow(this) as MainWindow)?.HideDragHint();
            UpdateTabMergeDragState(e, sourceFenceId);
            return;
        }
        UpdateDragState(e);
    }

    private void FenceControl_PreviewDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (ShouldRouteDropToFolderIcon(e)) return;
        if (TryGetTabStripIndex(e.OriginalSource as DependencyObject, out var fileDropTabIndex) &&
            TryGetDroppedFiles(e, out _))
        {
            if (_tabConfigs != null && fileDropTabIndex < _tabConfigs.Count)
                ActivateTabForFileDrop(fileDropTabIndex, _tabConfigs[fileDropTabIndex], deferUntilDragEventCompletes: true);
            (Window.GetWindow(this) as MainWindow)?.HideDragHint();
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (IsVisualDescendantOf(e.OriginalSource as DependencyObject, TabStripPanel)) return;
        FenceControl_Drop(sender, e);
    }

    private void HandleFileDragOverTab(System.Windows.DragEventArgs e, int selectedIndex,
        IReadOnlyList<string> paths)
    {
        var targetConfig = _tabConfigs != null && selectedIndex < _tabConfigs.Count
            ? _tabConfigs[selectedIndex]
            : null;
        var canDropItems = targetConfig != null && CanDropPathsOnTab(targetConfig, paths);
        e.Effects = canDropItems
            ? GetRequestedDropEffect(e)
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
        ClearDragHighlight();
        if (!canDropItems) return;

        ActivateTabForFileDrop(selectedIndex, targetConfig!, deferUntilDragEventCompletes: true);
        (Window.GetWindow(this) as MainWindow)?.ShowDragHint(targetConfig!.Title, paths, e.Data, e.Effects);
    }

    private bool TryGetTabStripIndex(DependencyObject? source, out int selectedIndex)
    {
        selectedIndex = -1;
        if (source == null || !IsVisualDescendantOf(source, TabStripPanel)) return false;
        var tab = FindVisualParent<Border>(source);
        if (tab?.Parent != TabStripPanel || tab.Tag is not int tabIndex) return false;
        selectedIndex = tabIndex;
        return true;
    }

    internal static bool CanDropPathsOnTab(FenceConfig target, IReadOnlyList<string> paths) =>
        paths.Count > 0 && (target.IsDesktopGroup || paths.All(path => !FolderItemService.IsShellNamespacePath(path)));

    private bool ShouldRouteDropToFolderIcon(System.Windows.DragEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            FindVisualParent<System.Windows.Controls.ListViewItem>(source) is not
                { DataContext: FolderItem target } container ||
            (target.Kind != "Folder" && !FolderItemService.IsRecycleBinNamespacePath(target.FullPath)) ||
            !TryGetDroppedFiles(e, out _))
        {
            return false;
        }

        return IsPointerOverFolderIcon(container, e);
    }

    private void FenceControl_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (DesktopDragData.GetCachedData(e.Data, TabFenceIdFormat) is string sourceFenceId)
        {
            (Window.GetWindow(this) as MainWindow)?.HideDragHint();
            UpdateTabMergeDragState(e, sourceFenceId);
            return;
        }
        UpdateDragState(e);
    }

    private void FenceControl_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        // DragLeave is a routed event. Child ListViewItems raise it whenever the
        // pointer crosses an icon/template boundary, and that event bubbles to
        // the Fence even though the pointer is still inside this control. Treating
        // those child transitions as a real Fence exit repeatedly changed border
        // thickness and forced layout during a drag.
        var host = Window.GetWindow(this) as MainWindow;
        if (IsPointerInsideFence(Forms.Cursor.Position) &&
            (host == null || host.IsTopmostFenceAtScreenPoint(this, Forms.Cursor.Position))) return;
        if (DesktopDragData.GetCachedData(e.Data, TabPreviewContextFormat) is TabDragPreviewContext previewContext)
        {
            previewContext.ClearReorderPreview();
            previewContext.SetMergePreview(false, 0);
        }
        ClearDragHighlight();
        if (host != null && !host.IsMiniFencesWindowAtScreenPoint(Forms.Cursor.Position))
        {
            host.EndNativeShellDragImage();
            host.HideDragHint();
        }
    }

    private bool IsPointerInsideFence(System.Drawing.Point screenPoint)
    {
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return false;
        try
        {
            var local = PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            return new Rect(0, 0, ActualWidth, ActualHeight).Contains(local);
        }
        catch
        {
            return false;
        }
    }

    private async void FenceControl_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var host = Window.GetWindow(this) as MainWindow;
        if (host != null && !host.IsTopmostFenceAtScreenPoint(this, Forms.Cursor.Position))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            ClearDragHighlight();
            return;
        }
        host?.HideDragHint();
        ClearDragHighlight();
        if (DesktopDragData.GetCachedData(e.Data, TabFenceIdFormat) is string sourceFenceId)
        {
            var inMergeZone = IsTabMergeTargetPoint(e.GetPosition(this));
            if (IsExternalTabMoveAllowed(e.Data) && CanAcceptTabMerge(sourceFenceId) && inMergeZone)
            {
                AppLogger.Log($"Tab title-zone drop accepted. Source={sourceFenceId}; Target={Config.Title}");
                TabMergeRequested?.Invoke(sourceFenceId,
                    GetTabInsertionIndex(e.GetPosition(TabStripPanel).X));
                e.Effects = System.Windows.DragDropEffects.Move;
            }
            else if (BelongsToThisTabGroup(sourceFenceId))
            {
                var sourceIndex = _tabConfigs?.ToList().FindIndex(config =>
                    string.Equals(config.Id, sourceFenceId, StringComparison.OrdinalIgnoreCase)) ?? -1;
                var dragSourceIndex = DesktopDragData.GetCachedData(e.Data, TabIndexFormat) is int dataIndex
                    ? dataIndex
                    : sourceIndex;
                if (_tabReorderPreviewFromIndex == dragSourceIndex &&
                    _tabReorderPreviewToIndex is int previewTarget && previewTarget != dragSourceIndex)
                {
                    AppLogger.Log($"Tab reorder accepted from preview slot. Source={sourceFenceId}; From={dragSourceIndex}; To={previewTarget}");
                    TabReorderRequested?.Invoke(dragSourceIndex, previewTarget);
                }
                else
                {
                    if (sourceIndex >= 0) TabSelectedRequested?.Invoke(sourceIndex);
                    AppLogger.Log($"Tab returned anywhere inside its original Fence. Source={sourceFenceId}; Target={Config.Title}");
                }
                e.Effects = System.Windows.DragDropEffects.Move;
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
            return;
        }
        if (!TryGetDroppedFiles(e, out var paths))
        {
            AppLogger.Log($"Fence drop ignored because item path data was unavailable: {Config.Title}");
            return;
        }
        if (!Config.IsDesktopGroup && paths.Any(FolderItemService.IsShellNamespacePath))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (_dropOperationInProgress)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }
        AppLogger.Log($"Fence drop received {paths.Length} item(s): {Config.Title}");
        var requestedEffect = GetRequestedDropEffect(e);
        if (requestedEffect == System.Windows.DragDropEffects.None)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (Config.IsDesktopGroup)
        {
            var desktopPaths = paths.Where(path =>
                FolderItemService.IsShellNamespacePath(path) || IsDirectChildOfDesktopRoot(path)).ToArray();
            var pathsToRestore = paths.Where(path =>
                !FolderItemService.IsShellNamespacePath(path) && !IsDirectChildOfDesktopRoot(path)).ToArray();
            var insertionIndex = GetDropInsertionIndex(e);
            e.Effects = requestedEffect;
            e.Handled = true;
            AppLogger.Log($"Desktop Fence drop released to OLE before file work. Items={paths.Length}; Restore={pathsToRestore.Length}; Effect={requestedEffect}");
            var restoreResult = await RunDropOperationAsync<FolderMoveResult?>(async () =>
            {
                if (pathsToRestore.Length == 0) return null;
                var desktopRoot = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var operation = FolderItemService.GetTransferOperation(requestedEffect);
                if (operation is null || ActionHistory is null)
                    return await Task.Run(() => TransferItems(pathsToRestore, desktopRoot, requestedEffect));
                return await ActionHistory.ExecuteFileTransferAsync(
                    $"{FolderItemService.GetOperationDisplayName(operation.Value)}到桌面",
                    _folderItemService, pathsToRestore, desktopRoot, operation.Value);
            });
            if (restoreResult != null && (restoreResult.Errors.Count > 0 || restoreResult.Skipped > 0))
            {
                System.Windows.MessageBox.Show(BuildMoveSummary(restoreResult), "MiniFences", MessageBoxButton.OK,
                    restoreResult.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
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

            Config.AssignedPaths = ItemsList.Items
                .OfType<FolderItem>()
                .Select(item => item.FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            Config.SortMode = "None";
            DesktopItemsAssigned?.Invoke(this, new DesktopItemsAssignedEventArgs(assignablePaths, insertionIndex));
            return;
        }

        var portalPath = GetPortalPath();
        e.Effects = requestedEffect;
        e.Handled = true;
        AppLogger.Log($"Portal Fence drop released to OLE before file work. Destination={portalPath}; Items={paths.Length}; Effect={requestedEffect}");
        var operation = FolderItemService.GetTransferOperation(requestedEffect);
        var result = await RunDropOperationAsync(() => operation is null || ActionHistory is null
            ? Task.Run(() => TransferItems(paths, portalPath, requestedEffect))
            : ActionHistory.ExecuteFileTransferAsync(
                $"{FolderItemService.GetOperationDisplayName(operation.Value)}到 Fence“{Config.Title}”",
                _folderItemService, paths, portalPath, operation.Value));
        if (result.Errors.Count > 0)
        {
            var message = BuildMoveSummary(result);
            System.Windows.MessageBox.Show(message, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadFolderItems();
            return;
        }

        LoadFolderItems();
        // The destination can refresh itself, but the source Fence is a
        // separate view (and may watch a different directory). Notify the
        // window coordinator immediately so every Fence drops stale source
        // items as soon as the file move finishes instead of waiting for a
        // FileSystemWatcher packet.
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        if (result.Skipped > 0)
        {
            System.Windows.MessageBox.Show(BuildMoveSummary(result), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private int GetDropInsertionIndex(System.Windows.DragEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var container = FindVisualParent<System.Windows.Controls.ListViewItem>(source);
        if (container == null)
        {
            var bounds = Enumerable.Range(0, ItemsList.Items.Count)
                .Select(index => ItemsList.ItemContainerGenerator.ContainerFromIndex(index) as System.Windows.Controls.ListViewItem)
                .Where(item => item != null)
                .Select(item =>
                {
                    var origin = item!.TranslatePoint(new System.Windows.Point(0, 0), ItemsList);
                    return new Rect(origin, item.RenderSize);
                })
                .ToArray();
            return GetGridInsertionIndex(bounds, e.GetPosition(ItemsList));
        }

        var index = ItemsList.ItemContainerGenerator.IndexFromContainer(container);
        if (index < 0) return ItemsList.Items.Count;
        var point = e.GetPosition(container);
        var insertAfter = point.X >= container.ActualWidth / 2;
        return Math.Clamp(index + (insertAfter ? 1 : 0), 0, ItemsList.Items.Count);
    }

    internal static int GetGridInsertionIndex(IReadOnlyList<Rect> itemBounds, System.Windows.Point point)
    {
        if (itemBounds.Count == 0) return 0;
        if (point.Y <= itemBounds.Min(bounds => bounds.Top)) return 0;
        if (point.Y >= itemBounds.Max(bounds => bounds.Bottom)) return itemBounds.Count;

        var nearestCenterY = itemBounds
            .Select(bounds => bounds.Top + bounds.Height / 2)
            .OrderBy(centerY => Math.Abs(centerY - point.Y))
            .First();
        var rowTolerance = Math.Max(1, itemBounds.Max(bounds => bounds.Height) / 2);
        var row = itemBounds
            .Select((bounds, index) => (bounds, index))
            .Where(entry => Math.Abs(entry.bounds.Top + entry.bounds.Height / 2 - nearestCenterY) < rowTolerance)
            .OrderBy(entry => entry.bounds.Left)
            .ToArray();
        if (row.Length == 0) return itemBounds.Count;

        foreach (var entry in row)
        {
            if (point.X < entry.bounds.Left + entry.bounds.Width / 2) return entry.index;
        }

        return Math.Min(itemBounds.Count, row[^1].index + 1);
    }

    private void UpdateDragState(System.Windows.DragEventArgs e)
    {
        var host = Window.GetWindow(this) as MainWindow;
        if (host != null && !host.TryActivateTopmostFenceDragTarget(this, Forms.Cursor.Position))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            ClearDragHighlight();
            return;
        }
        if (_dropOperationInProgress)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            (Window.GetWindow(this) as MainWindow)?.HideDragHint();
            ClearDragHighlight();
            return;
        }
        if (!TryGetDroppedFiles(e, out var paths))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            (Window.GetWindow(this) as MainWindow)?.HideDragHint();
            ClearDragHighlight();
            return;
        }
        if (!Config.IsDesktopGroup && paths.Any(FolderItemService.IsShellNamespacePath))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            host?.HideDragHint();
            ClearDragHighlight();
            return;
        }

        e.Effects = GetRequestedDropEffect(e);
        e.Handled = true;
        SetDragHighlight();
        host?.ShowDragSourceHint(paths, e.Data);
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

    private void UpdateTabMergeDragState(System.Windows.DragEventArgs e, string sourceFenceId)
    {
        var inMergeZone = IsTabMergeTargetPoint(e.GetPosition(this));
        var belongsToThisGroup = BelongsToThisTabGroup(sourceFenceId);
        var canMerge = IsExternalTabMoveAllowed(e.Data) && inMergeZone && CanAcceptTabMerge(sourceFenceId);
        var canMergeOrReturn = canMerge || belongsToThisGroup;
        e.Effects = canMergeOrReturn ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
        if (DesktopDragData.GetCachedData(e.Data, TabPreviewContextFormat) is TabDragPreviewContext previewContext)
        {
            var tabStripPoint = e.GetPosition(TabStripPanel);
            var reorderPointAccepted = previewContext.ExternalMoveAllowed
                ? GetScreenHeaderReorderBoundsForTabDrag().Contains(
                    new System.Windows.Point(Forms.Cursor.Position.X, Forms.Cursor.Position.Y))
                : IsPointWithinTabSlotColumns(tabStripPoint);
            if (belongsToThisGroup && reorderPointAccepted &&
                DesktopDragData.GetCachedData(e.Data, TabIndexFormat) is int fromIndex)
                PreviewTabReorder(fromIndex,
                    previewContext.GetReorderTargetIndex(tabStripPoint.X, fromIndex));
            else if (!reorderPointAccepted)
                previewContext.ClearReorderPreview();
            previewContext.SetMergePreview(
                ShouldCompactTabDragPreview(inMergeZone, belongsToThisGroup, canMerge),
                belongsToThisGroup ? 96 : ActualWidth / 3);
        }
        // The compact dragged tab is the merge affordance. Extra target borders
        // made the Fence flash a large blue frame and did not match browser tabs.
        ClearDragHighlight();
    }

    private bool CanAcceptTabMerge(string sourceFenceId) =>
        CanAcceptTabMerge(sourceFenceId, Config.Id, _tabConfigs);

    private bool IsPointOverTabSlots(System.Windows.Point point)
    {
        if (TabStripPanel.Visibility != Visibility.Visible || point.Y < 0 || point.Y > TabStripPanel.ActualHeight)
            return false;
        return IsPointWithinTabSlotColumns(point);
    }

    private bool IsPointWithinTabSlotColumns(System.Windows.Point point)
    {
        if (TabStripPanel.Visibility != Visibility.Visible) return false;
        var tabCount = Math.Min(_tabConfigs?.Count ?? 0, TabStripPanel.ColumnDefinitions.Count);
        var tabSlotsWidth = 0d;
        for (var index = 0; index < tabCount; index++)
            tabSlotsWidth += TabStripPanel.ColumnDefinitions[index].ActualWidth;
        return point.X >= 0 && point.X <= tabSlotsWidth;
    }

    private bool BelongsToThisTabGroup(string sourceFenceId) =>
        _tabConfigs?.Any(config => string.Equals(config.Id, sourceFenceId, StringComparison.OrdinalIgnoreCase)) == true;

    internal static bool CanAcceptTabMerge(string sourceFenceId, string targetFenceId, IReadOnlyList<FenceConfig>? targetTabs) =>
        !string.Equals(targetFenceId, sourceFenceId, StringComparison.OrdinalIgnoreCase) &&
        (targetTabs == null || !targetTabs.Any(config =>
            string.Equals(config.Id, sourceFenceId, StringComparison.OrdinalIgnoreCase)));

    private bool IsTabMergeDropPoint(System.Windows.Point point) =>
        IsTabMergeDropPoint(point, ActualWidth > 0 ? ActualWidth : Width);

    private bool IsTabMergeTargetPoint(System.Windows.Point point)
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        return width > 0 && point.X >= 0 && point.X <= width &&
               point.Y >= -10 && point.Y <= CollapsedHeight + 10;
    }

    private static System.Windows.DragDropEffects GetRequestedDropEffect(System.Windows.DragEventArgs e) =>
        DesktopDragData.GetRequestedDropEffect(e.Data, e.KeyStates, e.AllowedEffects);

    private FolderMoveResult TransferItems(IEnumerable<string> paths, string destination,
        System.Windows.DragDropEffects effect) => effect switch
        {
            System.Windows.DragDropEffects.Link => _folderItemService.CreateShortcutsIntoFolder(paths, destination),
            System.Windows.DragDropEffects.Copy => _folderItemService.CopyIntoFolder(paths, destination),
            System.Windows.DragDropEffects.Move => _folderItemService.MoveIntoFolder(paths, destination),
            _ => new FolderMoveResult(0, paths.Count(), ["The drag source did not request a supported operation."], [])
        };

    private void ActivateTabForFileDrop(int selectedIndex, FenceConfig targetConfig,
        bool deferUntilDragEventCompletes)
    {
        if (string.Equals(targetConfig.Id, Config.Id, StringComparison.OrdinalIgnoreCase) ||
            _fileDragActivationRequestedTabIndex == selectedIndex)
        {
            return;
        }

        _fileDragActivationRequestedTabIndex = selectedIndex;
        if (!deferUntilDragEventCompletes)
        {
            TabSelectedRequested?.Invoke(selectedIndex);
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (IsLoaded) TabSelectedRequested?.Invoke(selectedIndex);
        }, DispatcherPriority.Input);
    }

    internal bool IsHeaderMergePoint(System.Windows.Point workspacePoint)
    {
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = Config.Left;
        if (double.IsNaN(top)) top = Config.Top;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        return new Rect(left, top - 10, width, CollapsedHeight + 20).Contains(workspacePoint);
    }

    internal int GetTabInsertionIndexForHeaderPoint(System.Windows.Point workspacePoint)
    {
        var left = Canvas.GetLeft(this);
        if (double.IsNaN(left)) left = Config.Left;
        return GetTabInsertionIndex(workspacePoint.X - left);
    }

    private int GetTabInsertionIndex(double localX)
    {
        var tabCount = _tabConfigs?.Count ?? 0;
        if (tabCount == 0)
            return localX < (ActualWidth > 0 ? ActualWidth : Width) / 2 ? 0 : 1;

        var x = 0d;
        for (var index = 0; index < tabCount; index++)
        {
            var width = index < TabStripPanel.ColumnDefinitions.Count
                ? TabStripPanel.ColumnDefinitions[index].ActualWidth
                : 0;
            if (width <= 0)
                width = TabStripPanel.Children.OfType<Border>()
                    .FirstOrDefault(tab => tab.Tag is int originalIndex && originalIndex == index)?.ActualWidth ?? 72;
            if (localX < x + width / 2) return index;
            x += width;
        }
        return tabCount;
    }

    internal static bool IsTabMergeDropPoint(System.Windows.Point point, double fenceWidth) =>
        fenceWidth > 0 && point.Y >= 0 && point.Y <= CollapsedHeight &&
        point.X >= fenceWidth / 3 && point.X <= fenceWidth * 2 / 3;

    internal void ClearDragHighlight()
    {
        if (!_dragHighlightActive) return;
        _dragHighlightActive = false;
        DropHighlightBorder.Visibility = Visibility.Collapsed;
    }

    private void SetDragHighlight()
    {
        if (_dragHighlightActive) return;
        _dragHighlightActive = true;
        DropHighlightBorder.Visibility = Visibility.Visible;
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
        TitleBar.Background = FenceAppearanceBrush.CreateHeaderBrush(Config);
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
        SetHoverExpanded(true);
        UpdateResizeHandleVisibility(e.GetPosition(this));
    }

    private void FenceControl_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        UpdateResizeHandleVisibility(e.GetPosition(this));
    }

    private void FenceControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindVisualParent<System.Windows.Controls.ListView>(source) != null)
        {
            return;
        }

        if (!ScrollItemsByWheelDelta(e.Delta)) return;
        e.Handled = true;
    }

    private void TabStatusText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Config.TabGroupId) || _tabStripTitles.Count <= 1) return;
        var menu = BuildTabPickerMenu();
        menu.PlacementTarget = TabStatusText;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildTabPickerMenu()
    {
        var menu = new ContextMenu();
        for (var index = 0; index < _tabStripTitles.Count; index++)
        {
            var selectedIndex = index;
            var item = new MenuItem
            {
                Header = _tabStripTitles[index],
                IsCheckable = true,
                IsChecked = index == _activeTabIndexForTesting,
                StaysOpenOnClick = false,
                MinWidth = 180
            };
            item.Click += (_, _) => TabSelectedRequested?.Invoke(selectedIndex);
            menu.Items.Add(item);
        }
        if (!string.IsNullOrWhiteSpace(Config.TabGroupId) && _tabStripTitles.Count > 1)
        {
            menu.Items.Add(new Separator());
            var detach = new MenuItem { Header = _loc.T("RemoveFromTabStack") };
            detach.Click += UnstackTabMenuItem_Click;
            menu.Items.Add(detach);
        }
        return menu;
    }

    internal bool ScrollItemsDuringDrag(int delta) => ScrollItemsByWheelDelta(delta);

    private bool ScrollItemsByWheelDelta(int delta)
    {
        var scrollViewer = FindVisualChild<ScrollViewer>(ItemsList);
        if (scrollViewer == null || delta == 0) return false;
        var wheelNotches = Math.Max(1, Math.Abs(delta) / Mouse.MouseWheelDeltaForOneLine);
        var configuredLines = SystemParameters.WheelScrollLines;
        if (configuredLines < 0)
        {
            for (var notch = 0; notch < wheelNotches; notch += 1)
            {
                if (delta > 0) scrollViewer.PageUp();
                else scrollViewer.PageDown();
            }
        }
        else
        {
            var lines = Math.Max(1, configuredLines) * wheelNotches;
            for (var line = 0; line < lines; line += 1)
            {
                if (delta > 0) scrollViewer.LineUp();
                else scrollViewer.LineDown();
            }
        }
        return true;
    }

    private void FenceControl_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isResizing) ResizeThumb.Visibility = Visibility.Collapsed;
        SetHoverExpanded(false);
    }

    private void UpdateResizeHandleVisibility(System.Windows.Point pointer)
    {
        ResizeThumb.Visibility = !IsVisuallyCollapsed && !Config.IsLocked &&
                                 (_isResizing || IsNearResizeHandle(
                                     pointer, ActualWidth, ActualHeight,
                                     ResizeThumb.VerticalAlignment == VerticalAlignment.Top))
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        var bottomDocked = ShouldUseBottomTitleLayout(
            Config.EdgeDock, isVisuallyCollapsed, BottomDockTitleAtBottom, TopDockTitleAtBottomOnExpand);
        MinHeight = isVisuallyCollapsed ? CollapsedHeight : ExpandedMinHeight;
        ContentArea.Visibility = isVisuallyCollapsed ? Visibility.Collapsed : Visibility.Visible;
        var showFooter = !isVisuallyCollapsed && !Config.UseCleanStyle;
        FooterPanel.Visibility = showFooter ? Visibility.Visible : Visibility.Collapsed;
        ResizeThumb.Visibility = !isVisuallyCollapsed && !Config.IsLocked && _isResizing
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResizeThumb.VerticalAlignment = bottomDocked ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        ResizeThumb.Margin = bottomDocked ? new Thickness(0, 4, 4, 0) : new Thickness(0, 0, 4, 4);
        ResizeThumb.Tag = bottomDocked ? "Top" : "Bottom";
        ResizeThumb.Cursor = bottomDocked ? System.Windows.Input.Cursors.SizeNESW : System.Windows.Input.Cursors.SizeNWSE;
        ApplyDockedVisualOrder(bottomDocked, isVisuallyCollapsed, showFooter);
        Height = isVisuallyCollapsed
            ? CollapsedHeight
            : Math.Max(ExpandedMinHeight, Config.ExpandedHeight ?? Config.Height);
        if (Parent is Canvas canvas && canvas.ActualHeight > 0)
        {
            var dockArea = GetDockWorkArea(canvas);
            if (!string.IsNullOrWhiteSpace(Config.EdgeDock))
                Canvas.SetTop(this, GetDockedFenceTop(Config.EdgeDock, Height, dockArea));
        }
        TitleBar.CornerRadius = isVisuallyCollapsed
            ? new CornerRadius(8)
            : bottomDocked
                ? new CornerRadius(0, 0, 8, 8)
                : new CornerRadius(8, 8, 0, 0);
        ContentBackground.CornerRadius = bottomDocked
            ? new CornerRadius(8, 8, 0, 0)
            : new CornerRadius(0, 0, 8, 8);
        UpdateTabStripCornerRadii(bottomDocked);

        // A Canvas does not always immediately remeasure a child after only its
        // row definitions change. Settle the visual tree before a later drag can
        // make a delayed expansion appear.
        InvalidateMeasure();
        InvalidateArrange();
        UpdateLayout();
    }

    private bool UsesBottomTitleLayout() => ShouldUseBottomTitleLayout(
        Config.EdgeDock, IsVisuallyCollapsed, BottomDockTitleAtBottom, TopDockTitleAtBottomOnExpand);

    internal static bool ShouldUseBottomTitleLayout(
        string? dockEdge,
        bool isVisuallyCollapsed,
        bool bottomDockTitleAtBottom,
        bool topDockTitleAtBottomOnExpand)
    {
        if (string.Equals(dockEdge, "Bottom", StringComparison.OrdinalIgnoreCase))
            return bottomDockTitleAtBottom;
        return string.Equals(dockEdge, "Top", StringComparison.OrdinalIgnoreCase) &&
               !isVisuallyCollapsed &&
               topDockTitleAtBottomOnExpand;
    }

    private Rect GetDockWorkArea(Canvas canvas)
    {
        var fallback = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);
        if (DragWorkAreaProvider == null) return fallback;

        try
        {
            var left = Canvas.GetLeft(this);
            var top = Canvas.GetTop(this);
            if (double.IsNaN(left)) left = Config.Left;
            if (double.IsNaN(top)) top = Config.Top;
            var screenPoint = canvas.PointToScreen(new System.Windows.Point(left + Width / 2, top + CollapsedHeight / 2));
            return DragWorkAreaProvider(new System.Drawing.Point(
                (int)Math.Round(screenPoint.X),
                (int)Math.Round(screenPoint.Y)));
        }
        catch
        {
            return DragWorkAreaProvider(Forms.Cursor.Position);
        }
    }

    internal static double GetDockedFenceTop(string? dockEdge, double fenceHeight, Rect usableArea)
    {
        if (string.Equals(dockEdge, "Bottom", StringComparison.OrdinalIgnoreCase))
            return Math.Max(usableArea.Top, usableArea.Bottom - fenceHeight);
        return usableArea.Top;
    }

    private void ApplyDockedVisualOrder(bool bottomDocked, bool collapsed, bool showFooter)
    {
        if (bottomDocked)
        {
            FirstRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            ContentRow.Height = showFooter ? new GridLength(22) : new GridLength(0);
            FooterRow.Height = new GridLength(CollapsedHeight);
            Grid.SetRow(ContentBackground, 0);
            Grid.SetRowSpan(ContentBackground, 2);
            Grid.SetRow(ContentArea, 0);
            Grid.SetRow(FooterPanel, 1);
            Grid.SetRow(TitleBar, 2);
            return;
        }

        FirstRow.Height = new GridLength(CollapsedHeight);
        ContentRow.Height = collapsed ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        FooterRow.Height = showFooter ? new GridLength(22) : new GridLength(0);
        Grid.SetRow(ContentBackground, 1);
        Grid.SetRowSpan(ContentBackground, 2);
        Grid.SetRow(ContentArea, 1);
        Grid.SetRow(FooterPanel, 2);
        Grid.SetRow(TitleBar, 0);
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
        MergePreview.Opacity = active ? 1 : 0;
        if (!active)
        {
            MergePreviewPulse.BeginAnimation(OpacityProperty, null);
            return;
        }
        MergePreviewPulse.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.35, 1,
                TimeSpan.FromMilliseconds(420))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            });
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
            _mergePreviewLeft = 0;
            _mergePreviewWidth = 0;
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
        _mergePreviewLeft = previewLeft;
        _mergePreviewWidth = targetWidth;
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
        if (!Config.IsDesktopGroup && Directory.Exists(item.FullPath))
        {
            NavigatePortal(item.FullPath, true);
            return;
        }
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
        var path = GetPortalPath();
        if (!System.IO.Directory.Exists(path))
        {
            StatusText.Text = $"{_loc.T("FolderNotFound")}: {path}";
            return;
        }

        if (!string.IsNullOrWhiteSpace(_lastLoadError))
        {
            StatusText.Text = $"{_loc.T("CouldNotLoadFolder")}: {FormatFileOperationError(_lastLoadError, "CouldNotLoadFolder")}";
            return;
        }

        StatusText.Text = $"{path} - {ItemsList.Items.Count} {_loc.T("ItemCount")}";
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

    internal System.Windows.Point GetHeaderDragMergePoint()
    {
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = Config.Left;
        if (double.IsNaN(top)) top = Config.Top;
        var headerCenterX = _isMergeCompactPreview
            ? _mergePreviewLeft + _mergePreviewWidth / 2
            : (ActualWidth > 0 ? ActualWidth : Width) / 2;
        return new System.Windows.Point(left + headerCenterX, top + CollapsedHeight / 2);
    }

    internal static bool IsVisualDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ancestor)) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index += 1)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            var descendant = FindVisualChild<T>(child);
            if (descendant != null) return descendant;
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
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

    private void BeginChooseColor(bool chooseHeader)
    {
        var currentColor = chooseHeader ? Config.HeaderColor : Config.BackgroundColor;
        AppLogger.Log($"Opening {(chooseHeader ? "title" : "background")} color picker for '{Config.Title}'.");

        // The desktop host deliberately uses WS_EX_NOACTIVATE. Wait until the
        // context menu has closed, then show our own activating WPF dialog.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var dialog = new ColorPickerDialog(currentColor, _loc, chooseHeader);
                if (dialog.ShowDialog() != true) return;
                var color = dialog.SelectedColor;
                if (chooseHeader) Config.HeaderColor = color;
                else Config.BackgroundColor = color;
                ApplyStyle();
                Changed?.Invoke(this, EventArgs.Empty);
                AppLogger.Log($"Changed {(chooseHeader ? "title" : "background")} color for '{Config.Title}' to {color}.");
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Could not open the Fence color picker", ex);
                System.Windows.MessageBox.Show(
                    _loc.Language == LocalizationService.Chinese
                        ? $"无法打开颜色选择窗口：{ex.Message}"
                        : $"Could not open the color picker: {ex.Message}",
                    "MiniFences",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private sealed record FenceAppearance(
        string BackgroundColor,
        string HeaderColor,
        double Opacity,
        string TitleAlignment,
        bool ShowPath,
        bool UseCleanStyle)
    {
        internal static FenceAppearance From(FenceConfig config) => new(
            config.BackgroundColor,
            config.HeaderColor,
            config.Opacity,
            config.TitleAlignment,
            config.ShowPath,
            config.UseCleanStyle);

        internal void ApplyTo(FenceConfig config)
        {
            config.BackgroundColor = BackgroundColor;
            config.HeaderColor = HeaderColor;
            config.Opacity = Opacity;
            config.TitleAlignment = TitleAlignment;
            config.ShowPath = ShowPath;
            config.UseCleanStyle = UseCleanStyle;
        }
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
