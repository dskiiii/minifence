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
using System.Windows.Media.Animation;
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
    private readonly ActionHistoryService _actionHistoryService = new();
    private readonly DisplayLayoutService _displayLayoutService = new();
    private readonly StartupService _startupService = new();
    private readonly GitHubUpdateService _updateService = new();
    private readonly AutoOrganizerService _autoOrganizerService = new();
    private readonly DesktopIconLayoutService _desktopIconLayoutService = new();
    private readonly FolderItemService _folderItemService = new();
    private readonly DesktopDoubleClickTracker _desktopDoubleClickTracker = new();
    private readonly ShellDropTargetBridge _shellDropTarget = new();
    private readonly LocalizationService _loc = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _autoOrganizeTimer;
    private readonly DispatcherTimer _desktopIconStateTimer;
    private readonly DispatcherTimer _desktopContentsRefreshTimer;
    private readonly DispatcherTimer _desktopCompatibilityTimer;
    private readonly DispatcherTimer _dragPageSwitchTimer;
    private readonly DispatcherTimer _dragRegionRefreshTimer;
    private readonly DispatcherTimer _dragHintReleaseTimer;
    private readonly DispatcherTimer _shellDragLeaveTimer;
    private readonly DispatcherTimer _dragTargetClearTimer;
    private readonly DispatcherTimer _hotkeyPollTimer;
    private bool _fenceHeaderDragActive;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly HashSet<string> _pendingAutoOrganizePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressedAutoOrganizePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _activeTabByGroup = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _desktopAutoOrganizeWatcher;
    private readonly List<FileSystemWatcher> _desktopContentsWatchers = [];
    private Forms.ToolStripMenuItem? _showMiniFencesMenuItem;
    private Forms.ToolStripMenuItem? _toggleFencesMenuItem;
    private Forms.ToolStripMenuItem? _undoMenuItem;
    private Forms.ToolStripMenuItem? _checkForUpdatesMenuItem;
    private Forms.ToolStripMenuItem? _exitMenuItem;
    private SettingsWindow? _settingsWindow;
    private AppConfig _config = new();
    private bool _isExiting;
    private bool _fencesHidden;
    private readonly bool _openSettingsOnLoad;
    private readonly bool _useTopLevelDesktopCompatibilityMode;
    private int _desktopLayerCorrectionGeneration;
    private bool _desktopZOrderUpdateInProgress;
    private bool _showDesktopModeActive;
    private bool _showDesktopRestorePending;
    private long _ignoreNormalForegroundUntilTicks;
    private bool _winDKeyDown;
    private WinEventProc? _foregroundWinEventProc;
    private IntPtr _foregroundWinEventHook;
    private LowLevelMouseProc? _mouseHookProc;
    private IntPtr _mouseHookHandle;
    private LowLevelKeyboardProc? _keyboardHookProc;
    private IntPtr _keyboardHookHandle;
    private IntPtr _desktopHostHandle;
    private bool _isDesktopHosted;
    private HotkeyGesture? _previousPageGesture;
    private HotkeyGesture? _nextPageGesture;
    private HotkeyGesture? _toggleTopmostGesture;
    private readonly HotkeyGesture?[] _directPageGestures = new HotkeyGesture?[12];
    private bool _previousPageNativeHotkeyRegistered;
    private bool _nextPageNativeHotkeyRegistered;
    private bool _toggleTopmostNativeHotkeyRegistered;
    private readonly bool[] _directPageNativeHotkeysRegistered = new bool[12];
    private bool _previousPageHotkeyWasDown;
    private bool _nextPageHotkeyWasDown;
    private bool _toggleTopmostHotkeyWasDown;
    private readonly bool[] _directPageHotkeysWereDown = new bool[12];
    private readonly long[] _hotkeyLastActivatedTicks = new long[15];
    private int _suppressedMouseHotkeyButton;
    private bool _desktopShortcutContextActive;
    private long _desktopShortcutContextRevision;
    private System.Drawing.Point _lastMouseScreenPoint;
    private bool _hoverUpdatePending;
    private bool _restoreNativeDesktopIconsOnExit;
    private bool _nativeDesktopIconsHiddenByMiniFences;
    private bool _desktopIconRestoreCompleted;
    private bool _desktopIconWatchdogStarted;
    private bool _windowsDesktopIconsVisible = true;
    private bool _desktopItemDragActive;
    private volatile bool _oleDragActive;
    private bool _dragFeedbackActive;
    private bool _dragFeedbackPinnedUntilClear;
    private DragFeedbackWindow? _dragFeedbackWindow;
    private DragTargetHintWindow? _dragTargetHintWindow;
    private bool _dragSourceExpanded;
    private double _dragSourceTextWidth = DragFeedbackWindow.LabelWidthDips;
    private ImageSource? _dragHintIconSource;
    private string? _dragHintIconPath;
    private ImageSource? _dragHintCachedPathIcon;
    private string? _dragHintLabel;
    private string? _dragSourceLabel;
    private int _pendingDragPageDirection;
    private bool _fencesTopmost;
    private bool _keepTopLevelDesktopAfterTopmost;
    private bool _topmostTransitionInProgress;
    private bool _configSaveErrorShown;
    private bool _updateCheckInProgress;
    private FenceControl? _mergePreviewTarget;
    private FenceControl? _mergePreviewSource;
    private readonly HashSet<string> _selectedLoosePaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _looseSelectionAnchor;
    private System.Windows.Controls.TextBox? _activeInlineRenameEditor;
    private string _displayTopologyKey = "";
    private volatile bool _desktopFenceSelectionActive;
    private System.Drawing.Point _desktopFenceSelectionStart;
    private CancellationTokenSource? _looseIconLoadCancellation;
    private readonly Dictionary<Guid, DateTime> _transferStartedAt = [];
    private FolderTransferCompletedEventArgs? _lastTransferCompleted;

    public MainWindow(bool openSettingsOnLoad = false)
    {
        _openSettingsOnLoad = openSettingsOnLoad;
        var desktopHostModeOverride = Environment.GetEnvironmentVariable("MINIFENCES_DESKTOP_HOST_MODE");
        _useTopLevelDesktopCompatibilityMode =
            ShouldUseTopLevelDesktopFallback(Environment.OSVersion.Version, desktopHostModeOverride);
        AppLogger.Log(
            $"Program started. Version={typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}; " +
            $"OS={Environment.OSVersion.VersionString}; DesktopHostMode=" +
            $"{(_useTopLevelDesktopCompatibilityMode ? "TopLevelCompatibility" : "ExplorerChild")}");
        InitializeComponent();
        FolderItemService.TransferProgressChanged += FolderItemService_TransferProgressChanged;
        FolderItemService.TransferConfirmationRequested += FolderItemService_TransferConfirmationRequested;
        FolderItemService.TransferCompleted += FolderItemService_TransferCompleted;
        // WPF's OLE drop pipeline already supplies all file data needed by the
        // Fences. Do not create a second IDropTargetHelper session: forwarding
        // every DragOver packet to it made movement slow near a Fence and could
        // leave Explorer's large layered drag image behind after an async drop.
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
        // Shell/user-preference events are the primary synchronization path.
        // A slow fallback poll repairs missed Explorer notifications without
        // waking both MiniFences and Explorer twice per second while idle.
        _desktopIconStateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _desktopIconStateTimer.Tick += (_, _) => SynchronizeWindowsDesktopIconState();
        _desktopContentsRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _desktopContentsRefreshTimer.Tick += (_, _) =>
        {
            _desktopContentsRefreshTimer.Stop();
            RenderFences();
        };
        _desktopCompatibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _desktopCompatibilityTimer.Tick += (_, _) =>
        {
            EnsureDesktopHostAttachment();
            RecoverTopLevelDesktopWindow("periodic desktop-layer check");
            SendBehindNormalWindows(force: true);
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
        _dragHintReleaseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragHintRefreshIntervalMilliseconds) };
        _dragHintReleaseTimer.Tick += (_, _) =>
        {
            if (!_dragFeedbackActive)
            {
                _dragHintReleaseTimer.Stop();
                return;
            }

            // WPF's modal DoDragDrop loop can bypass a child DragLeave when
            // the pointer is released over Explorer. Read the global button
            // state instead of this process's WinForms state: an Explorer
            // source drag otherwise looks like it has already been released.
            if (ShouldClearDragFeedback(
                    IsKeyDown((int)VkEscape),
                    IsKeyDown(VkLButton)))
                ClearDragHint();
        };
        _shellDragLeaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _shellDragLeaveTimer.Tick += (_, _) =>
        {
            _shellDragLeaveTimer.Stop();
            _shellDropTarget.Show(true);
            _shellDropTarget.DragLeave();
            _oleDragActive = false;
            ClearDragHint();
        };
        _dragTargetClearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _dragTargetClearTimer.Tick += (_, _) =>
        {
            _dragTargetClearTimer.Stop();
            if (!_dragFeedbackActive) return;
            _dragTargetHintWindow?.HideTarget();
        };
        // Some graphics/keyboard drivers consume Ctrl+Alt+Arrow before Windows
        // can deliver WM_HOTKEY or WH_KEYBOARD_LL. Polling the physical state is
        // a third, independent fallback for those machines.
        _hotkeyPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _hotkeyPollTimer.Tick += (_, _) => PollGlobalHotkeys();
        _hotkeyPollTimer.Start();
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
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
            AppLogger.Log("Window hit-test hook installed for transparent desktop pass-through.");
        }

        InstallDesktopDoubleClickHook();
        InstallForegroundWindowHook();
        ApplyDesktopWindowStyles();
        if (_useTopLevelDesktopCompatibilityMode)
        {
            AppLogger.Log("Using top-level desktop compatibility mode with stable Explorer ownership and Shell z-order suppression.");
        }
        else
        {
            AttachToDesktopHost();
        }
        RegisterGlobalHotkeys();
        UpdateDesktopWindowRegion();

        AppLogger.Log(_isDesktopHosted
            ? "MiniFences attached to the Explorer desktop host."
            : _useTopLevelDesktopCompatibilityMode
                ? "MiniFences top-level compatibility window initialized at the desktop layer."
                : "Desktop host relationship failed; using unowned top-level fallback.");
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDesktopWorkAreaBounds();
        _config = _configService.Load();
        // SourceInitialized runs before Loaded. Register again after loading
        // the persisted configuration so non-default shortcuts are active.
        RegisterGlobalHotkeys();
        _displayTopologyKey = DisplayLayoutService.CreateTopologyKey(DisplayLayoutService.GetCurrentDisplays());
        _displayLayoutService.TryRestoreProfile(_displayTopologyKey, _config, Width, Height);
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
        else
        {
            // Integration is off, so MiniFences must never leave Explorer's icons hidden.
            // This also repairs the stale hidden state left by an interrupted older build.
            if (_desktopIconLayoutService.SetVisible(true))
                AppLogger.Log("Explorer desktop icons restored because desktop integration is disabled.");
            _windowsDesktopIconsVisible = true;
        }
        _desktopIconStateTimer.Start();
        if (_useTopLevelDesktopCompatibilityMode)
        {
            _desktopCompatibilityTimer.Start();
            ScheduleTopLevelDesktopRecovery("startup");
        }
        if (convertedLegacyDesktop) SaveConfigWithWarning();
        ConfigureAutoOrganizerWatcher();
        ConfigureDesktopContentsWatcher();
        if (_actionHistoryService.LastRecoveryReport is { } recovery)
        {
            Dispatcher.BeginInvoke(() =>
            {
                var chinese = string.Equals(_loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
                var message = chinese
                    ? $"检测到上次有 {recovery.Transactions} 个未完成的文件操作。\n已恢复源项目：{recovery.RestoredSources}\n已清理未完成副本：{recovery.CleanedPartials}"
                    : $"Detected {recovery.Transactions} interrupted file operation(s).\nRestored sources: {recovery.RestoredSources}\nCleaned partial copies: {recovery.CleanedPartials}";
                if (recovery.Failures > 0) message += chinese
                    ? $"\n需要人工检查：{recovery.Failures}\n\n可在“设置 → 操作历史”查看详情。"
                    : $"\nNeed manual review: {recovery.Failures}\n\nSee Settings → Action history for details.";
                System.Windows.MessageBox.Show(this, message, "MiniFences 安全恢复",
                    MessageBoxButton.OK, recovery.Failures > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }, DispatcherPriority.Loaded);
        }
        if (_openSettingsOnLoad)
        {
            Dispatcher.BeginInvoke(ShowSettingsWindow, DispatcherPriority.Loaded);
        }
        Dispatcher.BeginInvoke(
            () => _ = CheckForUpdatesAsync(interactive: false),
            DispatcherPriority.ApplicationIdle);
        Dispatcher.BeginInvoke(
            () => SendBehindNormalWindows(force: _useTopLevelDesktopCompatibilityMode),
            DispatcherPriority.ApplicationIdle);
    }

    /* diagnostic helper removed
    private void InitializeDragDiagnosticMode()
    {
        Activate();
        Topmost = true;
        var root = Path.Combine(Path.GetTempPath(), "MiniFences-MainWindow-DragDiagnostic");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "B Target Folder"));
        var sourcePath = Path.Combine(root, "A Drag Me.txt");
        if (!File.Exists(sourcePath)) File.WriteAllText(sourcePath, "Temporary drag diagnostic item.");
        _loc.Language = LocalizationService.Chinese;
        Workspace.Children.Clear();
        var config = new FenceConfig
        {
            Id = "drag-diagnostic",
            Title = "拖拽诊断：把文件拖到文件夹",
            FolderPath = root,
            Left = 40,
            Top = 40,
            Width = 620,
            Height = 360,
            ShowPath = true,
            BackgroundColor = "#F02A3038",
            HeaderColor = "#FF357EA8"
        };
        var fence = new FenceControl(config) { Width = config.Width, Height = config.Height };
        fence.SetLocalization(_loc);
        Canvas.SetLeft(fence, config.Left);
        Canvas.SetTop(fence, config.Top);
        Workspace.Children.Add(fence);
        fence.LoadFolderItems();
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(900);
            try
            {
                var from = fence.GetItemCenterOnScreenForTesting(1);
                var to = fence.GetItemCenterOnScreenForTesting(0);
                AppLogger.Log($"Automatic drag diagnostic coordinates. From={from.X:0},{from.Y:0}; To={to.X:0},{to.Y:0}");
                SetCursorPos((int)Math.Round(from.X), (int)Math.Round(from.Y));
                await Task.Delay(200);
                mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                for (var step = 1; step <= 70; step++)
                {
                    var x = from.X + (to.X - from.X) * step / 70;
                    var y = from.Y + (to.Y - from.Y) * step / 70;
                    SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
                    await Task.Delay(10);
                }
                await Task.Delay(600);
                keybd_event((byte)VkEscape, 0, 0, UIntPtr.Zero);
                keybd_event((byte)VkEscape, 0, KeyEventKeyUp, UIntPtr.Zero);
                mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                Title = _dragDiagnosticFrameCaptured
                    ? "MiniFences Drag Diagnostic - CAPTURED"
                    : "MiniFences Drag Diagnostic - NO TARGET FRAME";
            }
            catch (Exception ex)
            {
                Title = "MiniFences Drag Diagnostic - FAILED";
                AppLogger.LogException("Automatic drag diagnostic failed", ex);
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    */
    private void RenderFences(bool preserveLooseDesktopItems = false)
    {
        var membershipChanged = NormalizeDuplicateDesktopMemberships();
        if (preserveLooseDesktopItems)
        {
            foreach (var fenceControl in Workspace.Children.OfType<FenceControl>().ToArray())
                Workspace.Children.Remove(fenceControl);
        }
        else
        {
            Workspace.Children.Clear();
            RenderLooseDesktopItems();
        }
        var layoutChanged = false;
        foreach (var group in _config.Fences.Where(fence => IsFenceVisibleOnPage(fence, _config.CurrentPage))
                     .GroupBy(fence => string.IsNullOrWhiteSpace(fence.TabGroupId) ? fence.Id : fence.TabGroupId!))
        {
            var tabs = group.ToList();
            var active = _activeTabByGroup.TryGetValue(group.Key, out var activeId)
                ? tabs.FirstOrDefault(fence => string.Equals(fence.Id, activeId, StringComparison.OrdinalIgnoreCase))
                : null;
            active ??= tabs[0];
            _activeTabByGroup[group.Key] = active.Id;
            layoutChanged |= AddFenceControl(active, tabs.Count, tabs.IndexOf(active), tabs.Select(tab => tab.Title).ToArray(), tabs);
        }

        AppLogger.Log($"Rendered page {_config.CurrentPage + 1}/{GetPageCount()} with {Workspace.Children.OfType<FenceControl>().Count()} visible Fence(s) and {Workspace.Children.OfType<DesktopLooseIconControl>().Count()} loose icon(s).");
        ApplyFenceVisibility();
        UpdateMenuState();
        _settingsWindow?.RefreshFromMainWindow();
        if (layoutChanged || membershipChanged)
        {
            SaveConfigWithWarning();
        }
    }

    private void RenderLooseDesktopItems()
    {
        _looseIconLoadCancellation?.Cancel();
        _looseIconLoadCancellation?.Dispose();
        _looseIconLoadCancellation = null;
        if (!_config.EnableDesktopIconIntegration) return;
        var assigned = _config.Fences.Where(fence => fence.IsDesktopGroup)
            .SelectMany(fence => fence.AssignedPaths)
            .Select(path => FolderItemService.IsShellNamespacePath(path) ? path : TryGetFullPath(path))
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
        var systemItems = FolderItemService.LoadVisibleDesktopShellItems()
            .Where(item => !assigned.Contains(item.FullPath));
        var items = systemItems.Concat(_folderItemService.LoadAssignedItems(paths)).ToArray();
        _config.DesktopIconOrder = items.Select(item => item.FullPath).ToList();
        var rows = GetDesktopIconGridRows();
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
                control.ActionHistory = _actionHistoryService;
                control.IsExplorerDesktopPointForDrag = IsExplorerDesktopPoint;
                // The drag guard must distinguish an actual Fence/icon from
                // empty desktop space. The workspace itself covers the whole
                // desktop, so using its bounds here prevented desktop drops
                // from releasing membership and made the icon disappear.
                control.IsMiniFencesSurfacePointForDrag = IsPointOverVisibleFence;
                control.SelectionRequested += HandleLooseIconSelection;
                control.ItemRenamed += HandleLooseDesktopItemRenamed;
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
        _looseIconLoadCancellation = new CancellationTokenSource();
        _ = LoadLooseDesktopIconsAsync(items, _looseIconLoadCancellation.Token);
    }

    private async Task LoadLooseDesktopIconsAsync(IReadOnlyList<FolderItem> items, CancellationToken cancellationToken)
    {
        try
        {
            await _folderItemService.LoadIconsAsync(items, (item, icon) =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                Dispatcher.BeginInvoke(() =>
                {
                    // Keep the managed placeholder if a shell extension fails to return
                    // a usable icon. Assigning null made the loose desktop icon invisible.
                    if (!cancellationToken.IsCancellationRequested && icon != null) item.Icon = icon;
                }, DispatcherPriority.Background);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A new render superseded this icon pass.
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Loose desktop icon loading failed", ex);
        }
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
        foreach (var linkedGroup in _config.Fences
                     .Where(fence => !string.IsNullOrWhiteSpace(fence.ContentLinkId))
                     .GroupBy(fence => fence.ContentLinkId!, StringComparer.OrdinalIgnoreCase))
        {
            var linkedFences = linkedGroup.ToArray();
            var canonicalFence = linkedFences[0];
            var canonical = canonicalFence.AssignedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var fence in linkedFences.Skip(1))
            {
                if (fence.SynchronizeLinkedLayout != canonicalFence.SynchronizeLinkedLayout)
                {
                    fence.SynchronizeLinkedLayout = canonicalFence.SynchronizeLinkedLayout;
                    changed = true;
                }
                if (!fence.AssignedPaths.SequenceEqual(canonical, StringComparer.OrdinalIgnoreCase))
                {
                    fence.AssignedPaths = canonical.ToList();
                    changed = true;
                }
            }
            changed |= SynchronizeLinkedFenceLayout(_config, canonicalFence);
        }
        return changed;
    }

    internal static bool IsFenceVisibleOnPage(FenceConfig fence, int pageIndex) =>
        fence.ShowOnAllPages || fence.PageIndex == pageIndex;

    internal static IReadOnlyList<FenceConfig> GetContentLinkedFences(AppConfig config, FenceConfig fence)
    {
        if (string.IsNullOrWhiteSpace(fence.ContentLinkId)) return [fence];
        var linked = config.Fences
            .Where(candidate => string.Equals(candidate.ContentLinkId, fence.ContentLinkId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return linked.Length == 0 ? [fence] : linked;
    }

    private void SynchronizeLinkedFenceContent(FenceConfig source)
    {
        foreach (var target in GetContentLinkedFences(_config, source))
        {
            if (ReferenceEquals(target, source)) continue;
            target.Kind = source.Kind;
            target.FolderPath = source.FolderPath;
            target.AssignedPaths = source.AssignedPaths.ToList();
            if (!target.IsDesktopGroup && string.IsNullOrWhiteSpace(target.PortalCurrentPath))
                target.PortalCurrentPath = source.FolderPath;
        }
    }

    internal static bool SynchronizeLinkedFenceLayout(AppConfig config, FenceConfig source)
    {
        if (!source.SynchronizeLinkedLayout) return false;
        var changed = false;
        foreach (var target in GetContentLinkedFences(config, source))
        {
            if (ReferenceEquals(target, source)) continue;
            if (target.Left == source.Left && target.Top == source.Top &&
                target.Width == source.Width && target.Height == source.Height &&
                target.ExpandedHeight == source.ExpandedHeight &&
                target.IsCollapsed == source.IsCollapsed &&
                string.Equals(target.EdgeDock, source.EdgeDock, StringComparison.OrdinalIgnoreCase)) continue;

            target.Left = source.Left;
            target.Top = source.Top;
            target.Width = source.Width;
            target.Height = source.Height;
            target.ExpandedHeight = source.ExpandedHeight;
            target.IsCollapsed = source.IsCollapsed;
            target.EdgeDock = source.EdgeDock;
            changed = true;
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
        else if (ShouldPreserveLooseSelectionOnPointerDown(
                     _selectedLoosePaths.Contains(path),
                     _selectedLoosePaths.Count,
                     modifiers))
        {
            // Keep an existing selection intact when the drag starts on one of
            // its selected items, matching Explorer multi-selection behavior.
            _looseSelectionAnchor = path;
        }
        else
        {
            _selectedLoosePaths.Clear();
            _selectedLoosePaths.Add(path);
            _looseSelectionAnchor = path;
        }
        UpdateLooseIconSelectionVisuals();
        SetLooseIconSelectionActiveForRename(true);
    }

    internal static bool ShouldPreserveLooseSelectionOnPointerDown(
        bool clickedItemSelected,
        int selectedCount,
        ModifierKeys modifiers) =>
        clickedItemSelected &&
        selectedCount > 0 &&
        (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == ModifierKeys.None;

    private void UpdateLooseIconSelectionVisuals()
    {
        var controls = Workspace.Children.OfType<DesktopLooseIconControl>().ToList();
        var existing = controls.Select(control => control.Item.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedLoosePaths.RemoveWhere(path => !existing.Contains(path));
        var dragPaths = _selectedLoosePaths.ToArray();
        var expandSingleName = _selectedLoosePaths.Count == 1;
        foreach (var control in controls)
        {
            control.SetSelected(_selectedLoosePaths.Contains(control.Item.FullPath), expandSingleName);
            control.DragPaths = control.IsSelected ? dragPaths : [control.Item.FullPath];
        }
        Dispatcher.BeginInvoke(UpdateDesktopWindowRegion, DispatcherPriority.Loaded);
    }

    private void SetLooseIconSelectionActiveForRename(bool active)
    {
        foreach (var control in Workspace.Children.OfType<DesktopLooseIconControl>())
            control.SetSelectionActiveForRename(active);
    }

    private void HandleLooseDesktopItemRenamed(DesktopLooseIconControl control, string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return;
        SuppressAutoOrganizationAfterUserRename(newPath);
        if (_selectedLoosePaths.Remove(oldPath)) _selectedLoosePaths.Add(newPath);
        if (string.Equals(_looseSelectionAnchor, oldPath, StringComparison.OrdinalIgnoreCase))
            _looseSelectionAnchor = newPath;

        var replacementIndex = _config.DesktopIconOrder.FindIndex(path =>
            string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase));
        if (replacementIndex >= 0)
        {
            _config.DesktopIconOrder[replacementIndex] = newPath;
            _config.DesktopIconOrder = _config.DesktopIconOrder
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        UpdateLooseIconSelectionVisuals();
        ScheduleConfigSave();
        AppLogger.Log($"Loose desktop icon renamed without rebuilding desktop layer: {oldPath} -> {newPath}");
    }

    private void SuppressAutoOrganizationAfterUserRename(string newPath)
    {
        if (!_config.EnableAutoOrganizeNewDesktopItems || string.IsNullOrWhiteSpace(newPath)) return;
        _suppressedAutoOrganizePaths.Add(newPath);
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            _suppressedAutoOrganizePaths.Remove(newPath);
        }, DispatcherPriority.Background);
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

    private bool AddFenceControl(FenceConfig fence, int tabCount = 1, int tabIndex = 0,
        IReadOnlyList<string>? tabTitles = null, IReadOnlyList<FenceConfig>? tabConfigs = null)
    {
        var layoutChanged = ClampFenceConfigToWorkspace(fence);
        if (tabCount > 1 && tabConfigs != null) SynchronizeTabGroupPresentationState(fence, tabConfigs);
        var control = new FenceControl(fence);
        control.ActionHistory = _actionHistoryService;
        control.ActionHistoryConfig = _config;
        control.SnapToGrid = _config.EnableSnapToGrid;
        control.GridSize = _config.GridSize;
        control.SnapWhileDragging = _config.SnapWhileDragging;
        control.RollupEnabled = _config.EnableRollup;
        control.DoubleClickRollupEnabled = _config.DoubleClickTitleRollup;
        control.ClickTitleToExpandEnabled = _config.ClickTitleToExpand;
        control.HoverTitleToExpandEnabled = _config.HoverTitleToExpand;
        control.BottomDockTitleAtBottom = _config.BottomDockTitleAtBottom;
        control.TopDockTitleAtBottomOnExpand = _config.TopDockTitleAtBottomOnExpand;
        control.RefreshAppearance();
        control.IsExplorerDesktopPointForDrag = IsExplorerDesktopPoint;
        control.IsMiniFencesSurfacePointForDrag = IsPointOverVisibleFence;
        control.DragWorkAreaProvider = GetWorkspaceWorkArea;
        control.SetLocalization(_loc);
        control.SetTabStatus(tabCount, tabIndex, tabTitles,
            string.Equals(_config.TabViewMode, "Strip", StringComparison.OrdinalIgnoreCase), _config.HoverSwitchTabs,
            string.Equals(_config.TabWidthMode, "Equal", StringComparison.OrdinalIgnoreCase), tabConfigs,
            adaptiveTabWidths: string.Equals(_config.TabWidthMode, "Adaptive", StringComparison.OrdinalIgnoreCase),
            selectionStyle: _config.TabSelectionStyle,
            selectionLineColor: _config.TabSelectionLineColor,
            selectionLineThickness: _config.TabSelectionLineThickness);
        Canvas.SetLeft(control, fence.Left);
        Canvas.SetTop(control, fence.Top);
        System.Windows.Controls.Panel.SetZIndex(control, 100 + Math.Max(0, fence.LayerOrder));
        control.AddHandler(UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((_, _) => BringFenceToFront(control)), true);
        control.Changed += (_, _) =>
        {
            SynchronizeLinkedFenceContent(control.Config);
            SynchronizeLinkedFenceLayout(_config, control.Config);
            if (!string.IsNullOrWhiteSpace(control.Config.TabGroupId))
                SynchronizeTabGroupPresentationState(control.Config, GetTabs(control.Config.TabGroupId));
            UpdateDesktopWindowRegion();
            ScheduleConfigSave();
            _settingsWindow?.RefreshFromMainWindow();
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
        control.ItemsChanged += (_, _) => Dispatcher.BeginInvoke(
            () => RenderFences(),
            DispatcherPriority.Background);
        control.ContentRefreshed += (_, _) => _settingsWindow?.RefreshFromMainWindow();
        control.ItemSelectionRequested += (_, _) => HandleFenceItemSelection(control);
        control.ItemRenamed += (_, newPath) => SuppressAutoOrganizationAfterUserRename(newPath);
        control.HeaderDragCompleted += (_, _) =>
        {
            _fenceHeaderDragActive = false;
            HandleHeaderDragCompleted(control);
            UpdateDesktopWindowRegion();
        };
        control.HeaderDragMoved += (_, _) =>
        {
            if (!_fenceHeaderDragActive)
            {
                _fenceHeaderDragActive = true;
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero) SetWindowRgn(handle, IntPtr.Zero, true);
            }
            UpdateMergePreview(control);
            if (!_dragRegionRefreshTimer.IsEnabled) _dragRegionRefreshTimer.Start();
        };
        control.HeaderDragCanceled += (_, _) =>
        {
            _fenceHeaderDragActive = false;
            ClearMergePreview();
            UpdateDesktopWindowRegion();
        };
        control.DeleteRequested += (_, _) => DeleteFence(control);
        control.MoveToPreviousPageRequested += (_, _) => MoveFenceToPage(control, control.Config.PageIndex - 1);
        control.MoveToNextPageRequested += (_, _) => MoveFenceToPage(control, control.Config.PageIndex + 1);
        control.MoveToNewPageRequested += (_, _) => MoveFenceToPage(control, GetPageCount());
        control.StackWithNearestRequested += (_, _) => StackWithNearestFence(control);
        control.NextTabRequested += (_, _) => SwitchToNextTab(control);
        control.PreviousTabRequested += (_, _) => SwitchTab(control, -1);
        control.TabSelectedRequested += index => SwitchToTab(control, index);
        control.TabReorderRequested += (from, to) => ReorderTabs(control.Config.TabGroupId, from, to);
        control.TabDetachRequested += (index, screenPoint) =>
        {
            var workspacePoint = Workspace.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            DetachTab(control.Config.TabGroupId, index, workspacePoint.X, workspacePoint.Y, centerOnCursor: true);
        };
        control.TabDragStarted += draggedIndex => Dispatcher.BeginInvoke(
            () => ShowRemainingTabDuringDrag(control, draggedIndex),
            DispatcherPriority.Input);
        control.TabDragCanceled += draggedIndex => RestoreCanceledTabDrag(control.Config.TabGroupId, draggedIndex);
        control.TabMergeRequested += (sourceFenceId, insertionIndex) =>
            MergeDraggedTab(sourceFenceId, control.Config, insertionIndex);
        control.UnstackRequested += (_, _) => UnstackFence(control);
        Workspace.Children.Add(control);
        Dispatcher.BeginInvoke(UpdateDesktopWindowRegion, DispatcherPriority.Loaded);
        ApplyFenceVisibility(control);
        control.Dispatcher.BeginInvoke(() =>
        {
            ClampFenceToUsableWorkArea(control);
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

    private void ClearAllItemSelections()
    {
        foreach (var fence in Workspace.Children.OfType<FenceControl>()) fence.ClearItemSelection();
        _selectedLoosePaths.Clear();
        _looseSelectionAnchor = null;
        UpdateLooseIconSelectionVisuals();
        Keyboard.ClearFocus();
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
        var pageFences = _config.Fences.Where(fence => IsFenceVisibleOnPage(fence, _config.CurrentPage)).ToArray();
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
            .Where(candidate => IsFenceVisibleOnPage(candidate, _config.CurrentPage) && candidate.Id != control.Config.Id)
            .OrderBy(candidate => Math.Pow(candidate.Left - control.Config.Left, 2) + Math.Pow(candidate.Top - control.Config.Top, 2))
            .FirstOrDefault();
        if (target == null)
        {
            System.Windows.MessageBox.Show(this, _loc.T("NoFenceToStack"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StackFences(control.Config, target);
    }

    private bool MergeByHeaderOverlap(FenceControl sourceControl)
    {
        sourceControl.SyncConfigFromLayout();
        var target = ReferenceEquals(sourceControl, _mergePreviewSource) && _mergePreviewTarget != null
            ? _mergePreviewTarget
            : FindMergeTarget(sourceControl);
        var insertionIndex = target?.GetTabInsertionIndexForHeaderPoint(sourceControl.GetHeaderDragMergePoint());
        ClearMergePreview();
        if (target == null) return false;
        if (_config.ConfirmTabCreation && System.Windows.MessageBox.Show(this,
                $"将“{sourceControl.Config.Title}”与“{target.Config.Title}”合并为标签页？",
                "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        StackFences(sourceControl.Config, target.Config, insertionIndex: insertionIndex);
        return true;
    }

    private bool IsPointerOverHeader(FenceConfig target)
    {
        var pointer = Mouse.GetPosition(Workspace);
        return new Rect(target.Left, target.Top, target.Width, 34).Contains(pointer);
    }

    private void HandleHeaderDragCompleted(FenceControl control)
    {
        var headerDragDistance = Math.Sqrt(
            Math.Pow(Canvas.GetLeft(control) - control.HeaderDragStartLeft, 2) +
            Math.Pow(Canvas.GetTop(control) - control.HeaderDragStartTop, 2));
        var isCompactTabDrag = control.IsShiftDetachHeaderDrag &&
                               string.Equals(_config.TabViewMode, "Compact", StringComparison.OrdinalIgnoreCase);
        var originalGroupBounds = new Rect(
            control.HeaderDragStartLeft,
            control.HeaderDragStartTop,
            control.ActualWidth > 0 ? control.ActualWidth : control.Width,
            control.ActualHeight > 0 ? control.ActualHeight : control.Height);
        originalGroupBounds.Inflate(12, 12);
        var releasedInsideOriginalGroup = originalGroupBounds.Contains(Mouse.GetPosition(Workspace));
        var isCompactTabReorder = isCompactTabDrag && releasedInsideOriginalGroup &&
                                  Math.Abs(control.HeaderDragDeltaX) >= 24 &&
                                  Math.Abs(Canvas.GetTop(control) - control.HeaderDragStartTop) < 48;
        if (isCompactTabDrag && releasedInsideOriginalGroup)
        {
            ClearMergePreview();
            var tabs = GetTabs(control.Config.TabGroupId);
            foreach (var tab in tabs)
            {
                tab.Left = control.HeaderDragStartLeft;
                tab.Top = control.HeaderDragStartTop;
            }
            Canvas.SetLeft(control, control.HeaderDragStartLeft);
            Canvas.SetTop(control, control.HeaderDragStartTop);
            control.SyncConfigFromLayout();
            if (isCompactTabReorder)
            {
                var currentIndex = tabs.FindIndex(tab => tab.Id == control.Config.Id);
                ReorderTabs(control.Config.TabGroupId, currentIndex,
                    currentIndex + (control.HeaderDragDeltaX < 0 ? -1 : 1));
            }
            else
            {
                SaveConfigWithWarning();
            }
            return;
        }
        if (control.IsShiftDetachHeaderDrag && headerDragDistance < 24)
        {
            Canvas.SetLeft(control, control.HeaderDragStartLeft);
            Canvas.SetTop(control, control.HeaderDragStartTop);
            control.SyncConfigFromLayout();
            ClearMergePreview();
            return;
        }
        if (control.IsShiftDetachHeaderDrag && !isCompactTabReorder && !string.IsNullOrWhiteSpace(control.Config.TabGroupId))
        {
            if (MergeDetachedTabByHeaderOverlap(control)) return;
            ClearMergePreview();
            DetachTab(control.Config.TabGroupId, GetTabIndex(control.Config), Canvas.GetLeft(control), Canvas.GetTop(control));
            return;
        }
        if (!control.IsShiftHeaderDrag && !string.IsNullOrWhiteSpace(control.Config.TabGroupId))
        {
            ClearMergePreview();
            ClampFenceToUsableWorkArea(control, preferPointerMonitor: true);
            if (TryAutoRollupAtUsableEdge(control)) return;
            foreach (var tab in GetTabs(control.Config.TabGroupId))
            {
                tab.Left = Canvas.GetLeft(control);
                tab.Top = Canvas.GetTop(control);
            }
            control.ClearEdgeDock();
            SaveConfigWithWarning();
            return;
        }
        // The drag itself may temporarily cross a monitor edge so the grabbed
        // point can follow the mouse. Once released, restore the Fence to that
        // monitor's usable area before edge-rollup logic sees the taskbar region.
        ClampFenceToUsableWorkArea(control, preferPointerMonitor: true);
        if (TryAutoRollupAtUsableEdge(control)) return;
        control.ClearEdgeDock();
        if (CanHeaderDragMerge(control.Config, control.IsShiftHeaderDrag) &&
            MergeByHeaderOverlap(control))
        {
            return;
        }
        ClampFenceToUsableWorkArea(control, preferPointerMonitor: true);
    }

    private bool TryAutoRollupAtUsableEdge(FenceControl control)
    {
        if (!_config.EnableRollup || !_config.AutoRollupAtScreenEdge) return false;
        var workArea = GetWorkspaceWorkArea(Forms.Cursor.Position);
        var width = control.ActualWidth > 0 ? control.ActualWidth : control.Width;
        var height = control.ActualHeight > 0 ? control.ActualHeight : control.Height;
        var bounds = new Rect(Canvas.GetLeft(control), Canvas.GetTop(control), width, height);
        var edge = GetAutoRollupEdge(bounds, workArea, allowBottom: _config.AllowBottomEdgeRollup);
        if (edge == null) return false;

        ClearMergePreview();
        control.DockAndRollUp(edge);
        Canvas.SetTop(control, edge == "Top"
            ? workArea.Top
            : Math.Max(workArea.Top, workArea.Bottom - FenceControl.CollapsedHeight));
        control.SyncConfigFromLayout();
        if (!string.IsNullOrWhiteSpace(control.Config.TabGroupId))
            SynchronizeTabGroupPresentationState(control.Config, GetTabs(control.Config.TabGroupId));
        SaveConfigWithWarning();
        AppLogger.Log($"Fence '{control.Config.Title}' docked and rolled up at the {edge.ToLowerInvariant()} usable-work-area edge.");
        return true;
    }

    internal static string? GetAutoRollupEdge(
        Rect fenceBounds,
        Rect usableArea,
        double threshold = 12,
        bool allowBottom = true)
    {
        if (usableArea.Width <= 0 || usableArea.Height <= 0) return null;
        if (fenceBounds.Top <= usableArea.Top + threshold) return "Top";
        if (allowBottom && fenceBounds.Bottom >= usableArea.Bottom - threshold) return "Bottom";
        return null;
    }

    private bool MergeDetachedTabByHeaderOverlap(FenceControl sourceControl)
    {
        var target = ReferenceEquals(sourceControl, _mergePreviewSource) && _mergePreviewTarget != null
            ? _mergePreviewTarget
            : FindMergeTarget(sourceControl);
        if (target == null) return false;
        var insertionIndex = target.GetTabInsertionIndexForHeaderPoint(sourceControl.GetHeaderDragMergePoint());
        if (_config.ConfirmTabCreation && System.Windows.MessageBox.Show(this,
                $"将“{sourceControl.Config.Title}”移入“{target.Config.Title}”的标签组？",
                "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            ClearMergePreview();
            return false;
        }

        ClearMergePreview();
        MergeDraggedTab(sourceControl.Config.Id, target.Config, insertionIndex);
        return true;
    }

    private void UpdateMergePreview(FenceControl sourceControl)
    {
        if (!CanHeaderDragMerge(sourceControl.Config, sourceControl.IsShiftHeaderDrag))
        {
            ClearMergePreview();
            return;
        }
        var target = FindMergeTarget(sourceControl);
        if (ReferenceEquals(target, _mergePreviewTarget) && ReferenceEquals(sourceControl, _mergePreviewSource)) return;
        ClearMergePreview();
        _mergePreviewTarget = target;
        _mergePreviewSource = target == null ? null : sourceControl;
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
        var sourceHeaderCenter = sourceControl.GetHeaderDragMergePoint();
        return Workspace.Children.OfType<FenceControl>()
            .Where(control => control.Config.Id != source.Id && control.Config.PageIndex == source.PageIndex)
            .Where(control => string.IsNullOrWhiteSpace(source.TabGroupId) ||
                              !string.Equals(control.Config.TabGroupId, source.TabGroupId, StringComparison.OrdinalIgnoreCase))
            .Where(control => control.IsHeaderMergePoint(sourceHeaderCenter))
            // Overlapping Fences are common on the desktop. The user is aiming at
            // the visible topmost title strip, not whichever hidden Fence happens
            // to have the nearest geometric center.
            .OrderByDescending(System.Windows.Controls.Panel.GetZIndex)
            .ThenByDescending(control => Workspace.Children.IndexOf(control))
            .FirstOrDefault();
    }

    internal static bool IsPointerInMergeZone(System.Windows.Point pointer, FenceConfig target)
        => IsPointInMergeZone(pointer, target, verticalTolerance: 0);

    internal static bool IsPointInMergeZone(System.Windows.Point point, FenceConfig target,
        double verticalTolerance)
    {
        var mergeZoneLeft = target.Left + target.Width / 3;
        return new Rect(mergeZoneLeft, target.Top - Math.Max(0, verticalTolerance),
            target.Width / 3, 34 + Math.Max(0, verticalTolerance) * 2).Contains(point);
    }

    internal static bool IsMergeCandidate(FenceConfig source, FenceConfig target)
    {
        var sourceCenter = new System.Windows.Point(source.Left + source.Width / 2, source.Top + 17);
        var mergeZoneLeft = target.Left + target.Width / 3;
        return new Rect(mergeZoneLeft, target.Top, target.Width / 3, 34).Contains(sourceCenter);
    }

    private void StackFences(FenceConfig source, FenceConfig target,
        IEnumerable<string>? additionalAffectedFenceIds = null, int? insertionIndex = null)
    {
        var affectedFenceIds = GetRelatedFenceIds(source)
            .Concat(GetRelatedFenceIds(target))
            .Concat(additionalAffectedFenceIds ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        RememberStandaloneGeometry(source);
        RememberStandaloneGeometry(target);
        var groupId = string.IsNullOrWhiteSpace(target.TabGroupId) ? Guid.NewGuid().ToString("N") : target.TabGroupId;
        target.TabGroupId = groupId;
        source.TabGroupId = groupId;
        if (insertionIndex is int targetInsertionIndex)
            PlaceTabAtGroupIndex(source, groupId, targetInsertionIndex);
        else
            PlaceTabRelativeToTarget(source, target, Mouse.GetPosition(Workspace).X < target.Left + target.Width / 2);
        SynchronizeTabGroupPresentationState(target, _config.Fences.Where(fence =>
            fence.Id == source.Id ||
            string.Equals(fence.TabGroupId, groupId, StringComparison.OrdinalIgnoreCase)));
        _activeTabByGroup[groupId] = source.Id;
        RenderAffectedFenceGroups(affectedFenceIds);
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

    internal static void SynchronizeTabGroupPresentationState(FenceConfig source, IEnumerable<FenceConfig> tabs)
    {
        foreach (var tab in tabs)
        {
            CopyFenceGeometry(source, tab);
            tab.IsCollapsed = source.IsCollapsed;
            tab.EdgeDock = source.EdgeDock;
            tab.ExpandedHeight = source.ExpandedHeight;
        }
    }

    private static void RememberStandaloneGeometry(FenceConfig fence)
    {
        fence.PreTabWidth ??= fence.Width;
        fence.PreTabHeight ??= fence.Height;
    }

    private void SwitchToNextTab(FenceControl control)
    {
        SwitchTab(control, 1);
    }

    private void ShowRemainingTabDuringDrag(FenceControl control, int draggedIndex)
    {
        if (string.IsNullOrWhiteSpace(control.Config.TabGroupId) ||
            !Workspace.Children.Contains(control))
        {
            return;
        }

        var tabs = GetTabs(control.Config.TabGroupId);
        if (draggedIndex < 0 || draggedIndex >= tabs.Count) return;

        // Keep the current FenceControl alive for the whole OLE drag. Replacing it
        // here removed the tab Border that initiated DoDragDrop, so the newly built
        // control received the release as a generic Fence drop and cancelled the
        // reorder (the log showed Target changing to the temporary remainder tab).
        // Hiding only the dragged tab preserves all tab DragOver/Drop handlers.
        control.HideTabForActiveDrag(tabs[draggedIndex].Id);
        AppLogger.Log($"Tab drag source preserved for reorder. Source={tabs[draggedIndex].Id}; Host={control.Config.Title}");
    }

    internal static int GetTabDragRemainderIndex(int activeIndex, int draggedIndex, int tabCount)
    {
        if (tabCount < 2 || activeIndex < 0 || activeIndex >= tabCount ||
            draggedIndex < 0 || draggedIndex >= tabCount)
        {
            return -1;
        }

        if (draggedIndex != activeIndex) return activeIndex;
        return draggedIndex == tabCount - 1 ? draggedIndex - 1 : draggedIndex + 1;
    }

    private void SwitchTab(FenceControl control, int direction)
    {
        if (string.IsNullOrWhiteSpace(control.Config.TabGroupId)) return;
        var tabs = GetTabs(control.Config.TabGroupId);
        if (tabs.Count < 2) return;
        var index = tabs.FindIndex(fence => fence.Id == control.Config.Id);
        control.SyncConfigFromLayout();
        SynchronizeTabGroupPresentationState(control.Config, tabs);
        var nextIndex = (index + direction + tabs.Count) % tabs.Count;
        _activeTabByGroup[control.Config.TabGroupId] = tabs[nextIndex].Id;
        ReplaceActiveTabControl(control, tabs, nextIndex);
        ScheduleConfigSave();
    }

    private void SwitchToTab(FenceControl control, int tabIndex)
    {
        if (string.IsNullOrWhiteSpace(control.Config.TabGroupId)) return;
        var tabs = _config.Fences.Where(fence => string.Equals(fence.TabGroupId, control.Config.TabGroupId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (tabIndex < 0 || tabIndex >= tabs.Count || tabs[tabIndex].Id == control.Config.Id) return;
        control.SyncConfigFromLayout();
        SynchronizeTabGroupPresentationState(control.Config, tabs);
        _activeTabByGroup[control.Config.TabGroupId] = tabs[tabIndex].Id;
        ReplaceActiveTabControl(control, tabs, tabIndex);
        ScheduleConfigSave();
    }

    private void ReplaceActiveTabControl(FenceControl currentControl, IReadOnlyList<FenceConfig> tabs, int activeIndex)
    {
        if (activeIndex < 0 || activeIndex >= tabs.Count) return;

        var childIndex = Workspace.Children.IndexOf(currentControl);
        var zIndex = System.Windows.Controls.Panel.GetZIndex(currentControl);
        Workspace.Children.Remove(currentControl);

        AddFenceControl(
            tabs[activeIndex],
            tabs.Count,
            activeIndex,
            tabs.Select(tab => tab.Title).ToArray(),
            tabs);

        var replacement = Workspace.Children.OfType<FenceControl>()
            .LastOrDefault(candidate => string.Equals(candidate.Config.Id, tabs[activeIndex].Id, StringComparison.OrdinalIgnoreCase));
        if (replacement != null)
        {
            System.Windows.Controls.Panel.SetZIndex(replacement, zIndex);
            var addedIndex = Workspace.Children.IndexOf(replacement);
            if (childIndex >= 0 && addedIndex >= 0 && childIndex < Workspace.Children.Count - 1)
            {
                Workspace.Children.RemoveAt(addedIndex);
                Workspace.Children.Insert(Math.Min(childIndex, Workspace.Children.Count), replacement);
            }
        }

        UpdateDesktopWindowRegion();
        ApplyFenceVisibility();
        UpdateMenuState();
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
        if (!string.IsNullOrWhiteSpace(groupId)) _activeTabByGroup[groupId] = moved.Id;
        var firstConfigIndex = _config.Fences.FindIndex(fence => tabs.Any(tab => tab.Id == fence.Id));
        _config.Fences.RemoveAll(fence => tabs.Any(tab => tab.Id == fence.Id));
        _config.Fences.InsertRange(Math.Max(0, firstConfigIndex), tabs);
        AppLogger.Log($"Reordered tab group {groupId}. From={fromIndex}; To={toIndex}; Active={moved.Title}");
        RenderAffectedFenceGroups(tabs.Select(tab => tab.Id).ToArray());
        SaveConfigWithWarning();
    }

    private void RestoreCanceledTabDrag(string? groupId, int draggedIndex)
    {
        var tabs = GetTabs(groupId);
        if (string.IsNullOrWhiteSpace(groupId) || draggedIndex < 0 || draggedIndex >= tabs.Count) return;
        _activeTabByGroup[groupId] = tabs[draggedIndex].Id;
        RenderAffectedFenceGroups(tabs.Select(tab => tab.Id).ToArray());
    }

    private void PlaceTabRelativeToTarget(FenceConfig source, FenceConfig target, bool before)
    {
        if (source.Id == target.Id) return;
        _config.Fences.Remove(source);
        var targetIndex = _config.Fences.IndexOf(target);
        _config.Fences.Insert(Math.Clamp(targetIndex + (before ? 0 : 1), 0, _config.Fences.Count), source);
    }

    private void PlaceTabAtGroupIndex(FenceConfig source, string groupId, int insertionIndex)
    {
        _config.Fences.Remove(source);
        var groupTabs = _config.Fences.Where(fence =>
            string.Equals(fence.TabGroupId, groupId, StringComparison.OrdinalIgnoreCase)).ToList();
        insertionIndex = Math.Clamp(insertionIndex, 0, groupTabs.Count);
        var configIndex = insertionIndex < groupTabs.Count
            ? _config.Fences.IndexOf(groupTabs[insertionIndex])
            : groupTabs.Count > 0
                ? _config.Fences.IndexOf(groupTabs[^1]) + 1
                : _config.Fences.Count;
        _config.Fences.Insert(Math.Clamp(configIndex, 0, _config.Fences.Count), source);
    }

    private void DetachTab(string? groupId, int tabIndex, double left, double top, bool centerOnCursor = false)
    {
        var tabs = GetTabs(groupId);
        if (tabs.Count < 2 || tabIndex < 0 || tabIndex >= tabs.Count) return;
        var detached = tabs[tabIndex];
        detached.TabGroupId = null;
        RestoreStandaloneGeometry(detached);
        detached.Left = Math.Max(0, centerOnCursor ? left - detached.Width / 2 : left);
        detached.Top = Math.Max(0, centerOnCursor ? top - 17 : top);
        var cursor = Forms.Cursor.Position;
        var workArea = GetWorkspaceWorkArea(cursor);
        var clamped = ClampFencePositionToWorkArea(
            new System.Windows.Point(detached.Left, detached.Top),
            new System.Windows.Size(detached.Width, detached.Height),
            workArea);
        detached.Left = clamped.X;
        detached.Top = clamped.Y;
        detached.IsCollapsed = false;
        detached.EdgeDock = null;
        if (DissolveSingleItemTabGroup(_config, groupId!)) _activeTabByGroup.Remove(groupId!);
        else if (_activeTabByGroup.TryGetValue(groupId!, out var activeId) && activeId == detached.Id) _activeTabByGroup.Remove(groupId!);
        RenderAffectedFenceGroups(tabs.Select(tab => tab.Id).ToArray());
        SaveConfigWithWarning();
    }

    private void MergeDraggedTab(string sourceFenceId, FenceConfig target, int? insertionIndex = null)
    {
        var source = _config.Fences.FirstOrDefault(fence =>
            string.Equals(fence.Id, sourceFenceId, StringComparison.OrdinalIgnoreCase));
        if (source == null || source.Id == target.Id ||
            (!string.IsNullOrWhiteSpace(source.TabGroupId) &&
             string.Equals(source.TabGroupId, target.TabGroupId, StringComparison.OrdinalIgnoreCase))) return;

        var previousGroupId = source.TabGroupId;
        var previousGroupIds = string.IsNullOrWhiteSpace(previousGroupId)
            ? Array.Empty<string>()
            : GetTabs(previousGroupId).Select(tab => tab.Id).ToArray();
        if (!string.IsNullOrWhiteSpace(previousGroupId))
        {
            source.TabGroupId = null;
            if (DissolveSingleItemTabGroup(_config, previousGroupId))
                _activeTabByGroup.Remove(previousGroupId);
        }

        StackFences(source, target, previousGroupIds, insertionIndex);
    }

    private void UnstackFence(FenceControl control)
    {
        if (string.IsNullOrWhiteSpace(control.Config.TabGroupId)) return;
        var groupId = control.Config.TabGroupId;
        var affectedFenceIds = GetTabs(groupId).Select(tab => tab.Id).ToArray();
        control.Config.TabGroupId = null;
        RestoreStandaloneGeometry(control.Config);
        if (DissolveSingleItemTabGroup(_config, groupId)) _activeTabByGroup.Remove(groupId);
        if (_activeTabByGroup.TryGetValue(groupId, out var activeId) && activeId == control.Config.Id)
        {
            _activeTabByGroup.Remove(groupId);
        }
        RenderAffectedFenceGroups(affectedFenceIds);
        SaveConfigWithWarning();
    }

    private IReadOnlyList<string> GetRelatedFenceIds(FenceConfig fence)
    {
        return string.IsNullOrWhiteSpace(fence.TabGroupId)
            ? [fence.Id]
            : _config.Fences
                .Where(candidate => string.Equals(candidate.TabGroupId, fence.TabGroupId, StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Id)
                .ToArray();
    }

    private void RenderAffectedFenceGroups(IReadOnlyCollection<string> affectedFenceIds)
    {
        if (affectedFenceIds.Count == 0) return;
        var affected = affectedFenceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var control in Workspace.Children.OfType<FenceControl>()
                     .Where(control => affected.Contains(control.Config.Id))
                     .ToArray())
        {
            Workspace.Children.Remove(control);
        }

        var groups = _config.Fences
            .Where(fence => IsFenceVisibleOnPage(fence, _config.CurrentPage))
            .GroupBy(fence => string.IsNullOrWhiteSpace(fence.TabGroupId) ? fence.Id : fence.TabGroupId!)
            .Where(group => group.Any(fence => affected.Contains(fence.Id)))
            .ToArray();
        foreach (var group in groups)
        {
            var tabs = group.ToList();
            var active = _activeTabByGroup.TryGetValue(group.Key, out var activeId)
                ? tabs.FirstOrDefault(fence => string.Equals(fence.Id, activeId, StringComparison.OrdinalIgnoreCase))
                : null;
            active ??= tabs[0];
            _activeTabByGroup[group.Key] = active.Id;
            AddFenceControl(active, tabs.Count, tabs.IndexOf(active), tabs.Select(tab => tab.Title).ToArray(), tabs);
        }

        ApplyFenceVisibility();
        UpdateMenuState();
        UpdateDesktopWindowRegion();
        AppLogger.Log($"Updated {groups.Length} affected Fence group(s) without rebuilding unrelated Fences.");
    }

    internal static bool DissolveSingleItemTabGroup(AppConfig config, string groupId)
    {
        var remaining = config.Fences
            .Where(fence => string.Equals(fence.TabGroupId, groupId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (remaining.Count >= 2) return false;
        foreach (var fence in remaining)
        {
            fence.TabGroupId = null;
            RestoreStandaloneGeometry(fence);
        }
        return true;
    }

    private static void RestoreStandaloneGeometry(FenceConfig fence)
    {
        if (fence.PreTabWidth is > 0) fence.Width = fence.PreTabWidth.Value;
        if (fence.PreTabHeight is > 0)
        {
            fence.Height = fence.PreTabHeight.Value;
            fence.ExpandedHeight = fence.PreTabHeight.Value;
        }
        fence.PreTabWidth = null;
        fence.PreTabHeight = null;
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
        var linkedTargets = GetContentLinkedFences(_config, target).Where(fence => fence.IsDesktopGroup).ToArray();
        _actionHistoryService.ExecuteMembershipChange(
            insertionIndex.HasValue ? "调整桌面项目顺序" : "分配桌面项目", _config, () =>
        {
            foreach (var path in movedPaths)
            {
                foreach (var fence in _config.Fences.Where(fence => fence.IsDesktopGroup))
                    fence.AssignedPaths.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            }

            var updatedOrder = insertionIndex.HasValue
                ? ReorderFenceItems(currentTargetOrder, movedPaths, insertionIndex.Value)
                : target.AssignedPaths.Concat(movedPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var linkedTarget in linkedTargets)
            {
                linkedTarget.AssignedPaths = updatedOrder.ToList();
                if (insertionIndex.HasValue) linkedTarget.SortMode = "None";
            }
            return movedPaths.Length > 0;
        });
        RenderFences();
        SaveConfigWithWarning();
        RefreshUndoCommands();
    }

    private async Task DropItemsOnCombinedFenceTabAsync(
        FenceControl sourceControl,
        int tabIndex,
        IReadOnlyList<string> paths)
    {
        var groupId = sourceControl.Config.TabGroupId;
        var tabs = GetTabs(groupId);
        if (string.IsNullOrWhiteSpace(groupId) || tabIndex < 0 || tabIndex >= tabs.Count || paths.Count == 0) return;
        var target = tabs[tabIndex];
        var distinctPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (target.IsDesktopGroup)
        {
            var alreadyOnDesktop = distinctPaths.Where(path =>
                FolderItemService.IsShellNamespacePath(path) ||
                FenceControl.IsDirectChildOfDesktopRoot(path)).ToArray();
            var pathsToRestore = distinctPaths.Where(path =>
                !FolderItemService.IsShellNamespacePath(path) &&
                !FenceControl.IsDirectChildOfDesktopRoot(path)).ToArray();
            FolderMoveResult? restoreResult = null;
            if (pathsToRestore.Length > 0)
            {
                var desktopRoot = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                restoreResult = await _actionHistoryService.ExecuteFileTransferAsync(
                    "移动到桌面", _folderItemService, pathsToRestore, desktopRoot,
                    FolderTransferOperation.Move);
            }

            var assignablePaths = alreadyOnDesktop
                .Concat(restoreResult?.MovedPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (assignablePaths.Length > 0)
            {
                _activeTabByGroup[groupId] = target.Id;
                AssignDesktopItems(target, assignablePaths);
            }
            if (restoreResult is { Errors.Count: > 0 })
                System.Windows.MessageBox.Show(string.Join("\n", restoreResult.Errors.Take(8)),
                    "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (distinctPaths.Any(FolderItemService.IsShellNamespacePath)) return;
        var destination = target.FolderPath;
        var result = await _actionHistoryService.ExecuteFileTransferAsync(
            $"移动到 Fence“{target.Title}”", _folderItemService, distinctPaths, destination,
            FolderTransferOperation.Move);
        _activeTabByGroup[groupId] = target.Id;
        RenderFences();
        SaveConfigWithWarning();
        RefreshUndoCommands();
        if (result.Errors.Count > 0)
            System.Windows.MessageBox.Show(string.Join("\n", result.Errors.Take(8)),
                "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        if (!_actionHistoryService.ExecuteMembershipChange("取消分配桌面项目", _config, () =>
            {
                foreach (var fence in _config.Fences.Where(fence => fence.IsDesktopGroup))
                    removed += fence.AssignedPaths.RemoveAll(path => releasable.Contains(path));
                return removed > 0;
            })) return;
        AppLogger.Log($"Released {removed} desktop item(s) from Fence membership.");
        RenderFences();
        SaveConfigWithWarning();
        RefreshUndoCommands();
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
        _oleDragActive = false;
        _shellDragLeaveTimer.Stop();
        _shellDropTarget.DragLeave();
        ClearDragHint();
        UpdateDesktopWindowRegion();
        AppLogger.Log("Desktop item drag capture released.");
    }

    internal void SetDragHintIcon(ImageSource? icon)
    {
        if (icon == null)
        {
            _dragHintIconSource = null;
            _dragFeedbackWindow?.SetIcon(null);
            return;
        }

        try
        {
            if (icon is Freezable freezable && !freezable.IsFrozen)
            {
                var clone = freezable.Clone();
                if (clone.CanFreeze) clone.Freeze();
                icon = clone as ImageSource ?? icon;
            }
        }
        catch
        {
            // Keep the original source if it is already usable on this UI thread.
        }

        _dragHintIconSource = icon;
        _dragFeedbackWindow?.SetIcon(icon);
    }

    internal void ShowDragSourceHint(
        ImageSource? icon,
        string? label = null,
        bool expanded = false,
        double textWidth = DragFeedbackWindow.LabelWidthDips,
        bool pinUntilClear = false)
    {
        if (_isExiting) return;
        if (pinUntilClear) _dragFeedbackPinnedUntilClear = true;
        // A child folder hot-zone reports null when it is not a valid target.
        // Keep the current source image instead of replacing it with the small
        // generic application icon while crossing that child.
        if (icon != null) SetDragHintIcon(icon);
        else if (_dragHintIconSource == null) SetDragHintIcon(CreateDragHintFallbackIcon());
        if (!string.IsNullOrWhiteSpace(label))
        {
            _dragSourceLabel = label;
            _dragSourceExpanded = expanded;
            _dragSourceTextWidth = textWidth;
        }
        _dragHintLabel = _dragSourceLabel;
        ClearDragTargetHint();
        ActivateDragFeedback();
    }

    /* diagnostic helper removed
    private void CaptureDragDiagnosticFrame()
    {
        try
        {
            var bounds = Forms.SystemInformation.VirtualScreen;
            using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
            var path = Path.Combine(Path.GetTempPath(), "MiniFences-MainWindow-DragDiagnostic-Hover.png");
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            AppLogger.Log($"Drag diagnostic hover frame captured: {path}");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Drag diagnostic frame capture failed", ex);
        }
    }

    */
    internal void ShowDragHint(
        string target,
        ImageSource? icon = null,
        bool shellDragImageActive = false,
        System.Windows.DragDropEffects effect = System.Windows.DragDropEffects.Move)
    {
        if (_isExiting || string.IsNullOrWhiteSpace(target)) return;
        if (shellDragImageActive)
        {
            // The Shell owns both appearance and pointer cadence. Do not layer
            // MiniFences' tracked window on top of the native drag image.
            if (_dragFeedbackActive) HideDragHint();
            return;
        }
        if (_dragHintIconSource == null && icon != null) SetDragHintIcon(icon);
        _dragHintLabel = _dragSourceLabel;
        _dragTargetClearTimer.Stop();
        _dragTargetHintWindow ??= new DragTargetHintWindow();
        var targetText = effect == System.Windows.DragDropEffects.Link
            ? _loc.IsChinese ? $"创建链接到 {target}" : $"Create link in {target}"
            : effect == System.Windows.DragDropEffects.Copy
                ? _loc.IsChinese ? $"复制到 {target}" : $"Copy to {target}"
                : string.Format(_loc.T("DragMoveTo"), target);
        if (!string.Equals(_dragTargetHintWindow.TargetText, targetText, StringComparison.Ordinal))
            AppLogger.Log($"Showing drag move target: {target}");
        _dragTargetHintWindow.ShowTarget(targetText);
/* obsolete localized fallback
            _loc.IsChinese ? $"移动到 {target}" : $"Move to {target}");
*/
        ActivateDragFeedback();
    }

    internal void ShowDragHint(
        string target,
        IReadOnlyList<string> paths,
        System.Windows.IDataObject data,
        System.Windows.DragDropEffects effect = System.Windows.DragDropEffects.Move)
    {
        if (DesktopDragData.IsMiniFencesSource(data))
        {
            if (DesktopDragData.HasNativeShellImage(data))
            {
                HideDragHint();
                DesktopDragData.SetDropDescription(data, effect, target, _loc.IsChinese);
                return;
            }
            ShowDragHint(target, GetDragHintIconForPaths(paths), shellDragImageActive: false, effect);
            return;
        }

        if (DesktopDragData.HasShellDragImage(data))
        {
            HideDragHint();
            UpdateNativeShellDragImage(data);
            SetDragHintIcon(null);
            DesktopDragData.SetDropDescription(data, effect, target, _loc.IsChinese);
            return;
        }

        DesktopDragData.SetDropDescription(data, effect, target, _loc.IsChinese);
        ShowDragHint(target, GetDragHintIconForPaths(paths), shellDragImageActive: false, effect);
    }

    internal void ShowDragHint(string target, System.Windows.IDataObject data,
        System.Windows.DragDropEffects effect = System.Windows.DragDropEffects.Move)
    {
        if (DesktopDragData.IsMiniFencesSource(data))
        {
            if (DesktopDragData.HasNativeShellImage(data))
            {
                HideDragHint();
                DesktopDragData.SetDropDescription(data, effect, target, _loc.IsChinese);
                return;
            }
            ShowDragHint(target, _dragHintIconSource, shellDragImageActive: false, effect);
            return;
        }

        DesktopDragData.SetDropDescription(data, effect, target, _loc.IsChinese);
        if (DesktopDragData.HasShellDragImage(data))
        {
            HideDragHint();
            UpdateNativeShellDragImage(data);
            SetDragHintIcon(null);
            return;
        }

        ShowDragHint(target, _dragHintIconSource, shellDragImageActive: false, effect);
    }

    internal void ShowDragSourceHint(IReadOnlyList<string> paths, System.Windows.IDataObject data)
    {
        if (DesktopDragData.IsMiniFencesSource(data) && DesktopDragData.HasNativeShellImage(data))
        {
            HideDragHint();
            DesktopDragData.SetDropDescription(data, System.Windows.DragDropEffects.None, null, _loc.IsChinese);
            return;
        }
        if (!DesktopDragData.IsMiniFencesSource(data))
        {
            DesktopDragData.SetDropDescription(data, System.Windows.DragDropEffects.None, null, _loc.IsChinese);
            if (DesktopDragData.HasShellDragImage(data))
            {
                HideDragHint();
                UpdateNativeShellDragImage(data);
                return;
            }
        }
        var icon = GetDragHintIconForPaths(paths);
        if (icon != null) SetDragHintIcon(icon);
        _dragHintLabel = _dragSourceLabel;
        ClearDragTargetHint();
        ActivateDragFeedback();
    }

    internal void ClearDragTargetHint()
    {
        _dragTargetClearTimer.Stop();
        if (_dragTargetHintWindow?.TargetText == null) return;
        _dragTargetHintWindow.HideTarget();
    }

    private void ScheduleDropTargetClear()
    {
        if (_dragTargetHintWindow?.TargetText == null) return;
        _dragTargetClearTimer.Stop();
        _dragTargetClearTimer.Start();
    }

    internal static bool ShouldShowCustomDragFeedback(bool shellDragImageActive) => !shellDragImageActive;

    internal static bool ShouldUseCustomDragImage(bool miniFencesSource, bool nativeShellImageAvailable) =>
        miniFencesSource || !nativeShellImageAvailable;

    private void UpdateNativeShellDragImage(System.Windows.IDataObject data)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var cursor = Forms.Cursor.Position;
        if (!_shellDropTarget.IsActive)
            _shellDropTarget.DragEnter(handle, data, cursor, System.Windows.DragDropEffects.Move);
        _shellDropTarget.Show(true);
        _shellDropTarget.DragOver(cursor, System.Windows.DragDropEffects.Move);
    }

    internal void EndNativeShellDragImage()
    {
        _shellDropTarget.DragLeave();
        _oleDragActive = false;
    }

    internal static bool ShouldClearDragFeedback(bool escapeDown, bool leftButtonDown) =>
        escapeDown || !leftButtonDown;

    private void ActivateDragFeedback()
    {
        _dragFeedbackActive = true;
        _dragFeedbackWindow ??= new DragFeedbackWindow();
        _dragFeedbackWindow.SetIcon(_dragHintIconSource);
        _dragFeedbackWindow.SetTargetText(_dragHintLabel, _dragSourceExpanded, _dragSourceTextWidth);
        _dragFeedbackWindow.EnsureShown();
        // OLE's modal drag loop does not reliably dispatch the low-level mouse
        // hook or DispatcherTimer on every machine. DragOver is the authoritative
        // cursor cadence during DoDragDrop, so position synchronously here too.
        _dragFeedbackWindow.UpdatePosition();
        if (!_dragHintReleaseTimer.IsEnabled) _dragHintReleaseTimer.Start();
    }

    internal ImageSource? GetDragHintIconForPaths(IEnumerable<string> paths)
    {
        var path = paths.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (string.IsNullOrWhiteSpace(path)) return null;
        _dragSourceLabel ??= System.IO.Path.GetFileName(path);
        _dragHintLabel = _dragSourceLabel;
        if (string.Equals(path, _dragHintIconPath, StringComparison.OrdinalIgnoreCase))
            return _dragHintCachedPathIcon;

        try
        {
            _dragHintCachedPathIcon = FolderItemService.GetGuaranteedPathIcon(path);
            _dragHintIconPath = path;
            return _dragHintCachedPathIcon;
        }
        catch
        {
            _dragHintCachedPathIcon = CreateDragHintFallbackIcon();
            _dragHintIconPath = path;
            return _dragHintCachedPathIcon;
        }
    }

    private void UpdateDragHintPosition()
    {
        _dragFeedbackWindow?.UpdatePosition();
        _dragTargetHintWindow?.UpdatePosition();
    }

    internal const int DragHintRefreshIntervalMilliseconds = 16;

    internal void HideDragHint()
    {
        _dragTargetClearTimer.Stop();
        if (_dragFeedbackPinnedUntilClear)
        {
            // Routed DragLeave fires while crossing child template boundaries.
            // During a MiniFences-owned drag it must not touch either feedback
            // HWND. A real folder exit is handled separately by
            // ClearDragTargetHint and its short debounce timer.
            return;
        }
        _dragFeedbackActive = false;
        _dragFeedbackWindow?.StopRealtimeTracking();
        _dragFeedbackWindow?.Hide();
        _dragTargetHintWindow?.HideTarget();
        _dragHintReleaseTimer.Stop();
    }

    internal static bool ShouldEndShellDragForScreenPoint(bool pointerInsideMiniFences) =>
        !pointerInsideMiniFences;

    internal static bool ShouldIgnoreIntermediateDragHide(bool pinnedUntilClear) =>
        pinnedUntilClear;

    internal void ClearDragHint()
    {
        ClearAllFenceDragHighlights(Workspace.Children.OfType<FenceControl>());
        _dragFeedbackPinnedUntilClear = false;
        HideDragHint();
        SetDragHintIcon(null);
        _dragHintIconPath = null;
        _dragHintCachedPathIcon = null;
        _dragHintLabel = null;
        _dragSourceLabel = null;
        _dragSourceExpanded = false;
        _dragSourceTextWidth = DragFeedbackWindow.LabelWidthDips;
        _dragFeedbackWindow?.SetTargetText(null);
        _dragTargetHintWindow?.HideTarget();
    }

    internal static void ClearAllFenceDragHighlights(IEnumerable<FenceControl> fences)
    {
        foreach (var fence in fences) fence.ClearDragHighlight();
    }

    private bool HasAnyFenceDragHighlight() =>
        Workspace.Children.OfType<FenceControl>().Any(fence => fence.IsDragHighlightedForTesting);

    private static ImageSource CreateDragHintFallbackIcon()
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                SystemIcons.Application.Handle,
                new Int32Rect(0, 0, 0, 0),
                System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch
        {
            return new DrawingImage();
        }
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
        var hasPaths = DesktopDragData.TryGetPaths(e.Data, out var paths);
        var requestedEffect = hasPaths
            ? DesktopDragData.GetRequestedDropEffect(e.Data, e.KeyStates, e.AllowedEffects)
            : System.Windows.DragDropEffects.None;
        e.Effects = hasPaths
            ? requestedEffect
            : System.Windows.DragDropEffects.None;
        if (hasPaths)
            ShowDragSourceHint(paths, e.Data); /* old desktop target label disabled
                _loc.IsChinese ? "桌面" : "Desktop",
                paths,
                e.Data); */
        else
            HideDragHint();
        e.Handled = true;
    }

    private async void Workspace_Drop(object sender, System.Windows.DragEventArgs e)
    {
        HideDragHint();
        if (DesktopDragData.TryGetPaths(e.Data, out var paths))
        {
            var dropPoint = e.GetPosition(Workspace);
            var requestedEffect = DesktopDragData.GetRequestedDropEffect(e.Data, e.KeyStates, e.AllowedEffects);
            var alreadyOnDesktop = paths.Where(path =>
                FolderItemService.IsShellNamespacePath(path) || FenceControl.IsDirectChildOfDesktopRoot(path)).ToArray();
            var externalPaths = paths.Where(path =>
                !FolderItemService.IsShellNamespacePath(path) && !FenceControl.IsDirectChildOfDesktopRoot(path)).ToArray();
            e.Effects = requestedEffect;
            e.Handled = true;

            FolderMoveResult? transfer = null;
            if (externalPaths.Length > 0)
            {
                var desktopRoot = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var operation = FolderItemService.GetTransferOperation(requestedEffect);
                transfer = operation is not null
                    ? await _actionHistoryService.ExecuteFileTransferAsync(
                        $"{FolderItemService.GetOperationDisplayName(operation.Value)}到桌面",
                        _folderItemService, externalPaths, desktopRoot, operation.Value)
                    : new FolderMoveResult(0, externalPaths.Length,
                        ["The drag source did not request a supported operation."], []);
            }

            var positionedPaths = alreadyOnDesktop
                .Concat(transfer?.MovedPaths ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (positionedPaths.Length == 0)
            {
                e.Effects = System.Windows.DragDropEffects.None;
                if (transfer is { Errors.Count: > 0 })
                    System.Windows.MessageBox.Show(string.Join(Environment.NewLine, transfer.Errors.Take(8)),
                        "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UpdateDesktopIconOrder(positionedPaths, dropPoint);
            if (!DesktopDragData.IsLooseIconDrag(e.Data))
                ReleaseDesktopMembership(positionedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
            else
            {
                RenderFences();
                SaveConfigWithWarning();
            }
            e.Effects = requestedEffect;
            AppLogger.Log($"Workspace positioned {positionedPaths.Length} desktop item(s). Effect={requestedEffect}; External={externalPaths.Length}");
            if (transfer is { Errors.Count: > 0 })
                System.Windows.MessageBox.Show(string.Join(Environment.NewLine, transfer.Errors.Take(8)),
                    "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Workspace_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (!IsMiniFencesWindowAtScreenPoint(System.Windows.Forms.Cursor.Position))
        {
            EndNativeShellDragImage();
            HideDragHint();
        }
    }

    private void UpdateDesktopIconOrder(IReadOnlyList<string> paths, System.Windows.Point dropPoint)
    {
        var orderedPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = GetDesktopIconGridRows();
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
            _nativeDesktopIconsHiddenByMiniFences = true;
            StartDesktopIconWatchdog();
            AppLogger.Log("Explorer desktop icons hidden while MiniFences renders grouped icons.");
        }
    }

    private void StartDesktopIconWatchdog()
    {
        if (_desktopIconWatchdogStarted || string.IsNullOrWhiteSpace(Environment.ProcessPath)) return;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--desktop-icon-watchdog");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (Process.Start(startInfo) != null)
            {
                _desktopIconWatchdogStarted = true;
                AppLogger.Log("Desktop icon recovery watchdog started.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not start the desktop icon recovery watchdog", ex);
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
        var fences = Workspace.Children.OfType<FenceControl>().ToArray();
        AppLogger.Log($"Refresh all requested for {fences.Length} Fence(s).");
        foreach (var fence in fences)
        {
            fence.LoadFolderItems();
        }
        AppLogger.Log($"Refresh all completed for {fences.Length} Fence(s).");
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
        RefreshUndoCommands();
        DesktopSwapMonitorsMenuItem.IsEnabled = Forms.Screen.AllScreens.Length > 1;
        UpdateMenuState();
    }

    private void SwapMonitorContentsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var screens = Forms.Screen.AllScreens.OrderBy(screen => screen.Bounds.Left).ThenBy(screen => screen.Bounds.Top).ToArray();
        if (screens.Length < 2) return;
        SyncAllFenceLayouts();
        var minLeft = screens.Min(screen => screen.Bounds.Left);
        var minTop = screens.Min(screen => screen.Bounds.Top);
        var physicalWidth = screens.Max(screen => screen.Bounds.Right) - minLeft;
        var physicalHeight = screens.Max(screen => screen.Bounds.Bottom) - minTop;
        var scaleX = Width / Math.Max(1, physicalWidth);
        var scaleY = Height / Math.Max(1, physicalHeight);
        var bounds = screens.Select(screen => new System.Windows.Rect(
            (screen.Bounds.Left - minLeft) * scaleX,
            (screen.Bounds.Top - minTop) * scaleY,
            screen.Bounds.Width * scaleX,
            screen.Bounds.Height * scaleY)).ToArray();
        var before = _configService.CaptureLayout(_config);
        var moved = DisplayLayoutService.CycleMonitorContents(_config, bounds);
        if (moved == 0) return;
        RenderFences();
        SaveConfigWithWarning();
        _actionHistoryService.RecordLayoutChange("交换显示器内容", before, _configService.CaptureLayout(_config));
        RefreshUndoCommands();
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
        var beforeLayout = _configService.CaptureLayout(_config);
        try { _configService.SaveSnapshot(_config); }
        catch (Exception ex) { AppLogger.LogException("Failed to save automatic pre-restore layout snapshot", ex); }
        _config = _configService.ApplyLayout(_config, layout, out var invalidPathCount);
        _fencesHidden = _config.FencesHidden;
        RenderFences();
        ConfigureAutoOrganizerWatcher();
        SaveConfigWithWarning();
        _actionHistoryService.RecordLayoutRestore(beforeLayout, _configService.CaptureLayout(_config));
        RefreshUndoCommands();
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
        if (_fencesTopmost && ReferenceEquals(e.OriginalSource, Workspace))
        {
            SetFencesTopmost(false, ensureVisible: true);
            e.Handled = true;
            return;
        }
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

        var releasedItemCount = control.Config.AssignedPaths.Count;
        var deletedFenceTitle = control.Config.Title;
        if (!_actionHistoryService.ExecuteFenceDeletion(_config, control.Config)) return;
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Fence deleted: {deletedFenceTitle}; released {releasedItemCount} desktop item(s).");
        RefreshUndoCommands();
    }

    private void MoveFenceToPage(FenceControl control, int pageIndex)
    {
        var targetPage = Math.Max(0, pageIndex);
        var moveTargets = GetFencePageMoveTargets(_config, control.Config);
        if (moveTargets.All(fence => fence.PageIndex == targetPage))
        {
            return;
        }

        SyncAllFenceLayouts();
        _config.PageCount = Math.Max(_config.PageCount, targetPage + 1);
        foreach (var fence in moveTargets) fence.PageIndex = targetPage;
        _config.CurrentPage = targetPage;
        AppLogger.Log($"Moved Fence group containing '{control.Config.Title}' ({moveTargets.Count} Fence(s)) to page {targetPage + 1}.");
        RenderFences();
        SaveConfigWithWarning();
    }

    internal static IReadOnlyList<FenceConfig> GetFencePageMoveTargets(AppConfig config, FenceConfig fence)
    {
        if (string.IsNullOrWhiteSpace(fence.TabGroupId)) return [fence];
        var group = config.Fences
            .Where(candidate => string.Equals(candidate.TabGroupId, fence.TabGroupId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return group.Length == 0 ? [fence] : group;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        FolderItemService.CancelActiveTransfers();
        FolderItemService.TransferProgressChanged -= FolderItemService_TransferProgressChanged;
        FolderItemService.TransferConfirmationRequested -= FolderItemService_TransferConfirmationRequested;
        FolderItemService.TransferCompleted -= FolderItemService_TransferCompleted;
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
            // A close request means the desktop host is going away. Hiding this
            // window would also hide every MiniFences-rendered icon while leaving
            // Explorer's native icon view hidden.
            _isExiting = true;
        }

        _settingsWindow?.Close();
        _saveTimer.Stop();
        _autoOrganizeTimer.Stop();
        _desktopIconStateTimer.Stop();
        _desktopContentsRefreshTimer.Stop();
        _desktopCompatibilityTimer.Stop();
        _dragPageSwitchTimer.Stop();
        _dragRegionRefreshTimer.Stop();
        _dragHintReleaseTimer.Stop();
        _shellDragLeaveTimer.Stop();
        _dragTargetClearTimer.Stop();
        _hotkeyPollTimer.Stop();
        _shellDropTarget.Dispose();
        _dragFeedbackWindow?.Close();
        _dragFeedbackWindow = null;
        _dragTargetHintWindow?.Close();
        _dragTargetHintWindow = null;
        _looseIconLoadCancellation?.Cancel();
        _looseIconLoadCancellation?.Dispose();
        _looseIconLoadCancellation = null;
        RestoreNativeDesktopIconsOnExit();
        StopAutoOrganizerWatcher();
        StopDesktopContentsWatcher();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        UninstallDesktopDoubleClickHook();
        UninstallForegroundWindowHook();
        UnregisterGlobalHotkeys();
        System.Windows.Application.Current.SessionEnding -= MainWindow_SessionEnding;
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        base.OnClosing(e);
    }

    private void FolderItemService_TransferProgressChanged(object? sender, FolderTransferProgress progress)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var finished = progress.Stage is "Completed" or "CompletedWithErrors" or "Canceled" or "Failed";
            if (progress.Stage == "Queued" || !_transferStartedAt.ContainsKey(progress.TransferId))
                _transferStartedAt[progress.TransferId] = DateTime.UtcNow;
            if (finished)
            {
                _transferStartedAt.Remove(progress.TransferId);
                TransferProgressBorder.Visibility = Visibility.Collapsed;
                return;
            }
            TransferProgressBorder.Visibility = Visibility.Visible;
            _lastTransferCompleted = null;
            CancelTransferButton.Content = string.Equals(_loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase)
                ? "安全取消" : "Cancel safely";
            OpenTransferTargetButton.Visibility = Visibility.Collapsed;
            var chinese = string.Equals(_loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
            TransferProgressTitle.Text = chinese
                ? $"正在{FolderItemService.GetOperationDisplayName(progress.Operation)}文件…"
                : $"{FolderItemService.GetOperationDisplayName(progress.Operation, false)} files…";
            if (progress.TotalBytes > 0)
            {
                var elapsed = Math.Max(0.1, (DateTime.UtcNow - _transferStartedAt[progress.TransferId]).TotalSeconds);
                var bytesPerSecond = progress.CompletedBytes / elapsed;
                var remainingSeconds = bytesPerSecond > 0
                    ? Math.Max(0, progress.TotalBytes - progress.CompletedBytes) / bytesPerSecond
                    : 0;
                var eta = remainingSeconds > 0 ? $" · 剩余约 {TimeSpan.FromSeconds(remainingSeconds):mm\\:ss}" : "";
                TransferProgressDetail.Text =
                    $"{Path.GetFileName(progress.CurrentPath)} · {FormatBytes(progress.CompletedBytes)}/{FormatBytes(progress.TotalBytes)} · {FormatBytes((long)bytesPerSecond)}/s{eta}";
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Maximum = Math.Max(1, progress.TotalBytes);
                TransferProgressBar.Value = Math.Clamp(progress.CompletedBytes, 0, Math.Max(1, progress.TotalBytes));
            }
            else
            {
                TransferProgressDetail.Text = progress.CurrentPath is null
                    ? progress.Stage == "Queued" ? "正在等待其他文件操作完成" : "正在准备安全传输"
                    : $"{progress.CompletedItems}/{progress.TotalItems}  {Path.GetFileName(progress.CurrentPath)}";
                TransferProgressBar.IsIndeterminate = progress.TotalItems <= 0;
                TransferProgressBar.Maximum = Math.Max(1, progress.TotalItems);
                TransferProgressBar.Value = Math.Clamp(progress.CompletedItems, 0, Math.Max(1, progress.TotalItems));
            }
            CancelTransferButton.IsEnabled = progress.CanCancel;
        });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024 * 1024):0.##} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / (1024d * 1024):0.##} MB";
        if (bytes >= 1024) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes} B";
    }

    private async void CancelTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastTransferCompleted is { } completed)
        {
            if (completed.Result.Errors.Count == 0)
            {
                TransferProgressBorder.Visibility = Visibility.Collapsed;
                _lastTransferCompleted = null;
                return;
            }
            var completedSources = completed.Result.Moves?.Select(move => move.SourcePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            var failedSources = completed.SourcePaths.Where(path => !completedSources.Contains(path)).ToArray();
            if (failedSources.Length == 0) return;
            CancelTransferButton.IsEnabled = false;
            TransferProgressDetail.Text = "正在重试失败项目…";
            await _actionHistoryService.ExecuteFileTransferAsync("重试失败的文件操作",
                _folderItemService, failedSources, completed.DestinationFolder, completed.Operation);
            return;
        }
        CancelTransferButton.IsEnabled = false;
        TransferProgressDetail.Text = "正在安全取消并恢复源文件…";
        FolderItemService.CancelActiveTransfers();
    }

    private void FolderItemService_TransferCompleted(object? sender, FolderTransferCompletedEventArgs completed)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lastTransferCompleted = completed;
            var chinese = string.Equals(_loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
            TransferProgressBorder.Visibility = Visibility.Visible;
            TransferProgressTitle.Text = completed.WasCanceled
                ? chinese ? "文件操作已安全取消" : "File operation canceled safely"
                : completed.Result.Errors.Count == 0
                ? chinese ? "文件操作已完成" : "File operation completed"
                : chinese ? "文件操作部分失败" : "File operation partially failed";
            TransferProgressDetail.Text = completed.WasCanceled
                ? chinese ? $"取消前已完成 {completed.Result.Moved} 项；源文件已保留或恢复" :
                    $"Completed {completed.Result.Moved} item(s) before cancellation; sources were preserved or restored"
                : completed.Result.Errors.Count == 0
                ? chinese ? $"已完成 {completed.Result.Moved} 项" : $"Completed {completed.Result.Moved} item(s)"
                : chinese ? $"完成 {completed.Result.Moved} 项，失败 {completed.Result.Errors.Count} 项" :
                    $"Completed {completed.Result.Moved}; failed {completed.Result.Errors.Count}";
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressBar.Maximum = 1;
            TransferProgressBar.Value = 1;
            CancelTransferButton.Content = completed.Result.Errors.Count == 0
                ? chinese ? "关闭" : "Close"
                : chinese ? "重试失败项" : "Retry failed";
            CancelTransferButton.IsEnabled = true;
            OpenTransferTargetButton.Content = chinese ? "打开目标" : "Open destination";
            OpenTransferTargetButton.Visibility = Visibility.Visible;
        });
    }

    private void OpenTransferTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastTransferCompleted is not { } completed || !Directory.Exists(completed.DestinationFolder)) return;
        Process.Start(new ProcessStartInfo(completed.DestinationFolder) { UseShellExecute = true });
    }

    private void FolderItemService_TransferConfirmationRequested(
        object? sender, FolderTransferConfirmationEventArgs request)
    {
        void Confirm()
        {
            var chinese = string.Equals(_loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
            var sizeText = request.TotalBytes >= 1024L * 1024 * 1024
                ? $"{request.TotalBytes / (1024d * 1024 * 1024):0.##} GB"
                : $"{request.TotalBytes / (1024d * 1024):0.##} MB";
            var risk = chinese
                ? request.CrossVolume ? "这是跨磁盘移动。完成后，原位置将不再保留这些项目。" : "这是大批量移动。完成后，原位置将不再保留这些项目。"
                : request.CrossVolume ? "This is a cross-drive move. The items will no longer remain at the source after completion." : "This is a large move. The items will no longer remain at the source after completion.";
            var message = chinese
                ? $"{risk}\n\n项目：{request.SourcePaths.Count} 个顶层项目，约 {request.FileCount} 个文件（{sizeText}）\n目标：{request.DestinationFolder}\n\nMiniFences 会先验证目标副本，确认完整后才移除源数据。是否继续？"
                : $"{risk}\n\nItems: {request.SourcePaths.Count} top-level, about {request.FileCount} files ({sizeText})\nDestination: {request.DestinationFolder}\n\nMiniFences verifies the destination before removing source data. Continue?";
            request.Approved = System.Windows.MessageBox.Show(this, message, chinese ? "确认移动" : "Confirm move",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
        if (Dispatcher.CheckAccess()) Confirm();
        else Dispatcher.Invoke(Confirm);
    }

    internal void RestoreNativeDesktopIconsOnExit()
    {
        if (_desktopIconRestoreCompleted) return;
        if (!_nativeDesktopIconsHiddenByMiniFences &&
            !_restoreNativeDesktopIconsOnExit &&
            !_config.EnableDesktopIconIntegration)
        {
            return;
        }

        if (_desktopIconLayoutService.SetVisible(true))
        {
            _nativeDesktopIconsHiddenByMiniFences = false;
            _windowsDesktopIconsVisible = true;
            _desktopIconRestoreCompleted = true;
            AppLogger.Log("Explorer desktop icons restored on exit.");
        }
        else
        {
            AppLogger.Log("Explorer desktop icon restoration was requested on exit but the icon view was not available.");
        }
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
            if (_useTopLevelDesktopCompatibilityMode)
                ScheduleTopLevelDesktopRecovery("WPF minimized state");
            else
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
            ClampFenceToUsableWorkArea(fence);
        }

        UpdateDesktopWindowRegion();
    }

    private void ClampFenceToUsableWorkArea(FenceControl fence, bool preferPointerMonitor = false)
    {
        try
        {
            var left = Canvas.GetLeft(fence);
            var top = Canvas.GetTop(fence);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            var width = fence.ActualWidth > 0 ? fence.ActualWidth : fence.Width;
            var height = fence.ActualHeight > 0 ? fence.ActualHeight : fence.Height;

            System.Drawing.Point monitorPoint;
            if (preferPointerMonitor)
            {
                monitorPoint = Forms.Cursor.Position;
            }
            else
            {
                var center = Workspace.PointToScreen(new System.Windows.Point(left + width / 2, top + Math.Min(height / 2, 34)));
                monitorPoint = new System.Drawing.Point((int)Math.Round(center.X), (int)Math.Round(center.Y));
            }

            var usableArea = GetWorkspaceWorkArea(monitorPoint);
            var clamped = ClampFencePositionToWorkArea(
                new System.Windows.Point(left, top),
                new System.Windows.Size(width, height),
                usableArea);
            Canvas.SetLeft(fence, clamped.X);
            Canvas.SetTop(fence, clamped.Y);
            fence.SyncConfigFromLayout();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to clamp Fence to the usable monitor work area", ex);
            fence.ClampToParentBounds();
        }
    }

    private Rect GetWorkspaceWorkArea(System.Drawing.Point monitorPoint)
    {
        var workingArea = Forms.Screen.FromPoint(monitorPoint).WorkingArea;
        var topLeft = Workspace.PointFromScreen(new System.Windows.Point(workingArea.Left, workingArea.Top));
        var bottomRight = Workspace.PointFromScreen(new System.Windows.Point(workingArea.Right, workingArea.Bottom));
        return new Rect(
            Math.Min(topLeft.X, bottomRight.X),
            Math.Min(topLeft.Y, bottomRight.Y),
            Math.Abs(bottomRight.X - topLeft.X),
            Math.Abs(bottomRight.Y - topLeft.Y));
    }

    private int GetDesktopIconGridRows()
    {
        var workspaceHeight = ActualHeight > 0 ? ActualHeight : Height;
        if (workspaceHeight <= 0) return 1;
        try
        {
            var primary = Forms.Screen.PrimaryScreen;
            var monitorPoint = primary == null
                ? Forms.Cursor.Position
                : new System.Drawing.Point(
                    primary.Bounds.Left + primary.Bounds.Width / 2,
                    primary.Bounds.Top + primary.Bounds.Height / 2);
            var usableArea = GetWorkspaceWorkArea(monitorPoint);
            var top = Math.Max(8, usableArea.Top + 8);
            var bottom = Math.Min(workspaceHeight, usableArea.Bottom);
            const double iconHeight = 92;
            const double cellHeight = 96;
            var available = bottom - top - iconHeight;
            return Math.Max(1, (int)Math.Floor(Math.Max(0, available) / cellHeight) + 1);
        }
        catch
        {
            return Math.Max(1, (int)Math.Floor(workspaceHeight / 96));
        }
    }

    internal static System.Windows.Point ClampFencePositionToWorkArea(
        System.Windows.Point requested,
        System.Windows.Size fenceSize,
        System.Windows.Rect usableArea)
    {
        if (usableArea.Width <= 0 || usableArea.Height <= 0) return requested;
        var maximumLeft = Math.Max(usableArea.Left, usableArea.Right - fenceSize.Width);
        var maximumTop = Math.Max(usableArea.Top, usableArea.Bottom - fenceSize.Height);
        return new System.Windows.Point(
            Math.Clamp(requested.X, usableArea.Left, maximumLeft),
            Math.Clamp(requested.Y, usableArea.Top, maximumTop));
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
        var visibleHeight = GetFenceLayoutHeight(fence);
        fence.Left = Math.Clamp(fence.Left, 0, Math.Max(0, workspaceWidth - fence.Width));
        fence.Top = Math.Clamp(fence.Top, 0, Math.Max(0, workspaceHeight - visibleHeight));
        return Math.Abs(fence.Left - originalLeft) > 0.01 ||
               Math.Abs(fence.Top - originalTop) > 0.01 ||
               Math.Abs(fence.Width - originalWidth) > 0.01 ||
               Math.Abs(fence.Height - originalHeight) > 0.01;
    }

    internal static double GetFenceLayoutHeight(FenceConfig fence) =>
        fence.IsCollapsed ? FenceControl.CollapsedHeight : fence.Height;

    private void SaveConfigWithWarning()
    {
        _saveTimer.Stop();
        SyncAllFenceLayouts();
        try
        {
            _configService.Save(_config);
            if (!string.IsNullOrWhiteSpace(_displayTopologyKey))
                _displayLayoutService.SaveProfile(_displayTopologyKey, _config,
                    Workspace.ActualWidth > 0 ? Workspace.ActualWidth : Width,
                    Workspace.ActualHeight > 0 ? Workspace.ActualHeight : Height);
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
        _checkForUpdatesMenuItem = new Forms.ToolStripMenuItem("Check for updates");
        _checkForUpdatesMenuItem.Click += async (_, _) => await CheckForUpdatesAsync(interactive: true);
        menu.Items.Add(_checkForUpdatesMenuItem);
        _toggleFencesMenuItem = new Forms.ToolStripMenuItem("Hide Fences");
        _toggleFencesMenuItem.Click += (_, _) => ToggleFencesVisibility();
        menu.Items.Add(_toggleFencesMenuItem);
        _undoMenuItem = new Forms.ToolStripMenuItem("Undo Last Action");
        _undoMenuItem.Click += (_, _) => Dispatcher.Invoke(() => UndoLastAction());
        menu.Items.Add(_undoMenuItem);
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

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_updateCheckInProgress || _isExiting) return;
        _updateCheckInProgress = true;
        if (_checkForUpdatesMenuItem is not null) _checkForUpdatesMenuItem.Enabled = false;
        try
        {
            var result = await _updateService.CheckForUpdateAsync(forceRefresh: interactive);
            if (!result.IsAvailable || result.Update is null)
            {
                if (interactive)
                {
                    var message = result.Error is null
                        ? (_loc.IsChinese ? "当前已经是最新版本。" : "MiniFences is already up to date.")
                        : (_loc.IsChinese ? "暂时无法检查更新，请稍后重试。" : "Updates could not be checked right now. Please try again later.");
                    System.Windows.MessageBox.Show(this, message, "MiniFences",
                        MessageBoxButton.OK,
                        result.Error is null ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                return;
            }

            var update = result.Update;
            var notes = string.IsNullOrWhiteSpace(update.ReleaseNotes)
                ? ""
                : $"\n\n{update.ReleaseNotes.Trim()[..Math.Min(1000, update.ReleaseNotes.Trim().Length)]}";
            var prompt = _loc.IsChinese
                ? $"发现 MiniFences 新版本 {update.Version}。\n\n现在下载并自动更新吗？{notes}"
                : $"MiniFences {update.Version} is available.\n\nDownload and install it now?{notes}";
            var answer = System.Windows.MessageBox.Show(this, prompt, "MiniFences",
                MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return;

            _trayIcon.BalloonTipTitle = "MiniFences";
            _trayIcon.BalloonTipText = _loc.IsChinese ? "正在下载更新，请稍候。" : "Downloading the update...";
            _trayIcon.ShowBalloonTip(3000);
            var archive = await _updateService.DownloadAndVerifyAsync(update);
            var launched = UpdateInstaller.Launch(
                archive,
                AppContext.BaseDirectory,
                Environment.ProcessId,
                restartInBackground: !_openSettingsOnLoad);
            if (!launched)
            {
                System.Windows.MessageBox.Show(this,
                    _loc.IsChinese ? "更新程序启动失败，当前版本未改变。" : "The updater could not be started. The current version was not changed.",
                    "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isExiting = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("MiniFences update download failed", ex);
            if (interactive)
            {
                System.Windows.MessageBox.Show(this,
                    _loc.IsChinese ? $"更新失败，当前版本未改变。\n\n{ex.Message}" : $"The update failed and the current version was not changed.\n\n{ex.Message}",
                    "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _updateCheckInProgress = false;
            if (_checkForUpdatesMenuItem is not null) _checkForUpdatesMenuItem.Enabled = true;
        }
    }

    private void UndoLastActionMenuItem_Click(object sender, RoutedEventArgs e) => UndoLastAction();

    private void UndoLastAction() => UndoLastAction(this);

    private void UndoLastAction(Window owner)
    {
        var transaction = _actionHistoryService.GetNextUndo();
        if (transaction is null) return;
        var result = _actionHistoryService.Undo(transaction.Id, _config, _configService);
        if (result.RestoredCount > 0)
        {
            RenderFences();
            ConfigureAutoOrganizerWatcher();
            SaveConfigWithWarning();
        }
        if (result.Errors.Count > 0)
            System.Windows.MessageBox.Show(owner, string.Join(Environment.NewLine, result.Errors),
                "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshUndoCommands();
        _settingsWindow?.RefreshFromMainWindow();
    }

    private void RefreshUndoCommands()
    {
        var transaction = _actionHistoryService.GetNextUndo();
        var header = _loc.T("UndoLastAction");
        if (DesktopUndoMenuItem is not null)
        {
            DesktopUndoMenuItem.Header = header;
            DesktopUndoMenuItem.IsEnabled = transaction is not null;
        }
        if (_undoMenuItem is not null)
        {
            _undoMenuItem.Text = header;
            _undoMenuItem.Enabled = transaction is not null;
        }
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

    internal void CreateFenceFromDesktopContext()
    {
        Dispatcher.BeginInvoke(() =>
        {
            EnsureDesktopWindowVisible();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var fence = ConfigService.CreateNewFence(_config.Fences.Count, desktop);
            fence.Kind = FenceConfig.DesktopGroupKind;
            fence.Title = $"{_loc.T("NewFence")} {_config.Fences.Count + 1}";
            fence.AssignedPaths = [];
            fence.PageIndex = _config.CurrentPage;
            var cursor = Forms.Cursor.Position;
            try
            {
                var point = Workspace.PointFromScreen(new System.Windows.Point(cursor.X, cursor.Y));
                fence.Left = Math.Max(0, point.X);
                fence.Top = Math.Max(0, point.Y);
            }
            catch { PlaceFenceOnCurrentPage(fence); }
            ClampFenceConfigToWorkspace(fence);
            _config.Fences.Add(fence);
            RenderFences();
            SaveConfigWithWarning();
        }, DispatcherPriority.Background);
    }

    internal void RequestExitFromAnotherInstance()
    {
        ExitApplication();
    }

    private void ShowSettingsWindow()
    {
        Dispatcher.Invoke(() =>
        {
            RecordDesktopShortcutMouseContext(false, "settings opened");
            // Independent rename editors are short-lived topmost HWNDs so they
            // can receive input above the Explorer-hosted desktop. They must
            // be dismissed before opening Settings or they protrude through
            // that normal application window. Cancel rather than commit so
            // opening Settings can never rename a file accidentally.
            CancelActiveRenamesForSettings();
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

    internal void CancelActiveRenamesForSettings()
    {
        foreach (var icon in Workspace.Children.OfType<DesktopLooseIconControl>())
            icon.CancelActiveRenameForSettings();
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
            fence.CancelActiveRenameForSettings();
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
        var workArea = new System.Windows.Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        Width = workArea.Width;
        Height = workArea.Height;
        if (_isDesktopHosted)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (_desktopHostHandle != IntPtr.Zero &&
                GetClientRect(_desktopHostHandle, out var clientRect))
            {
                var physicalWidth = Math.Max(0, clientRect.Right - clientRect.Left);
                var physicalHeight = Math.Max(0, clientRect.Bottom - clientRect.Top);
                if (physicalWidth > 0 && physicalHeight > 0)
                {
                    var dpi = VisualTreeHelper.GetDpi(this);
                    var logicalSize = GetHostedLogicalSize(
                        physicalWidth,
                        physicalHeight,
                        dpi.DpiScaleX,
                        dpi.DpiScaleY);
                    Width = logicalSize.Width;
                    Height = logicalSize.Height;
                    SetWindowPos(
                        handle,
                        HwndTop,
                        clientRect.Left,
                        clientRect.Top,
                        physicalWidth,
                        physicalHeight,
                        SwpNoActivate);
                    AppLogger.Log(
                        $"Applied Explorer desktop client bounds: physical={clientRect.Left},{clientRect.Top},{physicalWidth},{physicalHeight}; " +
                        $"dpi={dpi.DpiScaleX:0.###},{dpi.DpiScaleY:0.###}; logical={Width:0.###},{Height:0.###}");
                    return;
                }
            }

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

    internal static System.Windows.Size GetHostedLogicalSize(
        int physicalWidth,
        int physicalHeight,
        double dpiScaleX,
        double dpiScaleY)
    {
        var safeScaleX = double.IsFinite(dpiScaleX) && dpiScaleX > 0 ? dpiScaleX : 1;
        var safeScaleY = double.IsFinite(dpiScaleY) && dpiScaleY > 0 ? dpiScaleY : 1;
        return new System.Windows.Size(
            Math.Max(0, physicalWidth) / safeScaleX,
            Math.Max(0, physicalHeight) / safeScaleY);
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        ReapplyDesktopWorkAreaBounds("display settings changed");
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            ReapplyDesktopWorkAreaBounds("system resumed from sleep");
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
        var changed = false;
        if (_config.EnableDesktopIconIntegration)
        {
            // A false value is expected after MiniFences hides Explorer's icons. Do not
            // interpret our own action as a request to hide every MiniFences surface.
            if (visible)
            {
                _windowsDesktopIconsVisible = true;
                UpdateNativeDesktopIconVisibility();
            }
        }
        else
        {
            changed = visible != _windowsDesktopIconsVisible;
            _windowsDesktopIconsVisible = visible;
        }
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
            var oldWidth = Workspace.ActualWidth > 0 ? Workspace.ActualWidth : Width;
            var oldHeight = Workspace.ActualHeight > 0 ? Workspace.ActualHeight : Height;
            var topologyKey = DisplayLayoutService.CreateTopologyKey(DisplayLayoutService.GetCurrentDisplays());
            var virtualWidth = SystemParameters.VirtualScreenWidth;
            var virtualHeight = SystemParameters.VirtualScreenHeight;
            if (string.Equals(topologyKey, _displayTopologyKey, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(oldWidth - virtualWidth) < 0.5 &&
                Math.Abs(oldHeight - virtualHeight) < 0.5)
            {
                EnsureDesktopHostAttachment();
                ApplyDesktopWorkAreaBounds();
                ClampAllFencesToWorkspace();
                SendBehindNormalWindows();
                AppLogger.Log("Display topology is unchanged; preserved existing Fence controls and positions.");
                return;
            }
            if (!string.IsNullOrWhiteSpace(_displayTopologyKey))
                _displayLayoutService.SaveProfile(_displayTopologyKey, _config, oldWidth, oldHeight);
            EnsureDesktopHostAttachment();
            ApplyDesktopWorkAreaBounds();
            if (!_displayLayoutService.TryRestoreProfile(topologyKey, _config, Width, Height))
                DisplayLayoutService.RemapToWorkspace(_config, oldWidth, oldHeight, Width, Height);
            _displayTopologyKey = topologyKey;
            RenderFences();
            ScheduleConfigSave();
            SendBehindNormalWindows();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void SendBehindNormalWindows()
    {
        SendBehindNormalWindows(force: false);
    }

    private void SendBehindNormalWindows(bool force)
    {
        if (_fencesTopmost || (_useTopLevelDesktopCompatibilityMode && _showDesktopModeActive)) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            if (_isDesktopHosted && GetParent(handle) == _desktopHostHandle)
            {
                SetDesktopWindowPos(handle, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                return;
            }

            var desktopHost = FindDesktopViewHost();
            if (desktopHost == IntPtr.Zero)
            {
                SetDesktopWindowPos(handle, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                AppLogger.Log("Desktop host was not found; MiniFences was placed at the bottom for input safety.");
                return;
            }

            var windowDirectlyAboveDesktop = GetWindow(desktopHost, GwHwndPrev);
            if (IsDesktopLayerPlacementStable(handle, windowDirectlyAboveDesktop))
            {
                return;
            }

            var insertAfter = windowDirectlyAboveDesktop == IntPtr.Zero
                ? HwndTop
                : windowDirectlyAboveDesktop;
            SetDesktopWindowPos(
                handle,
                insertAfter,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
            AppLogger.Log("MiniFences positioned directly above the Explorer desktop layer and below normal windows.");
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to send MiniFences behind normal windows", ex);
        }
    }

    private bool SetDesktopWindowPos(
        IntPtr handle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags)
    {
        var previous = _desktopZOrderUpdateInProgress;
        _desktopZOrderUpdateInProgress = true;
        try
        {
            return SetWindowPos(handle, insertAfter, x, y, width, height, flags);
        }
        finally
        {
            _desktopZOrderUpdateInProgress = previous;
        }
    }

    internal static bool IsDesktopLayerPlacementStable(
        IntPtr miniFencesWindow,
        IntPtr windowDirectlyAboveDesktop) =>
        miniFencesWindow != IntPtr.Zero && windowDirectlyAboveDesktop == miniFencesWindow;

    private static bool IsWindowAboveTarget(IntPtr window, IntPtr target)
    {
        if (window == IntPtr.Zero || target == IntPtr.Zero || window == target) return false;
        var current = window;
        for (var index = 0; index < 4096; index++)
        {
            current = GetWindow(current, GwHwndNext);
            if (current == IntPtr.Zero) return false;
            if (current == target) return true;
        }
        return false;
    }

    private bool AttachToDesktopHost()
    {
        if (_useTopLevelDesktopCompatibilityMode)
        {
            return false;
        }

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

    private bool AttachToTopLevelDesktopOwner()
    {
        if (!_useTopLevelDesktopCompatibilityMode || _fencesTopmost || _isExiting) return false;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var desktopHost = FindDesktopViewHost();
            if (handle == IntPtr.Zero || desktopHost == IntPtr.Zero) return false;
            var currentOwner = GetWindow(handle, GwOwner);
            if (currentOwner == desktopHost)
            {
                _desktopHostHandle = desktopHost;
                return true;
            }

            var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
            var popupStyle = (style & ~WsChild) | WsPopup;
            if (popupStyle != style)
                SetWindowLongPtr(handle, GwlStyle, new IntPtr(popupStyle));
            SetWindowLongPtr(handle, GwlHwndParent, desktopHost);
            var attached = GetWindow(handle, GwOwner) == desktopHost;
            if (attached)
            {
                _desktopHostHandle = desktopHost;
                AppLogger.Log("Attached top-level MiniFences window ownership to the Explorer desktop host.");
            }
            return attached;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to attach top-level MiniFences ownership to Explorer", ex);
            return false;
        }
    }

    private void DetachTopLevelDesktopOwner()
    {
        if (!_useTopLevelDesktopCompatibilityMode) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && GetWindow(handle, GwOwner) != IntPtr.Zero)
            {
                SetWindowLongPtr(handle, GwlHwndParent, IntPtr.Zero);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to detach top-level MiniFences ownership from Explorer", ex);
        }
        finally
        {
            _desktopHostHandle = IntPtr.Zero;
        }
    }

    private void EnsureDesktopHostAttachment()
    {
        if (_fencesTopmost || _isExiting) return;
        if (_keepTopLevelDesktopAfterTopmost)
        {
            // AllowsTransparency WPF windows can stop painting after their
            // HWND is changed from WS_POPUP back to WS_CHILD. Once topmost has
            // detached this surface, keep its HWND top-level and maintain its
            // desktop z-order instead of reparenting it again.
            return;
        }
        if (_useTopLevelDesktopCompatibilityMode)
        {
            if (_isDesktopHosted)
            {
                DetachFromDesktopHost();
                ApplyDesktopWorkAreaBounds();
            }
            var compatibilityHandle = new WindowInteropHelper(this).Handle;
            var compatibilityDesktopHost = FindDesktopViewHost();
            var currentOwner = compatibilityHandle == IntPtr.Zero
                ? IntPtr.Zero
                : GetWindow(compatibilityHandle, GwOwner);
            if (NeedsTopLevelDesktopOwnerAttachment(
                    compatibilityMode: true,
                    fencesTopmost: _fencesTopmost,
                    currentOwner,
                    compatibilityDesktopHost) &&
                AttachToTopLevelDesktopOwner())
            {
                ApplyDesktopWorkAreaBounds();
                UpdateDesktopWindowRegion();
                SendBehindNormalWindows(force: true);
            }
            return;
        }

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
        if (_useTopLevelDesktopCompatibilityMode)
        {
            DetachTopLevelDesktopOwner();
            return;
        }
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

    internal static bool NeedsTopLevelDesktopOwnerAttachment(
        bool compatibilityMode,
        bool fencesTopmost,
        IntPtr currentOwner,
        IntPtr desktopHost) =>
        compatibilityMode &&
        !fencesTopmost &&
        desktopHost != IntPtr.Zero &&
        currentOwner != desktopHost;

    private void ToggleFencesTopmost() =>
        SetFencesTopmost(!_fencesTopmost, ensureVisible: true);

    private void SetFencesTopmost(bool enabled, bool ensureVisible)
    {
        if (_topmostTransitionInProgress) return;
        _topmostTransitionInProgress = true;
        try
        {
            _fencesTopmost = enabled;
            if (ensureVisible)
            {
                // Topmost is a temporary presentation state, not a hide/show
                // operation. Never restore a stale hidden value when Escape is
                // pressed; that made every Fence disappear after leaving it.
                _fencesHidden = false;
                _config.FencesHidden = false;
            }

            PeekBackdrop.Visibility = Visibility.Collapsed;
            PeekBackdrop.Opacity = 0;
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            ApplyFenceVisibility();

            var handle = new WindowInteropHelper(this).Handle;
            if (enabled)
            {
                DetachFromDesktopHost();
                _keepTopLevelDesktopAfterTopmost = !_useTopLevelDesktopCompatibilityMode;
                ApplyDesktopWorkAreaBounds();
                handle = new WindowInteropHelper(this).Handle;
                // Topmost means the Fence surfaces, not a full-screen Peek mode.
                // Keep the transparent desktop area out of the native hit-test
                // region so normal windows remain sharp and interactive.
                UpdateDesktopWindowRegion();
                if (handle != IntPtr.Zero)
                    SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                AppLogger.Log("Fences pinned above normal windows; visible=True.");
            }
            else
            {
                // This branch is intentionally idempotent. It also repairs a
                // detached/hidden desktop window when Settings asks to restore
                // after Escape has already changed the logical flag.
                if (handle != IntPtr.Zero)
                    SetDesktopWindowPos(handle, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                var attached = _useTopLevelDesktopCompatibilityMode
                    ? AttachToTopLevelDesktopOwner()
                    : !_keepTopLevelDesktopAfterTopmost && AttachToDesktopHost();
                ApplyDesktopWorkAreaBounds();
                ApplyFenceVisibility();
                UpdateDesktopWindowRegion();
                SendBehindNormalWindows(force: true);
                AppLogger.Log(
                    $"Fences restored to the desktop layer; visible=True; attached={attached}; " +
                    $"topLevelDesktop={_keepTopLevelDesktopAfterTopmost}.");
            }

            ScheduleConfigSave();
            _settingsWindow?.ReloadState();
        }
        finally
        {
            _topmostTransitionInProgress = false;
        }
    }


    private void PeekBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_fencesTopmost) SetFencesTopmost(false, ensureVisible: true);
    }

    private void RegisterGlobalHotkeys()
    {
        UnregisterGlobalHotkeys();
        _previousPageGesture = ParseHotkey(_config.PreviousPageHotkey);
        _nextPageGesture = ParseHotkey(_config.NextPageHotkey);
        _toggleTopmostGesture = ParseHotkey(_config.ToggleTopmostHotkey);
        for (var index = 0; index < _directPageGestures.Length; index += 1)
            _directPageGestures[index] = index < _config.DirectPageHotkeys.Count &&
                                         !string.IsNullOrWhiteSpace(_config.DirectPageHotkeys[index])
                ? ParseHotkey(_config.DirectPageHotkeys[index])
                : null;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            // Page switching is desktop-scoped. RegisterHotKey would consume
            // F1/F2 and other configured keys even while another app is active.
            _previousPageNativeHotkeyRegistered = false;
            _nextPageNativeHotkeyRegistered = false;
            AppLogger.Log("Desktop-scoped hook enabled: Previous page");
            AppLogger.Log("Desktop-scoped hook enabled: Next page");
            _toggleTopmostNativeHotkeyRegistered = TryRegisterNativeHotkey(handle, HotkeyToggleTopmost, _toggleTopmostGesture, "Pin/restore Fences");
            for (var index = 0; index < _directPageGestures.Length; index += 1)
            {
                _directPageNativeHotkeysRegistered[index] = false;
                if (_directPageGestures[index] != null)
                    AppLogger.Log($"Desktop-scoped hook enabled: Direct page {index + 1}");
            }
        }
        _keyboardHookProc = KeyboardHookProc;
        _keyboardHookHandle = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, GetModuleHandle(null), 0);
        if (_keyboardHookHandle == IntPtr.Zero)
            AppLogger.Log($"Global keyboard hook installation failed. Win32Error={Marshal.GetLastWin32Error()}");
        else
            AppLogger.Log($"Custom hotkeys enabled: {_config.PreviousPageHotkey}; {_config.NextPageHotkey}; {_config.ToggleTopmostHotkey}");
    }

    private static uint GetNativeHotkeyModifiers(HotkeyGesture gesture)
    {
        var modifiers = ModNoRepeat;
        if (gesture.Alt) modifiers |= ModAlt;
        if (gesture.Control) modifiers |= ModControl;
        if (gesture.Shift) modifiers |= ModShift;
        if (gesture.Win) modifiers |= ModWin;
        return modifiers;
    }

    private static bool TryRegisterNativeHotkey(IntPtr handle, int id, HotkeyGesture? gesture, string label)
    {
        if (gesture == null) return false;
        if (IsMouseButtonKey(gesture.Key))
        {
            AppLogger.Log($"Mouse global hotkey enabled through mouse hook: {label}; Id={id}; Button=0x{gesture.Key:X2}");
            return false;
        }
        if (RegisterHotKey(handle, id, GetNativeHotkeyModifiers(gesture), gesture.Key))
        {
            AppLogger.Log($"Windows global hotkey registered: {label}; Id={id}; Key=0x{gesture.Key:X2}");
            return true;
        }

        AppLogger.Log($"Windows global hotkey registration failed: {label}; Id={id}; Key=0x{gesture.Key:X2}; Win32Error={Marshal.GetLastWin32Error()}. Falling back to keyboard hook.");
        return false;
    }

    private void UnregisterGlobalHotkeys()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = UnregisterHotKey(handle, HotkeyPreviousPage);
            _ = UnregisterHotKey(handle, HotkeyNextPage);
            _ = UnregisterHotKey(handle, HotkeyToggleTopmost);
            for (var index = 0; index < _directPageNativeHotkeysRegistered.Length; index += 1)
                _ = UnregisterHotKey(handle, HotkeyDirectPageBase + index);
        }
        _previousPageNativeHotkeyRegistered = false;
        _nextPageNativeHotkeyRegistered = false;
        _toggleTopmostNativeHotkeyRegistered = false;
        Array.Fill(_directPageNativeHotkeysRegistered, false);
        if (_keyboardHookHandle != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHookHandle);
        _keyboardHookHandle = IntPtr.Zero;
        _keyboardHookProc = null;
        ResetPolledHotkeyState();
    }

    private void PollGlobalHotkeys()
    {
        if (_isExiting || _settingsWindow?.IsCapturingHotkey == true)
        {
            ResetPolledHotkeyState();
            return;
        }

        var keyboardDesktopContext = IsDesktopShortcutContextActive();
        PollHotkeyEdge(_previousPageGesture, ref _previousPageHotkeyWasDown, HotkeyPreviousPage,
            IsMouseButtonKey(_previousPageGesture?.Key ?? 0)
                ? IsDesktopMouseShortcutPoint(Forms.Cursor.Position)
                : keyboardDesktopContext);
        PollHotkeyEdge(_nextPageGesture, ref _nextPageHotkeyWasDown, HotkeyNextPage,
            IsMouseButtonKey(_nextPageGesture?.Key ?? 0)
                ? IsDesktopMouseShortcutPoint(Forms.Cursor.Position)
                : keyboardDesktopContext);
        PollHotkeyEdge(_toggleTopmostGesture, ref _toggleTopmostHotkeyWasDown, HotkeyToggleTopmost);
        for (var index = 0; index < _directPageGestures.Length; index += 1)
            PollHotkeyEdge(_directPageGestures[index], ref _directPageHotkeysWereDown[index], HotkeyDirectPageBase + index,
                IsMouseButtonKey(_directPageGestures[index]?.Key ?? 0)
                    ? IsDesktopMouseShortcutPoint(Forms.Cursor.Position)
                    : keyboardDesktopContext);
    }

    private void PollHotkeyEdge(HotkeyGesture? gesture, ref bool wasDown, int hotkeyId, bool allowed = true)
    {
        if (!allowed)
        {
            wasDown = false;
            return;
        }
        var isDown = IsHotkeyDown(gesture);
        if (isDown && !wasDown) ActivateHotkey(hotkeyId, "physical-state polling");
        wasDown = isDown;
    }

    private static bool IsHotkeyDown(HotkeyGesture? gesture) =>
        gesture != null && IsKeyDown((int)gesture.Key) &&
        gesture.Control == IsKeyDown(VkControl) &&
        gesture.Alt == IsKeyDown(VkMenu) &&
        gesture.Shift == IsKeyDown(VkShift) &&
        gesture.Win == (IsKeyDown(VkLeftWin) || IsKeyDown(VkRightWin));

    private void ResetPolledHotkeyState()
    {
        _previousPageHotkeyWasDown = false;
        _nextPageHotkeyWasDown = false;
        _toggleTopmostHotkeyWasDown = false;
        Array.Fill(_directPageHotkeysWereDown, false);
    }

    private void ActivateHotkey(int hotkeyId, string source, bool desktopContextVerified = false)
    {
        var slot = hotkeyId switch
        {
            HotkeyPreviousPage => 0,
            HotkeyNextPage => 1,
            HotkeyToggleTopmost => 2,
            >= HotkeyDirectPageBase and < HotkeyDirectPageBase + 12 => 3 + hotkeyId - HotkeyDirectPageBase,
            _ => -1
        };
        if (slot < 0) return;
        if (IsPageHotkeyId(hotkeyId) &&
            !desktopContextVerified &&
            !IsDesktopShortcutContextActive())
        {
            AppLogger.Log($"Desktop page hotkey ignored outside desktop context. Id={hotkeyId}; Source={source}");
            return;
        }

        var now = Environment.TickCount64;
        if (now - _hotkeyLastActivatedTicks[slot] < 250) return;
        _hotkeyLastActivatedTicks[slot] = now;
        AppLogger.Log($"Global hotkey activated by {source}. Id={hotkeyId}; CurrentPage={_config.CurrentPage + 1}/{GetPageCount()}");

        switch (hotkeyId)
        {
            case HotkeyPreviousPage: SwitchPage(GetAdjacentPageIndex(_config.CurrentPage, GetPageCount(), -1)); break;
            case HotkeyNextPage: SwitchPage(GetAdjacentPageIndex(_config.CurrentPage, GetPageCount(), 1)); break;
            case HotkeyToggleTopmost: ToggleFencesTopmost(); break;
            default:
                var directPageIndex = hotkeyId - HotkeyDirectPageBase;
                if (directPageIndex >= 0 && directPageIndex < GetPageCount()) SwitchPage(directPageIndex);
                break;
        }
    }

    private static bool IsPageHotkeyId(int hotkeyId) =>
        hotkeyId is HotkeyPreviousPage or HotkeyNextPage ||
        hotkeyId is >= HotkeyDirectPageBase and < HotkeyDirectPageBase + 12;

    private bool IsDesktopShortcutContextActive()
    {
        if (_settingsWindow?.IsCapturingHotkey == true || _settingsWindow?.IsActive == true) return false;
        return _desktopShortcutContextActive;
    }

    private void SetDesktopShortcutContext(bool active, string reason)
    {
        if (_desktopShortcutContextActive == active) return;
        _desktopShortcutContextActive = active;
        AppLogger.Log($"Desktop shortcut context changed: Active={active}; Reason={reason}");
    }

    private void RecordDesktopShortcutMouseContext(bool active, string reason)
    {
        Interlocked.Increment(ref _desktopShortcutContextRevision);
        SetDesktopShortcutContext(active, reason);
    }

    private bool IsDesktopMouseShortcutPoint(System.Drawing.Point screenPoint) =>
        IsTopmostInteractiveSurfaceDesktop(screenPoint);

    private bool IsTopmostInteractiveSurfaceDesktop(System.Drawing.Point screenPoint) =>
        IsTopmostInteractiveSurfaceDesktop(screenPoint, out _);

    private bool IsTopmostInteractiveSurfaceDesktop(
        System.Drawing.Point screenPoint,
        out IntPtr topmostUnderlyingWindow)
    {
        var mainHandle = new WindowInteropHelper(this).Handle;
        var foundWindow = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            // The compatibility-mode MiniFences HWND can be returned above a
            // Chromium/WinUI window even though its transparent region passes
            // the actual click through. Skip only that desktop HWND, then use
            // the first real visible window beneath it.
            if (window == mainHandle || GetAncestor(window, GaRoot) == mainHandle) return true;
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            if (!GetWindowRect(window, out var rect) ||
                screenPoint.X < rect.Left || screenPoint.X >= rect.Right ||
                screenPoint.Y < rect.Top || screenPoint.Y >= rect.Bottom) return true;

            var cloaked = 0u;
            _ = DwmGetWindowAttribute(
                window,
                DwmwaCloaked,
                out cloaked,
                (uint)Marshal.SizeOf<uint>());
            if (cloaked != 0) return true;

            foundWindow = window;
            return false;
        }, IntPtr.Zero);

        topmostUnderlyingWindow = foundWindow;
        if (topmostUnderlyingWindow == IntPtr.Zero) return false;
        var desktopHost = FindDesktopViewHost();
        return IsWindowInHierarchy(topmostUnderlyingWindow, desktopHost);
    }

    private IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = wParam.ToInt32();
            var key = (uint)Marshal.ReadInt32(lParam);
            if (key == VkD && (message == WmKeyUp || message == WmSysKeyUp))
            {
                _winDKeyDown = false;
            }
            if (message != WmKeyDown && message != WmSysKeyDown)
            {
                return CallNextHookEx(_keyboardHookHandle, code, wParam, lParam);
            }
            if (key == VkEscape &&
                (_dragFeedbackActive || _shellDropTarget.IsActive || HasAnyFenceDragHighlight()))
            {
                // Clear synchronously inside the low-level keyboard callback.
                // DispatcherTimer and queued Dispatcher work can be starved by
                // OLE's modal DoDragDrop loop until the mouse button is released.
                _shellDragLeaveTimer.Stop();
                _shellDropTarget.DragLeave();
                ClearDragHint();
                return CallNextHookEx(_keyboardHookHandle, code, wParam, lParam);
            }
            if (_useTopLevelDesktopCompatibilityMode &&
                key == VkD &&
                !_winDKeyDown &&
                (IsKeyDown(VkLeftWin) || IsKeyDown(VkRightWin)))
            {
                _winDKeyDown = true;
                PrepareForShowDesktopToggle("Win+D");
            }
            if (_fencesTopmost && key == VkEscape)
            {
                Dispatcher.BeginInvoke(() => SetFencesTopmost(false, ensureVisible: true));
                return new IntPtr(1);
            }
            if (_fencesTopmost && key == VkD && (IsKeyDown(VkLeftWin) || IsKeyDown(VkRightWin)))
            {
                Dispatcher.BeginInvoke(() => SetFencesTopmost(false, ensureVisible: true));
                return CallNextHookEx(_keyboardHookHandle, code, wParam, lParam);
            }
            // A configured global shortcut may be the exact combination the
            // user is trying to record. Let WPF receive it instead of
            // swallowing the final key before Settings can finish capture.
            if (_settingsWindow?.IsCapturingHotkey == true || _settingsWindow?.IsActive == true)
                return CallNextHookEx(_keyboardHookHandle, code, wParam, lParam);
            Action? action = null;
            var desktopContext = IsDesktopShortcutContextActive();
            var directPageIndex = desktopContext
                ? Array.FindIndex(_directPageGestures, gesture => MatchesHotkey(gesture, key))
                : -1;
            if (directPageIndex >= 0 && _directPageNativeHotkeysRegistered[directPageIndex]) directPageIndex = -1;
            if (directPageIndex >= 0 && directPageIndex < GetPageCount())
                action = () => SwitchPage(directPageIndex);
            else if (desktopContext && !_previousPageNativeHotkeyRegistered && MatchesHotkey(_previousPageGesture, key))
                action = () => SwitchPage(GetAdjacentPageIndex(_config.CurrentPage, GetPageCount(), -1));
            else if (desktopContext && !_nextPageNativeHotkeyRegistered && MatchesHotkey(_nextPageGesture, key))
                action = () => SwitchPage(GetAdjacentPageIndex(_config.CurrentPage, GetPageCount(), 1));
            else if (!_toggleTopmostNativeHotkeyRegistered && MatchesHotkey(_toggleTopmostGesture, key)) action = ToggleFencesTopmost;
            if (action != null)
            {
                AppLogger.Log($"Global shortcut received. Key=0x{key:X2}; CurrentPage={_config.CurrentPage + 1}/{GetPageCount()}");
                Dispatcher.BeginInvoke(action);
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_keyboardHookHandle, code, wParam, lParam);
    }

    internal static int GetDirectPageIndex(uint key) =>
        key is >= VkF1 and <= VkF12 ? (int)(key - VkF1) : -1;

    internal static int GetAdjacentPageIndex(int currentPage, int pageCount, int direction)
    {
        if (pageCount <= 1) return 0;
        return ((currentPage + direction) % pageCount + pageCount) % pageCount;
    }

    internal void SettingsBeginHotkeyCapture() => UnregisterGlobalHotkeys();

    internal void SettingsEndHotkeyCapture() => RegisterGlobalHotkeys();

    private static bool MatchesHotkey(HotkeyGesture? gesture, uint key) =>
        gesture != null && !IsMouseButtonKey(gesture.Key) && gesture.Key == key &&
        gesture.Control == IsKeyDown(VkControl) &&
        gesture.Alt == IsKeyDown(VkMenu) &&
        gesture.Shift == IsKeyDown(VkShift) &&
        gesture.Win == (IsKeyDown(VkLeftWin) || IsKeyDown(VkRightWin));

    private static bool MatchesMouseHotkey(HotkeyGesture? gesture, uint button) =>
        gesture != null && IsMouseButtonKey(gesture.Key) && gesture.Key == button &&
        gesture.Control == IsKeyDown(VkControl) &&
        gesture.Alt == IsKeyDown(VkMenu) &&
        gesture.Shift == IsKeyDown(VkShift) &&
        gesture.Win == (IsKeyDown(VkLeftWin) || IsKeyDown(VkRightWin));

    private int GetMatchingMouseHotkeyId(uint button)
    {
        var directPageIndex = Array.FindIndex(_directPageGestures, gesture => MatchesMouseHotkey(gesture, button));
        if (directPageIndex >= 0) return HotkeyDirectPageBase + directPageIndex;
        if (MatchesMouseHotkey(_previousPageGesture, button)) return HotkeyPreviousPage;
        if (MatchesMouseHotkey(_nextPageGesture, button)) return HotkeyNextPage;
        if (MatchesMouseHotkey(_toggleTopmostGesture, button)) return HotkeyToggleTopmost;
        return -1;
    }

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static bool IsMouseButtonKey(uint key) => key is VkLButton or VkRButton;

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
        var key = ParseHotkeyKey(keyName);
        return key == 0 ? null : new HotkeyGesture(control, alt, shift, win, key);
    }

    internal static uint ParseHotkeyKey(string? keyName)
    {
        var normalized = keyName?.ToUpperInvariant() ?? "";
        if (normalized == "LEFT") return VkLeft;
        if (normalized == "RIGHT") return VkRight;
        if (normalized is "MOUSELEFT" or "MOUSE1") return VkLButton;
        if (normalized is "MOUSERIGHT" or "MOUSE2") return VkRButton;
        if (normalized == "SPACE") return VkSpace;
        if (normalized.Length == 1 && char.IsLetterOrDigit(normalized[0])) return normalized[0];
        if (normalized.StartsWith('F') && int.TryParse(normalized[1..], out var functionIndex) &&
            functionIndex is >= 1 and <= 12) return (uint)(VkF1 + functionIndex - 1);
        return 0;
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
            if (_useTopLevelDesktopCompatibilityMode)
            {
                var enabled = 1;
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaTransitionsForcedDisabled,
                    ref enabled,
                    (uint)Marshal.SizeOf<int>());
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaExcludedFromPeek,
                    ref enabled,
                    (uint)Marshal.SizeOf<int>());
            }
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

    internal bool TryGetElementPhysicalScreenBounds(
        FrameworkElement element,
        out System.Windows.Int32Rect bounds)
    {
        bounds = default;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !element.IsVisible) return false;

        try
        {
            element.UpdateLayout();
            var originDips = element.TranslatePoint(new System.Windows.Point(0, 0), this);
            var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                           ?? System.Windows.Media.Matrix.Identity;
            var originPixels = toDevice.Transform(originDips);
            var bottomRightPixels = toDevice.Transform(new System.Windows.Point(
                originDips.X + element.ActualWidth,
                originDips.Y + element.ActualHeight));
            var nativeOrigin = new NativePoint(
                (int)Math.Round(originPixels.X),
                (int)Math.Round(originPixels.Y));
            if (!ClientToScreen(handle, ref nativeOrigin)) return false;

            bounds = new System.Windows.Int32Rect(
                nativeOrigin.X,
                nativeOrigin.Y,
                Math.Max(1, (int)Math.Ceiling(bottomRightPixels.X - originPixels.X)),
                Math.Max(1, (int)Math.Ceiling(bottomRightPixels.Y - originPixels.Y)));
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to calculate desktop rename editor bounds", ex);
            return false;
        }
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

    internal static bool ShouldUseTopLevelDesktopFallback(Version windowsVersion, string? modeOverride = null)
    {
        if (string.Equals(modeOverride, "top-level", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modeOverride, "toplevel", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(modeOverride, "explorer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(modeOverride, "child", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return windowsVersion.Major >= 10 && windowsVersion.Build >= 26200;
    }

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
        // Activating the independent desktop rename editor necessarily
        // deactivates the Explorer-hosted surface. Reordering the desktop HWND
        // at that moment competes with the editor for foreground focus and can
        // leave the desktop feeling frozen.
        var hasIndependentRename = Workspace.Children.OfType<DesktopLooseIconControl>()
            .Any(control => control.HasIndependentRenameWindow);
        if (!ShouldRepairDesktopLayerOnDeactivation(hasIndependentRename)) return;
        if (!_useTopLevelDesktopCompatibilityMode)
        {
            Dispatcher.BeginInvoke(SendBehindNormalWindows, DispatcherPriority.ApplicationIdle);
        }
    }

    internal static bool ShouldRepairDesktopLayerOnDeactivation(bool hasIndependentRenameWindow) =>
        !hasIndependentRenameWindow;

    private void ScheduleTopLevelDesktopRecovery(string reason)
    {
        if (!_useTopLevelDesktopCompatibilityMode || _isExiting) return;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            foreach (var delay in new[] { 50, 250, 750 })
            {
                await Task.Delay(delay);
                if (_isExiting) return;
                RecoverTopLevelDesktopWindow(reason);
            }
        }, DispatcherPriority.Background);
    }

    private void RecoverTopLevelDesktopWindow(string reason)
    {
        if (!_useTopLevelDesktopCompatibilityMode || _isExiting || _fencesTopmost) return;
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var visible = IsWindowVisible(handle);
            var iconic = IsIconic(handle);
            var cloaked = 0u;
            _ = DwmGetWindowAttribute(
                handle,
                DwmwaCloaked,
                out cloaked,
                (uint)Marshal.SizeOf<uint>());
            var needsRecovery = ShouldRecoverTopLevelDesktopWindow(
                compatibilityMode: true,
                isExiting: false,
                visible,
                iconic,
                cloaked);
            if (needsRecovery)
            {
                var uncloak = 0;
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaCloak,
                    ref uncloak,
                    (uint)Marshal.SizeOf<int>());
                ShowWindow(handle, SwShowNoActivate);
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                ApplyDesktopWorkAreaBounds();
                AppLogger.Log(
                    $"Recovered top-level desktop window after {reason}. " +
                    $"Visible={visible}; Iconic={iconic}; Cloaked=0x{cloaked:X}");
                ScheduleDesktopLayerCorrectionAfterShellAnimation(reason);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to recover the top-level desktop compatibility window", ex);
        }
    }

    internal static bool ShouldRecoverTopLevelDesktopWindow(
        bool compatibilityMode,
        bool isExiting,
        bool visible,
        bool iconic,
        uint cloakedFlags) =>
        compatibilityMode && !isExiting && (!visible || iconic || cloakedFlags != 0);

    private void ScheduleDesktopLayerCorrectionAfterShellAnimation(string reason)
    {
        if (!_useTopLevelDesktopCompatibilityMode || _isExiting || _fencesTopmost) return;
        var generation = ++_desktopLayerCorrectionGeneration;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(1200);
            if (_isExiting || _fencesTopmost || generation != _desktopLayerCorrectionGeneration) return;
            SendBehindNormalWindows(force: true);
            AppLogger.Log($"Desktop layer corrected once after the Shell animation ({reason}).");
        }, DispatcherPriority.Background);
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

    private void InstallForegroundWindowHook()
    {
        if (_foregroundWinEventHook != IntPtr.Zero) return;
        _foregroundWinEventProc = ForegroundWinEventProc;
        _foregroundWinEventHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _foregroundWinEventProc,
            0,
            0,
            WineventOutOfContext);
        if (_foregroundWinEventHook == IntPtr.Zero)
            AppLogger.Log($"Foreground window hook installation failed. Win32Error={Marshal.GetLastWin32Error()}");
    }

    private void UninstallForegroundWindowHook()
    {
        if (_foregroundWinEventHook != IntPtr.Zero)
            UnhookWinEvent(_foregroundWinEventHook);
        _foregroundWinEventHook = IntPtr.Zero;
        _foregroundWinEventProc = null;
    }

    private void ForegroundWinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (window == IntPtr.Zero || _isExiting) return;
        var contextRevision = Interlocked.Read(ref _desktopShortcutContextRevision);
        Dispatcher.BeginInvoke(
            () => HandleForegroundWindowChanged(window, contextRevision),
            DispatcherPriority.Send);
    }

    private void HandleForegroundWindowChanged(IntPtr window, long contextRevision)
    {
        if (_isExiting) return;
        var desktopHost = FindDesktopViewHost();
        GetWindowThreadProcessId(window, out var foregroundProcessId);
        var isDesktopWindow = window == desktopHost || GetAncestor(window, GaRoot) == desktopHost;
        var mainHandle = new WindowInteropHelper(this).Handle;
        var isMiniFencesDesktopSurface = mainHandle != IntPtr.Zero &&
                                         (window == mainHandle || GetAncestor(window, GaRoot) == mainHandle);
        var isDesktopContext = isDesktopWindow || isMiniFencesDesktopSurface;
        if (ShouldApplyForegroundShortcutContext(
                contextRevision,
                Interlocked.Read(ref _desktopShortcutContextRevision),
                window,
                GetForegroundWindow()))
        {
            SetDesktopShortcutContext(isDesktopContext, isDesktopContext
                ? "current desktop surface became foreground"
                : "current non-desktop window became foreground");
        }
        if (foregroundProcessId != (uint)Environment.ProcessId && !isDesktopWindow)
        {
            var foregroundClass = GetWindowClassName(window);
            var foregroundProcessName = GetProcessName(foregroundProcessId);
            var hasActiveRename = Workspace.Children.OfType<DesktopLooseIconControl>()
                                      .Any(icon => icon.HasIndependentRenameWindow) ||
                                  Workspace.Children.OfType<FenceControl>()
                                      .Any(fence => fence.HasIndependentRenameWindow);
            if (hasActiveRename &&
                ShouldCancelRenameForForegroundWindow(foregroundClass, foregroundProcessName))
            {
                CancelActiveRenamesForSettings();
                AppLogger.Log($"Active rename cancelled after foreground switched to {foregroundProcessName}/{foregroundClass}.");
            }
            ClearAllItemSelections();
            if (string.Equals(Environment.GetEnvironmentVariable("MINIFENCES_RENAME_TRACE"), "1", StringComparison.Ordinal))
                AppLogger.Log($"Loose selection cleared by foreground HWND 0x{window.ToInt64():X}; PID={foregroundProcessId}.");
        }

        if (!_useTopLevelDesktopCompatibilityMode || _fencesTopmost) return;
        if (isDesktopWindow)
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero &&
                ShouldEnterShowDesktopFromDesktopForeground(
                    _showDesktopModeActive,
                    _showDesktopRestorePending,
                    IsWindowAboveTarget(handle, desktopHost)))
            {
                EnterShowDesktopMode("Explorer desktop was raised above MiniFences");
            }
        }
        else if (ShouldExitShowDesktopForNormalForeground(
                     _showDesktopModeActive,
                     Environment.TickCount64,
                     _ignoreNormalForegroundUntilTicks))
        {
            ExitShowDesktopMode("normal window became foreground");
        }
        else if (_showDesktopRestorePending)
        {
            ScheduleStableShowDesktopRestore("restored foreground window detected");
        }
        else if (ShouldRepairDesktopLayerAfterNormalForeground(
                     _showDesktopModeActive,
                     _showDesktopRestorePending))
        {
            // Normal applications can be activated after the Shell restore sequence
            // has completed. Validate the exact desktop adjacency on every such event
            // so an owner-group or Shell z-order drift is corrected before it can be
            // painted above the newly activated application.
            SendBehindNormalWindows(force: true);
        }
    }

    internal static bool ShouldCancelRenameForForegroundWindow(string windowClass, string processName)
    {
        if (processName.Equals("ctfmon", StringComparison.OrdinalIgnoreCase) ||
            processName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !windowClass.Equals("IME", StringComparison.OrdinalIgnoreCase) &&
               !windowClass.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase) &&
               !windowClass.Equals("CiceroUIWndFrame", StringComparison.OrdinalIgnoreCase) &&
               !windowClass.Contains("CandidateWindow", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldApplyForegroundShortcutContext(
        long eventRevision,
        long currentRevision,
        IntPtr eventWindow,
        IntPtr currentForegroundWindow) =>
        eventRevision == currentRevision &&
        eventWindow != IntPtr.Zero &&
        eventWindow == currentForegroundWindow;

    private static string GetProcessName(uint processId)
    {
        if (processId == 0) return "";
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    internal static bool ShouldEnterShowDesktopFromDesktopForeground(
        bool showDesktopModeActive,
        bool restorePending,
        bool miniFencesAboveDesktop) =>
        !showDesktopModeActive && !restorePending;

    internal static bool ShouldExitShowDesktopForNormalForeground(
        bool showDesktopModeActive,
        long nowTicks,
        long ignoreNormalForegroundUntilTicks) =>
        showDesktopModeActive && nowTicks >= ignoreNormalForegroundUntilTicks;

    internal static bool ShouldRepairDesktopLayerAfterNormalForeground(
        bool showDesktopModeActive,
        bool restorePending) =>
        !showDesktopModeActive && !restorePending;

    private void PrepareForShowDesktopToggle(string reason)
    {
        if (_showDesktopModeActive)
            ExitShowDesktopMode($"{reason} restore");
        else
            EnterShowDesktopMode($"{reason} show");
    }

    private void EnterShowDesktopMode(string reason)
    {
        if (_showDesktopModeActive || _isExiting || _fencesTopmost) return;
        _showDesktopRestorePending = false;
        ++_desktopLayerCorrectionGeneration;
        _showDesktopModeActive = true;
        _ignoreNormalForegroundUntilTicks = Environment.TickCount64 + 100;
        EnsureDesktopHostAttachment();
        AppLogger.Log($"Entered Show Desktop protection mode with stable Explorer ownership ({reason}).");
    }

    private void ExitShowDesktopMode(string reason)
    {
        if (!_showDesktopModeActive) return;
        _showDesktopModeActive = false;
        BeginShowDesktopRestore(reason);
        AppLogger.Log($"Exited Show Desktop ownership mode ({reason}).");
    }

    private void BeginShowDesktopRestore(string reason)
    {
        if (_isExiting || _fencesTopmost) return;
        _showDesktopRestorePending = true;
        var generation = ++_desktopLayerCorrectionGeneration;

        // Keep MiniFences directly above Explorer during restore. Never drop it
        // to HWND_BOTTOM: that creates a compositor gap and visible flashing.
        SendBehindNormalWindows(force: true);

        _ = Dispatcher.InvokeAsync(async () =>
        {
            foreach (var delay in new[] { 50, 150, 350, 750, 1500 })
            {
                await Task.Delay(delay);
                if (_isExiting || !_showDesktopRestorePending ||
                    generation != _desktopLayerCorrectionGeneration) return;
                SendBehindNormalWindows(force: true);
            }
            CompleteShowDesktopRestore($"{reason} settled");
        }, DispatcherPriority.Background);
    }

    private void ScheduleStableShowDesktopRestore(string reason)
    {
        if (!_showDesktopRestorePending || _isExiting || _fencesTopmost) return;
        SendBehindNormalWindows(force: true);
    }

    private void CompleteShowDesktopRestore(string reason)
    {
        if (!_showDesktopRestorePending || _isExiting || _fencesTopmost) return;
        _showDesktopRestorePending = false;
        ++_desktopLayerCorrectionGeneration;
        SendBehindNormalWindows(force: true);
        AppLogger.Log($"Completed Show Desktop restore after stable Shell z-order ({reason}).");
    }

    private bool IsShowDesktopButtonPoint(System.Drawing.Point point)
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var rect)) return false;
        return IsPointAtTaskbarShowDesktopEdge(
            point.X,
            point.Y,
            rect.Left,
            rect.Top,
            rect.Right,
            rect.Bottom,
            32);
    }

    internal static bool IsPointAtTaskbarShowDesktopEdge(
        int x,
        int y,
        int left,
        int top,
        int right,
        int bottom,
        int edgeSize)
    {
        if (x < left || x >= right || y < top || y >= bottom || edgeSize <= 0) return false;
        var width = right - left;
        var height = bottom - top;
        return width >= height
            ? x >= right - edgeSize
            : y >= bottom - edgeSize;
    }

    private IntPtr DesktopMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var hook = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var screenPoint = new System.Drawing.Point(hook.pt.x, hook.pt.y);
                if (wParam == new IntPtr(WmLButtonDown) || wParam == new IntPtr(WmRButtonDown))
                {
                    var pointIsDesktop = IsTopmostInteractiveSurfaceDesktop(screenPoint, out var shortcutHitWindow);
                    GetWindowThreadProcessId(shortcutHitWindow, out var shortcutHitProcessId);
                    var shortcutHitDescription = shortcutHitWindow == IntPtr.Zero
                        ? "none"
                        : $"{GetProcessName(shortcutHitProcessId)}/{GetWindowClassName(shortcutHitWindow)}";
                    RecordDesktopShortcutMouseContext(pointIsDesktop,
                        pointIsDesktop
                            ? $"mouse clicked exposed desktop surface ({shortcutHitDescription})"
                            : $"mouse clicked another window ({shortcutHitDescription})");
                }
                var pressedMouseButton = wParam == new IntPtr(WmLButtonDown)
                    ? (uint)VkLButton
                    : wParam == new IntPtr(WmRButtonDown)
                        ? (uint)VkRButton
                        : 0;
                if (pressedMouseButton != 0 && IsDesktopMouseShortcutPoint(screenPoint))
                {
                    var hotkeyId = GetMatchingMouseHotkeyId(pressedMouseButton);
                    if (hotkeyId >= 0)
                    {
                        _suppressedMouseHotkeyButton = (int)pressedMouseButton;
                        Dispatcher.BeginInvoke(
                            () => ActivateHotkey(hotkeyId, "desktop mouse hook", desktopContextVerified: true),
                            DispatcherPriority.Input);
                        return new IntPtr(1);
                    }
                }
                if ((wParam == new IntPtr(WmLButtonUp) && _suppressedMouseHotkeyButton == VkLButton) ||
                    (wParam == new IntPtr(WmRButtonUp) && _suppressedMouseHotkeyButton == VkRButton))
                {
                    _suppressedMouseHotkeyButton = 0;
                    return new IntPtr(1);
                }
                if (_desktopFenceSelectionActive)
                {
                    if (wParam == new IntPtr(WmMouseMove))
                        Dispatcher.BeginInvoke(() => UpdateFenceSelection(screenPoint), DispatcherPriority.Input);
                    else if (wParam == new IntPtr(WmLButtonUp))
                    {
                        _desktopFenceSelectionActive = false;
                        Dispatcher.BeginInvoke(() => CompleteFenceSelection(screenPoint), DispatcherPriority.Input);
                    }
                    return new IntPtr(1);
                }
                if (wParam == new IntPtr(WmLButtonDown))
                {
                    if (_useTopLevelDesktopCompatibilityMode && IsShowDesktopButtonPoint(screenPoint))
                        PrepareForShowDesktopToggle("taskbar Show Desktop button");
                    if (IsKeyDown(VkControl) && IsKeyDown(VkShift) && IsExplorerDesktopPoint(screenPoint))
                    {
                        _desktopFenceSelectionStart = screenPoint;
                        _desktopFenceSelectionActive = true;
                        Dispatcher.BeginInvoke(() => BeginFenceSelection(screenPoint), DispatcherPriority.Input);
                        return new IntPtr(1);
                    }
                    var clickTicks = Environment.TickCount64;
                    Dispatcher.BeginInvoke(
                        () => HandleGlobalLeftButtonDown(screenPoint, clickTicks),
                        DispatcherPriority.Input);
                }
                else if (wParam == new IntPtr(WmRButtonDown))
                {
                    Dispatcher.BeginInvoke(
                        () => CommitInlineRenamesOutside(screenPoint),
                        DispatcherPriority.Input);
                }
                else if (wParam == new IntPtr(WmMouseMove))
                {
                    _lastMouseScreenPoint = screenPoint;
                    if (ShouldUpdateDragFeedbackFromMouseMessage(_dragFeedbackActive, WmMouseMove))
                    {
                        _dragFeedbackWindow?.UpdatePosition(screenPoint);
                        _dragTargetHintWindow?.UpdatePosition(screenPoint);
                    }
                    // During OLE drag the Fence events already track the
                    // pointer. Queuing a second Input-priority hover pass for
                    // every low-level mouse packet competes with the native
                    // drag-image update and makes high-polling mice feel slow.
                    if (!_oleDragActive && !_hoverUpdatePending)
                    {
                        _hoverUpdatePending = true;
                        Dispatcher.BeginInvoke(HandleGlobalMouseMove, DispatcherPriority.Input);
                    }
                }
                else if (wParam == new IntPtr(WmMouseWheel) && HasAnyFenceDragHighlight())
                {
                    var delta = GetMouseWheelDelta(hook.mouseData);
                    if (delta != 0)
                    {
                        Dispatcher.BeginInvoke(
                            () => ScrollFenceDuringDragAt(screenPoint, delta),
                            DispatcherPriority.Input);
                        return new IntPtr(1);
                    }
                }
                else if (wParam == new IntPtr(WmLButtonUp))
                {
                    // OLE sources do not always route Drop/DragLeave back to an
                    // Explorer-hosted WPF child after an asynchronous drop.
                    // Clear the independent feedback synchronously on the real
                    // global release so it cannot remain visible on the desktop.
                    _oleDragActive = false;
                    _shellDragLeaveTimer.Stop();
                    _shellDropTarget.DragLeave();
                    if (_dragFeedbackActive || HasAnyFenceDragHighlight()) ClearDragHint();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Desktop double-click hook failed", ex);
        }

        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private void ScrollFenceDuringDragAt(System.Drawing.Point screenPoint, int delta)
    {
        if (!TryGetWorkspacePointFromPhysicalScreen(screenPoint, out var workspacePoint)) return;
        var target = GetTopmostFenceAtWorkspacePoint(
            Workspace.Children.OfType<FenceControl>(), workspacePoint);
        if (target?.IsDragHighlightedForTesting == true) target.ScrollItemsDuringDrag(delta);
    }

    internal static int GetMouseWheelDelta(uint mouseData) => unchecked((short)(mouseData >> 16));

    private void BeginFenceSelection(System.Drawing.Point screenPoint)
    {
        EnsureDesktopWindowVisible();
        if (!TryGetWorkspacePointFromPhysicalScreen(screenPoint, out var point)) return;
        FenceSelectionOverlay.Margin = new Thickness(point.X, point.Y, 0, 0);
        FenceSelectionOverlay.Width = 1;
        FenceSelectionOverlay.Height = 1;
        FenceSelectionOverlay.Visibility = Visibility.Visible;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowRgn(handle, IntPtr.Zero, true);
    }

    private void UpdateFenceSelection(System.Drawing.Point screenPoint)
    {
        if (!TryGetWorkspacePointFromPhysicalScreen(_desktopFenceSelectionStart, out var start) ||
            !TryGetWorkspacePointFromPhysicalScreen(screenPoint, out var current)) return;
        var left = Math.Min(start.X, current.X);
        var top = Math.Min(start.Y, current.Y);
        FenceSelectionOverlay.Margin = new Thickness(left, top, 0, 0);
        FenceSelectionOverlay.Width = Math.Abs(current.X - start.X);
        FenceSelectionOverlay.Height = Math.Abs(current.Y - start.Y);
    }

    private void CompleteFenceSelection(System.Drawing.Point screenPoint)
    {
        UpdateFenceSelection(screenPoint);
        var left = FenceSelectionOverlay.Margin.Left;
        var top = FenceSelectionOverlay.Margin.Top;
        var width = FenceSelectionOverlay.Width;
        var height = FenceSelectionOverlay.Height;
        FenceSelectionOverlay.Visibility = Visibility.Collapsed;
        if (width >= 80 && height >= 70)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var fence = ConfigService.CreateNewFence(_config.Fences.Count, desktop);
            fence.Kind = FenceConfig.DesktopGroupKind;
            fence.Title = $"{_loc.T("NewFence")} {_config.Fences.Count + 1}";
            fence.AssignedPaths = [];
            fence.PageIndex = _config.CurrentPage;
            fence.Left = Math.Max(0, left);
            fence.Top = Math.Max(0, top);
            fence.Width = Math.Max(240, width);
            fence.Height = Math.Max(180, height);
            ClampFenceConfigToWorkspace(fence);
            _config.Fences.Add(fence);
            RenderFences();
            SaveConfigWithWarning();
            AppLogger.Log($"Fence created by Ctrl+Shift desktop drag: {fence.Left},{fence.Top},{fence.Width},{fence.Height}");
        }
        UpdateDesktopWindowRegion();
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
        var physicalPoint = new System.Windows.Point(screenPoint.X, screenPoint.Y);
        // A click inside the editor belongs only to the TextBox. A click
        // outside commits first and then continues through this same pipeline,
        // so the user's first click still reaches/selects its intended target.
        if (Workspace.Children.OfType<FenceControl>()
            .Any(fence => fence.IsPointerInsideIndependentRename(physicalPoint, clickTicks)))
        {
            return;
        }
        CommitInlineRenamesOutside(screenPoint, clickTicks);
        CommitLooseDesktopRenamesOutside(screenPoint);
        var overMiniFencesWindow = IsMiniFencesWindowAtScreenPoint(screenPoint);
        var overExplorerDesktop = IsExplorerDesktopPoint(screenPoint);
        var overFenceItem = overMiniFencesWindow && IsPointOverFenceItem(screenPoint);
        if (!overFenceItem)
        {
            foreach (var fence in Workspace.Children.OfType<FenceControl>()) fence.ClearItemSelection();
            if (ShouldClearKeyboardFocusForGlobalClick(overMiniFencesWindow)) Keyboard.ClearFocus();
        }
        // UI Automation only sees Explorer's native ListView items. Loose
        // MiniFences icons are WPF controls in a no-activate desktop window,
        // so classify their managed bounds explicitly before clearing state.
        // With desktop integration enabled, Explorer's native icons are hidden;
        // querying cross-process UI Automation here only adds an unbounded UI
        // thread stall to every click. Managed icon bounds are authoritative.
        var pointOverDesktopItem = IsScreenPointOverLooseDesktopIcon(screenPoint) ||
                                   (!_config.EnableDesktopIconIntegration && IsPointOverDesktopItem(screenPoint));
        var clickedDesktopBlank = IsDesktopBlankClick(
            IsScreenPointInsideDesktopWorkArea(screenPoint),
            IsPointOverVisibleFence(screenPoint),
            pointOverDesktopItem,
            overExplorerDesktop);
        var clickedOtherWindow = ShouldClearSelectionForGlobalClick(
            overMiniFencesWindow,
            overExplorerDesktop,
            pointOverDesktopItem);
        var clickedMiniFencesBlank = overMiniFencesWindow && !overFenceItem && !pointOverDesktopItem;
        if ((clickedDesktopBlank || clickedOtherWindow || clickedMiniFencesBlank) && _selectedLoosePaths.Count > 0)
        {
            _selectedLoosePaths.Clear();
            _looseSelectionAnchor = null;
            UpdateLooseIconSelectionVisuals();
        }
        if (!ShouldHandleDesktopDoubleClick(
                _config.EnableDesktopDoubleClick,
                _config.EnableDesktopIconIntegration))
        {
            _desktopDoubleClickTracker.Reset();
            return;
        }

        var isEligibleDesktopBlank = clickedDesktopBlank;
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

    internal static bool ShouldCloseWpfContextMenuFromLowLevelMouseHook() => false;

    internal static bool IsDesktopBlankClick(
        bool insideDesktopWorkArea,
        bool overVisibleFence,
        bool overDesktopItem,
        bool overExplorerDesktop) =>
        insideDesktopWorkArea && !overVisibleFence && !overDesktopItem && overExplorerDesktop;

    internal static bool ShouldClearSelectionForGlobalClick(
        bool overMiniFencesWindow,
        bool overExplorerDesktop,
        bool overDesktopItem) =>
        !overMiniFencesWindow && (!overExplorerDesktop || !overDesktopItem);

    internal static bool ShouldClearKeyboardFocusForGlobalClick(bool overMiniFencesWindow) =>
        !overMiniFencesWindow;

    private bool IsScreenPointOverLooseDesktopIcon(System.Drawing.Point screenPoint)
    {
        if (!TryGetWorkspacePointFromPhysicalScreen(screenPoint, out var workspacePoint)) return false;
        foreach (var icon in Workspace.Children.OfType<DesktopLooseIconControl>())
        {
            if (icon.Visibility != Visibility.Visible) continue;
            var left = Canvas.GetLeft(icon);
            var top = Canvas.GetTop(icon);
            if (double.IsNaN(left) || double.IsNaN(top)) continue;
            var width = icon.ActualWidth > 0 ? icon.ActualWidth : icon.Width;
            var height = icon.ActualHeight > 0 ? icon.ActualHeight : icon.MinHeight;
            if (IsPointInsideLooseIconBounds(workspacePoint, left, top, width, height)) return true;
        }
        return false;
    }

    internal static bool IsPointInsideLooseIconBounds(
        System.Windows.Point workspacePoint,
        double left,
        double top,
        double width,
        double height) =>
        width > 0 && height > 0 &&
        new Rect(left, top, width, height).Contains(workspacePoint);

    internal static bool ShouldUpdateDragFeedbackFromMouseMessage(bool dragFeedbackActive, int message) =>
        dragFeedbackActive && message == WmMouseMove;

    internal static bool ShouldHandleDesktopDoubleClick(
        bool doubleClickEnabled,
        bool desktopIntegrationEnabled) =>
        doubleClickEnabled && desktopIntegrationEnabled;

    private void CommitInlineRenamesOutside(System.Drawing.Point screenPoint, long mouseEventTicks = long.MaxValue)
    {
        var wpfPoint = new System.Windows.Point(screenPoint.X, screenPoint.Y);
        var fences = Workspace.Children.OfType<FenceControl>().ToArray();
        foreach (var fence in fences)
        {
            fence.CommitInlineRenameIfPointerOutside(wpfPoint, mouseEventTicks);
        }
    }

    private void CommitLooseDesktopRenamesOutside(System.Drawing.Point screenPoint)
    {
        if (!IsMiniFencesWindowAtScreenPoint(screenPoint) && !IsExplorerDesktopPoint(screenPoint)) return;
        var physicalPoint = new System.Windows.Point(screenPoint.X, screenPoint.Y);
        foreach (var icon in Workspace.Children.OfType<DesktopLooseIconControl>())
            icon.CommitIndependentRenameIfPointerOutside(physicalPoint);
    }

    internal bool IsMiniFencesWindowAtScreenPoint(System.Drawing.Point screenPoint)
    {
        var window = WindowFromPoint(new NativePoint(screenPoint.X, screenPoint.Y));
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        // Settings, dialogs, ComboBox popups and ContextMenus use their own HWNDs.
        // Treat every window owned by this process as MiniFences so the desktop
        // mouse hook cannot clear WPF focus before their Click/DropDown events run.
        return processId == (uint)Environment.ProcessId;
    }

    private bool IsScreenPointInsideDesktopWorkArea(System.Drawing.Point screenPoint)
    {
        var workArea = new System.Windows.Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
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
        var desktopHost = FindDesktopViewHost();
        if (!IsWindowInHierarchy(handle, desktopHost)) return false;
        while (handle != IntPtr.Zero)
        {
            var className = GetWindowClassName(handle);
            if (ShouldTreatShellWindowAsDesktop(className, belongsToDesktopHost: true))
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

    private static bool IsWindowInHierarchy(IntPtr window, IntPtr ancestor)
    {
        if (window == IntPtr.Zero || ancestor == IntPtr.Zero) return false;
        for (var depth = 0; window != IntPtr.Zero && depth < 32; depth++)
        {
            if (window == ancestor) return true;
            window = GetParent(window);
        }
        return false;
    }

    internal static bool ShouldTreatShellWindowAsDesktop(string className, bool belongsToDesktopHost) =>
        belongsToDesktopHost && IsDesktopWindowClass(className);

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

    private bool IsPointOverFenceItem(System.Drawing.Point screenPoint)
    {
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            if (!IsScreenPointOverFence(fence, screenPoint)) continue;
            try
            {
                var local = fence.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
                for (DependencyObject? current = fence.InputHitTest(local) as DependencyObject;
                     current != null;
                     current = VisualTreeHelper.GetParent(current))
                {
                    if (current is System.Windows.Controls.ListViewItem) return true;
                    if (ReferenceEquals(current, fence)) break;
                }
            }
            catch
            {
                // An uncertain hit is treated as outside an item so a stale
                // selection cannot survive clicks in other windows/blank areas.
            }
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
        if (msg == WmAppDesktopStressToggle &&
            string.Equals(Environment.GetEnvironmentVariable("MINIFENCES_DESKTOP_STRESS_TEST"), "1", StringComparison.Ordinal))
        {
            PrepareForShowDesktopToggle("desktop stress harness");
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == App.WmExitMiniFences)
        {
            handled = true;
            Dispatcher.BeginInvoke(ExitApplication);
            return IntPtr.Zero;
        }

        if (msg == WmHotkey)
        {
            handled = true;
            var hotkeyId = wParam.ToInt32();
            ActivateHotkey(hotkeyId, "Windows WM_HOTKEY");
            return IntPtr.Zero;
        }

        if (msg == WmSysCommand && (wParam.ToInt64() & 0xfff0) == ScMinimize)
        {
            handled = true;
            if (_useTopLevelDesktopCompatibilityMode)
                EnterShowDesktopMode("minimize system command");
            AppLogger.Log("Ignored minimize system command for the desktop layer.");
            return IntPtr.Zero;
        }

        if (msg == WmWindowPosChanging &&
            _useTopLevelDesktopCompatibilityMode &&
            !_isExiting &&
            lParam != IntPtr.Zero)
        {
            var windowPos = Marshal.PtrToStructure<WindowPos>(lParam);
            var windowPosChanged = false;
            if (ShouldBlockTopLevelDesktopHide(true, false, windowPos.Flags))
            {
                windowPos.Flags &= ~SwpHideWindow;
                windowPos.Flags |= SwpNoZOrder;
                windowPosChanged = true;
                AppLogger.Log("Prevented Show Desktop from hiding the top-level desktop compatibility window.");
                EnterShowDesktopMode("Show Desktop hide request");
            }
            else if (ShouldBlockUnsolicitedDesktopZOrderChange(
                         compatibilityMode: true,
                         isExiting: false,
                         _fencesTopmost,
                         _desktopZOrderUpdateInProgress,
                         windowPos.Flags))
            {
                windowPos.Flags |= SwpNoZOrder;
                windowPosChanged = true;
                AppLogger.Log("Prevented an unsolicited Shell z-order change for the desktop layer.");
            }

            if (windowPosChanged)
                Marshal.StructureToPtr(windowPos, lParam, false);
        }

        if (msg == WmNcHitTest && !_desktopItemDragActive &&
            !IsScreenPointOverVisibleFence(lParam))
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    internal static bool ShouldBlockTopLevelDesktopHide(
        bool compatibilityMode,
        bool isExiting,
        uint windowPositionFlags) =>
        compatibilityMode && !isExiting && (windowPositionFlags & SwpHideWindow) != 0;

    internal static bool ShouldBlockUnsolicitedDesktopZOrderChange(
        bool compatibilityMode,
        bool isExiting,
        bool fencesTopmost,
        bool desktopZOrderUpdateInProgress,
        uint windowPositionFlags) =>
        compatibilityMode &&
        !isExiting &&
        !fencesTopmost &&
        !desktopZOrderUpdateInProgress &&
        (windowPositionFlags & SwpNoZOrder) == 0;

    private void UpdateDesktopWindowRegion()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }
        if (ShouldUseFullDesktopWindowRegion(
                _desktopItemDragActive,
                _fenceHeaderDragActive,
                _dragFeedbackActive))
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
            var pixelsPerDip = GetClientPixelsPerDip(handle);
            var workspaceOrigin = Workspace.TranslatePoint(new System.Windows.Point(0, 0), this);
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
                    var topLeft = new System.Windows.Point(
                        (workspaceOrigin.X + left) * pixelsPerDip.X,
                        (workspaceOrigin.Y + top) * pixelsPerDip.Y);
                    var bottomRight = new System.Windows.Point(
                        (workspaceOrigin.X + left + width) * pixelsPerDip.X,
                        (workspaceOrigin.Y + top + height) * pixelsPerDip.Y);
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
                    var topLeft = new System.Windows.Point(
                        (workspaceOrigin.X + left) * pixelsPerDip.X,
                        (workspaceOrigin.Y + top) * pixelsPerDip.Y);
                    var bottomRight = new System.Windows.Point(
                        (workspaceOrigin.X + left + width) * pixelsPerDip.X,
                        (workspaceOrigin.Y + top + height) * pixelsPerDip.Y);
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

    internal static bool ShouldUseFullDesktopWindowRegion(
        bool desktopItemDragActive,
        bool fenceHeaderDragActive,
        bool dragFeedbackActive) =>
        desktopItemDragActive || fenceHeaderDragActive;

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
        if (!TryGetWorkspacePointFromPhysicalScreen(screenPoint, out var workspacePoint)) return false;
        var width = Workspace.ActualWidth > 0 ? Workspace.ActualWidth : ActualWidth;
        var height = Workspace.ActualHeight > 0 ? Workspace.ActualHeight : ActualHeight;
        return IsPointInsideWorkspace(workspacePoint, width, height);
    }

    internal static bool IsPointInsideWorkspace(System.Windows.Point point, double width, double height) =>
        width > 0 && height > 0 && point.X >= 0 && point.Y >= 0 && point.X < width && point.Y < height;

    private bool IsScreenPointOverVisibleFence(System.Windows.Point screenPoint)
    {
        if (!TryGetWorkspacePointFromPhysicalScreen(
                new System.Drawing.Point((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)),
                out var workspacePoint)) return false;

        var hit = false;

        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            if (IsWorkspacePointOverFence(fence, workspacePoint))
            {
                hit = true;
                break;
            }
        }

        if (!hit) foreach (var icon in Workspace.Children.OfType<DesktopLooseIconControl>())
        {
            if (icon.Visibility != Visibility.Visible) continue;
            var width = icon.ActualWidth > 0 ? icon.ActualWidth : icon.Width;
            var height = icon.ActualHeight > 0 ? icon.ActualHeight : icon.MinHeight;
            var bounds = new Rect(Canvas.GetLeft(icon), Canvas.GetTop(icon), width, height);
            if (!bounds.Contains(workspacePoint)) continue;
            hit = true;
            break;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("MINIFENCES_HIT_TEST_LOG"), "1", StringComparison.Ordinal))
            AppLogger.Log($"Desktop hit test: screen={screenPoint.X:0},{screenPoint.Y:0}; workspace={workspacePoint.X:0.##},{workspacePoint.Y:0.##}; hit={hit}.");
        return hit;
    }

    private bool IsScreenPointOverFence(FenceControl fence, System.Drawing.Point screenPoint) =>
        TryGetWorkspacePointFromPhysicalScreen(screenPoint, out var workspacePoint) &&
        IsWorkspacePointOverFence(fence, workspacePoint);

    internal bool IsTopmostFenceAtScreenPoint(FenceControl candidate, System.Drawing.Point screenPoint)
    {
        if (!TryGetWorkspacePointFromPhysicalScreen(screenPoint, out var workspacePoint)) return false;
        return ReferenceEquals(GetTopmostFenceAtWorkspacePoint(
            Workspace.Children.OfType<FenceControl>(), workspacePoint), candidate);
    }

    internal bool TryActivateTopmostFenceDragTarget(FenceControl candidate, System.Drawing.Point screenPoint)
    {
        if (!IsTopmostFenceAtScreenPoint(candidate, screenPoint)) return false;
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            if (!ReferenceEquals(fence, candidate)) fence.ClearDragHighlight();
        }
        return true;
    }

    internal static FenceControl? GetTopmostFenceAtWorkspacePoint(
        IEnumerable<FenceControl> fences,
        System.Windows.Point workspacePoint) =>
        fences
            .Select((fence, childOrder) => new { fence, childOrder })
            .Where(entry => IsWorkspacePointOverFence(entry.fence, workspacePoint))
            .OrderByDescending(entry => System.Windows.Controls.Panel.GetZIndex(entry.fence))
            .ThenByDescending(entry => entry.childOrder)
            .Select(entry => entry.fence)
            .FirstOrDefault();

    private bool TryGetWorkspacePointFromPhysicalScreen(
        System.Drawing.Point screenPoint,
        out System.Windows.Point workspacePoint)
    {
        workspacePoint = default;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;
        try
        {
            var clientPixels = new NativePoint(screenPoint.X, screenPoint.Y);
            if (!ScreenToClient(handle, ref clientPixels)) return false;
            var pixelsPerDip = GetClientPixelsPerDip(handle);
            var workspaceOrigin = Workspace.TranslatePoint(new System.Windows.Point(0, 0), this);
            workspacePoint = ConvertClientPhysicalPixelsToWorkspaceDips(
                new System.Windows.Point(clientPixels.X, clientPixels.Y),
                pixelsPerDip.X,
                pixelsPerDip.Y,
                workspaceOrigin);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to convert physical screen point into the desktop workspace", ex);
            return false;
        }
    }

    internal static System.Windows.Point ConvertClientPhysicalPixelsToWorkspaceDips(
        System.Windows.Point clientPhysicalPixels,
        double pixelsPerDipX,
        double pixelsPerDipY,
        System.Windows.Point workspaceOriginDips)
    {
        if (!double.IsFinite(pixelsPerDipX) || pixelsPerDipX <= 0) pixelsPerDipX = 1;
        if (!double.IsFinite(pixelsPerDipY) || pixelsPerDipY <= 0) pixelsPerDipY = 1;
        return new System.Windows.Point(
            clientPhysicalPixels.X / pixelsPerDipX - workspaceOriginDips.X,
            clientPhysicalPixels.Y / pixelsPerDipY - workspaceOriginDips.Y);
    }

    private System.Windows.Point GetClientPixelsPerDip(IntPtr handle)
    {
        var fallback = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                       ?? System.Windows.Media.Matrix.Identity;
        var scaleX = fallback.M11 > 0 ? fallback.M11 : 1;
        var scaleY = fallback.M22 > 0 ? fallback.M22 : 1;
        if (!GetClientRect(handle, out var clientRect))
            return new System.Windows.Point(scaleX, scaleY);

        var logicalWidth = ActualWidth;
        var logicalHeight = ActualHeight;
        var physicalWidth = clientRect.Right - clientRect.Left;
        var physicalHeight = clientRect.Bottom - clientRect.Top;
        if (logicalWidth > 0 && physicalWidth > 0)
        {
            var candidate = physicalWidth / logicalWidth;
            if (double.IsFinite(candidate) && candidate is >= 0.25 and <= 8) scaleX = candidate;
        }
        if (logicalHeight > 0 && physicalHeight > 0)
        {
            var candidate = physicalHeight / logicalHeight;
            if (double.IsFinite(candidate) && candidate is >= 0.25 and <= 8) scaleY = candidate;
        }
        return new System.Windows.Point(scaleX, scaleY);
    }

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
        SetFencesVisibility(_fencesHidden);
    }

    private void SetFencesVisibility(bool visible)
    {
        CloseOpenContextMenus(force: true);
        _fencesHidden = !visible;
        if (visible)
        {
            EnsureDesktopWindowVisible();
            ClampAllFencesToWorkspace();
        }
        _config.FencesHidden = _fencesHidden;
        ApplyFenceVisibility();
        ScheduleConfigSave();
        AppLogger.Log(visible
            ? "All Fences explicitly shown."
            : "All Fences explicitly hidden.");
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
        var fences = _config.Fences.Where(fence => IsFenceVisibleOnPage(fence, _config.CurrentPage) && !fence.IsLocked).ToList();
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
            if (!ShouldQueueAutoOrganization(path, _suppressedAutoOrganizePaths))
            {
                AppLogger.Log($"Skipped automatic organization for user-renamed desktop item: {path}");
                return;
            }
            _pendingAutoOrganizePaths.Add(path);
            _autoOrganizeTimer.Stop();
            _autoOrganizeTimer.Start();
        });
    }

    internal static bool ShouldQueueAutoOrganization(string path, ISet<string> suppressedPaths) =>
        !suppressedPaths.Contains(path);

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
        var normalized = LocalizationService.NormalizeLanguage(language);
        if (string.Equals(_loc.Language, normalized, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_config.Language, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _loc.Language = normalized;
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
        DesktopSwapMonitorsMenuItem.Header = string.Equals(_loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase)
            ? "交换显示器内容"
            : "Swap monitor contents";
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
        if (_checkForUpdatesMenuItem != null) _checkForUpdatesMenuItem.Text = _loc.T("CheckForUpdates");
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
            icon.Visibility = !ShouldShowLooseDesktopIcons(_fencesHidden, _config.EnableDesktopIconIntegration)
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
        fence.Visibility = !ShouldShowFence(
                               _fencesHidden,
                               _config.EnableDesktopIconIntegration,
                               fence.Config.IsDesktopGroup)
            ? Visibility.Hidden
            : Visibility.Visible;
    }

    internal static bool ShouldShowLooseDesktopIcons(bool fencesHidden, bool desktopIntegrationEnabled) =>
        !fencesHidden && desktopIntegrationEnabled;

    internal static bool ShouldShowFence(bool fencesHidden, bool desktopIntegrationEnabled, bool isDesktopGroup) =>
        !fencesHidden && (!isDesktopGroup || desktopIntegrationEnabled);

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
        return pageCount > 1 && !_config.Fences.Any(fence => !fence.ShowOnAllPages && fence.PageIndex == _config.CurrentPage);
    }

    internal void RequestApplicationRestart()
    {
        if (_isExiting) return;

        try
        {
            SaveConfigWithWarning();
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                throw new InvalidOperationException("The current process path is unavailable.");

            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            if (string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                var assemblyPath = typeof(MainWindow).Assembly.Location;
                if (string.IsNullOrWhiteSpace(assemblyPath))
                    throw new InvalidOperationException("The application assembly path is unavailable.");
                startInfo.ArgumentList.Add(assemblyPath);
            }

            startInfo.ArgumentList.Add("--restart-after-exit");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("--background");
            Process.Start(startInfo);
            AppLogger.Log("MiniFences is restarting after a list display setting change.");
            _isExiting = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to restart MiniFences after a list display setting change", ex);
            System.Windows.MessageBox.Show(this,
                _loc.IsChinese ? "设置已保存，但软件自动重启失败，请手动重启软件。" : "The setting was saved, but automatic restart failed. Please restart MiniFences manually.",
                "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
        var historyBefore = _configService.CaptureLayout(_config);
        var assigned = _autoOrganizerService.AssignDesktopItemsByTypeWithUndo(_config);
        foreach (var fence in _config.Fences.Where(fence => !existingIds.Contains(fence.Id)))
        {
            PlaceFenceOnCurrentPage(fence);
        }

        RenderFences();
        SaveConfigWithWarning();
        _actionHistoryService.RecordLayoutChange("自动整理桌面", historyBefore, _configService.CaptureLayout(_config));
        RefreshUndoCommands();

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
    internal bool IsMiniFencesEnabled =>
        IsMiniFencesEnabledState(_fencesHidden, _config.EnableDesktopIconIntegration);
    internal static bool IsMiniFencesEnabledState(bool fencesHidden, bool desktopIconIntegrationEnabled) =>
        !fencesHidden && desktopIconIntegrationEnabled;
    internal bool AreFencesTopmost => _fencesTopmost;
    internal bool IsDesktopDoubleClickEnabled => _config.EnableDesktopDoubleClick;
    internal bool IsDesktopIconIntegrationEnabled => _config.EnableDesktopIconIntegration;
    internal string TabViewMode => _config.TabViewMode;
    internal string TabWidthMode => _config.TabWidthMode;
    internal string TabSelectionStyle => _config.TabSelectionStyle;
    internal string TabSelectionLineColor => _config.TabSelectionLineColor;
    internal string TabSelectionLineThickness => _config.TabSelectionLineThickness;
    internal bool IsTabCreationEnabled => _config.EnableTabCreation;
    internal bool IsTabCreationConfirmationEnabled => _config.ConfirmTabCreation;
    internal bool IsHoverTabSwitchEnabled => _config.HoverSwitchTabs;
    internal bool IsRollupEnabled => _config.EnableRollup;
    internal bool IsDoubleClickTitleRollupEnabled => _config.DoubleClickTitleRollup;
    internal bool IsAutoRollupAtScreenEdgeEnabled => _config.AutoRollupAtScreenEdge;
    internal bool IsBottomEdgeRollupAllowed => _config.AllowBottomEdgeRollup;
    internal bool IsBottomDockTitleAtBottomEnabled => _config.BottomDockTitleAtBottom;
    internal bool IsTopDockTitleAtBottomOnExpandEnabled => _config.TopDockTitleAtBottomOnExpand;
    internal bool IsClickTitleToExpandEnabled => _config.ClickTitleToExpand;
    internal bool IsHoverTitleToExpandEnabled => _config.HoverTitleToExpand;
    internal string PreviousPageHotkey => _config.PreviousPageHotkey;
    internal string NextPageHotkey => _config.NextPageHotkey;
    internal string ToggleTopmostHotkey => _config.ToggleTopmostHotkey;
    internal IReadOnlyList<string> DirectPageHotkeys => _config.DirectPageHotkeys;
    internal bool IsSnapToGridEnabled => _config.EnableSnapToGrid;
    internal int GridSize => _config.GridSize;
    internal bool IsSnapWhileDraggingEnabled => _config.SnapWhileDragging;
    internal bool IsAutoOrganizeEnabled => _config.EnableAutoOrganizeNewDesktopItems;
    internal string DefaultAutoOrganizeFenceId => _config.DefaultAutoOrganizeFenceId;
    internal bool IsStartWithWindowsEnabled => _startupService.IsEnabled();
    internal bool CanDeleteSettingsCurrentPage => CanDeleteCurrentPage(GetPageCount());
    internal IReadOnlyList<ActionTransaction> GetActionHistory() => _actionHistoryService.GetTransactions();
    internal void CreateDiagnosticBundle(string destinationZip) =>
        DiagnosticBundleService.Create(destinationZip, _config, _actionHistoryService.GetTransactions());
    internal bool CanUndoLastAction => _actionHistoryService.GetNextUndo() is not null;

    internal void UndoLastActionFromSettings(Window owner) => UndoLastAction(owner);

    internal void ClearActionHistory()
    {
        _actionHistoryService.Clear();
        RefreshUndoCommands();
    }

    internal void OpenHistoryLocation(ActionTransaction transaction, Window owner)
    {
        var path = transaction.Entries.SelectMany(entry => new[] { entry.DestinationPath, entry.SourcePath })
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (string.IsNullOrWhiteSpace(path)) return;
        var location = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        try { if (!string.IsNullOrWhiteSpace(location)) OpenShellPath(location); }
        catch (Exception ex) { System.Windows.MessageBox.Show(owner, ex.Message, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal void OpenRecycleBin(Window owner)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
        catch (Exception ex) { System.Windows.MessageBox.Show(owner, ex.Message, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

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
        if (fence is null) return Array.Empty<FolderItem>();
        var service = new FolderItemService();
        return fence.IsDesktopGroup
            ? service.LoadAssignedItems(fence.AssignedPaths)
            : service.LoadItems(fence.FolderPath);
    }

    internal IReadOnlyList<FolderItem> SettingsGetUnassignedDesktopItems()
    {
        var assigned = _config.Fences.Where(fence => fence.IsDesktopGroup)
            .SelectMany(fence => fence.AssignedPaths)
            .Select(path => FolderItemService.IsShellNamespacePath(path) ? path : TryGetFullPath(path))
            .Where(path => path != null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = FolderItemService.CollapseDesktopEntries(FolderItemService.EnumerateFileSystemEntriesSafe(GetDesktopRoots())
                .Where(ShouldShowLooseDesktopItem)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            .Where(path => !assigned.Contains(Path.GetFullPath(path)));
        var service = new FolderItemService();
        var systemItems = FolderItemService.LoadVisibleDesktopShellItems()
            .Where(item => !assigned.Contains(item.FullPath));
        return systemItems.Concat(service.LoadAssignedItems(paths)).ToArray();
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
        var linkedTargets = GetContentLinkedFences(_config, fence).Where(candidate => candidate.IsDesktopGroup).ToArray();
        if (!_actionHistoryService.ExecuteMembershipChange("取消分配桌面项目", _config,
                () => linkedTargets.Sum(target => target.AssignedPaths.RemoveAll(existing =>
                    paths.Contains(existing, StringComparer.OrdinalIgnoreCase))) > 0)) return;
        RenderFences();
        SaveConfigWithWarning();
        RefreshUndoCommands();
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
            ? fence.AssignedPaths.Count(path => FolderItemService.IsShellNamespacePath(path) || File.Exists(path) || Directory.Exists(path))
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
        ExactNames = rule.ExactNames,
        Extensions = rule.Extensions,
        ShortcutTargetPattern = rule.ShortcutTargetPattern,
        FoldersOnly = rule.FoldersOnly,
        MinimumSizeMb = rule.MinimumSizeMb,
        MaximumSizeMb = rule.MaximumSizeMb
    };

    internal void SettingsCreateFence() => CreateNewDesktopGroup();
    internal void SettingsRefreshAll() => RefreshAllFences();
    internal void SettingsToggleFences() => ToggleFencesVisibility();
    internal void SettingsSetFencesVisible(bool visible) => SetFencesVisibility(visible);
    internal void SettingsSetMiniFencesEnabled(bool enabled)
    {
        AppLogger.Log($"Settings requested MiniFences enabled state: {enabled}.");
        if (enabled)
        {
            _config.EnableDesktopIconIntegration = true;
            _fencesHidden = false;
            _config.FencesHidden = false;
            _restoreNativeDesktopIconsOnExit = _windowsDesktopIconsVisible;
            UpdateNativeDesktopIconVisibility();
            EnsureDesktopWindowVisible();
            ClampAllFencesToWorkspace();
        }
        else
        {
            _fencesHidden = true;
            _config.FencesHidden = true;
            _config.EnableDesktopIconIntegration = false;
            if (_desktopIconLayoutService.SetVisible(true))
            {
                _nativeDesktopIconsHiddenByMiniFences = false;
                _windowsDesktopIconsVisible = true;
            }
        }
        ApplyFenceVisibility();
        SaveConfigWithWarning();
        AppLogger.Log(enabled
            ? "MiniFences opened from Welcome; desktop integration enabled and Fences shown."
            : "MiniFences closed from Welcome; Fences hidden and Explorer desktop icons restored.");
    }
    internal void SettingsToggleFencesTopmost()
    {
        if (_fencesTopmost)
            SetFencesTopmost(false, ensureVisible: true);
        else
            SetFencesTopmost(true, ensureVisible: true);
    }
    internal void SettingsSwitchPage(int pageIndex) => SwitchPage(pageIndex);
    internal void SettingsCreatePage() => CreateNewPage();
    internal void SettingsMoveFenceToPage(string fenceId, int pageIndex)
    {
        var fence = _config.Fences.FirstOrDefault(candidate => string.Equals(candidate.Id, fenceId, StringComparison.OrdinalIgnoreCase));
        if (fence == null) return;
        var targetPage = Math.Clamp(pageIndex, 0, Math.Max(0, GetPageCount() - 1));
        var moveTargets = GetFencePageMoveTargets(_config, fence);
        if (moveTargets.All(candidate => candidate.PageIndex == targetPage)) return;
        SyncAllFenceLayouts();
        foreach (var target in moveTargets) target.PageIndex = targetPage;
        AppLogger.Log($"Moved Fence group containing '{fence.Title}' ({moveTargets.Count} Fence(s)) to page {targetPage + 1} from settings.");
        RenderFences();
        SaveConfigWithWarning();
    }

    internal bool SettingsCanPinFence(string fenceId)
    {
        var fence = FindFence(fenceId);
        return fence != null && GetContentLinkedFences(_config, fence).Count == 1;
    }

    internal bool SettingsSetFenceShowOnAllPages(string fenceId, bool showOnAllPages)
    {
        var fence = FindFence(fenceId);
        if (fence == null || (showOnAllPages && GetContentLinkedFences(_config, fence).Count > 1)) return false;
        var targets = GetFencePageMoveTargets(_config, fence);
        foreach (var target in targets) target.ShowOnAllPages = showOnAllPages;
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Fence '{fence.Title}' show on every page changed to {showOnAllPages}.");
        return true;
    }

    internal bool SettingsCanCreateLinkedFenceCopy(string fenceId, int pageIndex)
    {
        var source = FindFence(fenceId);
        if (source == null || source.ShowOnAllPages || pageIndex < 0 || pageIndex >= GetPageCount() || source.PageIndex == pageIndex)
            return false;
        return !GetContentLinkedFences(_config, source).Any(candidate => candidate.PageIndex == pageIndex);
    }

    internal bool SettingsCreateLinkedFenceCopy(string fenceId, int pageIndex)
    {
        var source = FindFence(fenceId);
        if (source == null || !SettingsCanCreateLinkedFenceCopy(fenceId, pageIndex)) return false;
        SyncAllFenceLayouts();
        source.ContentLinkId ??= Guid.NewGuid().ToString("N");
        var copy = CreateLinkedFenceCopy(source, pageIndex);
        ClampFenceConfigToWorkspace(copy);
        _config.Fences.Add(copy);
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Created synchronized Fence copy '{source.Title}' on page {pageIndex + 1}; Link={source.ContentLinkId}.");
        return true;
    }

    internal bool SettingsCanSynchronizeLinkedFenceLayout(string fenceId)
    {
        var fence = FindFence(fenceId);
        return fence != null && !fence.ShowOnAllPages && GetContentLinkedFences(_config, fence).Count > 1;
    }

    internal bool SettingsSetSynchronizeLinkedFenceLayout(string fenceId, bool enabled)
    {
        var fence = FindFence(fenceId);
        if (fence == null || !SettingsCanSynchronizeLinkedFenceLayout(fenceId)) return false;
        SyncAllFenceLayouts();
        foreach (var linked in GetContentLinkedFences(_config, fence)) linked.SynchronizeLinkedLayout = enabled;
        if (enabled) SynchronizeLinkedFenceLayout(_config, fence);
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Synchronized Fence layout for '{fence.Title}' changed to {enabled}.");
        return true;
    }

    internal static FenceConfig CreateLinkedFenceCopy(FenceConfig source, int pageIndex) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Title = source.Title,
        FolderPath = source.FolderPath,
        Kind = source.Kind,
        AssignedPaths = source.AssignedPaths.ToList(),
        PageIndex = Math.Max(0, pageIndex),
        ShowOnAllPages = false,
        ContentLinkId = source.ContentLinkId,
        SynchronizeLinkedLayout = source.SynchronizeLinkedLayout,
        Left = source.Left,
        Top = source.Top,
        Width = source.Width,
        Height = source.Height,
        LayerOrder = source.LayerOrder,
        ExpandedHeight = source.ExpandedHeight,
        BackgroundColor = source.BackgroundColor,
        HeaderColor = source.HeaderColor,
        HeaderGradientEnabled = source.HeaderGradientEnabled,
        HeaderGradientColor = source.HeaderGradientColor,
        Opacity = source.Opacity,
        TitleAlignment = source.TitleAlignment,
        ShowPath = source.ShowPath,
        UseCleanStyle = source.UseCleanStyle,
        SortMode = source.SortMode,
        IsLocked = source.IsLocked,
        IsCollapsed = source.IsCollapsed,
        EnableHoverExpand = source.EnableHoverExpand,
        EdgeDock = source.EdgeDock,
        PortalCurrentPath = source.PortalCurrentPath,
        PortalViewMode = source.PortalViewMode,
        PortalIconSize = source.PortalIconSize,
        PortalItemSpacing = source.PortalItemSpacing,
        ListShowType = source.ListShowType,
        ListShowSize = source.ListShowSize,
        ListShowTime = source.ListShowTime
    };
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
        else
        {
            if (_desktopIconLayoutService.SetVisible(true))
            {
                _nativeDesktopIconsHiddenByMiniFences = false;
                _windowsDesktopIconsVisible = true;
                AppLogger.Log("Explorer desktop icons restored after desktop integration was disabled.");
            }
        }
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Desktop icon integration changed: {enabled}.");
    }

    internal void SettingsSetTabOptions(string mode, string widthMode, string selectionStyle,
        string selectionLineColor, string selectionLineThickness,
        bool enableCreation, bool confirmCreation, bool hoverSwitch)
    {
        var normalizedMode = string.Equals(mode, "Strip", StringComparison.OrdinalIgnoreCase) ? "Strip" : "Compact";
        var normalizedWidthMode = string.Equals(widthMode, "Equal", StringComparison.OrdinalIgnoreCase)
            ? "Equal"
            : string.Equals(widthMode, "Adaptive", StringComparison.OrdinalIgnoreCase) ? "Adaptive" : "Content";
        var normalizedSelectionStyle = FenceControl.NormalizeTabSelectionStyle(selectionStyle);
        var normalizedLineColor = FenceControl.NormalizeTabSelectionLineColor(selectionLineColor);
        var normalizedLineThickness = FenceControl.NormalizeTabSelectionLineThickness(selectionLineThickness);
        if (string.Equals(_config.TabViewMode, normalizedMode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_config.TabWidthMode, normalizedWidthMode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_config.TabSelectionStyle, normalizedSelectionStyle, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_config.TabSelectionLineColor, normalizedLineColor, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_config.TabSelectionLineThickness, normalizedLineThickness, StringComparison.OrdinalIgnoreCase) &&
            _config.EnableTabCreation == enableCreation && _config.ConfirmTabCreation == confirmCreation &&
            _config.HoverSwitchTabs == hoverSwitch)
            return;

        _config.TabViewMode = normalizedMode;
        _config.TabWidthMode = normalizedWidthMode;
        _config.TabSelectionStyle = normalizedSelectionStyle;
        _config.TabSelectionLineColor = normalizedLineColor;
        _config.TabSelectionLineThickness = normalizedLineThickness;
        _config.EnableTabCreation = enableCreation;
        _config.ConfirmTabCreation = confirmCreation;
        _config.HoverSwitchTabs = hoverSwitch;
        RefreshVisibleFenceTabPresentation();
        SaveConfigWithWarning();
    }

    private void RefreshVisibleFenceTabPresentation()
    {
        foreach (var control in Workspace.Children.OfType<FenceControl>())
        {
            var tabs = GetTabs(control.Config.TabGroupId);
            if (tabs.Count == 0) tabs = [control.Config];
            var tabIndex = tabs.FindIndex(tab =>
                string.Equals(tab.Id, control.Config.Id, StringComparison.OrdinalIgnoreCase));
            control.SetTabStatus(tabs.Count, Math.Max(0, tabIndex), tabs.Select(tab => tab.Title).ToArray(),
                string.Equals(_config.TabViewMode, "Strip", StringComparison.OrdinalIgnoreCase),
                _config.HoverSwitchTabs,
                string.Equals(_config.TabWidthMode, "Equal", StringComparison.OrdinalIgnoreCase), tabs,
                adaptiveTabWidths: string.Equals(_config.TabWidthMode, "Adaptive", StringComparison.OrdinalIgnoreCase),
                selectionStyle: _config.TabSelectionStyle,
                selectionLineColor: _config.TabSelectionLineColor,
                selectionLineThickness: _config.TabSelectionLineThickness);
        }
        AppLogger.Log("Refreshed visible Fence tab presentation without reloading desktop items.");
    }

    internal void SettingsSetRollupOptions(bool enabled, bool doubleClick, bool autoEdge,
        bool allowBottomEdge, bool bottomTitle, bool topTitleAtBottomOnExpand, bool clickExpand, bool hoverExpand)
    {
        _config.EnableRollup = enabled;
        _config.DoubleClickTitleRollup = doubleClick;
        _config.AutoRollupAtScreenEdge = autoEdge;
        _config.AllowBottomEdgeRollup = allowBottomEdge;
        _config.BottomDockTitleAtBottom = bottomTitle;
        _config.TopDockTitleAtBottomOnExpand = topTitleAtBottomOnExpand;
        _config.ClickTitleToExpand = clickExpand;
        _config.HoverTitleToExpand = hoverExpand;
        RenderFences();
        SaveConfigWithWarning();
    }

    internal bool SettingsSetHotkeys(string previous, string next, string topmost, IReadOnlyList<string> directPages, out string error)
    {
        var normalizedDirectPages = Enumerable.Range(0, 12)
            .Select(index => index < directPages.Count ? directPages[index].Trim() : "")
            .ToArray();
        if (ParseHotkey(previous) == null || ParseHotkey(next) == null || ParseHotkey(topmost) == null ||
            normalizedDirectPages.Any(value => !string.IsNullOrWhiteSpace(value) && ParseHotkey(value) == null))
        {
            error = _loc.T("InvalidHotkey");
            return false;
        }
        var normalized = new[] { previous.Trim(), next.Trim(), topmost.Trim() };
        var allAssigned = normalized.Concat(normalizedDirectPages.Where(value => !string.IsNullOrWhiteSpace(value))).ToArray();
        if (allAssigned.Distinct(StringComparer.OrdinalIgnoreCase).Count() != allAssigned.Length)
        {
            error = _loc.T("DuplicateHotkey");
            return false;
        }
        _config.PreviousPageHotkey = normalized[0];
        _config.NextPageHotkey = normalized[1];
        _config.ToggleTopmostHotkey = normalized[2];
        _config.DirectPageHotkeys = normalizedDirectPages.ToList();
        RegisterGlobalHotkeys();
        SaveConfigWithWarning();
        error = "";
        return true;
    }

    internal bool SettingsSetGridOptions(bool enabled, int gridSize, bool snapWhileDragging, out string error)
    {
        if (gridSize is < 1 or > 256)
        {
            error = _loc.T("InvalidGridSize");
            return false;
        }
        _config.EnableSnapToGrid = enabled;
        _config.GridSize = gridSize;
        _config.SnapWhileDragging = snapWhileDragging;
        DesktopSnapToGridMenuItem.IsChecked = enabled;
        foreach (var fence in Workspace.Children.OfType<FenceControl>())
        {
            fence.SnapToGrid = enabled;
            fence.GridSize = gridSize;
            fence.SnapWhileDragging = snapWhileDragging;
        }
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
        var dialog = new ColorPickerDialog(currentValue, _loc, chooseHeader)
        {
            Owner = _settingsWindow
        };
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        var value = dialog.SelectedColor;
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

    internal bool SettingsChooseFenceGradientColor(string fenceId)
    {
        var targets = GetAppearanceTargets(fenceId);
        var sample = targets.FirstOrDefault();
        if (sample == null) return false;

        var dialog = new ColorPickerDialog(sample.HeaderGradientColor, _loc, chooseHeader: true)
        {
            Owner = _settingsWindow
        };
        if (dialog.ShowDialog() != true) return false;

        foreach (var fence in targets)
        {
            fence.HeaderGradientColor = dialog.SelectedColor;
            fence.HeaderGradientEnabled = true;
            RefreshFenceAppearance(fence);
        }
        SaveConfigWithWarning();
        return true;
    }

    internal void SettingsSetFenceHeaderGradient(string fenceId, bool enabled)
    {
        foreach (var fence in GetAppearanceTargets(fenceId))
        {
            fence.HeaderGradientEnabled = enabled;
            RefreshFenceAppearance(fence);
        }
        SaveConfigWithWarning();
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

    internal void SettingsSetFenceListColumns(string fenceId, bool showType, bool showSize, bool showTime)
    {
        foreach (var fence in GetAppearanceTargets(fenceId))
        {
            fence.ListShowType = showType;
            fence.ListShowSize = showSize;
            fence.ListShowTime = showTime;
            RefreshFenceListColumns(fence);
        }
        SaveConfigWithWarning();
        AppLogger.Log($"List columns updated in place: type={showType}, size={showSize}, time={showTime}.");
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
            fence.HeaderGradientEnabled = false;
            fence.HeaderGradientColor = "#CC8E5BB7";
            fence.Opacity = 1.0;
            fence.TitleAlignment = "Left";
            fence.ShowPath = true;
            fence.UseCleanStyle = false;
            fence.ListShowType = true;
            fence.ListShowSize = true;
            fence.ListShowTime = true;
            RefreshFenceAppearance(fence);
            RefreshFenceListColumns(fence);
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
            fence.HeaderGradientEnabled = source.HeaderGradientEnabled;
            fence.HeaderGradientColor = source.HeaderGradientColor;
            fence.Opacity = source.Opacity;
            fence.TitleAlignment = source.TitleAlignment;
            fence.ShowPath = source.ShowPath;
            fence.UseCleanStyle = source.UseCleanStyle;
            fence.ListShowType = source.ListShowType;
            fence.ListShowSize = source.ListShowSize;
            fence.ListShowTime = source.ListShowTime;
            RefreshFenceListColumns(fence);
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

    private void RefreshFenceListColumns(FenceConfig fence)
    {
        var control = Workspace.Children.OfType<FenceControl>().FirstOrDefault(candidate =>
            string.Equals(candidate.Config.Id, fence.Id, StringComparison.OrdinalIgnoreCase));
        control?.SetListColumnVisibility(fence.ListShowType, fence.ListShowSize, fence.ListShowTime);
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

        _actionHistoryService.ExecuteFenceChange($"更改 Fence“{fence.Title}”的文件夹", _config, fence, () =>
        {
            var oldFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fence.FolderPath));
            var selectedFolderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(dialog.SelectedPath));
            var hasGenericTitle =
                string.Equals(fence.Title, _loc.T("NewDesktopGroupDefaultName"), StringComparison.CurrentCultureIgnoreCase) ||
                string.Equals(fence.Title, _loc.T("NewFence"), StringComparison.CurrentCultureIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(oldFolderName) &&
                 string.Equals(fence.Title, oldFolderName, StringComparison.CurrentCultureIgnoreCase));
            fence.FolderPath = dialog.SelectedPath;
            var desktopGroup = FolderItemService.IsDesktopRootPath(dialog.SelectedPath);
            fence.PortalCurrentPath = desktopGroup ? null : dialog.SelectedPath;
            fence.Kind = desktopGroup ? FenceConfig.DesktopGroupKind : FenceConfig.FolderPortalKind;
            fence.AssignedPaths.Clear();
            SynchronizeLinkedFenceContent(fence);
            if (hasGenericTitle && !string.IsNullOrWhiteSpace(selectedFolderName))
                fence.Title = selectedFolderName;
            return true;
        });
        RenderFences();
        SaveConfigWithWarning();
        RefreshUndoCommands();
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

        if (!_actionHistoryService.ExecuteFenceDeletion(_config, fence)) return false;
        RenderFences();
        SaveConfigWithWarning();
        AppLogger.Log($"Fence deleted from settings: {fence.Id}; title: {fence.Title}");
        RefreshUndoCommands();
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

    internal void SettingsCheckForUpdates() => _ = CheckForUpdatesAsync(interactive: true);

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
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmMouseWheel = 0x020A;
    private const int WmNcHitTest = 0x0084;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmSysCommand = 0x0112;
    private const int WmHotkey = 0x0312;
    private const int WmAppDesktopStressToggle = 0x803F;
    private const int HotkeyPreviousPage = 4101;
    private const int HotkeyNextPage = 4102;
    private const int HotkeyToggleTopmost = 4103;
    private const int HotkeyDirectPageBase = 4200;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkLeft = 0x25;
    private const uint VkRight = 0x27;
    private const uint VkT = 0x54;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;
    private const int VkSpace = 0x20;
    private const uint VkEscape = 0x1B;
    private const uint VkD = 0x44;
    private const uint VkF1 = 0x70;
    private const uint VkF12 = 0x7B;
    private const int ScMinimize = 0xF020;
    private const int SwShowNoActivate = 4;
    private const uint DwmwaTransitionsForcedDisabled = 3;
    private const uint DwmwaExcludedFromPeek = 12;
    private const uint DwmwaCloak = 13;
    private const uint DwmwaCloaked = 14;
    private const int HtTransparent = -1;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int GwlHwndParent = -8;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExAppWindow = 0x00040000L;
    private const uint GwHwndPrev = 3;
    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const int RgnOr = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpHideWindow = 0x0080;
    private const uint SwpNoOwnerZOrder = 0x0200;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    private delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);
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
    private static extern IntPtr GetForegroundWindow();

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

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("User32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("User32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("User32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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

    [DllImport("User32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("User32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("User32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("User32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("Dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        IntPtr window,
        uint attribute,
        out uint value,
        uint valueSize);

    [DllImport("Dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        uint attribute,
        ref int value,
        uint valueSize);

    [DllImport("User32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("User32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("User32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr eventHookModule,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("User32.dll")]
    private static extern bool UnhookWinEvent(IntPtr eventHook);

    [DllImport("User32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

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
