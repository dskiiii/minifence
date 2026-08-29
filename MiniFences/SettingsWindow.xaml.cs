using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using MiniFences.Models;
using MiniFences.Services;

namespace MiniFences;

public partial class SettingsWindow : Window
{
    private const string AllAppearanceTargetId = "__all_fences__";
    private readonly MainWindow _mainWindow;
    private readonly FolderItemService _folderItemService = new();
    private bool _updatingControls;
    private FenceConfig? _copiedAppearance;
    private System.Windows.Point _pagePreviewDragStart;
    private string? _pagePreviewDragFenceId;
    private int _tabPreviewIndex;
    private string _tabSelectionLineColor = "#FF73D7FF";
    private bool _rollupPreviewCollapsed;
    private bool _rollupPreviewHoverExpanded;
    private DispatcherTimer? _rollupPreviewSingleClickTimer;
    private System.Windows.Point _rollupPreviewDragStart;
    private double _rollupPreviewDragTop;
    private bool _rollupPreviewPointerDown;
    private bool _rollupPreviewDragging;
    private string _rollupPreviewDock = "Standard";
    private bool _displayPreviewTemporarilyHidden;
    private bool _refreshFromMainWindowPending;
    private CancellationTokenSource? _contentIconLoadCancellation;
    private readonly HashSet<System.Windows.Controls.ComboBox> _tracedComboBoxes = [];
    private readonly HashSet<System.Windows.Controls.Button> _tracedButtons = [];
    private System.Windows.Controls.TextBox? _activeHotkeyCaptureBox;
    private string _hotkeyCaptureOriginalValue = "";
    private System.Windows.Media.Brush? _hotkeyCaptureOriginalBorderBrush;
    private Thickness _hotkeyCaptureOriginalBorderThickness;
    private ModifierKeys _hotkeyCaptureModifiers;
    internal bool IsCapturingHotkey => _activeHotkeyCaptureBox != null;

    public SettingsWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        InitializeComponent();
        ConfigureHotkeyCaptureBoxes();
        PreviewMouseDown += SettingsWindow_PreviewMouseDown;
        NavigationList.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            ReloadState();
            EnableUiTraceForTesting();
        };
        Activated += (_, _) => _mainWindow.CancelActiveRenamesForSettings();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) CloseTransientInteractions();
        };
        Deactivated += (_, _) => CancelHotkeyCapture();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseTransientInteractions();
        };
    }

    private void CloseTransientInteractions()
    {
        CancelHotkeyCapture();
        foreach (var comboBox in FindVisualChildren<System.Windows.Controls.ComboBox>(this))
            comboBox.IsDropDownOpen = false;
        _rollupPreviewSingleClickTimer?.Stop();
        _rollupPreviewPointerDown = false;
        _rollupPreviewDragging = false;
        if (RollupPreviewFence?.IsMouseCaptured == true) RollupPreviewFence.ReleaseMouseCapture();
        Keyboard.ClearFocus();
    }

    private void EnableUiTraceForTesting()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MINIFENCES_UI_TRACE"), "1", StringComparison.Ordinal)) return;
        AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((_, e) =>
        {
            if (e.OriginalSource is FrameworkElement element)
                AppLogger.Log($"Settings button Click reached: {element.Name}");
        }), true);
        TraceVisibleComboBoxesForTesting();
    }

    private void TraceVisibleComboBoxesForTesting()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MINIFENCES_UI_TRACE"), "1", StringComparison.Ordinal)) return;
        foreach (var comboBox in FindVisualChildren<System.Windows.Controls.ComboBox>(this))
        {
            if (comboBox.ActualWidth <= 0 || comboBox.ActualHeight <= 0 || !_tracedComboBoxes.Add(comboBox)) continue;
            var origin = comboBox.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0));
            AppLogger.Log($"Settings ComboBox bounds: {comboBox.Name}; Left={origin.X:0}; Top={origin.Y:0}; Width={comboBox.ActualWidth:0}; Height={comboBox.ActualHeight:0}");
            comboBox.DropDownOpened += (_, _) => AppLogger.Log($"Settings ComboBox opened: {comboBox.Name}");
            comboBox.DropDownClosed += (_, _) => AppLogger.Log($"Settings ComboBox closed: {comboBox.Name}");
        }
        foreach (var button in FindVisualChildren<System.Windows.Controls.Button>(this))
        {
            if (button.ActualWidth <= 0 || button.ActualHeight <= 0 || !_tracedButtons.Add(button)) continue;
            var origin = button.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0));
            AppLogger.Log($"Settings Button bounds: {button.Name}; Left={origin.X:0}; Top={origin.Y:0}; Width={button.ActualWidth:0}; Height={button.ActualHeight:0}");
        }
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

    protected override void OnClosed(EventArgs e)
    {
        _rollupPreviewSingleClickTimer?.Stop();
        _contentIconLoadCancellation?.Cancel();
        _contentIconLoadCancellation?.Dispose();
        _contentIconLoadCancellation = null;
        base.OnClosed(e);
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_activeHotkeyCaptureBox is { } hotkeyTextBox)
        {
            CaptureHotkey(hotkeyTextBox, e);
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        var index = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            Key.D0 or Key.NumPad0 => 9,
            _ => -1
        };
        if (index < 0 || index >= NavigationList.Items.Count) return;
        NavigationList.SelectedIndex = index;
        if (NavigationList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item) item.Focus();
        e.Handled = true;
    }

    private void SettingsWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeHotkeyCaptureBox is not { } textBox || e.OriginalSource is not DependencyObject source) return;
        if (!IsDescendantOrSelf(source, textBox))
        {
            CancelHotkeyCapture();
            return;
        }

        var mouseButtonName = e.ChangedButton switch
        {
            MouseButton.Left => "MouseLeft",
            MouseButton.Right => "MouseRight",
            _ => null
        };
        if (mouseButtonName == null) return;
        textBox.Text = FormatCapturedMouseHotkey(mouseButtonName, _hotkeyCaptureModifiers | Keyboard.Modifiers);
        FinishHotkeyCapture(restoreOriginalValue: false);
        textBox.SelectAll();
        e.Handled = true;
    }

    private static bool IsDescendantOrSelf(DependencyObject source, DependencyObject ancestor)
    {
        for (var current = source; current != null; current = GetInputParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static DependencyObject? GetInputParent(DependencyObject current)
    {
        if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(current);
        return LogicalTreeHelper.GetParent(current);
    }

    private void ConfigureHotkeyCaptureBoxes()
    {
        foreach (var textBox in GetHotkeyCaptureTextBoxes())
        {
            textBox.IsReadOnly = true;
            textBox.IsReadOnlyCaretVisible = false;
            textBox.Cursor = System.Windows.Input.Cursors.Hand;
            textBox.GotKeyboardFocus += (_, _) => textBox.SelectAll();
            textBox.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                BeginHotkeyCapture(textBox);
                textBox.Focus();
                textBox.SelectAll();
            };
        }
    }

    private void BeginHotkeyCapture(System.Windows.Controls.TextBox textBox)
    {
        if (ReferenceEquals(_activeHotkeyCaptureBox, textBox)) return;
        CancelHotkeyCapture();
        _activeHotkeyCaptureBox = textBox;
        _hotkeyCaptureOriginalValue = textBox.Text;
        _hotkeyCaptureOriginalBorderBrush = textBox.BorderBrush;
        _hotkeyCaptureOriginalBorderThickness = textBox.BorderThickness;
        _hotkeyCaptureModifiers = ModifierKeys.None;
        _mainWindow.SettingsBeginHotkeyCapture();
        textBox.BorderBrush = System.Windows.Media.Brushes.DodgerBlue;
        textBox.BorderThickness = new Thickness(2);
        AppLogger.Log($"Hotkey capture started: {textBox.Name}");
    }

    private void CancelHotkeyCapture() => FinishHotkeyCapture(restoreOriginalValue: true);

    private void FinishHotkeyCapture(bool restoreOriginalValue)
    {
        if (_activeHotkeyCaptureBox is not { } textBox) return;
        _activeHotkeyCaptureBox = null;
        if (restoreOriginalValue) textBox.Text = _hotkeyCaptureOriginalValue;
        textBox.BorderBrush = _hotkeyCaptureOriginalBorderBrush;
        textBox.BorderThickness = _hotkeyCaptureOriginalBorderThickness;
        _hotkeyCaptureOriginalValue = "";
        _hotkeyCaptureOriginalBorderBrush = null;
        _hotkeyCaptureModifiers = ModifierKeys.None;
        _mainWindow.SettingsEndHotkeyCapture();
        AppLogger.Log($"Hotkey capture {(restoreOriginalValue ? "canceled" : "completed")}: {textBox.Name}; Value={textBox.Text}");
    }

    private System.Windows.Controls.TextBox[] GetHotkeyCaptureTextBoxes() =>
    [
        PreviousPageHotkeyTextBox, NextPageHotkeyTextBox, TopmostHotkeyTextBox,
        .. GetDirectPageHotkeyTextBoxes()
    ];

    private void CaptureHotkey(System.Windows.Controls.TextBox textBox, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            e.Handled = true;
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            textBox.Clear();
            FinishHotkeyCapture(restoreOriginalValue: false);
            e.Handled = true;
            return;
        }

        var pressedModifier = GetCapturedModifier(key);
        if (pressedModifier != ModifierKeys.None)
        {
            _hotkeyCaptureModifiers |= pressedModifier;
            textBox.Text = FormatModifierPreview(_hotkeyCaptureModifiers);
            textBox.SelectAll();
            AppLogger.Log($"Hotkey capture modifier: {textBox.Name}; Modifiers={_hotkeyCaptureModifiers}");
            e.Handled = true;
            return;
        }

        var captured = FormatCapturedHotkey(key, _hotkeyCaptureModifiers | Keyboard.Modifiers);
        if (captured == null) return;
        textBox.Text = captured;
        FinishHotkeyCapture(restoreOriginalValue: false);
        textBox.SelectAll();
        e.Handled = true;
    }

    internal static string? FormatCapturedHotkey(Key key, ModifierKeys modifiers)
    {
        if (IsModifierKey(key)) return null;
        var keyName = GetCapturedKeyName(key);
        if (keyName == null) return null;

        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(keyName);
        return string.Join('+', parts);
    }

    internal static string FormatCapturedMouseHotkey(string mouseButtonName, ModifierKeys modifiers)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(mouseButtonName);
        return string.Join('+', parts);
    }

    internal static ModifierKeys GetCapturedModifier(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
        Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
        Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
        Key.LWin or Key.RWin => ModifierKeys.Windows,
        _ => ModifierKeys.None
    };

    private static bool IsModifierKey(Key key) => GetCapturedModifier(key) != ModifierKeys.None;

    internal static string FormatModifierPreview(ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count == 0 ? "…" : string.Join('+', parts) + "+…";
    }

    private static string? GetCapturedKeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return key.ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return ((int)key - (int)Key.NumPad0).ToString();
        if (key is >= Key.F1 and <= Key.F12) return key.ToString();
        return key switch
        {
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Space => "Space",
            _ => null
        };
    }

    internal void ReloadState()
    {
        var selectedFenceId = SelectedFence?.Id;
        var selectedStyleFenceId = SelectedStyleFence?.Id;
        _updatingControls = true;
        try
        {
            ApplyLocalizedText();
            var fences = _mainWindow.GetFenceSettingsSnapshot();
            FenceList.ItemsSource = null;
            FenceList.ItemsSource = fences;
            FenceList.SelectedItem = fences.FirstOrDefault(fence => fence.Id == selectedFenceId) ?? fences.FirstOrDefault();
            var styleTargets = fences.ToList();
            if (fences.FirstOrDefault() is { } firstFence)
            {
                var allTarget = CopyAppearance(firstFence);
                allTarget.Id = AllAppearanceTargetId;
                allTarget.Title = _mainWindow.Localization.T("AllFences");
                styleTargets.Insert(0, allTarget);
            }
            StyleFenceComboBox.ItemsSource = null;
            StyleFenceComboBox.ItemsSource = styleTargets;
            StyleFenceComboBox.SelectedItem = styleTargets.FirstOrDefault(fence => fence.Id == selectedStyleFenceId) ?? styleTargets.FirstOrDefault();
            var desktopFences = fences.Where(fence => fence.IsDesktopGroup).ToArray();
            DefaultFenceComboBox.ItemsSource = desktopFences;
            DefaultFenceComboBox.SelectedItem = desktopFences.FirstOrDefault(fence => fence.Id == _mainWindow.DefaultAutoOrganizeFenceId);
            RuleTargetComboBox.ItemsSource = desktopFences;
            EnableAutoOrganizeCheckBox.IsChecked = _mainWindow.IsAutoOrganizeEnabled;
            ClassificationSchemeComboBox.SelectedItem = ClassificationSchemeComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _mainWindow.ClassificationScheme, StringComparison.OrdinalIgnoreCase));
            UpdateClassificationPreview();
            var selectedRuleId = SelectedRule?.Id;
            var rules = _mainWindow.GetAutoOrganizeRulesSnapshot();
            RuleList.ItemsSource = rules;
            RuleList.SelectedItem = rules.FirstOrDefault(rule => rule.Id == selectedRuleId) ?? rules.FirstOrDefault();

            FenceCountValue.Text = _mainWindow.FenceCount.ToString();
            PageCountValue.Text = _mainWindow.SettingsPageCount.ToString();
            VisibilityValue.Text = _mainWindow.AreFencesHidden
                ? _mainWindow.Localization.T("Hidden")
                : _mainWindow.Localization.T("Visible");
            CurrentPageValue.Text = $"{_mainWindow.Localization.T("Page")} {_mainWindow.SettingsCurrentPage + 1} / {_mainWindow.SettingsPageCount}";
            WelcomeToggleButton.Content = _mainWindow.Localization.T(
                GetWelcomeVisibilityActionKey(_mainWindow.IsMiniFencesEnabled));
            WelcomeTopmostButton.Content = _mainWindow.AreFencesTopmost
                ? _mainWindow.Localization.T("RestoreFencesToDesktop")
                : _mainWindow.Localization.T("PinFencesOnTop");
            ShowFencesCheckBox.IsChecked = !_mainWindow.AreFencesHidden;
            DesktopDoubleClickCheckBox.IsChecked = _mainWindow.IsDesktopDoubleClickEnabled;
            DesktopIconIntegrationCheckBox.IsChecked = _mainWindow.IsDesktopIconIntegrationEnabled;
            EnableTabCreationCheckBox.IsChecked = _mainWindow.IsTabCreationEnabled;
            ConfirmTabCreationCheckBox.IsChecked = _mainWindow.IsTabCreationConfirmationEnabled;
            HoverSwitchTabsCheckBox.IsChecked = _mainWindow.IsHoverTabSwitchEnabled;
            EnableRollupCheckBox.IsChecked = _mainWindow.IsRollupEnabled;
            DoubleClickRollupCheckBox.IsChecked = _mainWindow.IsDoubleClickTitleRollupEnabled;
            AutoEdgeRollupCheckBox.IsChecked = _mainWindow.IsAutoRollupAtScreenEdgeEnabled;
            AllowBottomEdgeRollupCheckBox.IsChecked = _mainWindow.IsBottomEdgeRollupAllowed;
            BottomDockTitleCheckBox.IsChecked = _mainWindow.IsBottomDockTitleAtBottomEnabled;
            TopDockTitleAtBottomOnExpandCheckBox.IsChecked = _mainWindow.IsTopDockTitleAtBottomOnExpandEnabled;
            UpdateBottomRollupOptionAvailability();
            ClickTitleExpandCheckBox.IsChecked = _mainWindow.IsClickTitleToExpandEnabled;
            HoverTitleExpandCheckBox.IsChecked = _mainWindow.IsHoverTitleToExpandEnabled;
            TabViewComboBox.SelectedItem = TabViewComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _mainWindow.TabViewMode, StringComparison.OrdinalIgnoreCase));
            TabWidthComboBox.SelectedItem = TabWidthComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _mainWindow.TabWidthMode, StringComparison.OrdinalIgnoreCase));
            TabSelectionStyleComboBox.SelectedItem = TabSelectionStyleComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _mainWindow.TabSelectionStyle, StringComparison.OrdinalIgnoreCase));
            _tabSelectionLineColor = FenceControl.NormalizeTabSelectionLineColor(_mainWindow.TabSelectionLineColor);
            TabSelectionLineThicknessComboBox.SelectedItem = TabSelectionLineThicknessComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _mainWindow.TabSelectionLineThickness, StringComparison.OrdinalIgnoreCase));
            UpdateTabSelectionLineVisuals();
            UpdateTabOptionAvailability();
            PreviousPageHotkeyTextBox.Text = _mainWindow.PreviousPageHotkey;
            NextPageHotkeyTextBox.Text = _mainWindow.NextPageHotkey;
            TopmostHotkeyTextBox.Text = _mainWindow.ToggleTopmostHotkey;
            var directPageBoxes = GetDirectPageHotkeyTextBoxes();
            for (var index = 0; index < directPageBoxes.Length; index += 1)
                directPageBoxes[index].Text = index < _mainWindow.DirectPageHotkeys.Count
                    ? _mainWindow.DirectPageHotkeys[index]
                    : $"F{index + 1}";
            EnableGridSnappingCheckBox.IsChecked = _mainWindow.IsSnapToGridEnabled;
            GridSizeTextBox.Text = _mainWindow.GridSize.ToString();
            SnapWhileDraggingCheckBox.IsChecked = _mainWindow.IsSnapWhileDraggingEnabled;
            StartWithWindowsCheckBox.IsChecked = _mainWindow.IsStartWithWindowsEnabled;
            PreviousPageButton.IsEnabled = _mainWindow.SettingsCurrentPage > 0;
            NextPageButton.IsEnabled = _mainWindow.SettingsCurrentPage < _mainWindow.SettingsPageCount - 1;
            DeletePageButton.IsEnabled = _mainWindow.CanDeleteSettingsCurrentPage;
            if (PagesPanel.Visibility == Visibility.Visible) RebuildPagePreviews(fences);
            if (LayoutsPanel.Visibility == Visibility.Visible) ReloadLayouts();
            if (HistoryPanel.Visibility == Visibility.Visible) ReloadHistory();

            foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString(), _mainWindow.Localization.Language, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageComboBox.SelectedItem = item;
                    break;
                }
            }

            UpdateFenceButtons();
            if (FencesPanel.Visibility == Visibility.Visible) UpdateFenceContentEditor();
            if (AppearancePanel.Visibility == Visibility.Visible) UpdateAppearanceControls();
            UpdateRuleEditor();
            UpdateSettingsPreviews();
        }
        finally
        {
            _updatingControls = false;
        }
    }

    internal void RefreshFromMainWindow()
    {
        if (_refreshFromMainWindowPending || !IsLoaded) return;
        _refreshFromMainWindowPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshFromMainWindowPending = false;
            if (IsVisible) ReloadState();
        }, DispatcherPriority.Background);
    }

    private void ReloadLayouts()
    {
        var selectedName = (NamedLayoutsList.SelectedItem as LayoutEntry)?.DisplayName;
        var selectedSnapshot = (SnapshotsList.SelectedItem as LayoutEntry)?.Id;
        var named = _mainWindow.GetNamedLayoutEntries();
        var snapshots = _mainWindow.GetLayoutSnapshots();
        NamedLayoutsList.ItemsSource = named;
        NamedLayoutsList.SelectedItem = named.FirstOrDefault(entry => entry.DisplayName == selectedName) ?? named.FirstOrDefault();
        SnapshotsList.ItemsSource = snapshots;
        SnapshotsList.SelectedItem = snapshots.FirstOrDefault(entry => entry.Id == selectedSnapshot) ?? snapshots.FirstOrDefault();
        UpdateLayoutButtons();
    }

    private void ApplyLocalizedText()
    {
        var loc = _mainWindow.Localization;
        Title = loc.T("SettingsWindowTitle");
        SettingsCaptionText.Text = loc.T("Settings");
        var currentVersion = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
        SidebarVersionText.Text = loc.Language == LocalizationService.Chinese
            ? $"版本 {currentVersion}"
            : $"Version {currentVersion}";
        WelcomeNav.Content = loc.T("Welcome");
        FencesNav.Content = loc.T("FencesNav");
        PagesNav.Content = loc.T("PagesNav");
        LayoutsNav.Content = loc.T("LayoutManagement");
        OrganizeNav.Content = loc.T("ClassificationAndRules");
        VisibilityNav.Content = loc.T("Visibility");
        RollupNav.Content = loc.T("Rollup");
        TabsNav.Content = loc.T("Tabs");
        PersonalizeNav.Content = loc.T("FenceAppearance");
        var chinese = string.Equals(loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
        HistoryNav.Content = chinese ? "操作历史" : "Action history";
        HistoryTitle.Text = chinese ? "操作历史" : "Action history";
        HistoryDescription.Text = chinese
            ? "查看最近 30 天的操作。选择记录只用于打开相关位置；撤销始终作用于最近一步。"
            : "Review the last 30 days. Selection is only used to open related locations; undo always applies to the latest action.";
        UndoLastHistoryButton.Content = chinese ? "撤销上一步" : "Undo last action";
        OpenRelatedHistoryButton.Content = chinese ? "打开相关位置" : "Open related location";
        CreateDiagnosticBundleButton.Content = chinese ? "生成脱敏诊断包" : "Create redacted diagnostics";
        OpenRecycleBinButton.Content = chinese ? "打开回收站" : "Open Recycle Bin";
        ClearHistoryButton.Content = chinese ? "清空历史" : "Clear history";
        GeneralNav.Content = loc.T("General");
        AboutNav.Content = loc.T("About");

        WelcomeTitle.Text = loc.T("WelcomeTitle");
        WelcomeDescription.Text = loc.T("WelcomeDescription");
        OverviewTitle.Text = loc.T("Overview");
        FenceCountLabel.Text = loc.T("FencesNav");
        PageCountLabel.Text = loc.T("PagesNav");
        VisibilityLabel.Text = loc.T("DesktopVisibility");
        QuickActionsTitle.Text = loc.T("QuickActions");
        WelcomeNewFenceButton.Content = loc.T("NewFence");
        WelcomeRefreshButton.Content = loc.T("RefreshAll");
        WelcomeTopmostButton.Content = _mainWindow.AreFencesTopmost ? loc.T("RestoreFencesToDesktop") : loc.T("PinFencesOnTop");

        FencesTitle.Text = loc.T("FencesNav");
        FencesDescription.Text = loc.T("FencesSettingsDescription");
        AddFenceButton.Content = loc.T("NewFence");
        RenameFenceButton.Content = loc.T("RenameFence");
        ChooseFolderButton.Content = loc.T("ChooseFolder");
        OpenFolderButton.Content = loc.T("OpenBoundFolder");
        DeleteFenceButton.Content = loc.T("DeleteFence");
        FencePageLabel.Text = loc.T("Page");
        ShowOnAllPagesCheckBox.Content = loc.T("ShowOnAllPages");
        LinkedCopyPageLabel.Text = loc.T("LinkedCopyPage");
        CreateLinkedCopyButton.Content = loc.T("CreateLinkedCopy");
        SynchronizeLinkedLayoutCheckBox.Content = loc.T("SynchronizeLinkedLayout");
        UnassignedItemsTitle.Text = loc.T("UnassignedDesktopIcons");
        FenceItemsTitle.Text = loc.T("FenceContents");
        AssignItemsButton.ToolTip = loc.T("AssignToFence");
        UnassignItemsButton.ToolTip = loc.T("RemoveFromFence");

        PagesTitle.Text = loc.T("PagesNav");
        PagesDescription.Text = loc.T("PagesSettingsDescription");
        CurrentPageTitle.Text = loc.T("CurrentPage");
        PreviousPageButton.Content = loc.T("PreviousPage");
        NextPageButton.Content = loc.T("NextPage");
        NewPageButton.Content = loc.T("NewPage");
        DeletePageButton.Content = loc.T("DeleteEmptyCurrentPage");
        PagePreviewTitle.Text = loc.T("PagePreview");
        PagePreviewHint.Text = loc.T("PagePreviewHint");

        LayoutsTitle.Text = loc.T("LayoutManagement");
        LayoutsDescription.Text = loc.T("LayoutManagementDescription");
        NamedLayoutsTitle.Text = loc.T("SavedLayouts");
        SaveLayoutButton.Content = loc.T("SaveCurrentLayout");
        OverwriteLayoutButton.Content = loc.T("OverwriteLayout");
        RestoreLayoutButton.Content = loc.T("RestoreLayout");
        RenameLayoutButton.Content = loc.T("RenameFence");
        DeleteLayoutButton.Content = loc.T("DeleteFence");
        SnapshotsTitle.Text = loc.T("AutomaticSnapshots");
        SnapshotsDescription.Text = loc.T("AutomaticSnapshotsDescription");
        RestoreSnapshotButton.Content = loc.T("RestoreSnapshot");

        OrganizeTitle.Text = loc.T("Organization");
        OrganizeDescription.Text = loc.T("OrganizationDescription");
        CategoryTitle.Text = loc.T("DesktopOrganization");
        ClassificationSchemeLabel.Text = loc.T("ClassificationScheme");
        ClassificationPreviewTitle.Text = loc.T("FencesToCreate");
        foreach (var item in ClassificationSchemeComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(item.Tag?.ToString() == "Simple" ? "SimpleClassification" : "DetailedClassification");
        CreateCategoriesButton.Content = loc.T("CreateCategoryFences");
        OrganizeDesktopButton.Content = loc.T("OrganizeDesktop");
        UndoOrganizeButton.Content = loc.T("UndoLastOrganize");
        OrganizeNote.Text = loc.T("OrganizationNote");
        RulesTitle.Text = loc.T("AutoRules");
        EnableAutoOrganizeCheckBox.Content = loc.T("EnableAutoRules");
        DefaultFenceLabel.Text = loc.T("DefaultFence");
        AddRuleButton.Content = loc.T("AddRule");
        DeleteRuleButton.Content = loc.T("DeleteRule");
        RuleEnabledCheckBox.Content = loc.T("RuleEnabled");
        RuleNameLabel.Text = loc.T("RuleName");
        RuleTargetLabel.Text = loc.T("RuleTarget");
        RulePriorityLabel.Text = loc.T("RulePriority");
        RuleNamePatternLabel.Text = loc.T("RuleNamePattern");
        RuleExtensionsLabel.Text = loc.T("RuleExtensions");
        RuleExactNamesLabel.Text = string.Equals(loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase) ? "精确名称" : "Exact names";
        RuleShortcutTargetLabel.Text = string.Equals(loc.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase) ? "快捷方式目标" : "Shortcut target";
        RuleSizeLabel.Text = loc.T("RuleSizeRange");
        RuleFoldersOnlyCheckBox.Content = loc.T("FoldersOnly");

        PersonalizeTitle.Text = loc.T("Visibility");
        PersonalizeDescription.Text = loc.T("VisibilityDescription");
        ShowFencesCheckBox.Content = loc.T("ShowFencesOnDesktop");
        DesktopDoubleClickCheckBox.Content = loc.T("DesktopDoubleClickHideShow");
        DesktopIconIntegrationCheckBox.Content = loc.T("DesktopIconIntegration");
        TabSettingsTitle.Text = loc.T("Tabs");
        TabsDescription.Text = loc.T("TabsDescription");
        TabViewLabel.Text = loc.T("TabView");
        TabWidthLabel.Text = loc.T("TabWidths");
        TabSelectionStyleLabel.Text = loc.T("TabSelectionStyle");
        TabSelectionLineColorLabel.Text = loc.T("TabSelectionLineColor");
        TabSelectionLineThicknessLabel.Text = loc.T("TabSelectionLineThickness");
        TabSelectionLineColorButton.Content = loc.T("ChooseColor");
        TabThicknessThinLabel.Text = loc.T("TabLineThin");
        TabThicknessMediumLabel.Text = loc.T("TabLineMedium");
        TabThicknessThickLabel.Text = loc.T("TabLineThick");
        EnableTabCreationCheckBox.Content = loc.T("EnableTabCreation");
        ConfirmTabCreationCheckBox.Content = loc.T("ConfirmTabCreation");
        HoverSwitchTabsCheckBox.Content = loc.T("HoverSwitchTabs");
        foreach (var item in TabViewComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(string.Equals(item.Tag?.ToString(), "Strip", StringComparison.OrdinalIgnoreCase) ? "TitleTabStrip" : "CompactTabArrows");
        foreach (var item in TabWidthComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(item.Tag?.ToString() switch
            {
                "Equal" => "EqualTabWidths",
                "Adaptive" => "AdaptiveTabWidths",
                _ => "ContentTabWidths"
            });
        foreach (var item in TabSelectionStyleComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(item.Tag?.ToString() switch
            {
                "Underline" => "TabSelectionUnderline",
                "Overline" => "TabSelectionOverline",
                "DoubleLine" => "TabSelectionDoubleLine",
                _ => "TabSelectionFill"
            });
        RollupSettingsTitle.Text = loc.T("Rollup");
        RollupDescription.Text = loc.T("RollupDescription");
        EnableRollupCheckBox.Content = loc.T("RollupEnabled");
        DoubleClickRollupCheckBox.Content = loc.T("DoubleClickRollup");
        AutoEdgeRollupCheckBox.Content = loc.T("AutoEdgeRollup");
        AllowBottomEdgeRollupCheckBox.Content = loc.T("AllowBottomEdgeRollup");
        BottomDockTitleCheckBox.Content = loc.T("BottomDockTitle");
        TopDockTitleAtBottomOnExpandCheckBox.Content = loc.T("TopDockTitleAtBottomOnExpand");
        ClickTitleExpandCheckBox.Content = loc.T("ClickTitleExpand");
        HoverTitleExpandCheckBox.Content = loc.T("HoverTitleExpand");
        RollupPreviewHint.Text = loc.T("PreviewRollupHint");
        FenceAppearanceTitle.Text = loc.T("FenceAppearance");
        FenceAppearanceDescription.Text = loc.T("FenceAppearanceDescription");
        BackgroundColorLabel.Text = loc.T("BackgroundColor");
        HeaderColorLabel.Text = loc.T("HeaderColor");
        HeaderGradientCheckBox.Content = loc.T("HeaderGradient");
        TitleAlignmentLabel.Text = loc.T("TitleAlignment");
        FenceStyleLabel.Text = loc.T("FrameStyle");
        ShowPathCheckBox.Content = loc.T("ShowPath");
        ListColumnSettingsTitle.Text = loc.T("ListViewColumns");
        ListShowTypeCheckBox.Content = loc.T("ListShowType");
        ListShowSizeCheckBox.Content = loc.T("ListShowSize");
        ListShowTimeCheckBox.Content = loc.T("ListShowTime");
        foreach (var item in TitleAlignmentComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(item.Tag?.ToString() switch { "Center" => "AlignCenter", "Right" => "AlignRight", _ => "AlignLeft" });
        foreach (var item in FenceStyleComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(string.Equals(item.Tag?.ToString(), "Clean", StringComparison.OrdinalIgnoreCase) ? "CleanStyle" : "FramedStyle");
        OpacityLabel.Text = loc.T("Opacity");
        BackgroundColorButton.Content = loc.T("ChooseColor");
        HeaderColorButton.Content = loc.T("ChooseColor");
        HeaderGradientColorButton.Content = loc.T("ChooseGradientColor");
        ResetAppearanceButton.Content = loc.T("ResetAppearance");
        CopyAppearanceButton.Content = loc.T("CopyAppearance");
        PasteAppearanceButton.Content = loc.T("PasteAppearance");
        AppearancePreviewTitle.Text = loc.T("Preview");
        DisplayPreviewTitle.Text = loc.T("Preview");
        TabsPreviewTitle.Text = loc.T("Preview");
        RollupPreviewTitle.Text = loc.T("InteractivePreview");

        GeneralTitle.Text = loc.T("General");
        GeneralDescription.Text = loc.T("GeneralDescription");
        LanguageTitle.Text = loc.T("Language");
        StartupTitle.Text = loc.T("Startup");
        ShortcutsTitle.Text = loc.T("KeyboardShortcuts");
        HotkeyCaptureHint.Text = loc.T("HotkeyCaptureHint");
        PreviousPageHotkeyLabel.Text = loc.T("PreviousPage");
        NextPageHotkeyLabel.Text = loc.T("NextPage");
        TopmostHotkeyLabel.Text = loc.T("PinRestoreFences");
        DirectPageHotkeysTitle.Text = loc.T("DirectPageHotkeys");
        var directPageLabels = GetDirectPageHotkeyLabels();
        for (var index = 0; index < directPageLabels.Length; index += 1)
            directPageLabels[index].Text = $"{loc.T("Page")} {index + 1}";
        GridSnappingTitle.Text = loc.T("GridSnapping");
        EnableGridSnappingLabel.Text = loc.T("EnableGridSnapping");
        GridSizeLabel.Text = loc.T("GridSizePixels");
        SnapWhileDraggingLabel.Text = loc.T("SnapWhileDragging");
        ApplyHotkeysButton.Content = loc.T("ApplyShortcuts");
        var hotkeyToolTip = loc.T("HotkeyCaptureToolTip");
        foreach (var textBox in GetHotkeyCaptureTextBoxes()) textBox.ToolTip = hotkeyToolTip;
        StartWithWindowsCheckBox.Content = loc.T("StartWithWindows");
        CheckForUpdatesButton.Content = loc.T("CheckForUpdates");
        foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = string.Equals(item.Tag?.ToString(), LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase)
                ? loc.T("Chinese")
                : loc.T("English");
        }

        AboutTitle.Text = loc.T("About");
        AboutDescription.Text = loc.T("AboutDescription");
        VersionText.Text = $"MiniFences {currentVersion}";
        OpenConfigButton.Content = loc.T("OpenConfigFolder");
        OpenLogButton.Content = loc.T("OpenLogFile");
    }

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavigationList.SelectedItem is not ListBoxItem selected)
        {
            return;
        }

        var selectedPanelName = ResolvePanelName(selected.Tag?.ToString());
        foreach (var panel in GetSettingsPanels())
        {
            panel.Visibility = string.Equals(panel.Name, selectedPanelName, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        switch (selected.Tag?.ToString())
        {
            case "Fences": UpdateFenceContentEditor(); break;
            case "Pages": RebuildPagePreviews(_mainWindow.GetFenceSettingsSnapshot()); break;
            case "Layouts": ReloadLayouts(); break;
            case "Appearance": UpdateAppearanceControls(); break;
            case "History": ReloadHistory(); break;
        }
        if (string.Equals(Environment.GetEnvironmentVariable("MINIFENCES_UI_TRACE"), "1", StringComparison.Ordinal))
            Dispatcher.BeginInvoke(TraceVisibleComboBoxesForTesting, DispatcherPriority.Loaded);
    }

    internal static string? ResolvePanelName(string? navigationTag) => navigationTag switch
    {
        "Welcome" => "WelcomePanel",
        "Fences" => "FencesPanel",
        "Pages" => "PagesPanel",
        "Layouts" => "LayoutsPanel",
        "Organize" => "OrganizePanel",
        "Visibility" => "DisplayPanel",
        "Rollup" => "RollupPanel",
        "Tabs" => "TabsPanel",
        "Appearance" => "AppearancePanel",
        "History" => "HistoryPanel",
        "General" => "GeneralPanel",
        "About" => "AboutPanel",
        _ => null
    };

    private FrameworkElement[] GetSettingsPanels() =>
    [
        WelcomePanel,
        FencesPanel,
        PagesPanel,
        LayoutsPanel,
        OrganizePanel,
        DisplayPanel,
        RollupPanel,
        TabsPanel,
        AppearancePanel,
        HistoryPanel,
        GeneralPanel,
        AboutPanel
    ];

    private HistoryDisplayRow? SelectedHistory => HistoryList.SelectedItem as HistoryDisplayRow;

    private void ReloadHistory()
    {
        var selectedId = SelectedHistory?.Id;
        var chinese = string.Equals(_mainWindow.Localization.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
        var history = _mainWindow.GetActionHistory().Select(transaction => new HistoryDisplayRow(
            transaction,
            chinese ? transaction.DisplayName : transaction.ActionType switch
            {
                "DeleteFence" => "Delete Fence",
                "Membership" => "Change desktop assignment",
                "FileMove" => "Move or rename files",
                "FileCopy" => "Copy files",
                "LinkCreate" => "Create shortcuts",
                "RecycleDelete" => "Move to Recycle Bin",
                "LayoutRestore" => "Change layout",
                _ => transaction.DisplayName
            },
            transaction.Status switch
            {
                "Completed" => chinese ? "已完成" : "Completed",
                "PartiallyCompleted" => chinese ? "部分完成" : "Partially completed",
                "Undone" => chinese ? "已撤销" : "Undone",
                "PartiallyUndone" => chinese ? "部分撤销" : "Partially undone",
                "Pending" => chinese ? "等待执行" : "Pending",
                "InProgress" => chinese ? "正在执行" : "In progress",
                _ => chinese ? "失败" : "Failed"
            })).ToArray();
        HistoryList.ItemsSource = history;
        HistoryList.SelectedItem = history.FirstOrDefault(item => item.Id == selectedId) ?? history.FirstOrDefault();
        UpdateHistoryButtons();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHistoryButtons();

    private void UpdateHistoryButtons()
    {
        var selected = SelectedHistory;
        UndoLastHistoryButton.IsEnabled = _mainWindow.CanUndoLastAction;
        OpenRelatedHistoryButton.IsEnabled = selected?.Transaction.Entries.Any(entry =>
            !string.IsNullOrWhiteSpace(entry.SourcePath) || !string.IsNullOrWhiteSpace(entry.DestinationPath)) == true;
    }

    private void UndoLastHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.UndoLastActionFromSettings(this);
    }

    private void OpenRelatedHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedHistory is not null) _mainWindow.OpenHistoryLocation(SelectedHistory.Transaction, this);
    }

    private sealed class HistoryDisplayRow
    {
        public HistoryDisplayRow(ActionTransaction transaction, string displayName, string statusDisplay)
        {
            Transaction = transaction;
            DisplayName = displayName;
            StatusDisplay = statusDisplay;
            Details = BuildDetails(transaction);
        }
        public ActionTransaction Transaction { get; }
        public string Id => Transaction.Id;
        public DateTime DisplayTime => Transaction.DisplayTime;
        public int AffectedCount => Transaction.AffectedCount;
        public string DisplayName { get; }
        public string StatusDisplay { get; }
        public string Details { get; }

        private static string BuildDetails(ActionTransaction transaction)
        {
            var paths = transaction.Entries.Take(3).Select(entry =>
            {
                var source = entry.SourcePath ?? entry.ItemName ?? "";
                return string.IsNullOrWhiteSpace(entry.DestinationPath)
                    ? source
                    : $"{source}  →  {entry.DestinationPath}";
            });
            var detail = string.Join(Environment.NewLine, paths);
            if (transaction.Entries.Count > 3) detail += $"\n…另有 {transaction.Entries.Count - 3} 项";
            if (transaction.Errors.Count > 0) detail += $"\n错误：{transaction.Errors[0]}";
            return detail;
        }
    }

    private void OpenRecycleBinButton_Click(object sender, RoutedEventArgs e) => _mainWindow.OpenRecycleBin(this);

    private void CreateDiagnosticBundleButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存 MiniFences 脱敏诊断包",
            Filter = "ZIP archive (*.zip)|*.zip",
            FileName = $"MiniFences-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            AddExtension = true,
            DefaultExt = ".zip"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            _mainWindow.CreateDiagnosticBundle(dialog.FileName);
            System.Windows.MessageBox.Show(this,
                "诊断包已生成。文件路径和用户名已脱敏，不包含用户文件内容。",
                "MiniFences", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"无法生成诊断包：{ex.Message}",
                "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        var chinese = string.Equals(_mainWindow.Localization.Language, LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase);
        if (System.Windows.MessageBox.Show(this,
            chinese ? "只清除操作记录，不会删除文件或修改当前布局。确定继续吗？" : "This only clears records; files and the current layout are unchanged. Continue?",
            "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _mainWindow.ClearActionHistory();
        ReloadHistory();
    }

    private LayoutEntry? SelectedNamedLayout => NamedLayoutsList.SelectedItem as LayoutEntry;
    private LayoutEntry? SelectedSnapshot => SnapshotsList.SelectedItem as LayoutEntry;

    private void NamedLayoutsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLayoutButtons();
    private void SnapshotsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLayoutButtons();

    private void UpdateLayoutButtons()
    {
        var hasNamed = SelectedNamedLayout != null;
        OverwriteLayoutButton.IsEnabled = hasNamed;
        RestoreLayoutButton.IsEnabled = hasNamed;
        RenameLayoutButton.IsEnabled = hasNamed;
        DeleteLayoutButton.IsEnabled = hasNamed;
        RestoreSnapshotButton.IsEnabled = SelectedSnapshot != null;
    }

    private void SaveLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameFenceDialog(string.Empty, _mainWindow.Localization, "SaveLayoutAs", "LayoutName", "LayoutNameCannotBeEmpty") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (_mainWindow.NamedLayoutExists(dialog.InputText) &&
            System.Windows.MessageBox.Show(this, string.Format(_mainWindow.Localization.T("OverwriteLayoutQuestion"), dialog.InputText), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_mainWindow.SaveNamedLayout(dialog.InputText, out var error)) ShowLayoutError(error);
        ReloadLayouts();
    }

    private void OverwriteLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNamedLayout is not { } entry) return;
        if (System.Windows.MessageBox.Show(this, string.Format(_mainWindow.Localization.T("OverwriteLayoutQuestion"), entry.DisplayName), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_mainWindow.SaveNamedLayout(entry.DisplayName, out var error)) ShowLayoutError(error);
        ReloadLayouts();
    }

    private void RestoreLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNamedLayout is not { } entry) return;
        if (System.Windows.MessageBox.Show(this, string.Format(_mainWindow.Localization.T("RestoreNamedLayoutQuestion"), entry.DisplayName), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_mainWindow.RestoreNamedLayout(entry.DisplayName, out var invalid, out var error)) { ShowLayoutError(error); return; }
        ShowInvalidPathWarning(invalid); ReloadState();
    }

    private void RenameLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNamedLayout is not { } entry) return;
        var dialog = new RenameFenceDialog(entry.DisplayName, _mainWindow.Localization, "RenameLayout", "LayoutName", "LayoutNameCannotBeEmpty") { Owner = this };
        if (dialog.ShowDialog() != true || string.Equals(entry.DisplayName, dialog.InputText, StringComparison.Ordinal)) return;
        var overwrite = _mainWindow.NamedLayoutExists(dialog.InputText);
        if (overwrite && System.Windows.MessageBox.Show(this, string.Format(_mainWindow.Localization.T("OverwriteLayoutQuestion"), dialog.InputText), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_mainWindow.RenameNamedLayout(entry.DisplayName, dialog.InputText, overwrite, out var error)) ShowLayoutError(error);
        ReloadLayouts();
    }

    private void DeleteLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNamedLayout is not { } entry) return;
        if (System.Windows.MessageBox.Show(this, string.Format(_mainWindow.Localization.T("DeleteLayoutQuestion"), entry.DisplayName), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (!_mainWindow.DeleteNamedLayout(entry.DisplayName, out var error)) ShowLayoutError(error);
        ReloadLayouts();
    }

    private void RestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedSnapshot is not { } entry) return;
        if (System.Windows.MessageBox.Show(this, _mainWindow.Localization.T("RestoreLayoutSnapshotQuestion"), "MiniFences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_mainWindow.RestoreSnapshot(entry.Id, out var invalid, out var error)) { ShowLayoutError(error); return; }
        ShowInvalidPathWarning(invalid); ReloadState();
    }

    private void ShowInvalidPathWarning(int count)
    {
        if (count > 0) System.Windows.MessageBox.Show(this, string.Format(_mainWindow.Localization.T("LayoutInvalidPathsWarning"), count), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowLayoutError(string? error) => System.Windows.MessageBox.Show(this, error ?? _mainWindow.Localization.T("CouldNotLoadSavedLayout"), "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);

    private FenceConfig? SelectedFence => FenceList.SelectedItem as FenceConfig;
    private FenceConfig? SelectedStyleFence => StyleFenceComboBox.SelectedItem as FenceConfig;
    private AutoOrganizeRule? SelectedRule => RuleList.SelectedItem as AutoOrganizeRule;

    private void FenceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFenceButtons();
        if (!_updatingControls) UpdateFenceContentEditor();
    }

    private void UpdateFenceButtons()
    {
        var hasSelection = SelectedFence != null;
        RenameFenceButton.IsEnabled = hasSelection;
        ChooseFolderButton.IsEnabled = hasSelection;
        OpenFolderButton.IsEnabled = hasSelection;
        DeleteFenceButton.IsEnabled = hasSelection && _mainWindow.FenceCount > 1;
    }

    private void UpdateFenceContentEditor()
    {
        var fence = SelectedFence;
        FenceItemsTitle.Text = fence?.Title ?? _mainWindow.Localization.T("FenceContents");
        var hasSelection = fence != null;
        var assignmentEnabled = fence?.IsDesktopGroup == true;
        FencePageComboBox.ItemsSource = Enumerable.Range(1, _mainWindow.SettingsPageCount).ToArray();
        FencePageComboBox.SelectedItem = fence == null ? null : fence.PageIndex + 1;
        FencePageComboBox.IsEnabled = fence != null && !fence.ShowOnAllPages;
        ShowOnAllPagesCheckBox.IsChecked = fence?.ShowOnAllPages == true;
        ShowOnAllPagesCheckBox.IsEnabled = fence != null && _mainWindow.SettingsCanPinFence(fence.Id);
        LinkedCopyPageComboBox.ItemsSource = Enumerable.Range(1, _mainWindow.SettingsPageCount).ToArray();
        var preferredCopyPage = fence == null
            ? (int?)null
            : Enumerable.Range(0, _mainWindow.SettingsPageCount)
                .FirstOrDefault(page => _mainWindow.SettingsCanCreateLinkedFenceCopy(fence.Id, page), -1);
        LinkedCopyPageComboBox.SelectedItem = preferredCopyPage is >= 0 ? preferredCopyPage.Value + 1 : null;
        LinkedCopyPageComboBox.IsEnabled = fence != null && preferredCopyPage is >= 0;
        CreateLinkedCopyButton.IsEnabled = fence != null && preferredCopyPage is >= 0;
        SynchronizeLinkedLayoutCheckBox.IsChecked = fence?.SynchronizeLinkedLayout == true;
        SynchronizeLinkedLayoutCheckBox.IsEnabled = fence != null && _mainWindow.SettingsCanSynchronizeLinkedFenceLayout(fence.Id);
        var unassignedItems = assignmentEnabled ? _mainWindow.SettingsGetUnassignedDesktopItems() : Array.Empty<FolderItem>();
        var fenceItems = hasSelection ? _mainWindow.SettingsGetFenceItems(fence!.Id) : Array.Empty<FolderItem>();
        UnassignedItemsList.ItemsSource = unassignedItems;
        FenceItemsList.ItemsSource = fenceItems;
        UpdateFenceContentTransferButtons(assignmentEnabled);

        _contentIconLoadCancellation?.Cancel();
        _contentIconLoadCancellation?.Dispose();
        _contentIconLoadCancellation = null;
        if (!hasSelection) return;
        _contentIconLoadCancellation = new CancellationTokenSource();
        _ = LoadContentIconsAsync(
            unassignedItems.Concat(fenceItems).DistinctBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase).ToArray(),
            _contentIconLoadCancellation.Token);
    }

    private async Task LoadContentIconsAsync(IReadOnlyList<FolderItem> items, CancellationToken cancellationToken)
    {
        try
        {
            await _folderItemService.LoadIconsAsync(items, (item, icon) =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                Dispatcher.BeginInvoke(() =>
                {
                    if (!cancellationToken.IsCancellationRequested && icon is not null) item.Icon = icon;
                }, DispatcherPriority.Background);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A different Fence was selected before this icon pass completed.
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Settings item icon loading failed", ex);
        }
    }

    private void AssignItemsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFence is not { } fence) return;
        var paths = UnassignedItemsList.SelectedItems.OfType<FolderItem>().Select(item => item.FullPath).ToArray();
        if (paths.Length == 0) return;
        _mainWindow.SettingsAssignDesktopItems(fence.Id, paths);
        ReloadState();
    }

    private void FenceContentSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateFenceContentTransferButtons(SelectedFence?.IsDesktopGroup == true);

    private void UpdateFenceContentTransferButtons(bool enabled)
    {
        AssignItemsButton.IsEnabled = enabled && UnassignedItemsList.SelectedItems.Count > 0;
        UnassignItemsButton.IsEnabled = enabled && FenceItemsList.SelectedItems.Count > 0;
    }

    private void UnassignItemsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFence is not { } fence) return;
        var paths = FenceItemsList.SelectedItems.OfType<FolderItem>().Select(item => item.FullPath).ToArray();
        if (paths.Length == 0) return;
        _mainWindow.SettingsUnassignDesktopItems(fence.Id, paths);
        ReloadState();
    }

    private void FencePageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || SelectedFence is not { } fence || FencePageComboBox.SelectedItem is not int displayPage) return;
        _mainWindow.SettingsMoveFenceToPage(fence.Id, displayPage - 1);
        ReloadState();
    }

    private void StyleFenceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingControls)
        {
            UpdateAppearanceControls();
        }
    }

    private void UpdateAppearanceControls()
    {
        var fence = SelectedStyleFence;
        var enabled = fence != null;
        BackgroundColorButton.IsEnabled = enabled;
        HeaderColorButton.IsEnabled = enabled;
        HeaderGradientCheckBox.IsEnabled = enabled;
        HeaderGradientColorButton.IsEnabled = enabled && fence?.HeaderGradientEnabled == true;
        OpacitySlider.IsEnabled = enabled;
        ResetAppearanceButton.IsEnabled = enabled;
        CopyAppearanceButton.IsEnabled = enabled;
        PasteAppearanceButton.IsEnabled = enabled && _copiedAppearance != null;
        TitleAlignmentComboBox.IsEnabled = enabled;
        FenceStyleComboBox.IsEnabled = enabled;
        ShowPathCheckBox.IsEnabled = enabled;
        ListShowTypeCheckBox.IsEnabled = enabled;
        ListShowSizeCheckBox.IsEnabled = enabled;
        ListShowTimeCheckBox.IsEnabled = enabled;
        if (fence == null)
        {
            BackgroundColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
            HeaderColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
            HeaderGradientColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
            HeaderGradientCheckBox.IsChecked = false;
            ListShowTypeCheckBox.IsChecked = false;
            ListShowSizeCheckBox.IsChecked = false;
            ListShowTimeCheckBox.IsChecked = false;
            OpacityValueText.Text = "";
            PreviewFenceBorder.Visibility = Visibility.Hidden;
            return;
        }

        BackgroundColorSwatch.Background = BrushFromColor(fence.BackgroundColor, "#DD20242A");
        HeaderColorSwatch.Background = BrushFromColor(fence.HeaderColor, "#CC3F7FA8");
        HeaderGradientColorSwatch.Background = BrushFromColor(fence.HeaderGradientColor, "#CC8E5BB7");
        HeaderGradientCheckBox.IsChecked = fence.HeaderGradientEnabled;
        HeaderGradientColorButton.IsEnabled = fence.HeaderGradientEnabled;
        TitleAlignmentComboBox.SelectedItem = TitleAlignmentComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), fence.TitleAlignment, StringComparison.OrdinalIgnoreCase));
        FenceStyleComboBox.SelectedItem = FenceStyleComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), fence.UseCleanStyle ? "Clean" : "Framed", StringComparison.OrdinalIgnoreCase));
        ShowPathCheckBox.IsChecked = fence.ShowPath;
        ListShowTypeCheckBox.IsChecked = fence.ListShowType;
        ListShowSizeCheckBox.IsChecked = fence.ListShowSize;
        ListShowTimeCheckBox.IsChecked = fence.ListShowTime;
        OpacitySlider.Value = Math.Round(Math.Clamp(fence.Opacity, 0.0, 1.0) * 100);
        OpacityValueText.Text = $"{OpacitySlider.Value:0}%";
        UpdateAppearancePreview(fence, fence.Opacity);
    }

    private void UpdateAppearancePreview(FenceConfig fence, double opacity)
    {
        PreviewFenceBorder.Visibility = Visibility.Visible;
        PreviewFenceBorder.Opacity = 1.0;
        PreviewHeader.Background = FenceAppearanceBrush.CreateHeaderBrush(fence);
        PreviewHeader.BorderBrush = fence.UseCleanStyle
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            : System.Windows.Media.Brushes.Transparent;
        PreviewHeader.BorderThickness = fence.UseCleanStyle ? new Thickness(0, 0, 0, 1) : new Thickness(0);
        PreviewContent.Background = BrushFromColor(fence.BackgroundColor, "#DD20242A");
        PreviewContent.Opacity = Math.Clamp(opacity, 0.0, 1.0);
        PreviewFenceTitle.Text = fence.Title;
        PreviewPath.Text = _mainWindow.Localization.Language == LocalizationService.Chinese
            ? "C:\\Users\\\u7528\u6237\\\u684c\u9762 - 3 \u4e2a\u9879\u76ee"
            : "C:\\Users\\User\\Desktop - 3 items";
        PreviewFenceTitle.HorizontalAlignment = fence.TitleAlignment switch
        {
            "Center" => System.Windows.HorizontalAlignment.Center,
            "Right" => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Left
        };
        PreviewFenceTitle.TextAlignment = fence.TitleAlignment switch
        {
            "Center" => TextAlignment.Center,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };
        PreviewFenceTitle.Margin = fence.TitleAlignment == "Center" ? new Thickness(40, 0, 40, 0) :
            fence.TitleAlignment == "Right" ? new Thickness(40, 0, 11, 0) : new Thickness(11, 0, 40, 0);
        PreviewPath.Visibility = fence.ShowPath ? Visibility.Visible : Visibility.Collapsed;
        PreviewPathRow.Height = fence.ShowPath ? new GridLength(22) : new GridLength(0);
        PreviewFenceBorder.BorderThickness = fence.UseCleanStyle ? new Thickness(0) : new Thickness(1);
        var previewItems = _mainWindow.GetFencePreviewItems(fence.Id);
        var previewIcons = new[] { PreviewIcon1, PreviewIcon2, PreviewIcon3 };
        var previewNames = new[] { PreviewName1, PreviewName2, PreviewName3 };
        for (var index = 0; index < previewIcons.Length; index++)
        {
            var item = previewItems.ElementAtOrDefault(index);
            previewIcons[index].Source = item?.Icon;
            previewIcons[index].Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
            previewNames[index].Text = item?.Name ?? "";
            previewNames[index].Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static System.Windows.Media.Brush BrushFromColor(string value, string fallback)
    {
        try
        {
            return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value));
        }
        catch
        {
            return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback));
        }
    }

    private void NewFenceButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsCreateFence();
        ReloadState();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppLogger.Log("Settings Refresh All button clicked.");
            _mainWindow.SettingsRefreshAll();
            ReloadState();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Settings Refresh All failed", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WelcomeIntegrationButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = !_mainWindow.IsMiniFencesEnabled;
            AppLogger.Log($"Settings Open/Close Fences button clicked. RequestedEnabled={enabled}.");
            _mainWindow.SettingsSetMiniFencesEnabled(enabled);
            ReloadState();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Settings Open/Close Fences failed", ex);
            System.Windows.MessageBox.Show(this, ex.Message, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsToggleFencesTopmost();
        ReloadState();
    }

    private void ApplyHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_mainWindow.SettingsSetHotkeys(
                PreviousPageHotkeyTextBox.Text,
                NextPageHotkeyTextBox.Text,
                TopmostHotkeyTextBox.Text,
                GetDirectPageHotkeyTextBoxes().Select(textBox => textBox.Text).ToArray(),
                out var error))
        {
            System.Windows.MessageBox.Show(this, error, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ReloadState();
    }

    private void GridSettings_Changed(object sender, RoutedEventArgs e) => ApplyGridSettingsImmediately();

    private void GridSizeTextBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyGridSettingsImmediately();

    private void ApplyGridSettingsImmediately()
    {
        if (_updatingControls || !int.TryParse(GridSizeTextBox.Text, out var gridSize) || gridSize is < 1 or > 256) return;
        _mainWindow.SettingsSetGridOptions(
            EnableGridSnappingCheckBox.IsChecked == true, gridSize,
            SnapWhileDraggingCheckBox.IsChecked == true, out _);
    }

    private System.Windows.Controls.TextBox[] GetDirectPageHotkeyTextBoxes() =>
    [
        DirectPage1HotkeyTextBox, DirectPage2HotkeyTextBox, DirectPage3HotkeyTextBox, DirectPage4HotkeyTextBox,
        DirectPage5HotkeyTextBox, DirectPage6HotkeyTextBox, DirectPage7HotkeyTextBox, DirectPage8HotkeyTextBox,
        DirectPage9HotkeyTextBox, DirectPage10HotkeyTextBox, DirectPage11HotkeyTextBox, DirectPage12HotkeyTextBox
    ];

    private TextBlock[] GetDirectPageHotkeyLabels() =>
    [
        DirectPage1HotkeyLabel, DirectPage2HotkeyLabel, DirectPage3HotkeyLabel, DirectPage4HotkeyLabel,
        DirectPage5HotkeyLabel, DirectPage6HotkeyLabel, DirectPage7HotkeyLabel, DirectPage8HotkeyLabel,
        DirectPage9HotkeyLabel, DirectPage10HotkeyLabel, DirectPage11HotkeyLabel, DirectPage12HotkeyLabel
    ];

    private void RenameFenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFence is { } fence && _mainWindow.SettingsRenameFence(fence.Id, this))
        {
            ReloadState();
        }
    }

    private void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFence is { } fence && _mainWindow.SettingsChooseFenceFolder(fence.Id))
        {
            ReloadState();
        }
    }

    private void ShowOnAllPagesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || SelectedFence is not { } fence) return;
        if (!_mainWindow.SettingsSetFenceShowOnAllPages(fence.Id, ShowOnAllPagesCheckBox.IsChecked == true))
            ShowOnAllPagesCheckBox.IsChecked = fence.ShowOnAllPages;
        ReloadState();
    }

    private void LinkedCopyPageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || SelectedFence is not { } fence || LinkedCopyPageComboBox.SelectedItem is not int pageNumber)
        {
            CreateLinkedCopyButton.IsEnabled = false;
            return;
        }
        CreateLinkedCopyButton.IsEnabled = _mainWindow.SettingsCanCreateLinkedFenceCopy(fence.Id, pageNumber - 1);
    }

    private void CreateLinkedCopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFence is not { } fence || LinkedCopyPageComboBox.SelectedItem is not int pageNumber) return;
        if (_mainWindow.SettingsCreateLinkedFenceCopy(fence.Id, pageNumber - 1)) ReloadState();
    }

    private void SynchronizeLinkedLayoutCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || SelectedFence is not { } fence) return;
        if (!_mainWindow.SettingsSetSynchronizeLinkedFenceLayout(
                fence.Id, SynchronizeLinkedLayoutCheckBox.IsChecked == true))
            SynchronizeLinkedLayoutCheckBox.IsChecked = fence.SynchronizeLinkedLayout;
        ReloadState();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFence is { } fence)
        {
            _mainWindow.SettingsOpenFenceFolder(fence.Id, this);
        }
    }

    private void DeleteFenceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFence is { } fence && _mainWindow.SettingsDeleteFence(fence.Id, this))
        {
            ReloadState();
        }
    }

    private void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsSwitchPage(_mainWindow.SettingsCurrentPage - 1);
        ReloadState();
    }

    private void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsSwitchPage(_mainWindow.SettingsCurrentPage + 1);
        ReloadState();
    }

    private void NewPageButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsCreatePage();
        ReloadState();
    }

    private void DeletePageButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsDeleteCurrentPage();
        ReloadState();
    }

    private void RebuildPagePreviews(IReadOnlyList<FenceConfig> fences)
    {
        PagePreviewPanel.Children.Clear();
        var workspaceWidth = Math.Max(1, _mainWindow.SettingsWorkspaceWidth);
        var workspaceHeight = Math.Max(1, _mainWindow.SettingsWorkspaceHeight);
        const double previewWidth = 360;
        const double previewHeight = 203;
        for (var pageIndex = 0; pageIndex < _mainWindow.SettingsPageCount; pageIndex++)
        {
            var targetPage = pageIndex;
            var pageCanvas = new Canvas { Width = previewWidth, Height = previewHeight, Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(49, 58, 68)), AllowDrop = true };
            pageCanvas.Drop += (_, e) =>
            {
                if (e.Data.GetData("MiniFences.SettingsFenceId") is string fenceId)
                {
                    _mainWindow.SettingsMoveFenceToPage(fenceId, targetPage);
                    ReloadState();
                    e.Handled = true;
                }
            };
            pageCanvas.DragOver += (_, e) => { e.Effects = System.Windows.DragDropEffects.Move; e.Handled = true; };
            pageCanvas.MouseLeftButtonDown += (_, _) =>
            {
                if (_mainWindow.SettingsCurrentPage != targetPage)
                {
                    _mainWindow.SettingsSwitchPage(targetPage);
                    ReloadState();
                }
            };

            foreach (var fence in fences.Where(fence => MainWindow.IsFenceVisibleOnPage(fence, pageIndex)))
            {
                var block = new Border
                {
                    Width = Math.Max(42, Math.Min(previewWidth, fence.Width / workspaceWidth * previewWidth)),
                    Height = Math.Max(22, Math.Min(previewHeight, fence.Height / workspaceHeight * previewHeight)),
                    Background = BrushFromText(fence.BackgroundColor, System.Windows.Media.Color.FromArgb(220, 63, 127, 168)),
                    BorderBrush = System.Windows.Media.Brushes.White,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Cursor = System.Windows.Input.Cursors.SizeAll,
                    ToolTip = fence.Title,
                    Tag = fence.Id
                };
                var blockContent = new Grid();
                blockContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                blockContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                blockContent.Children.Add(new TextBlock { Text = fence.Title, Foreground = System.Windows.Media.Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 3, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis });
                var iconPanel = new WrapPanel { Margin = new Thickness(5, 2, 2, 2) };
                foreach (var item in _mainWindow.SettingsGetFenceItems(fence.Id).Take(8))
                {
                    iconPanel.Children.Add(new System.Windows.Controls.Image { Source = item.Icon, Width = 20, Height = 20, Stretch = Stretch.Uniform, Margin = new Thickness(2), ToolTip = item.Name });
                }
                Grid.SetRow(iconPanel, 1);
                blockContent.Children.Add(iconPanel);
                block.Child = blockContent;
                Canvas.SetLeft(block, Math.Clamp(fence.Left / workspaceWidth * previewWidth, 0, previewWidth - block.Width));
                Canvas.SetTop(block, Math.Clamp(fence.Top / workspaceHeight * previewHeight, 0, previewHeight - block.Height));
                block.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    _pagePreviewDragStart = e.GetPosition(block);
                    _pagePreviewDragFenceId = fence.Id;
                    e.Handled = true;
                };
                block.PreviewMouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || _pagePreviewDragFenceId != fence.Id) return;
                    var point = e.GetPosition(block);
                    if (Math.Abs(point.X - _pagePreviewDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                        Math.Abs(point.Y - _pagePreviewDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    var data = new System.Windows.DataObject("MiniFences.SettingsFenceId", fence.Id);
                    System.Windows.DragDrop.DoDragDrop(block, data, System.Windows.DragDropEffects.Move);
                    _pagePreviewDragFenceId = null;
                };
                pageCanvas.Children.Add(block);
            }

            var frame = new Border
            {
                BorderBrush = pageIndex == _mainWindow.SettingsCurrentPage ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)) : new SolidColorBrush(System.Windows.Media.Color.FromRgb(186, 194, 203)),
                BorderThickness = new Thickness(pageIndex == _mainWindow.SettingsCurrentPage ? 3 : 1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 16, 16),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{_mainWindow.Localization.T("Page")} {pageIndex + 1}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 6, 8, 5) },
                        pageCanvas
                    }
                }
            };
            PagePreviewPanel.Children.Add(frame);
        }
    }

    private static System.Windows.Media.Brush BrushFromText(string value, System.Windows.Media.Color fallback)
    {
        try { return new BrushConverter().ConvertFromString(value) as System.Windows.Media.Brush ?? new SolidColorBrush(fallback); }
        catch { return new SolidColorBrush(fallback); }
    }

    private void CreateCategoriesButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsCreateCategories();
        ReloadState();
    }

    private void OrganizeDesktopButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsOrganizeDesktop();
        ReloadState();
    }

    private void ClassificationSchemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls) return;
        var scheme = (ClassificationSchemeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Detailed";
        _mainWindow.SettingsSetClassificationScheme(scheme);
        UpdateClassificationPreview();
    }

    private void UpdateClassificationPreview()
    {
        if (ClassificationPreviewText == null) return;
        var scheme = (ClassificationSchemeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? _mainWindow.ClassificationScheme;
        ClassificationPreviewText.Text = string.Join("  ·  ", AutoOrganizerService.GetCategoriesForScheme(scheme));
    }

    private void UndoOrganizeButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsUndoOrganization();
        ReloadState();
    }

    private void EnableAutoOrganizeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_updatingControls) _mainWindow.SettingsSetAutoOrganizeEnabled(EnableAutoOrganizeCheckBox.IsChecked == true);
    }

    private void DefaultFenceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingControls && DefaultFenceComboBox.SelectedItem is FenceConfig fence)
            _mainWindow.SettingsSetDefaultAutoOrganizeFence(fence.Id);
    }

    private void AddRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var rule = _mainWindow.SettingsAddAutoOrganizeRule();
        ReloadState();
        RuleList.SelectedItem = RuleList.Items.OfType<AutoOrganizeRule>().FirstOrDefault(item => item.Id == rule.Id);
    }

    private void DeleteRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is { } rule)
        {
            _mainWindow.SettingsDeleteAutoOrganizeRule(rule.Id);
            ReloadState();
        }
    }

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRuleEditor();

    private void UpdateRuleEditor()
    {
        var rule = SelectedRule;
        var enabled = rule != null;
        DeleteRuleButton.IsEnabled = enabled;
        foreach (var control in new System.Windows.Controls.Control[] { RuleEnabledCheckBox, RuleNameTextBox, RuleTargetComboBox, RulePriorityTextBox, RuleNamePatternTextBox, RuleExtensionsTextBox, RuleMinimumSizeTextBox, RuleMaximumSizeTextBox, RuleFoldersOnlyCheckBox, RuleExactNamesTextBox, RuleShortcutTargetTextBox })
            control.IsEnabled = enabled;
        if (rule == null) return;
        _updatingControls = true;
        try
        {
            RuleEnabledCheckBox.IsChecked = rule.IsEnabled;
            RuleNameTextBox.Text = rule.Name;
            RuleTargetComboBox.SelectedItem = RuleTargetComboBox.Items.OfType<FenceConfig>().FirstOrDefault(fence => fence.Id == rule.TargetFenceId);
            RulePriorityTextBox.Text = rule.Priority.ToString();
            RuleNamePatternTextBox.Text = rule.NamePattern;
            RuleExtensionsTextBox.Text = rule.Extensions;
            RuleExactNamesTextBox.Text = rule.ExactNames;
            RuleShortcutTargetTextBox.Text = rule.ShortcutTargetPattern;
            RuleMinimumSizeTextBox.Text = rule.MinimumSizeMb?.ToString() ?? "";
            RuleMaximumSizeTextBox.Text = rule.MaximumSizeMb?.ToString() ?? "";
            RuleFoldersOnlyCheckBox.IsChecked = rule.FoldersOnly;
        }
        finally { _updatingControls = false; }
    }

    private void RuleEditor_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || SelectedRule is not { } rule) return;
        rule.IsEnabled = RuleEnabledCheckBox.IsChecked == true;
        rule.Name = string.IsNullOrWhiteSpace(RuleNameTextBox.Text) ? "New rule" : RuleNameTextBox.Text.Trim();
        rule.TargetFenceId = (RuleTargetComboBox.SelectedItem as FenceConfig)?.Id ?? "";
        rule.Priority = int.TryParse(RulePriorityTextBox.Text, out var priority) ? priority : 100;
        rule.NamePattern = RuleNamePatternTextBox.Text.Trim();
        rule.Extensions = RuleExtensionsTextBox.Text.Trim();
        rule.ExactNames = RuleExactNamesTextBox.Text.Trim();
        rule.ShortcutTargetPattern = RuleShortcutTargetTextBox.Text.Trim();
        rule.MinimumSizeMb = double.TryParse(RuleMinimumSizeTextBox.Text, out var minimum) ? Math.Max(0, minimum) : null;
        rule.MaximumSizeMb = double.TryParse(RuleMaximumSizeTextBox.Text, out var maximum) ? Math.Max(0, maximum) : null;
        rule.FoldersOnly = RuleFoldersOnlyCheckBox.IsChecked == true;
        _mainWindow.SettingsSaveAutoOrganizeRule(rule);
        RuleList.Items.Refresh();
    }

    private void ShowFencesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_updatingControls)
        {
            _displayPreviewTemporarilyHidden = false;
            _mainWindow.SettingsSetFencesVisible(ShowFencesCheckBox.IsChecked == true);
            ReloadState();
        }
        UpdateDisplayPreview();
    }

    internal static string GetWelcomeVisibilityActionKey(bool allFencesVisible) =>
        allFencesVisible ? "DisableMiniFences" : "EnableMiniFences";

    private void DesktopDoubleClickCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls)
        {
            return;
        }

        _mainWindow.SettingsSetDesktopDoubleClick(DesktopDoubleClickCheckBox.IsChecked == true);
        if (DesktopDoubleClickCheckBox.IsChecked != true) _displayPreviewTemporarilyHidden = false;
        ReloadState();
        UpdateDisplayPreview();
    }

    private void DesktopIconIntegrationCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        _mainWindow.SettingsSetDesktopIconIntegration(DesktopIconIntegrationCheckBox.IsChecked == true);
        ReloadState();
        UpdateDisplayPreview();
    }

    private void TabSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        var mode = (TabViewComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Compact";
        var widthMode = (TabWidthComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Content";
        var selectionStyle = (TabSelectionStyleComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Fill";
        var lineThickness = (TabSelectionLineThicknessComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Medium";
        _mainWindow.SettingsSetTabOptions(mode, widthMode, selectionStyle, _tabSelectionLineColor, lineThickness,
            EnableTabCreationCheckBox.IsChecked == true,
            ConfirmTabCreationCheckBox.IsChecked == true, HoverSwitchTabsCheckBox.IsChecked == true);
        UpdateTabOptionAvailability();
        UpdateTabPreview();
    }

    private void TabSelectionLineColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        var dialog = new ColorPickerDialog(_tabSelectionLineColor, _mainWindow.Localization,
            chooseHeader: true, _mainWindow.Localization.T("TabSelectionLineColor"))
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true) return;
        _tabSelectionLineColor = FenceControl.NormalizeTabSelectionLineColor(dialog.SelectedColor);
        UpdateTabSelectionLineVisuals();
        TabSettings_Changed(sender, e);
    }

    private void UpdateTabSelectionLineVisuals()
    {
        if (TabSelectionLineColorSwatch == null) return;
        var brush = BrushFromColor(_tabSelectionLineColor, "#FF73D7FF");
        TabSelectionLineColorSwatch.Background = brush;
    }

    private void UpdateTabOptionAvailability()
    {
        var strip = (TabViewComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Strip";
        var availability = GetTabOptionAvailability(strip, EnableTabCreationCheckBox.IsChecked == true);
        TabWidthComboBox.IsEnabled = availability.TabWidths;
        TabSelectionStyleComboBox.IsEnabled = availability.TabWidths;
        var lineOptionsEnabled = availability.TabWidths &&
                                 (TabSelectionStyleComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "Fill";
        TabSelectionLineColorButton.IsEnabled = lineOptionsEnabled;
        TabSelectionLineColorSwatch.Opacity = lineOptionsEnabled ? 1 : 0.45;
        TabSelectionLineThicknessComboBox.IsEnabled = lineOptionsEnabled;
        ConfirmTabCreationCheckBox.IsEnabled = availability.ConfirmCreation;
        HoverSwitchTabsCheckBox.IsEnabled = availability.HoverSwitch;
    }

    internal static (bool TabWidths, bool ConfirmCreation, bool HoverSwitch)
        GetTabOptionAvailability(bool titleTabStrip, bool tabCreationEnabled) =>
        (titleTabStrip, tabCreationEnabled, titleTabStrip);

    private void RollupSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        _mainWindow.SettingsSetRollupOptions(EnableRollupCheckBox.IsChecked == true,
            DoubleClickRollupCheckBox.IsChecked == true, AutoEdgeRollupCheckBox.IsChecked == true,
            AllowBottomEdgeRollupCheckBox.IsChecked == true,
            BottomDockTitleCheckBox.IsChecked == true, TopDockTitleAtBottomOnExpandCheckBox.IsChecked == true,
            ClickTitleExpandCheckBox.IsChecked == true, HoverTitleExpandCheckBox.IsChecked == true);
        UpdateBottomRollupOptionAvailability();
        UpdateRollupGestureOptionAvailability();
        if (ReferenceEquals(sender, AutoEdgeRollupCheckBox) && AutoEdgeRollupCheckBox.IsChecked == true)
        {
            _rollupPreviewCollapsed = true;
            _rollupPreviewHoverExpanded = false;
        }
        if (ReferenceEquals(sender, HoverTitleExpandCheckBox) && HoverTitleExpandCheckBox.IsChecked != true)
            _rollupPreviewHoverExpanded = false;
        if (EnableRollupCheckBox.IsChecked != true)
        {
            _rollupPreviewCollapsed = false;
            _rollupPreviewHoverExpanded = false;
        }
        UpdateRollupPreview();
    }

    private void UpdateRollupGestureOptionAvailability()
    {
        var enabled = EnableRollupCheckBox.IsChecked == true;
        DoubleClickRollupCheckBox.IsEnabled = enabled;
        AutoEdgeRollupCheckBox.IsEnabled = enabled;
        ClickTitleExpandCheckBox.IsEnabled = enabled;
        HoverTitleExpandCheckBox.IsEnabled = enabled;
    }

    private void UpdateBottomRollupOptionAvailability()
    {
        var availability = GetBottomRollupOptionAvailability(
            EnableRollupCheckBox.IsChecked == true,
            AutoEdgeRollupCheckBox.IsChecked == true,
            AllowBottomEdgeRollupCheckBox.IsChecked == true);
        AllowBottomEdgeRollupCheckBox.IsEnabled = availability.AllowBottomEdge;
        BottomDockTitleCheckBox.IsEnabled = availability.BottomTitle;
        TopDockTitleAtBottomOnExpandCheckBox.IsEnabled = availability.MoveTitle;
    }

    internal static (bool AllowBottomEdge, bool BottomTitle, bool MoveTitle)
        GetBottomRollupOptionAvailability(
            bool rollupEnabled,
            bool automaticEdgeRollupEnabled,
            bool allowBottomEdge)
    {
        var enabled = rollupEnabled && automaticEdgeRollupEnabled;
        return (enabled, enabled && allowBottomEdge, enabled);
    }

    private void UpdateSettingsPreviews()
    {
        UpdateDisplayPreview();
        UpdateTabPreview();
        UpdateTabOptionAvailability();
        UpdateRollupGestureOptionAvailability();
        UpdateRollupPreview();
    }

    private void UpdateDisplayPreview()
    {
        if (DisplayPreviewFence == null) return;
        DisplayPreviewFence.Visibility = ShowFencesCheckBox.IsChecked == true && !_displayPreviewTemporarilyHidden
            ? Visibility.Visible : Visibility.Hidden;
        DisplayPreviewIcons.Opacity = DesktopIconIntegrationCheckBox.IsChecked == true ? 0.28 : 1.0;
        DisplayPreviewStatus.Text = DesktopDoubleClickCheckBox.IsChecked == true
            ? _mainWindow.Localization.T("PreviewDoubleClickHint")
            : _mainWindow.Localization.T("PreviewVisibilityStatus");
    }

    private void DisplayPreviewDesktop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || DesktopDoubleClickCheckBox.IsChecked != true) return;
        _displayPreviewTemporarilyHidden = !_displayPreviewTemporarilyHidden;
        UpdateDisplayPreview();
    }

    private void UpdateTabPreview()
    {
        if (TabPreviewPanel == null) return;
        TabPreviewPanel.Children.Clear();
        TabPreviewPanel.ColumnDefinitions.Clear();
        var strip = (TabViewComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Strip";
        var widthMode = (TabWidthComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        var equal = widthMode == "Equal";
        var adaptive = widthMode == "Adaptive";
        var selectionStyle = FenceControl.NormalizeTabSelectionStyle(
            (TabSelectionStyleComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        var selectionLineColor = FenceControl.NormalizeTabSelectionLineColor(
            _tabSelectionLineColor);
        var selectionLineThickness = FenceControl.NormalizeTabSelectionLineThickness(
            (TabSelectionLineThicknessComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        var labels = new[]
        {
            _mainWindow.Localization.T("PreviewTabDesktop"),
            _mainWindow.Localization.T("PreviewTabWork"),
            _mainWindow.Localization.T("PreviewTabGames")
        };
        if (!strip)
        {
            TabPreviewPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TabPreviewPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = labels[_tabPreviewIndex],
                Foreground = System.Windows.Media.Brushes.White,
                Margin = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var navigation = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var previous = CreateCompactTabPreviewArrow("‹", -1);
            var status = new TextBlock
            {
                Text = $"{_tabPreviewIndex + 1}/{labels.Length}",
                MinWidth = 32,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(221, 255, 255, 255)),
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var next = CreateCompactTabPreviewArrow("›", 1);
            TabPreviewPanel.Children.Add(title);
            navigation.Children.Add(previous);
            navigation.Children.Add(status);
            navigation.Children.Add(next);
            Grid.SetColumn(navigation, 1);
            TabPreviewPanel.Children.Add(navigation);
        }
        else
        {
            for (var index = 0; index < labels.Length; index++)
            {
                TabPreviewPanel.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = equal
                        ? new GridLength(1, GridUnitType.Star)
                        : adaptive
                            ? new GridLength(FenceControl.GetAdaptiveTabWeight(labels[index]), GridUnitType.Star)
                            : GridLength.Auto
                });
                var selectedIndex = index;
                var selected = index == _tabPreviewIndex;
                var fillSelection = selected && selectionStyle == "Fill";
                var tab = new Border
                {
                    MinWidth = equal || adaptive ? 0 : 72,
                    MaxWidth = equal || adaptive ? double.PositiveInfinity : 150,
                    Padding = new Thickness(14, 0, 14, 0),
                    Background = new SolidColorBrush(fillSelection
                        ? System.Windows.Media.Color.FromArgb(210, 255, 255, 255)
                        : System.Windows.Media.Color.FromArgb(48, 0, 0, 0)),
                    BorderBrush = selected && !fillSelection
                        ? new SolidColorBrush(FenceControl.GetSelectionLineColor(selectionLineColor))
                        : System.Windows.Media.Brushes.Transparent,
                    BorderThickness = selected
                        ? FenceControl.GetSelectionBorderThickness(selectionStyle, selectionLineThickness)
                        : new Thickness(0),
                    CornerRadius = index == 0
                        ? new CornerRadius(7, 0, 0, 0)
                        : index == labels.Length - 1 ? new CornerRadius(0, 7, 0, 0) : new CornerRadius(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = labels[index],
                        Foreground = fillSelection
                            ? System.Windows.Media.Brushes.Black
                            : System.Windows.Media.Brushes.White,
                        FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                tab.MouseLeftButtonUp += (_, _) => { _tabPreviewIndex = selectedIndex; UpdateTabPreview(); };
                tab.MouseEnter += (_, _) =>
                {
                    if (HoverSwitchTabsCheckBox.IsChecked != true) return;
                    _tabPreviewIndex = selectedIndex;
                    UpdateTabPreview();
                };
                Grid.SetColumn(tab, index);
                TabPreviewPanel.Children.Add(tab);
            }
            if (!equal && !adaptive)
                TabPreviewPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        TabPreviewContent.Text = "📄    📁    🖼";
    }

    private TextBlock CreateCompactTabPreviewArrow(string glyph, int direction)
    {
        var arrow = new TextBlock
        {
            Text = glyph,
            Width = 20,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        arrow.MouseLeftButtonDown += (_, e) =>
        {
            _tabPreviewIndex = (_tabPreviewIndex + direction + 3) % 3;
            UpdateTabPreview();
            e.Handled = true;
        };
        return arrow;
    }

    private void UpdateRollupPreview()
    {
        if (RollupPreviewContent == null) return;
        var collapsed = EnableRollupCheckBox.IsChecked == true && _rollupPreviewCollapsed &&
                        !_rollupPreviewHoverExpanded;
        var topEdgePreview = AutoEdgeRollupCheckBox.IsChecked == true && _rollupPreviewDock == "Top";
        var topMovingPreview = topEdgePreview &&
                               TopDockTitleAtBottomOnExpandCheckBox.IsChecked == true;
        var bottomEdgePreview = AutoEdgeRollupCheckBox.IsChecked == true && _rollupPreviewDock == "Bottom" &&
                                AllowBottomEdgeRollupCheckBox.IsChecked == true;
        var titleAtBottom = topMovingPreview
            ? !collapsed
            : bottomEdgePreview && BottomDockTitleCheckBox.IsChecked == true;
        RollupPreviewContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (titleAtBottom)
        {
            RollupPreviewFirstRow.Height = collapsed ? new GridLength(0) : new GridLength(82);
            RollupPreviewContentRow.Height = new GridLength(34);
            Grid.SetRow(RollupPreviewContent, 0);
            Grid.SetRow(RollupPreviewHeaderBorder, 1);
            RollupPreviewHeaderBorder.CornerRadius = collapsed
                ? new CornerRadius(7)
                : new CornerRadius(0, 0, 7, 7);
        }
        else
        {
            RollupPreviewFirstRow.Height = new GridLength(34);
            RollupPreviewContentRow.Height = collapsed ? new GridLength(0) : new GridLength(82);
            Grid.SetRow(RollupPreviewHeaderBorder, 0);
            Grid.SetRow(RollupPreviewContent, 1);
            RollupPreviewHeaderBorder.CornerRadius = collapsed
                ? new CornerRadius(7)
                : new CornerRadius(7, 7, 0, 0);
        }
        RollupPreviewHeader.Text = collapsed
            ? _mainWindow.Localization.T("PreviewRolledUp")
            : topMovingPreview
                ? _mainWindow.Localization.T("PreviewExpandedTopDockBottomTitle")
                : titleAtBottom
                    ? _mainWindow.Localization.T("PreviewExpandedBottomTitle")
                    : _mainWindow.Localization.T("PreviewExpanded");
        RollupPreviewFence.Opacity = EnableRollupCheckBox.IsChecked == true ? 1.0 : 0.55;
        if (!_rollupPreviewDragging)
        {
            var fenceHeight = collapsed ? 34d : 116d;
            Canvas.SetTop(RollupPreviewFence, GetRollupPreviewTop(_rollupPreviewDock,
                RollupPreviewCanvas.ActualHeight > 0 ? RollupPreviewCanvas.ActualHeight : 210d, fenceHeight));
        }
    }

    private void RollupPreviewFence_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rollupPreviewPointerDown = true;
        _rollupPreviewDragging = false;
        _rollupPreviewDragStart = e.GetPosition(RollupPreviewCanvas);
        _rollupPreviewDragTop = Canvas.GetTop(RollupPreviewFence);
        if (double.IsNaN(_rollupPreviewDragTop)) _rollupPreviewDragTop = 0;
        RollupPreviewFence.CaptureMouse();
        if (EnableRollupCheckBox.IsChecked != true) return;
        if (DoubleClickRollupCheckBox.IsChecked == true && e.ClickCount == 2)
        {
            _rollupPreviewSingleClickTimer?.Stop();
            (_rollupPreviewCollapsed, _rollupPreviewHoverExpanded) =
                ApplyRollupPreviewDoubleClick(_rollupPreviewCollapsed, _rollupPreviewHoverExpanded);
            UpdateRollupPreview();
            e.Handled = true;
            return;
        }
        if (e.ClickCount != 1 || ClickTitleExpandCheckBox.IsChecked != true || !_rollupPreviewCollapsed) return;
        if (DoubleClickRollupCheckBox.IsChecked != true) { ExpandRollupPreviewPermanently(); return; }
        _rollupPreviewSingleClickTimer ??= CreateRollupPreviewSingleClickTimer();
        _rollupPreviewSingleClickTimer.Stop();
        _rollupPreviewSingleClickTimer.Start();
    }

    private void RollupPreviewFence_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_rollupPreviewPointerDown || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(RollupPreviewCanvas);
        var deltaY = point.Y - _rollupPreviewDragStart.Y;
        if (!_rollupPreviewDragging && Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance) return;
        _rollupPreviewDragging = true;
        _rollupPreviewSingleClickTimer?.Stop();
        var height = RollupPreviewFence.ActualHeight > 0 ? RollupPreviewFence.ActualHeight : 116d;
        var maximum = Math.Max(0, RollupPreviewCanvas.ActualHeight - height);
        Canvas.SetTop(RollupPreviewFence, Math.Clamp(_rollupPreviewDragTop + deltaY, 0, maximum));
    }

    private void RollupPreviewFence_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = CommitRollupPreviewDrag();
        _rollupPreviewPointerDown = false;
        _rollupPreviewDragging = false;
        RollupPreviewFence.ReleaseMouseCapture();
        if (wasDragging)
        {
            UpdateRollupPreview();
            e.Handled = true;
        }
    }

    private void RollupPreviewFence_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var wasDragging = CommitRollupPreviewDrag();
        _rollupPreviewPointerDown = false;
        _rollupPreviewDragging = false;
        if (wasDragging) UpdateRollupPreview();
    }

    private bool CommitRollupPreviewDrag()
    {
        if (!_rollupPreviewDragging) return false;
        var height = RollupPreviewFence.ActualHeight > 0 ? RollupPreviewFence.ActualHeight : 116d;
        _rollupPreviewDock = GetRollupPreviewDock(Canvas.GetTop(RollupPreviewFence),
            RollupPreviewCanvas.ActualHeight, height);
        _rollupPreviewHoverExpanded = false;
        if (ShouldRollupPreviewAtDock(EnableRollupCheckBox.IsChecked == true,
                AutoEdgeRollupCheckBox.IsChecked == true,
                AllowBottomEdgeRollupCheckBox.IsChecked == true, _rollupPreviewDock))
            _rollupPreviewCollapsed = true;
        return true;
    }

    internal static bool ShouldRollupPreviewAtDock(bool rollupEnabled, bool automaticEdgeRollupEnabled,
        bool allowBottomEdge, string dock) =>
        rollupEnabled && automaticEdgeRollupEnabled &&
        (dock == "Top" || (dock == "Bottom" && allowBottomEdge));

    internal static string GetRollupPreviewDock(double top, double areaHeight, double fenceHeight,
        double threshold = 18)
    {
        var maximum = Math.Max(0, areaHeight - fenceHeight);
        if (top <= threshold) return "Top";
        if (top >= maximum - threshold) return "Bottom";
        return "Standard";
    }

    internal static double GetRollupPreviewTop(string dock, double areaHeight, double fenceHeight)
    {
        var maximum = Math.Max(0, areaHeight - fenceHeight);
        return dock switch
        {
            "Top" => 0,
            "Bottom" => maximum,
            _ => maximum / 2
        };
    }

    private DispatcherTimer CreateRollupPreviewSingleClickTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 20)
        };
        timer.Tick += (_, _) => { timer.Stop(); ExpandRollupPreviewPermanently(); };
        return timer;
    }

    private void ExpandRollupPreviewPermanently()
    {
        _rollupPreviewCollapsed = false;
        _rollupPreviewHoverExpanded = false;
        UpdateRollupPreview();
    }

    internal static (bool Collapsed, bool HoverExpanded)
        ApplyRollupPreviewDoubleClick(bool collapsed, bool hoverExpanded) =>
        hoverExpanded ? (true, false) : (!collapsed, false);

    private void RollupPreviewFence_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (EnableRollupCheckBox.IsChecked == true && HoverTitleExpandCheckBox.IsChecked == true && _rollupPreviewCollapsed)
        {
            _rollupPreviewHoverExpanded = true;
            UpdateRollupPreview();
        }
    }

    private void RollupPreviewFence_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_rollupPreviewPointerDown) return;
        _rollupPreviewHoverExpanded = false;
        if (ShouldRollupPreviewAtDock(EnableRollupCheckBox.IsChecked == true,
                AutoEdgeRollupCheckBox.IsChecked == true,
                AllowBottomEdgeRollupCheckBox.IsChecked == true, _rollupPreviewDock))
            _rollupPreviewCollapsed = true;
        UpdateRollupPreview();
    }

    private void BackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStyleFence is { } fence && _mainWindow.SettingsChooseFenceColor(fence.Id, chooseHeader: false))
        {
            ReloadState();
        }
    }

    private void HeaderColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStyleFence is { } fence && _mainWindow.SettingsChooseFenceColor(fence.Id, chooseHeader: true))
        {
            ReloadState();
        }
    }

    private void HeaderGradientCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || SelectedStyleFence is not { } fence) return;
        _mainWindow.SettingsSetFenceHeaderGradient(fence.Id, HeaderGradientCheckBox.IsChecked == true);
        ReloadState();
    }

    private void HeaderGradientColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStyleFence is { } fence && _mainWindow.SettingsChooseFenceGradientColor(fence.Id))
        {
            ReloadState();
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValueText != null)
        {
            OpacityValueText.Text = $"{e.NewValue:0}%";
        }
        if (!_updatingControls && SelectedStyleFence is { } fence) UpdateAppearancePreview(fence, e.NewValue / 100.0);
    }

    private void CommitOpacity()
    {
        if (!_updatingControls && SelectedStyleFence is { } fence)
            _mainWindow.SettingsSetFenceOpacity(fence.Id, OpacitySlider.Value / 100.0);
    }

    private void OpacitySlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => CommitOpacity();
    private void OpacitySlider_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitOpacity();
    private void OpacitySlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e) => CommitOpacity();

    private void TitleAlignmentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || SelectedStyleFence is not { } fence || TitleAlignmentComboBox.SelectedItem is not ComboBoxItem item) return;
        _mainWindow.SettingsSetFencePresentation(fence.Id, item.Tag?.ToString(), null, null);
        fence.TitleAlignment = item.Tag?.ToString() ?? "Left";
        UpdateAppearancePreview(fence, OpacitySlider.Value / 100.0);
    }

    private void FenceStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || SelectedStyleFence is not { } fence || FenceStyleComboBox.SelectedItem is not ComboBoxItem item) return;
        var clean = string.Equals(item.Tag?.ToString(), "Clean", StringComparison.OrdinalIgnoreCase);
        _mainWindow.SettingsSetFencePresentation(fence.Id, null, null, clean);
        fence.UseCleanStyle = clean;
        UpdateAppearancePreview(fence, OpacitySlider.Value / 100.0);
    }

    private void ShowPathCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || SelectedStyleFence is not { } fence) return;
        var show = ShowPathCheckBox.IsChecked == true;
        _mainWindow.SettingsSetFencePresentation(fence.Id, null, show, null);
        fence.ShowPath = show;
        UpdateAppearancePreview(fence, OpacitySlider.Value / 100.0);
    }

    private void ResetAppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStyleFence is { } fence)
        {
            _mainWindow.SettingsResetFenceAppearance(fence.Id);
            ReloadState();
        }
    }

    private void CopyAppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStyleFence is not { } fence) return;
        _copiedAppearance = CopyAppearance(fence);
        PasteAppearanceButton.IsEnabled = true;
    }

    private void PasteAppearanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedStyleFence is not { } target || _copiedAppearance == null) return;
        _mainWindow.SettingsApplyFenceAppearance(target.Id, _copiedAppearance);
        ReloadState();
    }

    private static FenceConfig CopyAppearance(FenceConfig source) => new()
    {
        Id = source.Id,
        Title = source.Title,
        BackgroundColor = source.BackgroundColor,
        HeaderColor = source.HeaderColor,
        HeaderGradientEnabled = source.HeaderGradientEnabled,
        HeaderGradientColor = source.HeaderGradientColor,
        Opacity = source.Opacity,
        TitleAlignment = source.TitleAlignment,
        ShowPath = source.ShowPath,
        UseCleanStyle = source.UseCleanStyle,
        ListShowType = source.ListShowType,
        ListShowSize = source.ListShowSize,
        ListShowTime = source.ListShowTime
    };

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || LanguageComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _mainWindow.SettingsSetLanguage(item.Tag?.ToString() ?? LocalizationService.English);
        ReloadState();
    }

    private void StartWithWindowsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls)
        {
            return;
        }

        _mainWindow.SettingsSetStartWithWindows(StartWithWindowsCheckBox.IsChecked == true, this);
        ReloadState();
    }

    private void ListColumnVisibilityCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || SelectedStyleFence is not { } fence) return;
        _mainWindow.SettingsSetFenceListColumns(fence.Id,
            ListShowTypeCheckBox.IsChecked == true,
            ListShowSizeCheckBox.IsChecked == true,
            ListShowTimeCheckBox.IsChecked == true);
    }

    private void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e) =>
        _mainWindow.SettingsCheckForUpdates();

    private void OpenConfigButton_Click(object sender, RoutedEventArgs e) => _mainWindow.SettingsOpenConfigFolder();

    private void OpenLogButton_Click(object sender, RoutedEventArgs e) => _mainWindow.SettingsOpenLogFile();
}
