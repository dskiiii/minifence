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
    private bool _updatingControls;
    private FenceConfig? _copiedAppearance;
    private System.Windows.Point _pagePreviewDragStart;
    private string? _pagePreviewDragFenceId;
    private int _tabPreviewIndex;
    private bool _rollupPreviewCollapsed;
    private bool _displayPreviewTemporarilyHidden;
    private bool _hasLoaded;
    private DateTime _lastReloadUtc = DateTime.MinValue;

    public SettingsWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        InitializeComponent();
        NavigationList.SelectedIndex = 0;
        Loaded += (_, _) =>
        {
            _hasLoaded = true;
            ReloadState();
        };
        Activated += (_, _) =>
        {
            if (_hasLoaded && DateTime.UtcNow - _lastReloadUtc > TimeSpan.FromSeconds(1)) ReloadState();
        };
    }

    internal void ReloadState()
    {
        _lastReloadUtc = DateTime.UtcNow;
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
            WelcomeToggleButton.Content = _mainWindow.IsDesktopIconIntegrationEnabled
                ? _mainWindow.Localization.T("DisableMiniFences")
                : _mainWindow.Localization.T("EnableMiniFences");
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
            ClickTitleExpandCheckBox.IsChecked = _mainWindow.IsClickTitleToExpandEnabled;
            HoverTitleExpandCheckBox.IsChecked = _mainWindow.IsHoverTitleToExpandEnabled;
            TabViewComboBox.SelectedItem = TabViewComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _mainWindow.TabViewMode, StringComparison.OrdinalIgnoreCase));
            TabWidthComboBox.SelectedItem = TabWidthComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), _mainWindow.TabWidthMode, StringComparison.OrdinalIgnoreCase));
            PreviousPageHotkeyTextBox.Text = _mainWindow.PreviousPageHotkey;
            NextPageHotkeyTextBox.Text = _mainWindow.NextPageHotkey;
            TopmostHotkeyTextBox.Text = _mainWindow.ToggleTopmostHotkey;
            StartWithWindowsCheckBox.IsChecked = _mainWindow.IsStartWithWindowsEnabled;
            PreviousPageButton.IsEnabled = _mainWindow.SettingsCurrentPage > 0;
            NextPageButton.IsEnabled = _mainWindow.SettingsCurrentPage < _mainWindow.SettingsPageCount - 1;
            DeletePageButton.IsEnabled = _mainWindow.CanDeleteSettingsCurrentPage;
            if (PagesPanel.Visibility == Visibility.Visible) RebuildPagePreviews(fences);
            if (LayoutsPanel.Visibility == Visibility.Visible) ReloadLayouts();

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

    internal void RefreshFromMainWindow() => ReloadState();

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
        WelcomeNav.Content = loc.T("Welcome");
        FencesNav.Content = loc.T("FencesNav");
        PagesNav.Content = loc.T("PagesNav");
        LayoutsNav.Content = loc.T("LayoutManagement");
        OrganizeNav.Content = loc.T("ClassificationAndRules");
        VisibilityNav.Content = loc.T("Visibility");
        RollupNav.Content = loc.T("Rollup");
        TabsNav.Content = loc.T("Tabs");
        PersonalizeNav.Content = loc.T("FenceAppearance");
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
        EnableTabCreationCheckBox.Content = loc.T("EnableTabCreation");
        ConfirmTabCreationCheckBox.Content = loc.T("ConfirmTabCreation");
        HoverSwitchTabsCheckBox.Content = loc.T("HoverSwitchTabs");
        foreach (var item in TabViewComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(string.Equals(item.Tag?.ToString(), "Strip", StringComparison.OrdinalIgnoreCase) ? "TitleTabStrip" : "CompactTabArrows");
        foreach (var item in TabWidthComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(string.Equals(item.Tag?.ToString(), "Equal", StringComparison.OrdinalIgnoreCase) ? "EqualTabWidths" : "ContentTabWidths");
        RollupSettingsTitle.Text = loc.T("Rollup");
        RollupDescription.Text = loc.T("RollupDescription");
        EnableRollupCheckBox.Content = loc.T("RollupEnabled");
        DoubleClickRollupCheckBox.Content = loc.T("DoubleClickRollup");
        AutoEdgeRollupCheckBox.Content = loc.T("AutoEdgeRollup");
        ClickTitleExpandCheckBox.Content = loc.T("ClickTitleExpand");
        HoverTitleExpandCheckBox.Content = loc.T("HoverTitleExpand");
        FenceAppearanceTitle.Text = loc.T("FenceAppearance");
        FenceAppearanceDescription.Text = loc.T("FenceAppearanceDescription");
        BackgroundColorLabel.Text = loc.T("BackgroundColor");
        HeaderColorLabel.Text = loc.T("HeaderColor");
        TitleAlignmentLabel.Text = loc.T("TitleAlignment");
        FenceStyleLabel.Text = loc.T("FrameStyle");
        ShowPathCheckBox.Content = loc.T("ShowPath");
        foreach (var item in TitleAlignmentComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(item.Tag?.ToString() switch { "Center" => "AlignCenter", "Right" => "AlignRight", _ => "AlignLeft" });
        foreach (var item in FenceStyleComboBox.Items.OfType<ComboBoxItem>())
            item.Content = loc.T(string.Equals(item.Tag?.ToString(), "Clean", StringComparison.OrdinalIgnoreCase) ? "CleanStyle" : "FramedStyle");
        OpacityLabel.Text = loc.T("Opacity");
        BackgroundColorButton.Content = loc.T("ChooseColor");
        HeaderColorButton.Content = loc.T("ChooseColor");
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
        PreviousPageHotkeyLabel.Text = loc.T("PreviousPage");
        NextPageHotkeyLabel.Text = loc.T("NextPage");
        TopmostHotkeyLabel.Text = loc.T("PinRestoreFences");
        ApplyHotkeysButton.Content = loc.T("ApplyShortcuts");
        StartWithWindowsCheckBox.Content = loc.T("StartWithWindows");
        foreach (var item in LanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            item.Content = string.Equals(item.Tag?.ToString(), LocalizationService.Chinese, StringComparison.OrdinalIgnoreCase)
                ? loc.T("Chinese")
                : loc.T("English");
        }

        AboutTitle.Text = loc.T("About");
        AboutDescription.Text = loc.T("AboutDescription");
        VersionText.Text = $"MiniFences {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "0.20.8"}";
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
        }
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
        GeneralPanel,
        AboutPanel
    ];

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
        var enabled = fence?.IsDesktopGroup == true;
        FencePageComboBox.ItemsSource = Enumerable.Range(1, _mainWindow.SettingsPageCount).ToArray();
        FencePageComboBox.SelectedItem = fence == null ? null : fence.PageIndex + 1;
        FencePageComboBox.IsEnabled = fence != null;
        UnassignedItemsList.ItemsSource = enabled ? _mainWindow.SettingsGetUnassignedDesktopItems() : Array.Empty<FolderItem>();
        FenceItemsList.ItemsSource = enabled ? _mainWindow.SettingsGetFenceItems(fence!.Id) : Array.Empty<FolderItem>();
        AssignItemsButton.IsEnabled = enabled;
        UnassignItemsButton.IsEnabled = enabled;
    }

    private void AssignItemsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFence is not { } fence) return;
        var paths = UnassignedItemsList.SelectedItems.OfType<FolderItem>().Select(item => item.FullPath).ToArray();
        if (paths.Length == 0) return;
        _mainWindow.SettingsAssignDesktopItems(fence.Id, paths);
        ReloadState();
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
        OpacitySlider.IsEnabled = enabled;
        ResetAppearanceButton.IsEnabled = enabled;
        CopyAppearanceButton.IsEnabled = enabled;
        PasteAppearanceButton.IsEnabled = enabled && _copiedAppearance != null;
        TitleAlignmentComboBox.IsEnabled = enabled;
        FenceStyleComboBox.IsEnabled = enabled;
        ShowPathCheckBox.IsEnabled = enabled;
        if (fence == null)
        {
            BackgroundColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
            HeaderColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
            OpacityValueText.Text = "";
            PreviewFenceBorder.Visibility = Visibility.Hidden;
            return;
        }

        BackgroundColorSwatch.Background = BrushFromColor(fence.BackgroundColor, "#DD20242A");
        HeaderColorSwatch.Background = BrushFromColor(fence.HeaderColor, "#CC3F7FA8");
        TitleAlignmentComboBox.SelectedItem = TitleAlignmentComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), fence.TitleAlignment, StringComparison.OrdinalIgnoreCase));
        FenceStyleComboBox.SelectedItem = FenceStyleComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), fence.UseCleanStyle ? "Clean" : "Framed", StringComparison.OrdinalIgnoreCase));
        ShowPathCheckBox.IsChecked = fence.ShowPath;
        OpacitySlider.Value = Math.Round(Math.Clamp(fence.Opacity, 0.0, 1.0) * 100);
        OpacityValueText.Text = $"{OpacitySlider.Value:0}%";
        UpdateAppearancePreview(fence, fence.Opacity);
    }

    private void UpdateAppearancePreview(FenceConfig fence, double opacity)
    {
        PreviewFenceBorder.Visibility = Visibility.Visible;
        PreviewFenceBorder.Opacity = 1.0;
        PreviewHeader.Background = BrushFromColor(fence.HeaderColor, "#CC3F7FA8");
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
        _mainWindow.SettingsRefreshAll();
        ReloadState();
    }

    private void WelcomeIntegrationButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.SettingsSetDesktopIconIntegration(!_mainWindow.IsDesktopIconIntegrationEnabled);
        ReloadState();
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
                out var error))
        {
            System.Windows.MessageBox.Show(this, error, "MiniFences", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ReloadState();
    }

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

            foreach (var fence in fences.Where(fence => fence.PageIndex == pageIndex))
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
        foreach (var control in new System.Windows.Controls.Control[] { RuleEnabledCheckBox, RuleNameTextBox, RuleTargetComboBox, RulePriorityTextBox, RuleNamePatternTextBox, RuleExtensionsTextBox, RuleMinimumSizeTextBox, RuleMaximumSizeTextBox, RuleFoldersOnlyCheckBox })
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
        rule.MinimumSizeMb = double.TryParse(RuleMinimumSizeTextBox.Text, out var minimum) ? Math.Max(0, minimum) : null;
        rule.MaximumSizeMb = double.TryParse(RuleMaximumSizeTextBox.Text, out var maximum) ? Math.Max(0, maximum) : null;
        rule.FoldersOnly = RuleFoldersOnlyCheckBox.IsChecked == true;
        _mainWindow.SettingsSaveAutoOrganizeRule(rule);
        RuleList.Items.Refresh();
    }

    private void ShowFencesCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (!_updatingControls && ShowFencesCheckBox.IsChecked == _mainWindow.AreFencesHidden)
        {
            _mainWindow.SettingsToggleFences();
            ReloadState();
        }
        UpdateDisplayPreview();
    }

    private void DesktopDoubleClickCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingControls)
        {
            return;
        }

        _mainWindow.SettingsSetDesktopDoubleClick(DesktopDoubleClickCheckBox.IsChecked == true);
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
        _mainWindow.SettingsSetTabOptions(mode, widthMode, EnableTabCreationCheckBox.IsChecked == true,
            ConfirmTabCreationCheckBox.IsChecked == true, HoverSwitchTabsCheckBox.IsChecked == true);
        UpdateTabPreview();
    }

    private void RollupSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingControls) return;
        _mainWindow.SettingsSetRollupOptions(EnableRollupCheckBox.IsChecked == true,
            DoubleClickRollupCheckBox.IsChecked == true, AutoEdgeRollupCheckBox.IsChecked == true,
            ClickTitleExpandCheckBox.IsChecked == true, HoverTitleExpandCheckBox.IsChecked == true);
        _rollupPreviewCollapsed = EnableRollupCheckBox.IsChecked == true && AutoEdgeRollupCheckBox.IsChecked == true;
        UpdateRollupPreview();
    }

    private void UpdateSettingsPreviews()
    {
        UpdateDisplayPreview();
        UpdateTabPreview();
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
        var equal = (TabWidthComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Equal";
        var labels = new[] { "Desktop", "Work", "Games" };
        if (!strip)
        {
            TabPreviewPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TabPreviewPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var arrows = new TextBlock { Text = $"‹  {labels[_tabPreviewIndex]}  ›", Foreground = System.Windows.Media.Brushes.White, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            TabPreviewPanel.Children.Add(arrows);
        }
        else
        {
            for (var index = 0; index < labels.Length; index++)
            {
                TabPreviewPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = equal ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                var selectedIndex = index;
                var button = new System.Windows.Controls.Button
                {
                    Content = labels[index],
                    Padding = new Thickness(14, 0, 14, 0),
                    Foreground = System.Windows.Media.Brushes.White,
                    Background = index == _tabPreviewIndex ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(90, 255, 255, 255)) : System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0)
                };
                button.Click += (_, _) => { _tabPreviewIndex = selectedIndex; UpdateTabPreview(); };
                button.MouseEnter += (_, _) =>
                {
                    if (HoverSwitchTabsCheckBox.IsChecked != true) return;
                    _tabPreviewIndex = selectedIndex;
                    UpdateTabPreview();
                };
                Grid.SetColumn(button, index);
                TabPreviewPanel.Children.Add(button);
            }
        }
        TabPreviewContent.Text = $"{labels[_tabPreviewIndex]}  ·  " + (EnableTabCreationCheckBox.IsChecked == true
            ? _mainWindow.Localization.T("PreviewTabCreationOn") : _mainWindow.Localization.T("PreviewTabCreationOff"));
    }

    private void UpdateRollupPreview()
    {
        if (RollupPreviewContent == null) return;
        var collapsed = EnableRollupCheckBox.IsChecked == true && _rollupPreviewCollapsed;
        RollupPreviewContent.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RollupPreviewContentRow.Height = collapsed ? new GridLength(0) : new GridLength(82);
        RollupPreviewHeader.Text = collapsed
            ? _mainWindow.Localization.T("PreviewRolledUp")
            : _mainWindow.Localization.T("PreviewExpanded");
        RollupPreviewFence.Opacity = EnableRollupCheckBox.IsChecked == true ? 1.0 : 0.55;
    }

    private void RollupPreviewFence_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (EnableRollupCheckBox.IsChecked != true) return;
        if ((DoubleClickRollupCheckBox.IsChecked == true && e.ClickCount == 2) ||
            (ClickTitleExpandCheckBox.IsChecked == true && _rollupPreviewCollapsed))
        {
            _rollupPreviewCollapsed = !_rollupPreviewCollapsed;
            UpdateRollupPreview();
        }
    }

    private void RollupPreviewFence_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (EnableRollupCheckBox.IsChecked == true && HoverTitleExpandCheckBox.IsChecked == true && _rollupPreviewCollapsed)
        {
            _rollupPreviewCollapsed = false;
            UpdateRollupPreview();
        }
    }

    private void RollupPreviewFence_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (EnableRollupCheckBox.IsChecked == true && HoverTitleExpandCheckBox.IsChecked == true)
        {
            _rollupPreviewCollapsed = true;
            UpdateRollupPreview();
        }
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
        Opacity = source.Opacity,
        TitleAlignment = source.TitleAlignment,
        ShowPath = source.ShowPath,
        UseCleanStyle = source.UseCleanStyle
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

    private void OpenConfigButton_Click(object sender, RoutedEventArgs e) => _mainWindow.SettingsOpenConfigFolder();

    private void OpenLogButton_Click(object sender, RoutedEventArgs e) => _mainWindow.SettingsOpenLogFile();
}
