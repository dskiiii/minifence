using System.Diagnostics;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation;
using MiniFences.Models;
using MiniFences.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MiniFences;

public partial class MainWindow : Window
{
    private const string AllAppearanceTargetId = "__all_fences__";
    private readonly ConfigService _configService = new();
    private readonly StartupService _startupService = new();
    private readonly AutoOrganizerService _autoOrganizerService = new();
    private readonly DesktopIconLayoutService _desktopIconLayoutService = new();
    private readonly DesktopDoubleClickTracker _desktopDoubleClickTracker = new();
    private readonly LocalizationService _loc = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _autoOrganizeTimer;
    private readonly DispatcherTimer _desktopIconStateTimer;
    private readonly DispatcherTimer _desktopContentsRefreshTimer;
    private readonly DispatcherTimer _dragPageSwitchTimer;
    private readonly DispatcherTimer _dragRegionRefreshTimer;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly HashSet<string> _pendingAutoOrganizePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activeTabByGroup = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _desktopAutoOrganizeWatcher;
    private readonly List<FileSystemWatcher> _desktopContentsWatchers = [];
    private Forms.ToolStripMenuItem? _showMiniFencesMenuItem;
    private Forms.ToolStripMenuItem? _toggleFencesMenuItem;
    private Forms.ToolStripMenuItem? _exitMenuItem;
    private SettingsWindow? _settingsWindow;
    private AppConfig _config = new();
    private bool _isExiting;
    private bool _fencesHidden;
    private readonly bool _openSettingsOnLoad;
    private LowLevelMouseProc? _mouseHookProc;
    private IntPtr _mouseHookHandle;
    private LowLevelKeyboardProc? _keyboardHookProc;
    private IntPtr _keyboardHookHandle;
    private IntPtr _desktopHostHandle;
    private bool _isDesktopHosted;
    private HotkeyGesture? _previousPageGesture;
    private HotkeyGesture? _nextPageGesture;
    private HotkeyGesture? _toggleTopmostGesture;
    private System.Drawing.Point _lastMouseScreenPoint;
    private bool _hoverUpdatePending;
    private bool _restoreNativeDesktopIconsOnExit;
    private bool _windowsDesktopIconsVisible = true;
    private bool _desktopItemDragActive;
    private int _pendingDragPageDirection;
    private bool _fencesTopmost;
    private bool _configSaveErrorShown;
    private FenceControl? _mergePreviewTarget;
    private FenceControl? _mergePreviewSource;
    private readonly HashSet<string> _selectedLoosePaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _looseSelectionAnchor;
    private System.Windows.Controls.TextBox? _activeInlineRenameEditor;

    public MainWindow(bool openSettingsOnLoad = false)
    {
        _openSettingsOnLoad = openSettingsOnLoad;
        AppLogger.Log($"Program started. Version={typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}; OS={Environment.OSVersion.VersionString}");
        InitializeComponent();
        _saveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveConfigWithWarning();
        };
        _autoOrganizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _autoOrganizeTimer.Tick += (_, _) => ProcessPendingAutoOrganization();
        _desktopIconStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _desktopIconStateTimer.Tick += (_, _) => SynchronizeWindowsDesktopIconState();
        _desktopContentsRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _desktopContentsRefreshTimer.Tick += (_, _) =>
        {
            _desktopContentsRefreshTimer.Stop();
            RenderFences();
        };
        _dragPageSwitchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _dragPageSwitchTimer.Tick += (_, _) =>
        {
            _dragPageSwitchTimer.Stop();
            var target = _config.CurrentPage + _pendingDragPageDirection;
            if (_desktopItemDragActive && target >= 0 && target < GetPageCount())
            {
                SwitchPage(target);
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero) SetWindowRgn(handle, IntPtr.Zero, true);
                AppLogger.Log($"Switched to page {target + 1} while dragging a desktop item.");
            }
        };
        _dragRegionRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _dragRegionRefreshTimer.Tick += (_, _) =>
        {
            _dragRegionRefreshTimer.Stop();
            UpdateDesktopWindowRegion();
        };
        _trayIcon = CreateTrayIcon();
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Deactivated += (_, _) => HandleWindowDeactivated();
        SizeChanged += (_, _) =>
        {
            ClampAllFencesToWorkspace();
            UpdateDesktopWindowRegion();
        };
        System.Windows.Application.Current.SessionEnding += MainWindow_SessionEnding;
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
            AppLogger.Log("Window hit-test hook installed for transparent desktop pass-through.");
        }

        InstallDesktopDoubleClickHook();
        ApplyDesktopWindowStyles();
        AttachToDesktopHost();
        RegisterGlobalHotkeys();
        UpdateDesktopWindowRegion();

        AppLogger.Log(_isDesktopHosted
            ? "MiniFences attached to the Explorer desktop host."
            : "Desktop host attachment failed; using top-level desktop fallback.");
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDesktopWorkAreaBounds();
        _config = _configService.Load();
        var convertedLegacyDesktop = _autoOrganizerService.ConvertLegacyDesktopPortal(_config);
        _loc.Language = _config.Language;
        _fencesHidden = _config.FencesHidden;
        _windowsDesktopIconsVisible = ReadWindowsDesktopIconsVisible();
        _restoreNativeDesktopIconsOnExit = _windowsDesktopIconsVisible && _config.EnableDesktopIconIntegration;
        WarnAboutExternalFences();
        AppLogger.Log($"Loaded fence count: {_config.Fences.Count}");
        foreach (var fence in _config.Fences)
        {
            AppLogger.Log($"Fence '{fence.Title}' page: {fence.PageIndex}; folder: {fence.FolderPath}");
        }

        var normalizedDesktopEntries = NormalizeDuplicateDesktopMemberships();
        if (normalizedDesktopEntries) SaveConfigWithWarning();

        UpdateLocalizedText();
        RenderFences();
        if (_config.EnableDesktopIconIntegration)
        {
            UpdateNativeDesktopIconVisibility();
        }
        else if (_windowsDesktopIconsVisible)
        {
            _desktopIconLayoutService.SetVisible(true);
        }
        _desktopIconStateTimer.Start();
        if (convertedLegacyDesktop) SaveConfigWithWarning();
        ConfigureAutoOrganizerWatcher();
        ConfigureDesktopContentsWatcher();
        if (_openSettingsOnLoad)
        {
            Dispatcher.BeginInvoke(ShowSettingsWindow, DispatcherPriority.Loaded);
        }
        Dispatcher.BeginInvoke(SendBehindNormalWindows, DispatcherPriority.ApplicationIdle);
    }

    private void RenderFences()
    {
        var membershipChanged = NormalizeDuplicateDesktopMemberships();
        Workspace.Children.Clear();
        RenderLooseDesktopItems();
        var layoutChanged = false;
        foreach (var group in _config.Fences.Where(fence => fence.PageIndex == _config.CurrentPage)
                     .GroupBy(fence => string.IsNullOrWhiteSpace(fence.TabGroupId) ? fence.Id : fence.TabGroupId!))
        {
            var tabs = group.ToList();
            var active = _activeTabByGroup.TryGetValue(group.Key, out var activeId)
                ? tabs.FirstOrDefault(fence => string.Equals(fence.Id, activeId, StringComparison.OrdinalIgnoreCase))
                : null;
            active ??= tabs[0];
            _activeTabByGroup[group.Key] = active.Id;
            layoutChanged |= AddFenceControl(active, tabs.Count, tabs.IndexOf(active), tabs.Select(tab => tab.Title).ToArray());
        }

        AppLogger.Log($"Rendered page {_config.CurrentPage + 1}/{GetPageCount()} with {Workspace.Children.OfType<FenceControl>().Count()} visible Fence(s) and {Workspace.Children.OfType<DesktopLooseIconControl>().Count()} loose icon(s).");
        ApplyFenceVisibility();
        UpdateMenuState();
        if (layoutChanged || membershipChanged)
        {
            SaveConfigWithWarning();
        }
    }

    private void RenderLooseDesktopItems()
    {
        if (!_config.EnableDesktopIconIntegration) return;
        var assigned = _config.Fences.Where(fence => fence.IsDesktopGroup)
            .SelectMany(fence => fence.AssignedPaths)
            .Select(TryGetFullPath)
            .Where(path => path != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = _config.DesktopIconOrder
            .Select((path, index) => (path, index))
            .ToDictionary(entry => entry.path, entry => entry.index, StringComparer.OrdinalIgnoreCase);
        var paths = FolderItemService.CollapseDesktopEntries(FolderItemService.EnumerateFileSystemEntriesSafe(GetDesktopRoots())
                .Where(ShouldShowLooseDesktopItem)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .Where(path => !assigned.Contains(Path.GetFullPath(path)))
            .OrderBy(path => order.GetValueOrDefault(path, int.MaxValue))
            .ThenBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _config.DesktopIconOrder = paths.ToList();
        var items = new FolderItemService().LoadAssignedItems(paths);
        var rows = Math.Max(1, (int)Math.Floor((ActualHeight > 0 ? ActualHeight : Height) / 96));
        var cell = 0;
        foreach (var item in items)
        {
            while (true)
            {
                var column = cell / rows;
                var row = cell % rows;
                cell += 1;
                var left = 8 + column * 92;
                var top = 8 + row * 96;
                var control = new DesktopLooseIconControl(item, _loc);
                control.IsExplorerDesktopPointForDrag = IsExplorerDesktopPoint;
                control.IsMiniFencesSurfacePointForDrag = IsPointOverWorkspace;
                control.SelectionRequested += HandleLooseIconSelection;
                control.ItemsChanged += (_, _) => RenderFences();
                control.DesktopItemDragStarted += (_, _) => BeginDesktopItemDrag();
                control.DesktopItemDragEnded += (_, _) => EndDesktopItemDrag();
                Canvas.SetLeft(control, left);
                Canvas.SetTop(control, top);
                Workspace.Children.Add(control);
                break;
            }
        }
        UpdateLooseIconSelectionVisuals();
    }

    private static string? TryGetFullPath(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path); }
        catch { return null; }
    }

    private bool NormalizeDuplicateDesktopMemberships()
    {
        var groups = _config.Fences.Where(fence => fence.IsDesktopGroup).ToArray();
        var allPaths = groups.SelectMany(fence => fence.AssignedPaths).ToArray();
        var visible = FolderItemService.CollapseDesktopEntries(allPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var fence in groups)
        {
            var removed = fence.AssignedPaths.RemoveAll(path => !visible.Contains(path));
            changed |= removed > 0;
        }
        return changed;
    }

    private void HandleLooseIconSelection(DesktopLooseIconControl clicked, ModifierKeys modifiers)
    {
        foreach (var fence in Workspace.Children.OfType<FenceControl>()) fence.ClearItemSelection();
        var controls = Workspace.Children.OfType<DesktopLooseIconControl>().ToList();
        var path = clicked.Item.FullPath;
        if ((modifiers & ModifierKeys.Shift) != 0 && !string.IsNullOrWhiteSpace(_looseSelectionAnchor))
        {
            var anchorIndex = controls.FindIndex(control => string.Equals(control.Item.FullPath, _looseSelectionAnchor, StringComparison.OrdinalIgnoreCase));
            var clickedIndex = controls.IndexOf(clicked);
            if (anchorIndex >= 0 && clickedIndex >= 0)
            {
                if ((modifiers & ModifierKeys.Control) == 0) _selectedLoosePaths.Clear();
                for (var index = Math.Min(anchorIndex, clickedIndex); index <= Math.Max(anchorIndex, clickedIndex); index++)
                    _selectedLoosePaths.Add(controls[index].Item.FullPath);
            }
        }
        else if ((modifiers & ModifierKeys.Control) != 0)
        {
            if (!_selectedLoosePaths.Add(path)) _selectedLoosePaths.Remove(path);
            _looseSelectionAnchor = path;
        }
        else
        {
            _selectedLoosePaths.Clear();
            _selectedLoosePaths.Add(path);
            _looseSelectionAnchor = path;
        }
        UpdateLooseIconSelectionVisuals();
    }

    private void UpdateLooseIconSelectionVisuals()
    {
        var controls = Workspace.Children.OfType<DesktopLooseIconControl>().ToList();
        var existing = controls.Select(control => control.Item.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedLoosePaths.RemoveWhere(path => !existing.Contains(path));
        var dragPaths = _selectedLoosePaths.ToArray();
        foreach (var control in controls)
        {
            control.SetSelected(_selectedLoosePaths.Contains(control.Item.FullPath));
            control.DragPaths = control.IsSelected ? dragPaths : [control.Item.FullPath];
        }
        Dispatcher.BeginInvoke(UpdateDesktopWindowRegion, DispatcherPriority.Loaded);
    }

    private static bool ShouldShowLooseDesktopItem(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0;
        }
        catch { return false; }
    }

    private void WarnAboutExternalFences()
    {
        try
        {
            if (!Process.GetProcessesByName("Fences").Any(process => !process.HasExited))
            {
                return;
            }

            AppLogger.Log("Stardock Fences was detected. Desktop mouse input may be handled by the other desktop-layer application.");
            Dispatcher.BeginInvoke(() =>
                _trayIcon.ShowBalloonTip(
                    5000,
                    "MiniFences",
                    _loc.T("ExternalFencesDetected"),
                    Forms.ToolTipIcon.Warning),
                DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to detect external desktop-layer applications", ex);
        }
    }

    private bool AddFenceControl(FenceConfig fence, int tabCount = 1, int tabIndex = 0, IReadOnlyList<string>? tabTitles = null)
    {
        var layoutChanged = ClampFenceConfigToWorkspace(fence);
        var control = new FenceControl(fence);
        control.SnapToGrid = _config.EnableSnapToGrid;
        control.RollupEnabled = _config.EnableRollup;
        control.DoubleClickRollupEnabled = _config.DoubleClickTitleRollup;
        control.ClickTitleToExpandEnabled = _config.ClickTitleToExpand;
        control.HoverTitleToExpandEnabled = _config.HoverTitleToExpand;
        control.IsExplorerDesktopPointForDrag = IsExplorerDesktopPoint;
        control.IsMiniFencesSurfacePointForDrag = IsPointOverWorkspace;
        control.SetLocalization(_loc);
        control.SetTabStatus(tabCount, tabIndex, tabTitles,
            string.Equals(_config.TabViewMode, "Strip", StringComparison.OrdinalIgnoreCase), _config.HoverSwitchTabs,
            string.Equals(_config.TabWidthMode, "Equal", StringComparison.OrdinalIgnoreCase));
        Canvas.SetLeft(control, fence.Left);
        Canvas.SetTop(control, fence.Top);
        System.Windows.Controls.Panel.SetZIndex(control, 100 + Math.Max(0, fence.LayerOrder));
        control.AddHandler(UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((_, _) => BringFenceToFront(control)), true);
        control.Changed += (_, _) =>
        {
            UpdateDesktopWindowRegion();
            ScheduleConfigSave();
        };
        control.NewFenceRequested += (_, _) => CreateNewDesktopGroup();
        control.DesktopItemsAssigned += (_, e) => Dispatcher.BeginInvoke(
            () => AssignDesktopItems(control.Config, e.Paths, e.InsertionIndex),
            DispatcherPriority.Background);
        control.DesktopItemsReleased += (_, e) => Dispatcher.BeginInvoke(
            () => ReleaseDesktopItemsToDesktop(e.Paths, e.ScreenPoint),
            DispatcherPriority.Background);
        control.DesktopItemDragStarted += (_, _) => BeginDesktopItemDrag();
        control.DesktopItemDragEnded += (_, _) => EndDesktopItemDrag();
        control.ItemsChanged += (_, _) => Dispatcher.BeginInvoke(RenderFences, DispatcherPriority.Background);
        control.ItemSelectionRequested += (_, _) => HandleFenceItemSelection(control);
        control.HeaderDragCompleted += (_, _) => HandleHeaderDragCompleted(control);
        control.HeaderDragMoved += (_, _) =>
        {
            UpdateMergePreview(control);
            if (!_dragRegionRefreshTimer.IsEnabled) _dragRegionRefreshTimer.Start();
        };
        control.HeaderDragCanceled += (_, _) => ClearMergePreview();
        control.DeleteRequested += (_, _) => DeleteFence(control);
        control.MoveToPreviousPageRequested += (_, _) => MoveFenceToPage(control, control.Config.PageIndex - 1);
        control.MoveToNextPageRequested += (_, _) => MoveFenceToPage(control, control.Config.PageIndex + 1);
        control.MoveToNewPageRequested += (_, _) => MoveFenceToPage(control, GetPageCount());
        control.StackWithNearestRequested += (_, _) => StackWithNearestFence(control);
        control.NextTabRequested += (_, _) => SwitchToNextTab(control);
        control.PreviousTabRequested += (_, _) => SwitchTab(control, -1);
        control.TabSelectedRequested += index => SwitchToTab(control, index);
        control.TabReorderRequested += (from, to) => ReorderTabs(control.Config.TabGroupId, from, to);
        control.TabDetachRequested += index => DetachTab(control.Config.TabGroupId, index, control.Config.Left + 28, control.Config.Top + 28);
        control.UnstackRequested += (_, _) => UnstackFence(control);
        Workspace.Children.Add(control);
        Dispatcher.BeginInvoke(UpdateDesktopWindowRegion, DispatcherPriority.Loaded);
        ApplyFenceVisibility(control);
        control.Dispatcher.BeginInvoke(() =>
        {
            control.ClampToParentBounds();
            ScheduleConfigSave();
        }, DispatcherPriority.Loaded);
        return layoutChanged;
    }

    private void HandleFenceItemSelection(FenceControl activeFence)
    {
        ClearOtherFenceSelections(Workspace.Children.OfType<FenceControl>(), activeFence);
        _selectedLoosePaths.Clear();
        UpdateLooseIconSelectionVisuals();
    }

    internal static void ClearOtherFenceSelections(IEnumerable<FenceControl> fences, FenceControl activeFence)
    {
        foreach (var fence in fences)
        {
            if (!ReferenceEquals(fence, activeFence)) fence.ClearItemSelection();
        }
    }

    private void BringFenceToFront(FenceControl control)
    {
        var pageFences = _config.Fences.Where(fence => fence.PageIndex == control.Config.PageIndex).ToArray();
        var currentMaximum = pageFences.Length == 0 ? 0 : pageFences.Max(fence => fence.LayerOrder);
        var group = string.IsNullOrWhiteSpace(control.Config.TabGroupId)
            ? [control.Config]
            : pageFences.Where(fence => string.Equals(fence.TabGroupId, control.Config.TabGroupId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var outsideGroup = pageFences.Where(fence => !group.Contains(fence)).ToArray();
        if (group.All(fence => fence.LayerOrder == currentMaximum) &&
            outsideGroup.All(fence => fence.LayerOrder < currentMaximum))
        {
            System.Windows.Controls.Panel.SetZIndex(control, 100 + currentMaximum);
            return;
        }

        var nextLayer = currentMaximum >= 1_000_000
            ? NormalizeFenceLayers(pageFences, group)
            : currentMaximum + 1;
        foreach (var fence in group) fence.LayerOrder = nextLayer;
        System.Windows.Controls.Panel.SetZIndex(control, 100 + nextLayer);
        ScheduleConfigSave();
        AppLogger.Log($"Fence '{control.Config.Title}' raised to layer {nextLayer}.");
    }

    internal static int NormalizeFenceLayers(IReadOnlyList<FenceConfig> pageFences, IReadOnlyCollection<FenceConfig> activeGroup)
    {
        var layer = 0;
        foreach (var fence in pageFences.Where(fence => !activeGroup.Contains(fence)).OrderBy(fence => fence.LayerOrder))
            fence.LayerOrder = layer++;
        return layer;
    }

    private void StackWithNearestFence(FenceControl control)
    {
        var target = _config.Fences
            .Where(candidate => candidate.PageIndex == control.Config.PageIndex && candidate.Id != control.Config.Id)
            .OrderBy(candidate => Math.Pow(candidate.Left - control.Config.Left, 2) + Math.Pow(candidate.Top - control.Config.Top, 2))
            .FirstOrDefault();
        if (target == null)
        {
            System.Windows.MessageBox.Show(this, _loc.T("NoFenceToStack"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StackFences(control.Config, target);
    }

    private void MergeByHeaderOverlap(FenceControl sourceControl)
    {
        sourceControl.SyncConfigFromLayout();
        var target = ReferenceEquals(sourceControl, _mergePreviewSource) &&
                     _mergePreviewTarget != null && IsPointerOverHeader(_mergePreviewTarget.Config)
            ? _mergePreviewTarget
            : FindMergeTarget(sourceControl);
        ClearMergePreview();
        if (target == null) return;
        if (_config.ConfirmTabCreation && System.Windows.MessageBox.Show(this,
                $"将“{sourceControl.Config.Title}”与“{target.Config.Title}”合并为标签页？",
                "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StackFences(sourceControl.Config, target.Config);
    }

    private bool IsPointerOverHeader(FenceConfig target)
    {
        var pointer = Mouse.GetPosition(Workspace);
        return new Rect(target.Left, target.Top, target.Width, 34).Contains(pointer);
    }

    private void HandleHeaderDragCompleted(FenceControl control)
    {
        var isCompactTabReorder = control.IsShiftDetachHeaderDrag &&
                                  string.Equals(_config.TabViewMode, "Compact", StringComparison.OrdinalIgnoreCase) &&
                                  Math.Abs(control.HeaderDragDeltaX) >= 48 &&
                                  Math.Abs(Canvas.GetTop(control) - control.HeaderDragStartTop) < 48;
        if (control.IsShiftDetachHeaderDrag && !isCompactTabReorder && !string.IsNullOrWhiteSpace(control.Config.TabGroupId))
        {
            ClearMergePreview();
            DetachTab(control.Config.TabGroupId, GetTabIndex(control.Config), Canvas.GetLeft(control), Canvas.GetTop(control));
            return;
        }
        if (!control.IsShiftHeaderDrag && !string.IsNullOrWhiteSpace(control.Config.TabGroupId))
        {
            ClearMergePreview();
            foreach (var tab in GetTabs(control.Config.TabGroupId))
            {
                tab.Left = Canvas.GetLeft(control);
                tab.Top = Canvas.GetTop(control);
            }
            control.ClearEdgeDock();
            SaveConfigWithWarning();
            return;
        }
        if (_config.EnableRollup && _config.AutoRollupAtScreenEdge && Workspace.ActualHeight > 0)
        {
            var top = Canvas.GetTop(control);
            if (top <= 12)
            {
                ClearMergePreview();
                control.DockAndRollUp("Top");
                SaveConfigWithWarning();
                return;
            }
            if (top + control.ActualHeight >= Workspace.ActualHeight - 12)
            {
                ClearMergePreview();
                control.DockAndRollUp("Bottom");
                SaveConfigWithWarning();
                return;
            }
        }
        if (control.IsShiftHeaderDrag && string.Equals(_config.TabViewMode, "Compact", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(control.Config.TabGroupId) && Math.Abs(control.HeaderDragDeltaX) >= 48)
        {
            ClearMergePreview();
            var tabs = GetTabs(control.Config.TabGroupId);
            foreach (var tab in tabs)
            {
                tab.Left = control.HeaderDragStartLeft;
                tab.Top = control.HeaderDragStartTop;
            }
            var currentIndex = tabs.FindIndex(tab => tab.Id == control.Config.Id);
            ReorderTabs(control.Config.TabGroupId, currentIndex, currentIndex + (control.HeaderDragDeltaX < 0 ? -1 : 1));
            return;
        }
        control.ClearEdgeDock();
        if (CanHeaderDragMerge(control.Config, control.IsShiftHeaderDrag))
            MergeByHeaderOverlap(control);
    }

    private void UpdateMergePreview(FenceControl sourceControl)
    {
        if (!CanHeaderDragMerge(sourceControl.Config, sourceControl.IsShiftHeaderDrag))
        {
            ClearMergePreview();
            return;
        }
        if (_mergePreviewTarget != null && ReferenceEquals(sourceControl, _mergePreviewSource))
        {
            var pointer = Mouse.GetPosition(Workspace);
            var activeTarget = _mergePreviewTarget.Config;
            if (new Rect(activeTarget.Left, activeTarget.Top, activeTarget.Width, 34).Contains(pointer)) return;
            ClearMergePreview();
            return;
        }
        var target = FindMergeTarget(sourceControl);
        if (ReferenceEquals(target, _mergePreviewTarget) && ReferenceEquals(sourceControl, _mergePreviewSource)) return;
        ClearMergePreview();
        _mergePreviewTarget = target;
        _mergePreviewSource = target == null ? null : sourceControl;
        _mergePreviewTarget?.SetMergePreview(true);
        _mergePreviewSource?.SetMergeSourcePreview(true, target?.Config.Width / 3 ?? 0);
    }

    internal static bool CanHeaderDragMerge(FenceConfig source, bool shiftPressed) =>
        string.IsNullOrWhiteSpace(source.TabGroupId) || shiftPressed;

    private void ClearMergePreview()
    {
        _mergePreviewTarget?.SetMergePreview(false);
        _mergePreviewSource?.SetMergeSourcePreview(false);
        _mergePreviewTarget = null;
        _mergePreviewSource = null;
    }

    private FenceControl? FindMergeTarget(FenceControl sourceControl)
    {
        if (!_config.EnableTabCreation) return null;
        sourceControl.SyncConfigFromLayout();
        var source = sourceControl.Config;
        var pointer = Mouse.GetPosition(Workspace);
        return Workspace.Children.OfType<FenceControl>()
            .Where(control => control.Config.Id != source.Id && control.Config.PageIndex == source.PageIndex)
            .Where(control => string.IsNullOrWhiteSpace(source.TabGroupId) ||
                              !string.Equals(control.Config.TabGroupId, source.TabGroupId, StringComparison.OrdinalIgnoreCase))
            .Where(control => IsPointerInMergeZone(pointer, control.Config))
            .OrderBy(control => Math.Abs((control.Config.Left + control.Config.Width / 2) - pointer.X))
            .FirstOrDefault();
    }

    internal static bool IsPointerInMergeZone(System.Windows.Point pointer, FenceConfig target)
    {
        var mergeZoneLeft = target.Left + target.Width / 3;
        return new Rect(mergeZoneLeft, target.Top, target.Width / 3, 34).Contains(pointer);
    }

    internal static bool IsMergeCandidate(FenceConfig source, FenceConfig target)
    {
        var sourceCenter = new System.Windows.Point(source.Left + source.Width / 2, source.Top + 17);
        var mergeZoneLeft = target.Left + target.Width / 3;
        return new Rect(mergeZoneLeft, target.Top, target.Width / 3, 34).Contains(sourceCenter);
    }

    private void StackFences(FenceConfig source, FenceConfig target)
    {
        var groupId = string.IsNullOrWhiteSpace(target.TabGroupId) ? Guid.NewGuid().ToString("N") : target.TabGroupId;
        target.TabGroupId = groupId;
        source.TabGroupId = groupId;
        PlaceTabRelativeToTarget(source, target, Mouse.GetPosition(Workspace).X < target.Left + target.Width / 2);
        foreach (var tab in _config.Fences.Where(fence =>
                     fence.Id == source.Id ||
                     string.Equals(fence.TabGroupId, groupId, StringComparison.OrdinalIgnoreCase)))
        {
            CopyFenceGeometry(target, tab);
        }
        _activeTabByGroup[groupId] = source.Id;
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Merged Fence '{source.Title}' into tab group with '{target.Title}'.");
    }

    private static void CopyFenceGeometry(FenceConfig source, FenceConfig target)
    {
        target.Left = source.Left;
        target.Top = source.Top;
        target.Width = source.Width;
        target.Height = source.Height;
        target.ExpandedHeight = source.ExpandedHeight;
    }

    private void SwitchToNextTab(FenceControl control)
    {
        SwitchTab(control, 1);
    }

    private void SwitchTab(FenceControl control, int direction)
    {
        if (string.IsNullOrWhiteSpace(control.Config.TabGroupId)) return;
        var tabs = GetTabs(control.Config.TabGroupId);
        if (tabs.Count < 2) return;
        var index = tabs.FindIndex(fence => fence.Id == control.Config.Id);
        foreach (var tab in tabs) CopyFenceGeometry(control.Config, tab);
        var nextIndex = (index + direction + tabs.Count) % tabs.Count;
        _activeTabByGroup[control.Config.TabGroupId] = tabs[nextIndex].Id;
        RenderFences();
    }

    private void SwitchToTab(FenceControl control, int tabIndex)
    {
        if (string.IsNullOrWhiteSpace(control.Config.TabGroupId)) return;
        var tabs = _config.Fences.Where(fence => string.Equals(fence.TabGroupId, control.Config.TabGroupId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tabIndex < 0 || tabIndex >= tabs.Count || tabs[tabIndex].Id == control.Config.Id) return;
        control.SyncConfigFromLayout();
        foreach (var tab in tabs)
        {
            tab.Left = control.Config.Left;
            tab.Top = control.Config.Top;
            tab.Width = control.Config.Width;
            tab.Height = control.Config.Height;
        }
        _activeTabByGroup[control.Config.TabGroupId] = tabs[tabIndex].Id;
        RenderFences();
        ScheduleConfigSave();
    }

    private List<FenceConfig> GetTabs(string? groupId) => string.IsNullOrWhiteSpace(groupId)
        ? []
        : _config.Fences.Where(fence => string.Equals(fence.TabGroupId, groupId, StringComparison.OrdinalIgnoreCase)).ToList();

    private int GetTabIndex(FenceConfig fence) => GetTabs(fence.TabGroupId).FindIndex(tab => tab.Id == fence.Id);

    private void ReorderTabs(string? groupId, int fromIndex, int toIndex)
    {
        var tabs = GetTabs(groupId);
        if (tabs.Count < 2 || fromIndex < 0 || fromIndex >= tabs.Count) return;
        toIndex = Math.Clamp(toIndex, 0, tabs.Count - 1);
        if (fromIndex == toIndex) return;
        var moved = tabs[fromIndex];
        tabs.RemoveAt(fromIndex);
        tabs.Insert(toIndex, moved);
        var firstConfigIndex = _config.Fences.FindIndex(fence => tabs.Any(tab => tab.Id == fence.Id));
        _config.Fences.RemoveAll(fence => tabs.Any(tab => tab.Id == fence.Id));
        _config.Fences.InsertRange(Math.Max(0, firstConfigIndex), tabs);
        RenderFences();
        SaveConfigWithWarning();
    }

    private void PlaceTabRelativeToTarget(FenceConfig source, FenceConfig target, bool before)
    {
        if (source.Id == target.Id) return;
        _config.Fences.Remove(source);
        var targetIndex = _config.Fences.IndexOf(target);
        _config.Fences.Insert(Math.Clamp(targetIndex + (before ? 0 : 1), 0, _config.Fences.Count), source);
    }

    private void DetachTab(string? groupId, int tabIndex, double left, double top)
    {
        var tabs = GetTabs(groupId);
        if (tabs.Count < 2 || tabIndex < 0 || tabIndex >= tabs.Count) return;
        var detached = tabs[tabIndex];
        detached.TabGroupId = null;
        detached.Left = Math.Max(0, left);
        detached.Top = Math.Max(0, top);
        detached.IsCollapsed = false;
        detached.EdgeDock = null;
        if (DissolveSingleItemTabGroup(_config, groupId!)) _activeTabByGroup.Remove(groupId!);
        else if (_activeTabByGroup.TryGetValue(groupId!, out var activeId) && activeId == detached.Id) _activeTabByGroup.Remove(groupId!);
        RenderFences();
        SaveConfigWithWarning();
    }

    private void UnstackFence(FenceControl control)
    {
        if (string.IsNullOrWhiteSpace(control.Config.TabGroupId)) return;
        var groupId = control.Config.TabGroupId;
        control.Config.TabGroupId = null;
        if (DissolveSingleItemTabGroup(_config, groupId)) _activeTabByGroup.Remove(groupId);
        if (_activeTabByGroup.TryGetValue(groupId, out var activeId) && activeId == control.Config.Id)
        {
            _activeTabByGroup.Remove(groupId);
        }
        RenderFences();
        SaveConfigWithWarning();
    }

    internal static bool DissolveSingleItemTabGroup(AppConfig config, string groupId)
    {
        var remaining = config.Fences
            .Where(fence => string.Equals(fence.TabGroupId, groupId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (remaining.Count >= 2) return false;
        foreach (var fence in remaining) fence.TabGroupId = null;
        return true;
    }

    private void NewFenceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CreateNewDesktopGroup();
    }

    private void NewFolderPortalMenuItem_Click(object sender, RoutedEventArgs e) => CreateNewFenceFromFolderPicker();

    private void CreateNewDesktopGroup()
    {
        var dialog = new RenameFenceDialog(_loc.T("NewDesktopGroupDefaultName"), _loc, "NewFence", "FenceTitle", "TitleCannotBeEmpty")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;

        SyncAllFenceLayouts();
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var fence = ConfigService.CreateNewFence(_config.Fences.Count, desktop);
        fence.Kind = FenceConfig.DesktopGroupKind;
        fence.Title = dialog.InputText;
        fence.AssignedPaths = [];
        fence.PageIndex = _config.CurrentPage;
        PlaceFenceOnCurrentPage(fence);
        _config.Fences.Add(fence);
        AddFenceControl(fence);
        SaveConfigWithWarning();
    }

    private void AssignDesktopItems(FenceConfig target, IReadOnlyList<string> paths, int? insertionIndex = null)
    {
        var movedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var currentTargetOrder = target.AssignedPaths.ToArray();
        foreach (var path in movedPaths)
        {
            foreach (var fence in _config.Fences.Where(fence => fence.IsDesktopGroup))
            {
                fence.AssignedPaths.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            }
        }

        target.AssignedPaths = insertionIndex.HasValue
            ? ReorderFenceItems(currentTargetOrder, movedPaths, insertionIndex.Value)
            : target.AssignedPaths.Concat(movedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (insertionIndex.HasValue) target.SortMode = "None";
        RenderFences();
        SaveConfigWithWarning();
    }

    internal static List<string> ReorderFenceItems(IEnumerable<string> currentOrder, IEnumerable<string> movedPaths, int targetIndex)
    {
        var current = currentOrder.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var moved = movedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var clampedTarget = Math.Clamp(targetIndex, 0, current.Count);
        var removedBeforeTarget = current.Take(clampedTarget)
            .Count(path => moved.Contains(path, StringComparer.OrdinalIgnoreCase));
        var result = current
            .Where(path => !moved.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToList();
        result.InsertRange(Math.Clamp(clampedTarget - removedBeforeTarget, 0, result.Count), moved);
        return result;
    }

    private void ReleaseDesktopItemsToDesktop(IReadOnlyList<string> paths, System.Drawing.Point screenPoint)
    {
        if (IsPointOverVisibleFence(screenPoint) || !IsExplorerDesktopPoint(screenPoint))
        {
            AppLogger.Log($"Desktop item release ignored because the drop was not on desktop blank space: {screenPoint.X},{screenPoint.Y}");
            return;
        }

        var desktopRoots = GetDesktopRoots()
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var releasable = paths.Where(path =>
        {
            try
            {
                return desktopRoots.Contains(Path.GetFullPath(Path.GetDirectoryName(path) ?? ""));
            }
            catch { return false; }
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (releasable.Count == 0) return;

        try
        {
            var workspacePoint = Workspace.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            UpdateDesktopIconOrder(releasable.ToArray(), workspacePoint);
        }
        catch { }
        ReleaseDesktopMembership(releasable);
    }

    private void ReleaseDesktopMembership(IReadOnlySet<string> releasable)
    {
        var removed = 0;
        foreach (var fence in _config.Fences.Where(fence => fence.IsDesktopGroup))
            removed += fence.AssignedPaths.RemoveAll(path => releasable.Contains(path));
        if (removed == 0) return;
        AppLogger.Log($"Released {removed} desktop item(s) from Fence membership.");
        RenderFences();
        SaveConfigWithWarning();
    }

    private void BeginDesktopItemDrag()
    {
        _desktopItemDragActive = true;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowRgn(handle, IntPtr.Zero, true);
        AppLogger.Log("Desktop item drag capture enabled for the full workspace.");
    }

    private void EndDesktopItemDrag()
    {
        _dragPageSwitchTimer.Stop();
        _pendingDragPageDirection = 0;
        _desktopItemDragActive = false;
        UpdateDesktopWindowRegion();
        AppLogger.Log("Desktop item drag capture released.");
    }

    private void Workspace_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (_desktopItemDragActive)
        {
            var point = e.GetPosition(Workspace);
            var direction = point.X <= 36 ? -1 : point.X >= Workspace.ActualWidth - 36 ? 1 : 0;
            var canSwitch = direction != 0 &&
                            _config.CurrentPage + direction >= 0 &&
                            _config.CurrentPage + direction < GetPageCount();
            if (!canSwitch)
            {
                _dragPageSwitchTimer.Stop();
                _pendingDragPageDirection = 0;
            }
            else if (_pendingDragPageDirection != direction || !_dragPageSwitchTimer.IsEnabled)
            {
                _pendingDragPageDirection = direction;
                _dragPageSwitchTimer.Stop();
                _dragPageSwitchTimer.Start();
            }
        }
        e.Effects = DesktopDragData.TryGetPaths(e.Data, out _)
            ? System.Windows.DragDropEffects.Link
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void Workspace_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (DesktopDragData.TryGetPaths(e.Data, out var paths))
        {
            var dropPoint = e.GetPosition(Workspace);
            UpdateDesktopIconOrder(paths, dropPoint);
            if (!DesktopDragData.IsLooseIconDrag(e.Data))
                ReleaseDesktopMembership(paths.ToHashSet(StringComparer.OrdinalIgnoreCase));
            else
            {
                RenderFences();
                SaveConfigWithWarning();
            }
            e.Effects = System.Windows.DragDropEffects.Link;
            AppLogger.Log($"Workspace positioned {paths.Length} desktop item(s) without moving source files.");
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void UpdateDesktopIconOrder(IReadOnlyList<string> paths, System.Windows.Point dropPoint)
    {
        var orderedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = Math.Max(1, (int)Math.Floor((Workspace.ActualHeight > 0 ? Workspace.ActualHeight : Height) / 96));
        var column = Math.Max(0, (int)Math.Floor((dropPoint.X - 8) / 92));
        var row = Math.Clamp((int)Math.Floor((dropPoint.Y - 8) / 96), 0, rows - 1);
        _config.DesktopIconOrder = ReorderDesktopIcons(_config.DesktopIconOrder, orderedPaths, column * rows + row);
    }

    internal static List<string> ReorderDesktopIcons(IEnumerable<string> currentOrder, IEnumerable<string> movedPaths, int targetIndex)
    {
        var moved = movedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = currentOrder
            .Where(path => !moved.Contains(path, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.InsertRange(Math.Clamp(targetIndex, 0, result.Count), moved);
        return result;
    }

    private void UpdateNativeDesktopIconVisibility()
    {
        if (!_config.EnableDesktopIconIntegration || !_config.Fences.Any(fence => fence.IsDesktopGroup)) return;
        if (!_desktopIconLayoutService.IsVisible()) return;
        _restoreNativeDesktopIconsOnExit = _windowsDesktopIconsVisible;
        if (_desktopIconLayoutService.SetVisible(false))
        {
            AppLogger.Log("Explorer desktop icons hidden while MiniFences renders grouped icons.");
        }
    }

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsWindow();
    }

    private void CreateNewFenceFromFolderPicker()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _loc.T("ChooseNewFenceFolder"),
            SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        SyncAllFenceLayouts();
        var fence = ConfigService.CreateNewFence(_config.Fences.Count, dialog.SelectedPath);
        fence.PageIndex = _config.CurrentPage;
        PlaceFenceOnCurrentPage(fence);
        _config.Fences.Add(fence);
        AddFenceControl(fence);
        SaveConfigWithWarning();
    }

    private void RefreshAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RefreshAllFences();
    }

    private void RefreshAllFences()
    {
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            fence.LoadFolderItems();
        }
    }

    private void SwitchPage(int pageIndex)
    {
        var pageCount = GetPageCount();
        var targetPage = Math.Clamp(pageIndex, 0, pageCount - 1);
        if (targetPage == _config.CurrentPage)
        {
            return;
        }

        SyncAllFenceLayouts();
        _config.CurrentPage = targetPage;
        AppLogger.Log($"Switched to page {_config.CurrentPage + 1}/{GetPageCount()}.");
        RenderFences();
        SaveConfigWithWarning();
    }

    private void CreateNewPage()
    {
        SyncAllFenceLayouts();
        _config.CurrentPage = _config.PageCount;
        _config.PageCount = Math.Max(_config.PageCount + 1, _config.CurrentPage + 1);
        AppLogger.Log($"Created page {_config.CurrentPage + 1}.");
        RenderFences();
        SaveConfigWithWarning();
    }

    private int GetPageCount()
    {
        return PageService.GetPageCount(_config);
    }

    private void PlaceFenceOnCurrentPage(FenceConfig fence)
    {
        var workspaceWidth = Workspace.ActualWidth > 0 ? Workspace.ActualWidth : Width;
        var workspaceHeight = Workspace.ActualHeight > 0 ? Workspace.ActualHeight : Height;
        var existingFences = _config.Fences.Where(existing =>
            existing.PageIndex == fence.PageIndex &&
            !string.Equals(existing.Id, fence.Id, StringComparison.OrdinalIgnoreCase));
        var position = FenceLayoutService.FindAvailablePosition(existingFences, fence, workspaceWidth, workspaceHeight);
        fence.Left = position.Left;
        fence.Top = position.Top;
    }

    private void ToggleFencesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ToggleFencesVisibility();
    }

    private void PreviousPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SwitchPage(_config.CurrentPage - 1);
    }

    private void NextPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SwitchPage(_config.CurrentPage + 1);
    }

    private void DesktopContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateMenuState();
    }

    private void NewPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CreateNewPage();
    }

    private void DeleteCurrentPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DeleteCurrentPage();
    }

    private void DeleteCurrentPage()
    {
        SyncAllFenceLayouts();
        var pageNumber = _config.CurrentPage + 1;
        if (!PageService.TryDeleteEmptyPage(_config, _config.CurrentPage, out var error))
        {
            System.Windows.MessageBox.Show(this, error ?? _loc.T("CouldNotDeletePage"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AppLogger.Log($"Deleted empty page {pageNumber}.");
        RenderFences();
        SaveConfigWithWarning();
    }

    private void OrganizeDesktopMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OrganizeDesktopByType();
    }

    private void AutoOrganizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _config.EnableAutoOrganizeNewDesktopItems = DesktopAutoOrganizeMenuItem.IsChecked;
        ConfigureAutoOrganizerWatcher();
        SaveConfigWithWarning();
        AppLogger.Log($"Automatic desktop organization enabled: {_config.EnableAutoOrganizeNewDesktopItems}");
    }

    private void MigrateGroupsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, _loc.T("MigrateGroupsConfirm"), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var result = _autoOrganizerService.MigrateManagedFoldersToDesktopGroups(_config);
        RenderFences();
        SaveConfigWithWarning();
        var message = string.Format(_loc.T("MigrateGroupsResult"), result.MigratedFences, result.RestoredItems);
        if (result.Errors.Count > 0) message += $"\n\n{_loc.T("Errors")}\n{string.Join("\n", result.Errors.Take(8))}";
        System.Windows.MessageBox.Show(this, message, "MiniFences", MessageBoxButton.OK,
            result.Errors.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void CreateCategoryFencesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CreateCategoryFences();
    }

    private void UndoLastOrganizeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        UndoLastOrganization();
    }

    private void SaveLayoutSnapshotMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SyncAllFenceLayouts();
        _configService.SaveSnapshot(_config);
        System.Windows.MessageBox.Show(this, _loc.T("LayoutSnapshotSaved"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestoreLatestLayoutSnapshotMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_configService.TryLoadLatestSnapshot(out var snapshot, out var error) || snapshot == null)
        {
            System.Windows.MessageBox.Show(this, error ?? _loc.T("NoLayoutSnapshot"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (System.Windows.MessageBox.Show(this, _loc.T("RestoreLayoutSnapshotQuestion"), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var invalidPathCount = RestoreLayout(snapshot);
        ShowInvalidLayoutPathWarning(invalidPathCount);
        _settingsWindow?.RefreshFromMainWindow();
    }

    private void SaveNamedLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameFenceDialog(string.Empty, _loc, "SaveLayoutAs", "LayoutName", "LayoutNameCannotBeEmpty")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;

        if (_configService.NamedLayoutExists(dialog.InputText) &&
            System.Windows.MessageBox.Show(this, string.Format(_loc.T("OverwriteLayoutQuestion"), dialog.InputText), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SyncAllFenceLayouts();
        _configService.SaveNamedLayout(_config, dialog.InputText);
        System.Windows.MessageBox.Show(this, string.Format(_loc.T("LayoutSavedAs"), dialog.InputText), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DesktopLayoutsMenuItem_SubmenuOpened(object sender, RoutedEventArgs e)
    {
        while (DesktopLayoutsMenuItem.Items.Count > 2) DesktopLayoutsMenuItem.Items.RemoveAt(2);

        var names = _configService.GetNamedLayouts();
        if (names.Count == 0)
        {
            DesktopLayoutsMenuItem.Items.Add(new MenuItem { Header = _loc.T("NoSavedLayouts"), IsEnabled = false });
            return;
        }

        foreach (var name in names)
        {
            var item = new MenuItem { Header = name, Tag = name };
            item.Click += RestoreNamedLayoutMenuItem_Click;
            DesktopLayoutsMenuItem.Items.Add(item);
        }
    }

    private void RestoreNamedLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        string? error = null;
        if (sender is not MenuItem { Tag: string name } ||
            !_configService.TryLoadNamedLayout(name, out var layout, out error) || layout == null)
        {
            System.Windows.MessageBox.Show(this, error ?? _loc.T("CouldNotLoadSavedLayout"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (System.Windows.MessageBox.Show(this, string.Format(_loc.T("RestoreNamedLayoutQuestion"), name), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var invalidPathCount = RestoreLayout(layout);
        ShowInvalidLayoutPathWarning(invalidPathCount);
        _settingsWindow?.RefreshFromMainWindow();
    }

    private int RestoreLayout(LayoutDocument layout)
    {
        SyncAllFenceLayouts();
        try { _configService.SaveSnapshot(_config); }
        catch (Exception ex) { AppLogger.LogException("Failed to save automatic pre-restore layout snapshot", ex); }
        _config = _configService.ApplyLayout(_config, layout, out var invalidPathCount);
        _fencesHidden = _config.FencesHidden;
        RenderFences();
        ConfigureAutoOrganizerWatcher();
        SaveConfigWithWarning();
        return invalidPathCount;
    }

    private void ShowInvalidLayoutPathWarning(int count)
    {
        if (count > 0) System.Windows.MessageBox.Show(this, string.Format(_loc.T("LayoutInvalidPathsWarning"), count), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    internal IReadOnlyList<LayoutEntry> GetNamedLayoutEntries() => _configService.GetNamedLayoutEntries();
    internal IReadOnlyList<LayoutEntry> GetLayoutSnapshots() => _configService.GetSnapshots();
    internal bool NamedLayoutExists(string name) => _configService.NamedLayoutExists(name);

    internal bool SaveNamedLayout(string name, out string? error)
    {
        error = null;
        try { SyncAllFenceLayouts(); _configService.SaveNamedLayout(_config, name); return true; }
        catch (Exception ex) { AppLogger.LogException("Failed to save named layout", ex); error = ex.Message; return false; }
    }

    internal bool RestoreNamedLayout(string name, out int invalidPathCount, out string? error)
    {
        invalidPathCount = 0;
        if (!_configService.TryLoadNamedLayout(name, out var layout, out error) || layout == null) return false;
        invalidPathCount = RestoreLayout(layout); return true;
    }

    internal bool RestoreSnapshot(string id, out int invalidPathCount, out string? error)
    {
        invalidPathCount = 0;
        if (!_configService.TryLoadSnapshot(id, out var layout, out error) || layout == null) return false;
        invalidPathCount = RestoreLayout(layout); return true;
    }

    internal bool RenameNamedLayout(string oldName, string newName, bool overwrite, out string? error) =>
        _configService.RenameNamedLayout(oldName, newName, overwrite, out error);

    internal bool DeleteNamedLayout(string name, out string? error) => _configService.DeleteNamedLayout(name, out error);

    private void DesktopEnglishMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetLanguage(LocalizationService.English);
    }

    private void DesktopChineseMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SetLanguage(LocalizationService.Chinese);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void Workspace_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ReferenceEquals(e.OriginalSource, Workspace))
        {
            CloseOpenContextMenus();
            ToggleFencesVisibility();
            e.Handled = true;
        }
    }

    private void DeleteFence(FenceControl control)
    {
        if (_config.Fences.Count <= 1)
        {
            System.Windows.MessageBox.Show(this, _loc.T("KeepOneFence"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = System.Windows.MessageBox.Show(this, string.Format(_loc.T("DeleteFenceQuestion"), control.Config.Title), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Workspace.Children.Remove(control);
        _config.Fences.Remove(control.Config);
        UpdateDesktopWindowRegion();
        SaveConfigWithWarning();
    }

    private void MoveFenceToPage(FenceControl control, int pageIndex)
    {
        var targetPage = Math.Max(0, pageIndex);
        if (targetPage == control.Config.PageIndex)
        {
            return;
        }

        SyncAllFenceLayouts();
        _config.PageCount = Math.Max(_config.PageCount, targetPage + 1);
        control.Config.PageIndex = targetPage;
        _config.CurrentPage = targetPage;
        AppLogger.Log($"Moved Fence '{control.Config.Title}' to page {targetPage + 1}.");
        RenderFences();
        SaveConfigWithWarning();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SyncAllFenceLayouts();
        try
        {
            SaveConfigWithWarning();
        }
        catch
        {
            // SaveConfigWithWarning already showed the error.
        }

        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(1200, "MiniFences", _loc.T("StillRunningTray"), Forms.ToolTipIcon.Info);
            return;
        }

        _settingsWindow?.Close();
        _saveTimer.Stop();
        _autoOrganizeTimer.Stop();
        _desktopIconStateTimer.Stop();
        _desktopContentsRefreshTimer.Stop();
        _dragPageSwitchTimer.Stop();
        _dragRegionRefreshTimer.Stop();
        if (_config.EnableDesktopIconIntegration && _windowsDesktopIconsVisible && _desktopIconLayoutService.SetVisible(true))
            AppLogger.Log("Explorer desktop icons restored on exit.");
        StopAutoOrganizerWatcher();
        StopDesktopContentsWatcher();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        UninstallDesktopDoubleClickHook();
        UnregisterGlobalHotkeys();
        System.Windows.Application.Current.SessionEnding -= MainWindow_SessionEnding;
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        base.OnClosing(e);
    }

    private void MainWindow_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _isExiting = true;
        SyncAllFenceLayouts();
        try
        {
            SaveConfigWithWarning();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to save config during session ending", ex);
        }
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (!_isExiting && WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            SendBehindNormalWindows();
        }
    }

    private void SyncAllFenceLayouts()
    {
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            fence.SyncConfigFromLayout();
        }
    }

    private void ClampAllFencesToWorkspace()
    {
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            fence.ClampToParentBounds();
        }

        UpdateDesktopWindowRegion();
    }

    private bool ClampFenceConfigToWorkspace(FenceConfig fence)
    {
        var workspaceWidth = Workspace.ActualWidth > 0 ? Workspace.ActualWidth : Width;
        var workspaceHeight = Workspace.ActualHeight > 0 ? Workspace.ActualHeight : Height;
        if (workspaceWidth <= 0 || workspaceHeight <= 0)
        {
            return false;
        }

        var originalLeft = fence.Left;
        var originalTop = fence.Top;
        var originalWidth = fence.Width;
        var originalHeight = fence.Height;
        fence.Width = Math.Min(Math.Max(240, fence.Width), Math.Max(240, workspaceWidth));
        fence.Height = Math.Min(Math.Max(180, fence.Height), Math.Max(180, workspaceHeight));
        fence.Left = Math.Clamp(fence.Left, 0, Math.Max(0, workspaceWidth - fence.Width));
        fence.Top = Math.Clamp(fence.Top, 0, Math.Max(0, workspaceHeight - fence.Height));
        return Math.Abs(fence.Left - originalLeft) > 0.01 ||
               Math.Abs(fence.Top - originalTop) > 0.01 ||
               Math.Abs(fence.Width - originalWidth) > 0.01 ||
               Math.Abs(fence.Height - originalHeight) > 0.01;
    }

    private void SaveConfigWithWarning()
    {
        _saveTimer.Stop();
        SyncAllFenceLayouts();
        try
        {
            _configService.Save(_config);
            _configSaveErrorShown = false;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to save configuration", ex);
            if (!_configSaveErrorShown)
            {
                _configSaveErrorShown = true;
                System.Windows.MessageBox.Show(this, string.Format(_loc.T("CouldNotSaveConfig"), ex.Message), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void ScheduleConfigSave()
    {
        SyncAllFenceLayouts();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _showMiniFencesMenuItem = new Forms.ToolStripMenuItem("Open Settings");
        _showMiniFencesMenuItem.Click += (_, _) => ShowSettingsWindow();
        menu.Items.Add(_showMiniFencesMenuItem);
        _toggleFencesMenuItem = new Forms.ToolStripMenuItem("Hide Fences");
        _toggleFencesMenuItem.Click += (_, _) => ToggleFencesVisibility();
        menu.Items.Add(_toggleFencesMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        _exitMenuItem = new Forms.ToolStripMenuItem("Exit");
        _exitMenuItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(_exitMenuItem);

        var icon = new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "MiniFences",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowSettingsWindow();
        return icon;
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"));
            if (resource?.Stream != null)
            {
                using var source = new Icon(resource.Stream);
                return (Icon)source.Clone();
            }
        }
        catch
        {
            // Keep the tray usable even if a damaged deployment omits the icon resource.
        }

        return SystemIcons.Application;
    }

    internal void ShowFromTray()
    {
        ShowSettingsWindow();
    }

    internal void RequestExitFromAnotherInstance()
    {
        ExitApplication();
    }

    private void ShowSettingsWindow()
    {
        Dispatcher.Invoke(() =>
        {
            EnsureDesktopWindowVisible();
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(this);
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
                _settingsWindow.Show();
                AppLogger.Log("Settings window opened.");
            }
            else
            {
                if (_settingsWindow.WindowState == WindowState.Minimized)
                {
                    _settingsWindow.WindowState = WindowState.Normal;
                }

                _settingsWindow.Show();
                _settingsWindow.ReloadState();
            }

            _settingsWindow.Activate();
        });
    }

    private void EnsureDesktopWindowVisible()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        EnsureDesktopHostAttachment();
        ApplyDesktopWorkAreaBounds();
        Dispatcher.BeginInvoke(SendBehindNormalWindows, DispatcherPriority.ApplicationIdle);
    }

    private void ApplyDesktopWorkAreaBounds()
    {
        var workArea = SystemParameters.WorkArea;
        Width = workArea.Width;
        Height = workArea.Height;
        if (_isDesktopHosted)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                            ?? System.Windows.Media.Matrix.Identity;
            var screenOrigin = new NativePoint(
                (int)Math.Round(workArea.Left * transform.M11),
                (int)Math.Round(workArea.Top * transform.M22));
            ScreenToClient(_desktopHostHandle, ref screenOrigin);
            SetWindowPos(
                handle,
                HwndTop,
                screenOrigin.X,
                screenOrigin.Y,
                (int)Math.Round(workArea.Width * transform.M11),
                (int)Math.Round(workArea.Height * transform.M22),
                SwpNoActivate);
            AppLogger.Log($"Applied Explorer-hosted desktop work area bounds: {screenOrigin.X},{screenOrigin.Y},{Width},{Height}");
            return;
        }

        Left = workArea.Left;
        Top = workArea.Top;
        AppLogger.Log($"Applied top-level desktop work area bounds: {Left},{Top},{Width},{Height}");
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        ReapplyDesktopWorkAreaBounds("display settings changed");
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General)
        {
            Dispatcher.BeginInvoke(SynchronizeWindowsDesktopIconState, DispatcherPriority.Background);
            ReapplyDesktopWorkAreaBounds($"user preference changed: {e.Category}");
        }
    }

    private void SynchronizeWindowsDesktopIconState()
    {
        EnsureDesktopHostAttachment();
        var visible = ReadWindowsDesktopIconsVisible();
        var changed = visible != _windowsDesktopIconsVisible;
        _windowsDesktopIconsVisible = visible;
        if (visible) UpdateNativeDesktopIconVisibility();
        if (changed)
        {
            AppLogger.Log($"Windows desktop icon visibility changed: {visible}.");
            ApplyFenceVisibility();
        }
    }

    private static bool ReadWindowsDesktopIconsVisible()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return Convert.ToInt32(key?.GetValue("HideIcons", 0) ?? 0) == 0;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not read Windows desktop icon visibility", ex);
            return true;
        }
    }

    private void ReapplyDesktopWorkAreaBounds(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AppLogger.Log($"Reapplying desktop work area bounds because {reason}.");
            EnsureDesktopHostAttachment();
            ApplyDesktopWorkAreaBounds();
            ClampAllFencesToWorkspace();
            ScheduleConfigSave();
            SendBehindNormalWindows();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void SendBehindNormalWindows()
    {
        if (_fencesTopmost) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (_isDesktopHosted && GetParent(handle) == _desktopHostHandle)
            {
                SetWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                return;
            }

            var desktopHost = FindDesktopViewHost();
            if (desktopHost == IntPtr.Zero)
            {
                SetWindowPos(handle, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                AppLogger.Log("Desktop host was not found; MiniFences was placed at the bottom for input safety.");
                return;
            }

            var windowDirectlyAboveDesktop = GetWindow(desktopHost, GwHwndPrev);
            if (windowDirectlyAboveDesktop == handle)
            {
                return;
            }

            var insertAfter = windowDirectlyAboveDesktop == IntPtr.Zero
                ? HwndTop
                : windowDirectlyAboveDesktop;
            SetWindowPos(handle, insertAfter, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            AppLogger.Log("MiniFences positioned directly above the Explorer desktop layer and below normal windows.");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to send MiniFences behind normal windows", ex);
        }
    }

    private bool AttachToDesktopHost()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var desktopHost = FindDesktopViewHost();
            if (handle == IntPtr.Zero || desktopHost == IntPtr.Zero) return false;
            if (GetParent(handle) == desktopHost)
            {
                _desktopHostHandle = desktopHost;
                _isDesktopHosted = true;
                return true;
            }

            var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
            style = (style & ~WsPopup) | WsChild;
            SetWindowLongPtr(handle, GwlStyle, new IntPtr(style));
            SetParent(handle, desktopHost);
            _desktopHostHandle = desktopHost;
            _isDesktopHosted = GetParent(handle) == desktopHost;
            SetWindowPos(
                handle,
                HwndTop,
                0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
            return _isDesktopHosted;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to attach MiniFences to the Explorer desktop host", ex);
            _desktopHostHandle = IntPtr.Zero;
            _isDesktopHosted = false;
            return false;
        }
    }

    private void EnsureDesktopHostAttachment()
    {
        if (_fencesTopmost || _isExiting) return;
        var handle = new WindowInteropHelper(this).Handle;
        var currentDesktopHost = FindDesktopViewHost();
        if (_isDesktopHosted &&
            handle != IntPtr.Zero &&
            _desktopHostHandle != IntPtr.Zero &&
            _desktopHostHandle == currentDesktopHost &&
            GetParent(handle) == _desktopHostHandle) return;

        _isDesktopHosted = false;
        _desktopHostHandle = IntPtr.Zero;
        if (AttachToDesktopHost())
        {
            ApplyDesktopWorkAreaBounds();
            UpdateDesktopWindowRegion();
            AppLogger.Log("Explorer desktop host attachment restored.");
        }
    }

    private void DetachFromDesktopHost()
    {
        if (!_isDesktopHosted) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            SetParent(handle, IntPtr.Zero);
            var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
            style = (style & ~WsChild) | WsPopup;
            SetWindowLongPtr(handle, GwlStyle, new IntPtr(style));
            SetWindowPos(
                handle,
                HwndNoTopmost,
                0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to detach MiniFences from the Explorer desktop host", ex);
        }
        finally
        {
            _desktopHostHandle = IntPtr.Zero;
            _isDesktopHosted = false;
        }
    }

    private void ToggleFencesTopmost()
    {
        _fencesTopmost = !_fencesTopmost;
        var handle = new WindowInteropHelper(this).Handle;
        if (_fencesTopmost)
        {
            DetachFromDesktopHost();
            ApplyDesktopWorkAreaBounds();
            if (handle != IntPtr.Zero)
                SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        else
        {
            if (handle != IntPtr.Zero)
                SetWindowPos(handle, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            AttachToDesktopHost();
            ApplyDesktopWorkAreaBounds();
            SendBehindNormalWindows();
        }
        _settingsWindow?.ReloadState();
        AppLogger.Log(_fencesTopmost ? "Fences pinned above normal windows." : "Fences restored to the desktop layer.");
    }

    private void RegisterGlobalHotkeys()
    {
        UnregisterGlobalHotkeys();
        _previousPageGesture = ParseHotkey(_config.PreviousPageHotkey);
        _nextPageGesture = ParseHotkey(_config.NextPageHotkey);
        _toggleTopmostGesture = ParseHotkey(_config.ToggleTopmostHotkey);
        _keyboardHookProc = KeyboardHookProc;
        _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, GetModuleHandle(null), 0);
        if (_keyboardHookHandle == IntPtr.Zero)
            AppLogger.Log($"Global keyboard hook installation failed. Win32Error={Marshal.GetLastWin32Error()}");
        else
            AppLogger.Log($"Custom hotkeys enabled: {_config.PreviousPageHotkey}; {_config.NextPageHotkey}; {_config.ToggleTopmostHotkey}");
    }

    private void UnregisterGlobalHotkeys()
    {
        if (_keyboardHookHandle != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHookHandle);
        _keyboardHookHandle = IntPtr.Zero;
        _keyboardHookProc = null;
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam.ToInt32() == WmKeyDown || wParam.ToInt32() == WmSysKeyDown))
        {
            var key = (uint)Marshal.ReadInt32(lParam);
            Action? action = null;
            if (MatchesHotkey(_previousPageGesture, key)) action = () => SwitchPage(_config.CurrentPage - 1);
            else if (MatchesHotkey(_nextPageGesture, key)) action = () => SwitchPage(_config.CurrentPage + 1);
            else if (MatchesHotkey(_toggleTopmostGesture, key)) action = ToggleFencesTopmost;
            if (action != null)
            {
                Dispatcher.BeginInvoke(action);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_keyboardHookHandle, code, wParam, lParam);
    }

    private static bool MatchesHotkey(HotkeyGesture? gesture, uint key) =>
        gesture != null && gesture.Key == key &&
        gesture.Control == IsKeyDown(VkControl) &&
        gesture.Alt == IsKeyDown(VkMenu) &&
        gesture.Shift == IsKeyDown(VkShift) &&
        gesture.Win == (IsKeyDown(VkLeftWin) || IsKeyDown(VkRightWin));

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static HotkeyGesture? ParseHotkey(string value)
    {
        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var control = parts.Any(part => part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase));
        var alt = parts.Any(part => part.Equals("Alt", StringComparison.OrdinalIgnoreCase));
        var shift = parts.Any(part => part.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        var win = parts.Any(part => part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase));
        var keyName = parts.LastOrDefault(part => !part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) &&
                                                  !part.Equals("Control", StringComparison.OrdinalIgnoreCase) &&
                                                  !part.Equals("Alt", StringComparison.OrdinalIgnoreCase) &&
                                                  !part.Equals("Shift", StringComparison.OrdinalIgnoreCase) &&
                                                  !part.Equals("Win", StringComparison.OrdinalIgnoreCase) &&
                                                  !part.Equals("Windows", StringComparison.OrdinalIgnoreCase));
        var key = keyName?.ToUpperInvariant() switch
        {
            "LEFT" or "←" => (uint)VkLeft,
            "RIGHT" or "→" => (uint)VkRight,
            "SPACE" => (uint)VkSpace,
            { Length: 1 } text when char.IsLetterOrDigit(text[0]) => text[0],
            _ => 0u
        };
        return key == 0 ? null : new HotkeyGesture(control, alt, shift, win, key);
    }

    private void ApplyDesktopWindowStyles()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            extendedStyle |= WsExNoActivate | WsExToolWindow;
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(extendedStyle));
            SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
            AppLogger.Log("Desktop layer configured as a non-activating tool window.");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to apply non-activating desktop window styles", ex);
        }
    }

    internal void FocusInlineRenameEditor(System.Windows.Controls.TextBox editor)
    {
        _activeInlineRenameEditor = editor;
        SetInlineRenameWindowActivation(true);

        var handle = new WindowInteropHelper(this).Handle;
        var activated = Activate();
        var foregroundResult = handle != IntPtr.Zero && SetForegroundWindow(handle);
        var nativeFocusResult = handle == IntPtr.Zero ? IntPtr.Zero : SetFocus(handle);
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_activeInlineRenameEditor, editor) || !editor.IsVisible) return;
            var wpfFocused = editor.Focus();
            Keyboard.Focus(editor);
            editor.SelectAll();
            AppLogger.Log($"Inline rename focus requested. Activated={activated}; Foreground={foregroundResult}; WpfFocused={wpfFocused}; NativePreviousFocus=0x{nativeFocusResult.ToInt64():X}");
        }, DispatcherPriority.Input);
    }

    internal void ReleaseInlineRenameEditor(System.Windows.Controls.TextBox editor)
    {
        if (!ReferenceEquals(_activeInlineRenameEditor, editor)) return;
        _activeInlineRenameEditor = null;
        if (editor.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        SetInlineRenameWindowActivation(false);
    }

    private void SetInlineRenameWindowActivation(bool enabled)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            var updatedStyle = UpdateInlineRenameActivationStyle(extendedStyle, enabled);
            if (updatedStyle == extendedStyle) return;

            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(updatedStyle));
            SetWindowPos(
                handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to switch inline rename activation mode", ex);
        }
    }

    internal static long UpdateInlineRenameActivationStyle(long extendedStyle, bool enabled) =>
        enabled ? extendedStyle & ~WsExNoActivate : extendedStyle | WsExNoActivate;

    private static IntPtr FindDesktopViewHost()
    {
        var programManager = FindWindow("Progman", null);
        if (programManager != IntPtr.Zero && FindWindowEx(programManager, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
        {
            return programManager;
        }

        var result = IntPtr.Zero;
        EnumWindows((topLevelWindow, _) =>
        {
            var className = new StringBuilder(64);
            GetClassName(topLevelWindow, className, className.Capacity);
            if (!string.Equals(className.ToString(), "WorkerW", StringComparison.Ordinal)) return true;
            if (FindWindowEx(topLevelWindow, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                return true;
            }

            result = topLevelWindow;
            return false;
        }, IntPtr.Zero);

        if (result != IntPtr.Zero) return result;

        var desktopList = FindWindowEx(FindWindowEx(programManager, IntPtr.Zero, "SHELLDLL_DefView", null), IntPtr.Zero, "SysListView32", "FolderView");
        return desktopList != IntPtr.Zero ? GetAncestor(desktopList, GaRoot) : IntPtr.Zero;
    }

    private void HandleWindowDeactivated()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SendBehindNormalWindows();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void CloseOpenContextMenus(bool force = false, System.Drawing.Point? clickPoint = null)
    {
        var screenPoint = clickPoint.HasValue
            ? new System.Windows.Point(clickPoint.Value.X, clickPoint.Value.Y)
            : (System.Windows.Point?)null;
        if (ContextMenu?.IsOpen == true &&
            (force || !screenPoint.HasValue || !IsPointInsideContextMenu(ContextMenu, screenPoint.Value)))
        {
            ContextMenu.IsOpen = false;
        }

        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            fence.CloseOpenContextMenus(force, screenPoint);
        }
        foreach (var icon in Workspace.Children.OfType<DesktopLooseIconControl>())
        {
            icon.CloseContextMenu(force, screenPoint);
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

    private void InstallDesktopDoubleClickHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            return;
        }

        _mouseHookProc = DesktopMouseHookProc;
        _mouseHookHandle = SetWindowsHookEx(WhMouseLl, _mouseHookProc, GetModuleHandle(null), 0);
        if (_mouseHookHandle == IntPtr.Zero)
        {
            AppLogger.Log($"Failed to install desktop double-click mouse hook. Win32Error={Marshal.GetLastWin32Error()}");
            return;
        }

        AppLogger.Log("Desktop double-click mouse hook installed.");
    }

    private void UninstallDesktopDoubleClickHook()
    {
        if (_mouseHookHandle == IntPtr.Zero)
        {
            return;
        }

        if (!UnhookWindowsHookEx(_mouseHookHandle))
        {
            AppLogger.Log($"Failed to uninstall desktop double-click mouse hook. Win32Error={Marshal.GetLastWin32Error()}");
        }

        _mouseHookHandle = IntPtr.Zero;
        _mouseHookProc = null;
    }

    private IntPtr DesktopMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var hook = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var screenPoint = new System.Drawing.Point(hook.pt.x, hook.pt.y);
                if (wParam == new IntPtr(WmLButtonDown))
                {
                    var clickTicks = Environment.TickCount64;
                    Dispatcher.BeginInvoke(
                        () => HandleGlobalLeftButtonDown(screenPoint, clickTicks),
                        DispatcherPriority.Input);
                    Dispatcher.BeginInvoke(() => CloseOpenContextMenus(clickPoint: screenPoint), DispatcherPriority.ContextIdle);
                }
                else if (wParam == new IntPtr(WmRButtonDown))
                {
                    Dispatcher.BeginInvoke(
                        () => CommitInlineRenamesOutside(screenPoint),
                        DispatcherPriority.Input);
                    Dispatcher.BeginInvoke(() => CloseOpenContextMenus(clickPoint: screenPoint), DispatcherPriority.ContextIdle);
                }
                else if (wParam == new IntPtr(WmMouseMove))
                {
                    _lastMouseScreenPoint = screenPoint;
                    if (!_hoverUpdatePending)
                    {
                        _hoverUpdatePending = true;
                        Dispatcher.BeginInvoke(HandleGlobalMouseMove, DispatcherPriority.Input);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Desktop double-click hook failed", ex);
        }

        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private void HandleGlobalMouseMove()
    {
        _hoverUpdatePending = false;
        if (_fencesHidden)
        {
            return;
        }

        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            if (fence.IsTitleDragging) continue;
            fence.SetHoverExpandedFromDesktopHost(IsScreenPointOverFence(fence, _lastMouseScreenPoint));
        }
    }

    private void HandleGlobalLeftButtonDown(System.Drawing.Point screenPoint, long clickTicks)
    {
        CommitInlineRenamesOutside(screenPoint);
        var clickedDesktopBlank = IsScreenPointInsideDesktopWorkArea(screenPoint) &&
                                  !IsPointOverVisibleFence(screenPoint) &&
                                  IsExplorerDesktopPoint(screenPoint);
        if (clickedDesktopBlank && _selectedLoosePaths.Count > 0)
        {
            _selectedLoosePaths.Clear();
            _looseSelectionAnchor = null;
            UpdateLooseIconSelectionVisuals();
        }
        if (!_config.EnableDesktopDoubleClick)
        {
            _desktopDoubleClickTracker.Reset();
            return;
        }

        var isEligibleDesktopBlank =
            clickedDesktopBlank &&
            !IsPointOverDesktopItem(screenPoint);
        if (!_desktopDoubleClickTracker.RegisterClick(
                isEligibleDesktopBlank,
                screenPoint.X,
                screenPoint.Y,
                clickTicks))
        {
            return;
        }

        ToggleFencesVisibility();
        AppLogger.Log(_fencesHidden
            ? "Fences hidden by desktop double-click."
            : "Fences shown by desktop double-click.");
    }

    private void CommitInlineRenamesOutside(System.Drawing.Point screenPoint)
    {
        var wpfPoint = new System.Windows.Point(screenPoint.X, screenPoint.Y);
        var fences = Workspace.Children.OfType<FenceControl>().ToArray();
        var looseIcons = Workspace.Children.OfType<DesktopLooseIconControl>().ToArray();

        foreach (var fence in fences)
        {
            fence.CommitInlineRenameIfPointerOutside(wpfPoint);
        }

        foreach (var looseIcon in looseIcons)
        {
            looseIcon.CommitInlineRenameIfPointerOutside(wpfPoint);
        }
    }

    private bool IsScreenPointInsideDesktopWorkArea(System.Drawing.Point screenPoint)
    {
        var workArea = SystemParameters.WorkArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                        ?? System.Windows.Media.Matrix.Identity;
        var physicalWorkArea = new System.Windows.Rect(
            workArea.Left * transform.M11,
            workArea.Top * transform.M22,
            workArea.Width * transform.M11,
            workArea.Height * transform.M22);
        return physicalWorkArea.Contains(screenPoint.X, screenPoint.Y);
    }

    private bool IsExplorerDesktopPoint(System.Drawing.Point screenPoint)
    {
        var handle = WindowFromPoint(new NativePoint(screenPoint.X, screenPoint.Y));
        while (handle != IntPtr.Zero)
        {
            var className = GetWindowClassName(handle);
            if (IsDesktopWindowClass(className))
            {
                return true;
            }

            if (IsNonDesktopShellClass(className))
            {
                return false;
            }

            handle = GetParent(handle);
        }

        return false;
    }

    private static bool IsDesktopWindowClass(string className)
    {
        return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "SysListView32", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNonDesktopShellClass(string className)
    {
        return string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(className, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPointOverDesktopItem(System.Drawing.Point screenPoint)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            for (var depth = 0; element != null && depth < 8; depth += 1)
            {
                if (element.Current.ControlType == ControlType.ListItem)
                {
                    return true;
                }

                element = TreeWalker.ControlViewWalker.GetParent(element);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Desktop item hit-test failed; desktop double-click ignored for safety", ex);
            return true;
        }

        return false;
    }

    private static string GetWindowClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        return GetClassName(handle, builder, builder.Capacity) > 0
            ? builder.ToString()
            : "";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == App.WmExitMiniFences)
        {
            handled = true;
            Dispatcher.BeginInvoke(ExitApplication);
            return IntPtr.Zero;
        }

        if (msg == WmHotkey)
        {
            handled = true;
            switch (wParam.ToInt32())
            {
                case HotkeyPreviousPage: SwitchPage(_config.CurrentPage - 1); break;
                case HotkeyNextPage: SwitchPage(_config.CurrentPage + 1); break;
                case HotkeyToggleTopmost: ToggleFencesTopmost(); break;
            }
            return IntPtr.Zero;
        }

        if (msg == WmSysCommand && (wParam.ToInt64() & 0xfff0) == ScMinimize)
        {
            handled = true;
            AppLogger.Log("Ignored minimize system command for the Explorer-hosted desktop layer.");
            return IntPtr.Zero;
        }

        if (msg == WmNcHitTest && !_desktopItemDragActive && !IsScreenPointOverVisibleFence(lParam))
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    private void UpdateDesktopWindowRegion()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        if (_desktopItemDragActive)
        {
            SetWindowRgn(handle, IntPtr.Zero, true);
            return;
        }

        var combinedRegion = CreateRectRgn(0, 0, 0, 0);
        if (combinedRegion == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                ?? System.Windows.Media.Matrix.Identity;
            if (!_fencesHidden)
            {
                foreach (var fence in Workspace.Children.OfType<FenceControl>())
                {
                    if (fence.Visibility != Visibility.Visible)
                    {
                        continue;
                    }

                    var left = Canvas.GetLeft(fence);
                    var top = Canvas.GetTop(fence);
                    if (double.IsNaN(left)) left = 0;
                    if (double.IsNaN(top)) top = 0;
                    var width = fence.ActualWidth > 0 ? fence.ActualWidth : fence.Width;
                    var height = fence.ActualHeight > 0 ? fence.ActualHeight : fence.Height;
                    var topLeft = transform.Transform(new System.Windows.Point(left, top));
                    var bottomRight = transform.Transform(new System.Windows.Point(left + width, top + height));
                    var fenceRegion = CreateRectRgn(
                        (int)Math.Floor(topLeft.X),
                        (int)Math.Floor(topLeft.Y),
                        (int)Math.Ceiling(bottomRight.X) + 1,
                        (int)Math.Ceiling(bottomRight.Y) + 1);
                    if (fenceRegion == IntPtr.Zero)
                    {
                        continue;
                    }

                    CombineRgn(combinedRegion, combinedRegion, fenceRegion, RgnOr);
                    DeleteObject(fenceRegion);
                }
                foreach (var icon in Workspace.Children.OfType<DesktopLooseIconControl>().Where(icon => icon.Visibility == Visibility.Visible))
                {
                    var left = Canvas.GetLeft(icon);
                    var top = Canvas.GetTop(icon);
                    var width = icon.ActualWidth > 0 ? icon.ActualWidth : icon.Width;
                    var height = icon.ActualHeight > 0 ? icon.ActualHeight : icon.MinHeight;
                    var topLeft = transform.Transform(new System.Windows.Point(left, top));
                    var bottomRight = transform.Transform(new System.Windows.Point(left + width, top + height));
                    var iconRegion = CreateRectRgn(
                        (int)Math.Floor(topLeft.X), (int)Math.Floor(topLeft.Y),
                        (int)Math.Ceiling(bottomRight.X) + 1, (int)Math.Ceiling(bottomRight.Y) + 1);
                    if (iconRegion == IntPtr.Zero) continue;
                    CombineRgn(combinedRegion, combinedRegion, iconRegion, RgnOr);
                    DeleteObject(iconRegion);
                }
            }

            if (SetWindowRgn(handle, combinedRegion, true) != 0)
            {
                combinedRegion = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to update the desktop window region", ex);
        }
        finally
        {
            if (combinedRegion != IntPtr.Zero)
            {
                DeleteObject(combinedRegion);
            }
        }
    }

    private bool IsScreenPointOverVisibleFence(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var screenPoint = new System.Windows.Point((short)(value & 0xffff), (short)((value >> 16) & 0xffff));
        return IsScreenPointOverVisibleFence(screenPoint);
    }

    private bool IsPointOverVisibleFence(System.Drawing.Point screenPoint)
    {
        return IsScreenPointOverVisibleFence(new System.Windows.Point(screenPoint.X, screenPoint.Y));
    }

    private bool IsPointOverWorkspace(System.Drawing.Point screenPoint)
    {
        try
        {
            var workspacePoint = Workspace.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            var width = Workspace.ActualWidth > 0 ? Workspace.ActualWidth : ActualWidth;
            var height = Workspace.ActualHeight > 0 ? Workspace.ActualHeight : ActualHeight;
            return IsPointInsideWorkspace(workspacePoint, width, height);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsPointInsideWorkspace(System.Windows.Point point, double width, double height) =>
        width > 0 && height > 0 && point.X >= 0 && point.Y >= 0 && point.X < width && point.Y < height;

    private bool IsScreenPointOverVisibleFence(System.Windows.Point screenPoint)
    {
        var workspacePoint = Workspace.PointFromScreen(screenPoint);

        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            if (IsWorkspacePointOverFence(fence, workspacePoint))
            {
                return true;
            }
        }

        foreach (var icon in Workspace.Children.OfType<DesktopLooseIconControl>())
        {
            if (icon.Visibility != Visibility.Visible) continue;
            var width = icon.ActualWidth > 0 ? icon.ActualWidth : icon.Width;
            var height = icon.ActualHeight > 0 ? icon.ActualHeight : icon.MinHeight;
            var bounds = new Rect(Canvas.GetLeft(icon), Canvas.GetTop(icon), width, height);
            if (bounds.Contains(workspacePoint)) return true;
        }

        return false;
    }

    private bool IsScreenPointOverFence(FenceControl fence, System.Drawing.Point screenPoint) =>
        IsWorkspacePointOverFence(fence, Workspace.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y)));

    private static bool IsWorkspacePointOverFence(FenceControl fence, System.Windows.Point workspacePoint)
    {
        if (fence.Visibility != Visibility.Visible || !fence.IsHitTestVisible)
        {
            return false;
        }

        var left = Canvas.GetLeft(fence);
        var top = Canvas.GetTop(fence);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        const double edgeTolerance = 4;
        return workspacePoint.X >= left - edgeTolerance &&
               workspacePoint.X <= left + fence.ActualWidth + edgeTolerance &&
               workspacePoint.Y >= top - edgeTolerance &&
               workspacePoint.Y <= top + fence.ActualHeight + edgeTolerance;
    }

    private void ToggleFencesVisibility()
    {
        CloseOpenContextMenus(force: true);
        _fencesHidden = !_fencesHidden;
        if (!_fencesHidden)
        {
            EnsureDesktopWindowVisible();
        }
        _config.FencesHidden = _fencesHidden;
        ApplyFenceVisibility();
        ScheduleConfigSave();
    }

    private void SnapToGridMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _config.EnableSnapToGrid = DesktopSnapToGridMenuItem.IsChecked;
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            fence.SnapToGrid = _config.EnableSnapToGrid;
        }

        SaveConfigWithWarning();
    }

    private void AutoLayoutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item) ArrangeCurrentPage(item.Tag?.ToString() ?? "Balanced");
    }

    private void ArrangeCurrentPage(string preset)
    {
        SyncAllFenceLayouts();
        var fences = _config.Fences.Where(fence => fence.PageIndex == _config.CurrentPage && !fence.IsLocked).ToList();
        if (fences.Count == 0) return;
        var width = Workspace.ActualWidth > 0 ? Workspace.ActualWidth : Width;
        var height = Workspace.ActualHeight > 0 ? Workspace.ActualHeight : Height;
        const double margin = 28;
        const double gap = 18;
        int columns;
        int rows;
        if (preset == "Columns") { columns = fences.Count; rows = 1; }
        else if (preset == "Rows") { columns = 1; rows = fences.Count; }
        else
        {
            columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(fences.Count * width / Math.Max(1, height))));
            rows = (int)Math.Ceiling((double)fences.Count / columns);
        }
        var cellWidth = (width - margin * 2 - gap * (columns - 1)) / columns;
        var cellHeight = (height - margin * 2 - gap * (rows - 1)) / rows;
        if (preset == "Compact")
        {
            cellWidth = Math.Min(360, cellWidth);
            cellHeight = Math.Min(300, cellHeight);
        }
        for (var index = 0; index < fences.Count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var fence = fences[index];
            fence.Left = margin + column * (cellWidth + gap);
            fence.Top = margin + row * (cellHeight + gap);
            fence.Width = Math.Max(240, cellWidth);
            fence.Height = Math.Max(180, cellHeight);
            fence.ExpandedHeight = fence.Height;
        }
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Applied '{preset}' layout to {fences.Count} unlocked Fence(s) on page {_config.CurrentPage + 1}.");
    }

    private void ConfigureAutoOrganizerWatcher()
    {
        StopAutoOrganizerWatcher();
        if (!_config.EnableAutoOrganizeNewDesktopItems)
        {
            return;
        }

        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktopPath))
        {
            AppLogger.Log("Automatic organization was not started because the desktop folder is unavailable.");
            return;
        }

        _desktopAutoOrganizeWatcher = new FileSystemWatcher(desktopPath)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };
        _desktopAutoOrganizeWatcher.Created += DesktopAutoOrganizeWatcher_Changed;
        _desktopAutoOrganizeWatcher.Renamed += DesktopAutoOrganizeWatcher_Renamed;
        _desktopAutoOrganizeWatcher.Error += DesktopAutoOrganizeWatcher_Error;
        _desktopAutoOrganizeWatcher.EnableRaisingEvents = true;
        AppLogger.Log("Automatic organization watcher started.");
    }

    private void ConfigureDesktopContentsWatcher()
    {
        StopDesktopContentsWatcher();
        foreach (var desktop in GetDesktopRoots())
        {
            var watcher = new FileSystemWatcher(desktop)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };
            watcher.Created += DesktopContentsWatcher_Changed;
            watcher.Deleted += DesktopContentsWatcher_Changed;
            watcher.Renamed += DesktopContentsWatcher_Changed;
            watcher.Error += DesktopContentsWatcher_Error;
            watcher.EnableRaisingEvents = true;
            _desktopContentsWatchers.Add(watcher);
        }
    }

    private void DesktopContentsWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _desktopContentsRefreshTimer.Stop();
            _desktopContentsRefreshTimer.Start();
        });
    }

    private void StopDesktopContentsWatcher()
    {
        _desktopContentsRefreshTimer.Stop();
        foreach (var watcher in _desktopContentsWatchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= DesktopContentsWatcher_Changed;
            watcher.Deleted -= DesktopContentsWatcher_Changed;
            watcher.Renamed -= DesktopContentsWatcher_Changed;
            watcher.Error -= DesktopContentsWatcher_Error;
            watcher.Dispose();
        }
        _desktopContentsWatchers.Clear();
    }

    internal static IReadOnlyList<string> GetDesktopRoots()
    {
        return new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void StopAutoOrganizerWatcher()
    {
        _autoOrganizeTimer.Stop();
        _pendingAutoOrganizePaths.Clear();
        if (_desktopAutoOrganizeWatcher == null) return;

        _desktopAutoOrganizeWatcher.EnableRaisingEvents = false;
        _desktopAutoOrganizeWatcher.Created -= DesktopAutoOrganizeWatcher_Changed;
        _desktopAutoOrganizeWatcher.Renamed -= DesktopAutoOrganizeWatcher_Renamed;
        _desktopAutoOrganizeWatcher.Error -= DesktopAutoOrganizeWatcher_Error;
        _desktopAutoOrganizeWatcher.Dispose();
        _desktopAutoOrganizeWatcher = null;
    }

    private void DesktopAutoOrganizeWatcher_Changed(object sender, FileSystemEventArgs e) => QueueAutoOrganization(e.FullPath);

    private void DesktopAutoOrganizeWatcher_Renamed(object sender, RenamedEventArgs e) => QueueAutoOrganization(e.FullPath);

    private void DesktopAutoOrganizeWatcher_Error(object sender, ErrorEventArgs e)
    {
        AppLogger.LogException("Automatic organization watcher failed", e.GetException());
        Dispatcher.BeginInvoke(ConfigureAutoOrganizerWatcher, DispatcherPriority.Background);
    }

    private void DesktopContentsWatcher_Error(object sender, ErrorEventArgs e)
    {
        AppLogger.LogException("Desktop contents watcher failed", e.GetException());
        Dispatcher.BeginInvoke(() =>
        {
            ConfigureDesktopContentsWatcher();
            RenderFences();
        }, DispatcherPriority.Background);
    }

    private void QueueAutoOrganization(string path)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_config.EnableAutoOrganizeNewDesktopItems) return;
            _pendingAutoOrganizePaths.Add(path);
            _autoOrganizeTimer.Stop();
            _autoOrganizeTimer.Start();
        });
    }

    private void ProcessPendingAutoOrganization()
    {
        _autoOrganizeTimer.Stop();
        var pendingPaths = _pendingAutoOrganizePaths.ToArray();
        _pendingAutoOrganizePaths.Clear();
        foreach (var path in pendingPaths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }
            var target = _autoOrganizerService.ResolveTargetFence(_config, path);
            if (target != null)
            {
                AssignDesktopItems(target, [path]);
                AppLogger.Log($"Automatically assigned '{Path.GetFileName(path)}' to '{target.Title}' without moving it.");
            }
        }
    }

    private void SetLanguage(string language)
    {
        _loc.Language = language;
        _config.Language = _loc.Language;
        UpdateLocalizedText();
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            fence.SetLocalization(_loc);
        }

        SaveConfigWithWarning();
        AppLogger.Log($"Language changed: {_loc.Language}");
    }

    private void UpdateLocalizedText()
    {
        DesktopSettingsMenuItem.Header = _loc.T("Settings");
        DesktopNewFenceMenuItem.Header = _loc.T("NewFence");
        DesktopNewFolderPortalMenuItem.Header = _loc.T("NewFolderPortal");
        DesktopPageStatusMenuItem.Header = FormatPageText(GetPageCount());
        DesktopPreviousPageMenuItem.Header = _loc.T("PreviousPage");
        DesktopNextPageMenuItem.Header = _loc.T("NextPage");
        DesktopNewPageMenuItem.Header = _loc.T("NewPage");
        DesktopDeletePageMenuItem.Header = _loc.T("DeleteEmptyCurrentPage");
        DesktopToggleFencesMenuItem.Header = _fencesHidden ? _loc.T("ShowFences") : _loc.T("HideFences");
        DesktopSnapToGridMenuItem.Header = _loc.T("SnapToGrid");
        DesktopAutoLayoutMenuItem.Header = _loc.T("AutoArrange");
        DesktopBalancedLayoutMenuItem.Header = _loc.T("BalancedGrid");
        DesktopCompactLayoutMenuItem.Header = _loc.T("CompactGrid");
        DesktopColumnsLayoutMenuItem.Header = _loc.T("LayoutColumns");
        DesktopRowsLayoutMenuItem.Header = _loc.T("LayoutRows");
        DesktopSnapToGridMenuItem.IsChecked = _config.EnableSnapToGrid;
        DesktopRefreshAllMenuItem.Header = _loc.T("RefreshAll");
        DesktopCreateCategoryFencesMenuItem.Header = _loc.T("CreateCategoryFences");
        DesktopOrganizeMenuItem.Header = _loc.T("OrganizeDesktop");
        DesktopUndoOrganizeMenuItem.Header = _loc.T("UndoLastOrganize");
        DesktopAutoOrganizeMenuItem.Header = _loc.T("AutoOrganizeNewDesktopItems");
        DesktopAutoOrganizeMenuItem.IsChecked = _config.EnableAutoOrganizeNewDesktopItems;
        DesktopMigrateGroupsMenuItem.Header = _loc.T("MigrateFolderCategories");
        DesktopSaveLayoutMenuItem.Header = _loc.T("SaveLayoutSnapshot");
        DesktopRestoreLayoutMenuItem.Header = _loc.T("RestoreLatestLayoutSnapshot");
        DesktopLayoutsMenuItem.Header = _loc.T("SavedLayouts");
        DesktopSaveNamedLayoutMenuItem.Header = _loc.T("SaveLayoutAs");
        DesktopLanguageMenuItem.Header = _loc.T("Language");
        DesktopEnglishMenuItem.Header = _loc.T("English");
        DesktopEnglishMenuItem.IsChecked = string.Equals(_loc.Language, LocalizationService.English, StringComparison.OrdinalIgnoreCase);
        DesktopChineseMenuItem.Header = _loc.T("Chinese");
        DesktopChineseMenuItem.IsChecked = string.Equals(_loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
        DesktopExitMenuItem.Header = _loc.T("ExitMiniFences");

        if (_showMiniFencesMenuItem != null) _showMiniFencesMenuItem.Text = _loc.T("OpenSettings");
        if (_toggleFencesMenuItem != null) _toggleFencesMenuItem.Text = _fencesHidden ? _loc.T("ShowFences") : _loc.T("HideFences");
        if (_exitMenuItem != null) _exitMenuItem.Text = _loc.T("ExitMiniFences");
        _settingsWindow?.ReloadState();
        UpdateMenuState();
    }

    private void OpenConfigFolder()
    {
        try
        {
            var directory = Path.GetDirectoryName(_configService.ConfigPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(_loc.T("PathDoesNotExist"));
            }

            Directory.CreateDirectory(directory);
            OpenShellPath(directory);
            AppLogger.Log($"Opened config folder: {directory}");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to open config folder.", ex);
            System.Windows.MessageBox.Show(
                string.Format(_loc.T("CouldNotOpenConfigFolder"), ex.Message),
                "MiniFences",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenLogFile()
    {
        try
        {
            AppLogger.Log("Opening log file from tray menu.");
            OpenShellPath(AppLogger.LogPath);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to open log file.", ex);
            System.Windows.MessageBox.Show(
                string.Format(_loc.T("CouldNotOpenLogFile"), ex.Message),
                "MiniFences",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static void OpenShellPath(string path)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void ApplyFenceVisibility()
    {
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            ApplyFenceVisibility(fence);
        }
        foreach (var icon in Workspace.Children.OfType<DesktopLooseIconControl>())
        {
            icon.Visibility = _fencesHidden || !_windowsDesktopIconsVisible || !_config.EnableDesktopIconIntegration
                ? Visibility.Hidden
                : Visibility.Visible;
        }

        if (_toggleFencesMenuItem != null)
        {
            _toggleFencesMenuItem.Text = _fencesHidden ? _loc.T("ShowFences") : _loc.T("HideFences");
        }

        UpdateDesktopWindowRegion();
        UpdateMenuState();
    }

    private void ApplyFenceVisibility(FenceControl fence)
    {
        fence.Visibility = _fencesHidden || !_windowsDesktopIconsVisible ||
                           (fence.Config.IsDesktopGroup && !_config.EnableDesktopIconIntegration)
            ? Visibility.Hidden
            : Visibility.Visible;
    }

    private void UpdateMenuState()
    {
        var pageCount = GetPageCount();
        var pageText = FormatPageText(pageCount);

        DesktopPageStatusMenuItem.Header = pageText;
        DesktopPreviousPageMenuItem.IsEnabled = _config.CurrentPage > 0;
        DesktopNextPageMenuItem.IsEnabled = _config.CurrentPage < pageCount - 1;
        DesktopDeletePageMenuItem.IsEnabled = CanDeleteCurrentPage(pageCount);
        DesktopToggleFencesMenuItem.Header = _fencesHidden ? _loc.T("ShowFences") : _loc.T("HideFences");
        DesktopSnapToGridMenuItem.IsChecked = _config.EnableSnapToGrid;

        _trayIcon.Text = $"MiniFences - {_loc.T("Page")} {_config.CurrentPage + 1}/{pageCount}";
    }

    private string FormatPageText(int pageCount)
    {
        return $"{_loc.T("Page")} {_config.CurrentPage + 1} / {pageCount}";
    }

    private bool CanDeleteCurrentPage(int pageCount)
    {
        return pageCount > 1 && !_config.Fences.Any(fence => fence.PageIndex == _config.CurrentPage);
    }

    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    private void OrganizeDesktopByType()
    {
        var confirm = System.Windows.MessageBox.Show(this, _loc.T("AssignDesktopItemsConfirm"), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var existingIds = _config.Fences.Select(fence => fence.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigned = _autoOrganizerService.AssignDesktopItemsByTypeWithUndo(_config);
        foreach (var fence in _config.Fences.Where(fence => !existingIds.Contains(fence.Id)))
        {
            PlaceFenceOnCurrentPage(fence);
        }

        RenderFences();
        SaveConfigWithWarning();

        System.Windows.MessageBox.Show(this, string.Format(_loc.T("AssignedDesktopItems"), assigned), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CreateCategoryFences()
    {
        OrganizeDesktopByType();
    }

    private void UndoLastOrganization()
    {
        if (!_autoOrganizerService.HasDesktopAssignmentUndoHistory())
        {
            System.Windows.MessageBox.Show(this, _loc.T("NoOrganizationHistory"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(this,
            _loc.T("UndoOrganizeConfirm"),
            "MiniFences",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var result = _autoOrganizerService.UndoLastDesktopAssignment(_config);
        RenderFences();
        SaveConfigWithWarning();

        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            fence.LoadFolderItems();
        }

        var summary = string.Format(_loc.T("RestoredItems"), result.Moved);
        if (result.Skipped > 0)
        {
            summary += $"\n{string.Format(_loc.T("SkippedMissingItems"), result.Skipped)}";
        }

        if (result.RemovedFences > 0)
        {
            summary += $"\n{string.Format(_loc.T("RemovedEmptyCategoryFences"), result.RemovedFences)}";
        }

        if (result.Errors.Count > 0)
        {
            summary += $"\n\n{_loc.T("Errors")}\n{string.Join("\n", result.Errors.Take(8))}";
            if (result.Errors.Count > 8)
            {
                summary += $"\n{string.Format(_loc.T("MoreErrors"), result.Errors.Count - 8)}";
            }
        }

        System.Windows.MessageBox.Show(this, summary, "MiniFences", MessageBoxButton.OK,
            result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    internal LocalizationService Localization => _loc;
    internal int FenceCount => _config.Fences.Count;
    internal int SettingsCurrentPage => _config.CurrentPage;
    internal int SettingsPageCount => GetPageCount();
    internal double SettingsWorkspaceWidth => Workspace.ActualWidth > 0 ? Workspace.ActualWidth : SystemParameters.PrimaryScreenWidth;
    internal double SettingsWorkspaceHeight => Workspace.ActualHeight > 0 ? Workspace.ActualHeight : SystemParameters.PrimaryScreenHeight;
    internal bool AreFencesHidden => _fencesHidden;
    internal bool AreFencesTopmost => _fencesTopmost;
    internal bool IsDesktopDoubleClickEnabled => _config.EnableDesktopDoubleClick;
    internal bool IsDesktopIconIntegrationEnabled => _config.EnableDesktopIconIntegration;
    internal string TabViewMode => _config.TabViewMode;
    internal string TabWidthMode => _config.TabWidthMode;
    internal bool IsTabCreationEnabled => _config.EnableTabCreation;
    internal bool IsTabCreationConfirmationEnabled => _config.ConfirmTabCreation;
    internal bool IsHoverTabSwitchEnabled => _config.HoverSwitchTabs;
    internal bool IsRollupEnabled => _config.EnableRollup;
    internal bool IsDoubleClickTitleRollupEnabled => _config.DoubleClickTitleRollup;
    internal bool IsAutoRollupAtScreenEdgeEnabled => _config.AutoRollupAtScreenEdge;
    internal bool IsClickTitleToExpandEnabled => _config.ClickTitleToExpand;
    internal bool IsHoverTitleToExpandEnabled => _config.HoverTitleToExpand;
    internal string PreviousPageHotkey => _config.PreviousPageHotkey;
    internal string NextPageHotkey => _config.NextPageHotkey;
    internal string ToggleTopmostHotkey => _config.ToggleTopmostHotkey;
    internal bool IsAutoOrganizeEnabled => _config.EnableAutoOrganizeNewDesktopItems;
    internal string DefaultAutoOrganizeFenceId => _config.DefaultAutoOrganizeFenceId;
    internal bool IsStartWithWindowsEnabled => _startupService.IsEnabled();
    internal bool CanDeleteSettingsCurrentPage => CanDeleteCurrentPage(GetPageCount());

    internal IReadOnlyList<FenceConfig> GetFenceSettingsSnapshot()
    {
        SyncAllFenceLayouts();
        return _config.Fences
            .OrderBy(fence => fence.PageIndex)
            .ThenBy(fence => fence.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal IReadOnlyList<FolderItem> SettingsGetFenceItems(string fenceId)
    {
        var fence = _config.Fences.FirstOrDefault(candidate => string.Equals(candidate.Id, fenceId, StringComparison.OrdinalIgnoreCase));
        return fence?.IsDesktopGroup == true
            ? new FolderItemService().LoadAssignedItems(fence.AssignedPaths)
            : Array.Empty<FolderItem>();
    }

    internal IReadOnlyList<FolderItem> SettingsGetUnassignedDesktopItems()
    {
        var assigned = _config.Fences.Where(fence => fence.IsDesktopGroup)
            .SelectMany(fence => fence.AssignedPaths)
            .Select(TryGetFullPath)
            .Where(path => path != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = FolderItemService.CollapseDesktopEntries(FolderItemService.EnumerateFileSystemEntriesSafe(GetDesktopRoots())
                .Where(ShouldShowLooseDesktopItem)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .Where(path => !assigned.Contains(Path.GetFullPath(path)));
        return new FolderItemService().LoadAssignedItems(paths);
    }

    internal void SettingsAssignDesktopItems(string fenceId, IReadOnlyList<string> paths)
    {
        var fence = _config.Fences.FirstOrDefault(candidate => string.Equals(candidate.Id, fenceId, StringComparison.OrdinalIgnoreCase));
        if (fence?.IsDesktopGroup != true) return;
        AssignDesktopItems(fence, paths);
    }

    internal void SettingsUnassignDesktopItems(string fenceId, IReadOnlyList<string> paths)
    {
        var fence = _config.Fences.FirstOrDefault(candidate => string.Equals(candidate.Id, fenceId, StringComparison.OrdinalIgnoreCase));
        if (fence?.IsDesktopGroup != true) return;
        var removed = fence.AssignedPaths.RemoveAll(existing => paths.Contains(existing, StringComparer.OrdinalIgnoreCase));
        if (removed == 0) return;
        RenderFences();
        SaveConfigWithWarning();
    }

    internal IReadOnlyList<FolderItem> GetFencePreviewItems(string fenceId)
    {
        return
        [
            new FolderItem { Name = "Word \u6587\u6863", Kind = "DOCX", Icon = FolderItemService.GetTypeIcon(".docx") },
            new FolderItem { Name = "\u6587\u4ef6\u5939", Kind = "Folder", Icon = FolderItemService.GetTypeIcon("", isFolder: true) },
            new FolderItem { Name = "PDF \u6587\u6863", Kind = "PDF", Icon = FolderItemService.GetTypeIcon(".pdf") }
        ];
    }

    internal int GetFenceItemCount(string fenceId)
    {
        var fence = FindFence(fenceId);
        if (fence == null) return 0;
        return fence.IsDesktopGroup
            ? fence.AssignedPaths.Count(path => File.Exists(path) || Directory.Exists(path))
            : Directory.Exists(fence.FolderPath)
                ? Directory.EnumerateFileSystemEntries(fence.FolderPath).Count()
                : 0;
    }

    internal IReadOnlyList<AutoOrganizeRule> GetAutoOrganizeRulesSnapshot() =>
        _config.AutoOrganizeRules
            .OrderByDescending(rule => rule.Priority)
            .ThenBy(rule => rule.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(CloneRule)
            .ToArray();

    internal void SettingsSetAutoOrganizeEnabled(bool enabled)
    {
        _config.EnableAutoOrganizeNewDesktopItems = enabled;
        ConfigureAutoOrganizerWatcher();
        SaveConfigWithWarning();
    }

    internal string ClassificationScheme => _config.ClassificationScheme;
    internal void SettingsSetClassificationScheme(string scheme)
    {
        _config.ClassificationScheme = scheme is "Simple" ? "Simple" : "Detailed";
        SaveConfigWithWarning();
    }

    internal void SettingsSetDefaultAutoOrganizeFence(string fenceId)
    {
        _config.DefaultAutoOrganizeFenceId = fenceId;
        SaveConfigWithWarning();
    }

    internal AutoOrganizeRule SettingsAddAutoOrganizeRule()
    {
        var firstFence = _config.Fences.FirstOrDefault(fence => fence.IsDesktopGroup);
        var rule = new AutoOrganizeRule
        {
            Name = _loc.Language == LocalizationService.Chinese ? "\u65b0\u89c4\u5219" : "New rule",
            TargetFenceId = firstFence?.Id ?? "",
            Priority = _config.AutoOrganizeRules.Count == 0 ? 100 : _config.AutoOrganizeRules.Max(item => item.Priority) + 10
        };
        _config.AutoOrganizeRules.Add(rule);
        SaveConfigWithWarning();
        return CloneRule(rule);
    }

    internal void SettingsSaveAutoOrganizeRule(AutoOrganizeRule changed)
    {
        var rule = _config.AutoOrganizeRules.FirstOrDefault(item => item.Id == changed.Id);
        if (rule == null) return;
        rule.Name = changed.Name;
        rule.IsEnabled = changed.IsEnabled;
        rule.Priority = changed.Priority;
        rule.TargetFenceId = changed.TargetFenceId;
        rule.NamePattern = changed.NamePattern;
        rule.Extensions = changed.Extensions;
        rule.FoldersOnly = changed.FoldersOnly;
        rule.MinimumSizeMb = changed.MinimumSizeMb;
        rule.MaximumSizeMb = changed.MaximumSizeMb;
        ScheduleConfigSave();
    }

    internal void SettingsDeleteAutoOrganizeRule(string ruleId)
    {
        _config.AutoOrganizeRules.RemoveAll(rule => string.Equals(rule.Id, ruleId, StringComparison.OrdinalIgnoreCase));
        SaveConfigWithWarning();
    }

    private static AutoOrganizeRule CloneRule(AutoOrganizeRule rule) => new()
    {
        Id = rule.Id,
        Name = rule.Name,
        IsEnabled = rule.IsEnabled,
        Priority = rule.Priority,
        TargetFenceId = rule.TargetFenceId,
        NamePattern = rule.NamePattern,
        Extensions = rule.Extensions,
        FoldersOnly = rule.FoldersOnly,
        MinimumSizeMb = rule.MinimumSizeMb,
        MaximumSizeMb = rule.MaximumSizeMb
    };

    internal void SettingsCreateFence() => CreateNewDesktopGroup();
    internal void SettingsRefreshAll() => RefreshAllFences();
    internal void SettingsToggleFences() => ToggleFencesVisibility();
    internal void SettingsToggleFencesTopmost() => ToggleFencesTopmost();
    internal void SettingsSwitchPage(int pageIndex) => SwitchPage(pageIndex);
    internal void SettingsCreatePage() => CreateNewPage();
    internal void SettingsMoveFenceToPage(string fenceId, int pageIndex)
    {
        var fence = _config.Fences.FirstOrDefault(candidate => string.Equals(candidate.Id, fenceId, StringComparison.OrdinalIgnoreCase));
        if (fence == null) return;
        var targetPage = Math.Clamp(pageIndex, 0, Math.Max(0, GetPageCount() - 1));
        if (fence.PageIndex == targetPage) return;
        SyncAllFenceLayouts();
        if (!string.IsNullOrWhiteSpace(fence.TabGroupId))
        {
            var oldGroupId = fence.TabGroupId;
            fence.TabGroupId = null;
            if (DissolveSingleItemTabGroup(_config, oldGroupId)) _activeTabByGroup.Remove(oldGroupId);
        }
        fence.PageIndex = targetPage;
        AppLogger.Log($"Moved Fence '{fence.Title}' to page {targetPage + 1} from page preview.");
        RenderFences();
        SaveConfigWithWarning();
    }
    internal void SettingsDeleteCurrentPage() => DeleteCurrentPage();
    internal void SettingsCreateCategories() => CreateCategoryFences();
    internal void SettingsOrganizeDesktop() => OrganizeDesktopByType();
    internal void SettingsUndoOrganization() => UndoLastOrganization();
    internal void SettingsSetLanguage(string language) => SetLanguage(language);
    internal void SettingsOpenConfigFolder() => OpenConfigFolder();
    internal void SettingsOpenLogFile() => OpenLogFile();

    internal void SettingsSetDesktopDoubleClick(bool enabled)
    {
        _config.EnableDesktopDoubleClick = enabled;
        _desktopDoubleClickTracker.Reset();
        SaveConfigWithWarning();
        AppLogger.Log($"Desktop double-click hide/show setting changed: {enabled}");
    }

    internal void SettingsSetDesktopIconIntegration(bool enabled)
    {
        _config.EnableDesktopIconIntegration = enabled;
        if (enabled)
        {
            _restoreNativeDesktopIconsOnExit = _windowsDesktopIconsVisible;
            UpdateNativeDesktopIconVisibility();
        }
        else if (_windowsDesktopIconsVisible)
        {
            _desktopIconLayoutService.SetVisible(true);
        }
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Desktop icon integration changed: {enabled}.");
    }

    internal void SettingsSetTabOptions(string mode, string widthMode, bool enableCreation, bool confirmCreation, bool hoverSwitch)
    {
        _config.TabViewMode = string.Equals(mode, "Strip", StringComparison.OrdinalIgnoreCase) ? "Strip" : "Compact";
        _config.TabWidthMode = string.Equals(widthMode, "Equal", StringComparison.OrdinalIgnoreCase) ? "Equal" : "Content";
        _config.EnableTabCreation = enableCreation;
        _config.ConfirmTabCreation = confirmCreation;
        _config.HoverSwitchTabs = hoverSwitch;
        RenderFences();
        SaveConfigWithWarning();
    }

    internal void SettingsSetRollupOptions(bool enabled, bool doubleClick, bool autoEdge, bool clickExpand, bool hoverExpand)
    {
        _config.EnableRollup = enabled;
        _config.DoubleClickTitleRollup = doubleClick;
        _config.AutoRollupAtScreenEdge = autoEdge;
        _config.ClickTitleToExpand = clickExpand;
        _config.HoverTitleToExpand = hoverExpand;
        RenderFences();
        SaveConfigWithWarning();
    }

    internal bool SettingsSetHotkeys(string previous, string next, string topmost, out string error)
    {
        if (ParseHotkey(previous) == null || ParseHotkey(next) == null || ParseHotkey(topmost) == null)
        {
            error = _loc.T("InvalidHotkey");
            return false;
        }
        var normalized = new[] { previous.Trim(), next.Trim(), topmost.Trim() };
        if (normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
        {
            error = _loc.T("DuplicateHotkey");
            return false;
        }
        _config.PreviousPageHotkey = normalized[0];
        _config.NextPageHotkey = normalized[1];
        _config.ToggleTopmostHotkey = normalized[2];
        RegisterGlobalHotkeys();
        SaveConfigWithWarning();
        error = "";
        return true;
    }

    internal bool SettingsChooseFenceColor(string fenceId, bool chooseHeader)
    {
        var targets = GetAppearanceTargets(fenceId);
        var sample = targets.FirstOrDefault();
        if (sample == null)
        {
            return false;
        }

        var currentValue = chooseHeader ? sample.HeaderColor : sample.BackgroundColor;
        var currentColor = ParseMediaColor(currentValue, chooseHeader ? "#CC3F7FA8" : "#DD20242A");
        using var dialog = new Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(currentColor.R, currentColor.G, currentColor.B),
            FullOpen = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return false;
        }

        var selected = dialog.Color;
        var value = $"#{currentColor.A:X2}{selected.R:X2}{selected.G:X2}{selected.B:X2}";
        foreach (var fence in targets)
        {
            if (chooseHeader) fence.HeaderColor = value;
            else fence.BackgroundColor = value;
            RefreshFenceAppearance(fence);
        }

        SaveConfigWithWarning();
        AppLogger.Log($"Fence appearance color changed for {targets.Count} Fence(s); header: {chooseHeader}; color: {value}");
        return true;
    }

    internal void SettingsSetFenceOpacity(string fenceId, double opacity)
    {
        foreach (var fence in GetAppearanceTargets(fenceId))
        {
            fence.Opacity = Math.Clamp(opacity, 0.0, 1.0);
            RefreshFenceAppearance(fence);
        }
        ScheduleConfigSave();
    }

    internal void SettingsSetFencePresentation(string fenceId, string? alignment, bool? showPath, bool? cleanStyle)
    {
        foreach (var fence in GetAppearanceTargets(fenceId))
        {
            if (alignment is "Left" or "Center" or "Right") fence.TitleAlignment = alignment;
            if (showPath.HasValue) fence.ShowPath = showPath.Value;
            if (cleanStyle.HasValue) fence.UseCleanStyle = cleanStyle.Value;
            RefreshFenceAppearance(fence);
        }
        SaveConfigWithWarning();
    }

    internal void SettingsResetFenceAppearance(string fenceId)
    {
        var targets = GetAppearanceTargets(fenceId);
        if (targets.Count == 0)
        {
            return;
        }

        foreach (var fence in targets)
        {
            fence.BackgroundColor = "#DD20242A";
            fence.HeaderColor = "#CC3F7FA8";
            fence.Opacity = 1.0;
            fence.TitleAlignment = "Left";
            fence.ShowPath = true;
            fence.UseCleanStyle = false;
            RefreshFenceAppearance(fence);
        }
        SaveConfigWithWarning();
        AppLogger.Log($"Fence appearance reset for {targets.Count} Fence(s).");
    }

    internal void SettingsApplyFenceAppearance(string fenceId, FenceConfig source)
    {
        var targets = GetAppearanceTargets(fenceId);
        foreach (var fence in targets)
        {
            fence.BackgroundColor = source.BackgroundColor;
            fence.HeaderColor = source.HeaderColor;
            fence.Opacity = source.Opacity;
            fence.TitleAlignment = source.TitleAlignment;
            fence.ShowPath = source.ShowPath;
            fence.UseCleanStyle = source.UseCleanStyle;
            RefreshFenceAppearance(fence);
        }
        SaveConfigWithWarning();
        AppLogger.Log($"Copied appearance applied to {targets.Count} Fence(s).");
    }

    private IReadOnlyList<FenceConfig> GetAppearanceTargets(string fenceId)
    {
        return string.Equals(fenceId, AllAppearanceTargetId, StringComparison.OrdinalIgnoreCase)
            ? _config.Fences.ToArray()
            : FindFence(fenceId) is { } fence ? [fence] : [];
    }

    private void RefreshFenceAppearance(FenceConfig fence)
    {
        var control = Workspace.Children.OfType<FenceControl>().FirstOrDefault(candidate =>
            string.Equals(candidate.Config.Id, fence.Id, StringComparison.OrdinalIgnoreCase));
        control?.RefreshAppearance();
    }

    private static System.Windows.Media.Color ParseMediaColor(string value, string fallback)
    {
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
        }
        catch
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback);
        }
    }

    internal bool SettingsRenameFence(string fenceId, Window owner)
    {
        var fence = FindFence(fenceId);
        if (fence == null)
        {
            return false;
        }

        var dialog = new RenameFenceDialog(fence.Title, _loc)
        {
            Owner = owner
        };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        fence.Title = dialog.FenceTitle;
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Fence renamed from settings: {fence.Id}; title: {fence.Title}");
        return true;
    }

    internal bool SettingsChooseFenceFolder(string fenceId)
    {
        var fence = FindFence(fenceId);
        if (fence == null)
        {
            return false;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = _loc.T("ChooseThisFenceFolder"),
            SelectedPath = Directory.Exists(fence.FolderPath)
                ? fence.FolderPath
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            ShowNewFolderButton = true,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return false;
        }

        fence.FolderPath = dialog.SelectedPath;
        fence.Kind = FenceConfig.FolderPortalKind;
        fence.AssignedPaths.Clear();
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Fence folder changed from settings: {fence.Id}; folder: {fence.FolderPath}");
        return true;
    }

    internal void SettingsOpenFenceFolder(string fenceId, Window owner)
    {
        var fence = FindFence(fenceId);
        if (fence == null)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(fence.FolderPath))
            {
                throw new DirectoryNotFoundException(_loc.T("BoundFolderMissing"));
            }

            OpenShellPath(fence.FolderPath);
            AppLogger.Log($"Opened Fence folder from settings: {fence.FolderPath}");
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Failed to open Fence folder from settings: {fence.FolderPath}", ex);
            System.Windows.MessageBox.Show(owner, string.Format(_loc.T("CouldNotOpenFolder"), ex.Message), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal bool SettingsDeleteFence(string fenceId, Window owner)
    {
        var fence = FindFence(fenceId);
        if (fence == null)
        {
            return false;
        }

        if (_config.Fences.Count <= 1)
        {
            System.Windows.MessageBox.Show(owner, _loc.T("KeepOneFence"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var result = System.Windows.MessageBox.Show(owner, string.Format(_loc.T("DeleteFenceQuestion"), fence.Title), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return false;
        }

        _config.Fences.Remove(fence);
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Fence deleted from settings: {fence.Id}; title: {fence.Title}");
        return true;
    }

    internal void SettingsSetStartWithWindows(bool enabled, Window owner)
    {
        try
        {
            _startupService.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to update startup setting from settings window", ex);
            System.Windows.MessageBox.Show(owner, string.Format(_loc.T("CouldNotUpdateStartupSetting"), ex.Message), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private FenceConfig? FindFence(string fenceId)
    {
        return _config.Fences.FirstOrDefault(fence =>
            string.Equals(fence.Id, fenceId, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmLButtonUp = 0x0202;
    private const int WmNcHitTest = 0x0084;
    private const int WmSysCommand = 0x0112;
    private const int WmHotkey = 0x0312;
    private const int HotkeyPreviousPage = 4101;
    private const int HotkeyNextPage = 4102;
    private const int HotkeyToggleTopmost = 4103;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkLeft = 0x25;
    private const uint VkRight = 0x27;
    private const uint VkT = 0x54;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;
    private const int VkSpace = 0x20;
    private const int ScMinimize = 0xF020;
    private const int HtTransparent = -1;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint GwHwndPrev = 3;
    private const uint GaRoot = 2;
    private const int RgnOr = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private sealed record HotkeyGesture(bool Control, bool Alt, bool Shift, bool Win, uint Key);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    [DllImport("User32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("User32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("Gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("Gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("Gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("User32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    [DllImport("User32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("User32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("User32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("User32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("User32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("User32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

    [DllImport("User32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, index)
            : new IntPtr(GetWindowLong32(hWnd, index));
    }

    [DllImport("User32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr newValue);

    [DllImport("User32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int newValue);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newValue)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, index, newValue)
            : new IntPtr(SetWindowLong32(hWnd, index, newValue.ToInt32()));
    }

    [DllImport("User32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("User32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("User32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("User32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("User32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("User32.dll")]
    private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);

    [DllImport("User32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMousePoint
    {
        public readonly int x;
        public readonly int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MsllHookStruct
    {
        public readonly NativeMousePoint pt;
        public readonly uint mouseData;
        public readonly uint flags;
        public readonly uint time;
        public readonly IntPtr dwExtraInfo;
    }
}
