using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using MiniFences;
using MiniFences.Models;
using MiniFences.Services;

var root = Path.Combine(Path.GetTempPath(), "MiniFencesSmokeTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("MINIFENCES_LOG_PATH", Path.Combine(root, "logs", "app.log"));

try
{
    Assert(AppLogger.LogPath.StartsWith(root, StringComparison.OrdinalIgnoreCase),
        "Smoke-test diagnostics must be isolated from the user's production log.");
    TestConfigRoundTrip(root);
    TestDefaultConfig(root);
    TestLocalization();
    TestDesktopDoubleClickTracker();
    TestDesktopDragData();
    TestSettingsNavigation();
    TestTabMergeRules();
    TestFenceLayout();
    TestPageDeletion(root);
    TestFolderItemLoadingAndMove(root);
    TestShellOpenRequests(root);
    TestShellContextMenuPathSelection(root);
    TestShellContextMenuHostCommands();
    TestFenceControlBindingAndLayout(root);
    TestMetadataOrganizer(root);
    TestMultiRootMetadataOrganizer(root);
    Console.WriteLine("MiniFences smoke tests passed.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    try
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
    catch
    {
        // Temporary cleanup failure should not hide the real test result.
    }
}

static void TestShellContextMenuPathSelection(string root)
{
    var personal = Directory.CreateDirectory(Path.Combine(root, "shell-personal")).FullName;
    var common = Directory.CreateDirectory(Path.Combine(root, "shell-common")).FullName;
    var personalTarget = Path.Combine(personal, "target.txt");
    var personalPeer = Path.Combine(personal, "peer.txt");
    var commonPeer = Path.Combine(common, "public.txt");
    File.WriteAllText(personalTarget, "target");
    File.WriteAllText(personalPeer, "peer");
    File.WriteAllText(commonPeer, "public");

    var selected = ShellContextMenuService.SelectPathsForContextMenu(
        personalTarget,
        [commonPeer, personalPeer, personalTarget]);
    Assert(selected.Length == 2, "Shell context menu selection should exclude items from other directories.");
    Assert(string.Equals(selected[0], personalTarget, StringComparison.OrdinalIgnoreCase),
        "The right-clicked item must be the first Shell context menu item.");
    Assert(selected.Contains(personalPeer, StringComparer.OrdinalIgnoreCase),
        "Same-directory selected items should remain in the Shell context menu selection.");

    var commonSelection = ShellContextMenuService.SelectPathsForContextMenu(
        commonPeer,
        [personalTarget, personalPeer]);
    Assert(commonSelection.SequenceEqual([commonPeer], StringComparer.OrdinalIgnoreCase),
        "Cross-directory selection should follow the right-clicked item's directory even when it was not in the selected list.");

    var invalidSelection = ShellContextMenuService.SelectPathsForContextMenu(
        Path.Combine(personal, "missing.txt"),
        [personalPeer]);
    Assert(invalidSelection.Length == 0, "A missing right-click target should not open a Shell context menu.");
}

static void TestShellContextMenuHostCommands()
{
    Assert(ShellContextMenuService.ShouldHandleCommandInHost("rename"),
        "The Shell rename command must be handled by MiniFences so it can show its own rename editor.");
    Assert(ShellContextMenuService.ShouldHandleCommandInHost("RENAME"),
        "Shell command matching should be case-insensitive.");
    Assert(!ShellContextMenuService.ShouldHandleCommandInHost("delete") &&
           !ShellContextMenuService.ShouldHandleCommandInHost(null),
        "Commands implemented by the Shell must continue through the native context-menu handler.");
}

static void TestSettingsNavigation()
{
    var expectedPanels = new Dictionary<string, string>
    {
        ["Welcome"] = "WelcomePanel",
        ["Fences"] = "FencesPanel",
        ["Pages"] = "PagesPanel",
        ["Layouts"] = "LayoutsPanel",
        ["Organize"] = "OrganizePanel",
        ["Visibility"] = "DisplayPanel",
        ["Rollup"] = "RollupPanel",
        ["Tabs"] = "TabsPanel",
        ["Appearance"] = "AppearancePanel",
        ["General"] = "GeneralPanel",
        ["About"] = "AboutPanel"
    };

    foreach (var expected in expectedPanels)
    {
        Assert(SettingsWindow.ResolvePanelName(expected.Key) == expected.Value,
            $"Settings navigation tag '{expected.Key}' should show only '{expected.Value}'.");
    }
    Assert(expectedPanels.Values.Distinct(StringComparer.Ordinal).Count() == expectedPanels.Count,
        "Every Settings navigation entry should resolve to its own panel.");
    Assert(SettingsWindow.ResolvePanelName("Personalization") == null,
        "The removed combined Personalization page must not remain addressable.");
}

static void TestDesktopDragData()
{
    var paths = new[] { @"C:\Desktop\one.txt", @"C:\Desktop\two.txt" };
    var data = new System.Windows.DataObject();
    DesktopDragData.Set(data, paths, looseIcon: true, paths[0]);
    Assert(DesktopDragData.TryGetPaths(data, out var restored) && restored.SequenceEqual(paths),
        "Internal desktop drags should preserve item paths.");
    Assert(data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
           data.GetData(System.Windows.DataFormats.FileDrop) is string[] fileDrop && fileDrop.SequenceEqual(paths),
        "Desktop drags should expose standard Shell FileDrop data for browser and chat uploads.");
    Assert(DesktopDragData.IsLooseIconDrag(data) && DesktopDragData.GetAnchorPath(data) == paths[0],
        "Loose icon drags should preserve their origin and anchor item.");
    Assert(!DesktopDragData.ShouldCancelExplorerDesktopDrop(System.Windows.DragDropKeyStates.LeftMouseButton, true) &&
           DesktopDragData.ShouldCancelExplorerDesktopDrop(System.Windows.DragDropKeyStates.None, true) &&
           !DesktopDragData.ShouldCancelExplorerDesktopDrop(System.Windows.DragDropKeyStates.None, false),
        "A completed drop on the Explorer desktop should be canceled before Shell can move or copy source files.");
    Assert(DesktopDragData.ShouldReleaseDesktopMembershipAfterDrag(System.Windows.DragDropEffects.None, true) &&
           !DesktopDragData.ShouldReleaseDesktopMembershipAfterDrag(System.Windows.DragDropEffects.Copy, false),
        "Only a canceled Explorer-desktop drop may release Fence membership; external uploads must keep it.");
    var reordered = MainWindow.ReorderDesktopIcons(["one", "two", "three", "four"], ["three", "four"], 1);
    Assert(reordered.SequenceEqual(["one", "three", "four", "two"]),
        "Manual desktop icon dragging should change only the automatic grid order and keep multi-selection contiguous.");
}

static void TestDefaultConfig(string root)
{
    var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    if (Directory.Exists(commonDesktop))
    {
        Assert(MainWindow.GetDesktopRoots().Contains(Path.GetFullPath(commonDesktop), StringComparer.OrdinalIgnoreCase),
            "The Windows public desktop must be included in desktop icon rendering.");
        Assert(FenceControl.IsDirectChildOfDesktopRoot(Path.Combine(commonDesktop, "public-shortcut.lnk")),
            "Desktop Fences must accept items from the Windows public desktop.");
    }
    var personalDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    Assert(FenceControl.IsDirectChildOfDesktopRoot(Path.Combine(personalDesktop, "personal-file.txt")),
        "Desktop Fences must accept items from the personal desktop.");

    var desktop = Path.Combine(root, "default-desktop");
    Directory.CreateDirectory(desktop);
    var looseFile = Path.Combine(desktop, "keep-on-desktop.txt");
    File.WriteAllText(looseFile, "keep");

    var config = ConfigService.CreateDefaultAppConfig(desktop);
    Assert(config.Fences.Count == 1, "Default config should create one desktop group without category folders.");
    Assert(config.Fences.Single().IsDesktopGroup, "Default Fence should use desktop assignment metadata.");
    Assert(config.Fences.Single().AssignedPaths.Contains(looseFile), "Default desktop group should record existing desktop item membership.");
    Assert(!Directory.Exists(Path.Combine(desktop, "MiniFences Organized")), "Default config must not create a physical organization folder.");
    Assert(File.Exists(looseFile), "Default config should not move existing desktop files.");
    Assert(config.Fences.Select(fence => fence.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == config.Fences.Count, "Default Fences should have unique ids.");
    Assert(config.EnableSnapToGrid, "Grid snapping should be on by default.");
    Assert(config.EnableTabCreation, "Tab creation should be enabled by default.");
    Assert(config.TabViewMode == "Compact", "The compact tab view should remain the default for compatibility.");
    Assert(!config.ConfirmTabCreation && !config.HoverSwitchTabs, "Confirmation and hover switching should be opt-in.");
    Assert(config.EnableRollup && config.DoubleClickTitleRollup, "Roll-up and title double-click should remain enabled by default.");
    Assert(!config.AutoRollupAtScreenEdge && !config.ClickTitleToExpand && !config.HoverTitleToExpand,
        "Automatic edge roll-up and alternate expansion gestures should be opt-in.");
    Assert(config.Fences.All(fence => !fence.EnableHoverExpand), "Hover expansion should be off by default.");
    var hoverFence = new FenceConfig { IsCollapsed = true };
    Assert(!FenceControl.ShouldHoverExpand(hoverFence, dockedHoverEnabled: true),
        "The global edge-hover option must not affect a Fence after it is undocked.");
    hoverFence.EdgeDock = "Top";
    Assert(FenceControl.ShouldHoverExpand(hoverFence, dockedHoverEnabled: true),
        "A docked Fence should honor the global edge-hover option.");
    hoverFence.EdgeDock = null;
    hoverFence.EnableHoverExpand = true;
    Assert(FenceControl.ShouldHoverExpand(hoverFence, dockedHoverEnabled: false),
        "A Fence-specific hover option should continue to work away from screen edges.");
    Assert(!HasOverlappingFences(config.Fences), "Default Fences should not overlap in the starter workspace.");
    var starterCategories = AutoOrganizerService.GetStarterCategories().ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(AutoOrganizerService.GetOrganizerCategories().All(starterCategories.Contains), "Every organizer category should have a starter Fence.");

}

static void TestTabMergeRules()
{
    Assert(MainWindow.CanHeaderDragMerge(new FenceConfig(), shiftPressed: false),
        "An independent Fence should merge by ordinary title drag.");
    Assert(!MainWindow.CanHeaderDragMerge(new FenceConfig { TabGroupId = "tabs" }, shiftPressed: false),
        "An ordinary drag on a tab group should move the Fence instead of merging a tab.");
    Assert(MainWindow.CanHeaderDragMerge(new FenceConfig { TabGroupId = "tabs" }, shiftPressed: true),
        "Shift-drag should enable tab-group merge operations.");
    var target = new FenceConfig { Left = 300, Top = 100, Width = 300, Height = 240 };
    var edgeTouch = new FenceConfig { Left = 590, Top = 100, Width = 300, Height = 240 };
    var centered = new FenceConfig { Left = 300, Top = 104, Width = 300, Height = 240 };
    var leftThird = new FenceConfig { Left = 180, Top = 104, Width = 300, Height = 240 };
    var rightThird = new FenceConfig { Left = 420, Top = 104, Width = 300, Height = 240 };
    Assert(!MainWindow.IsMergeCandidate(edgeTouch, target), "A slight title edge overlap must not trigger tab merging.");
    Assert(MainWindow.IsMergeCandidate(centered, target), "The middle third of the target header should trigger tab merging.");
    Assert(!MainWindow.IsMergeCandidate(leftThird, target), "The left third of the target header must not trigger tab merging.");
    Assert(!MainWindow.IsMergeCandidate(rightThird, target), "The right third of the target header must not trigger tab merging.");
    Assert(MainWindow.IsPointerInMergeZone(new System.Windows.Point(450, 117), target), "A pointer in the middle third should trigger the initial merge preview.");
    Assert(!MainWindow.IsPointerInMergeZone(new System.Windows.Point(330, 117), target), "A pointer outside the middle third should not trigger the initial merge preview.");

    var config = new AppConfig
    {
        Fences =
        [
            new FenceConfig { Id = "removed", TabGroupId = null },
            new FenceConfig { Id = "remaining", TabGroupId = "group" }
        ]
    };
    Assert(MainWindow.DissolveSingleItemTabGroup(config, "group"), "A one-item tab group should be dissolved.");
    Assert(config.Fences[1].TabGroupId == null, "The remaining Fence should no longer expose tab-group actions.");
}

static void TestMetadataOrganizer(string root)
{
    var desktop = Path.Combine(root, "metadata-desktop");
    Directory.CreateDirectory(desktop);
    var document = Path.Combine(desktop, "report.docx");
    var image = Path.Combine(desktop, "photo.png");
    File.WriteAllText(document, "document");
    File.WriteAllText(image, "image");
    var documentContents = File.ReadAllText(document);
    var imageContents = File.ReadAllText(image);

    var config = new AppConfig { Fences = [new FenceConfig { Title = "Desktop", FolderPath = desktop }] };
    var organizer = new AutoOrganizerService(desktopPath: desktop, historyPath: Path.Combine(root, "metadata-history.json"));
    Assert(organizer.ConvertLegacyDesktopPortal(config), "Legacy Desktop portal should automatically convert to a metadata group.");
    Assert(config.Fences.Single().IsDesktopGroup && config.Fences.Single().AssignedPaths.Count == 2, "Converted Desktop group should record existing desktop items without moving them.");
    var assigned = organizer.AssignDesktopItemsByTypeWithUndo(config);

    Assert(assigned == 2, "Metadata organizer should assign both desktop items.");
    Assert(File.Exists(document) && File.ReadAllText(document) == documentContents, "Document must remain at its original desktop path.");
    Assert(File.Exists(image) && File.ReadAllText(image) == imageContents, "Image must remain at its original desktop path.");
    Assert(!Directory.Exists(Path.Combine(desktop, "MiniFences Organized")), "Metadata organization must not create category folders.");
    Assert(config.Fences.Single(fence => fence.Title == "\u6587\u6863\u8d44\u6599").AssignedPaths.Contains(document), "Document Fence should record membership only.");
    Assert(config.Fences.Single(fence => fence.Title == "\u622a\u56fe\u56fe\u7247").AssignedPaths.Contains(image), "Image Fence should record membership only.");
    Assert(config.Fences.Count(fence => AutoOrganizerService.GetStarterCategories().Contains(fence.Title)) == 2,
        "One-click organization should create only categories that contain actual items.");

    organizer.AssignDesktopItemsByTypeWithUndo(config);
    var undo = organizer.UndoLastOrganization(config);
    Assert(undo.Errors.Count == 0 && config.Fences.Single().Title == "Desktop",
        "A repeated no-op organization must not replace the previous useful assignment undo history.");
    Assert(config.Fences.Single().AssignedPaths.Contains(document) && config.Fences.Single().AssignedPaths.Contains(image),
        "Undo should restore the exact desktop Fence memberships from before organization.");
    Assert(File.Exists(document) && File.Exists(image), "Assignment undo must not move desktop source files.");
    organizer.AssignDesktopItemsByTypeWithUndo(config);

    config.ClassificationScheme = "Simple";
    Assert(organizer.AssignDesktopItemsByType(config) == 2, "The selected simple classification scheme should organize all items.");
    Assert(config.Fences.Single(fence => fence.Title == "\u6587\u6863\u548c\u6587\u4ef6").AssignedPaths.Contains(document), "Simple scheme should place documents in its visible document group.");
    Assert(config.Fences.Single(fence => fence.Title == "\u56fe\u7247\u548c\u5a92\u4f53").AssignedPaths.Contains(image), "Simple scheme should place images in its visible media group.");
    Assert(!config.Fences.Any(fence => fence.Title == "\u6587\u6863\u8d44\u6599" || fence.Title == "\u622a\u56fe\u56fe\u7247"), "Switching schemes must remove generated Fences that became empty.");
    config.ClassificationScheme = "Detailed";
    organizer.AssignDesktopItemsByType(config);

    var rulesTarget = config.Fences.Single(fence => fence.Title == "\u6587\u6863\u8d44\u6599");
    var fallbackTarget = new FenceConfig { Id = "fallback", Title = "\u5176\u4ed6", Kind = FenceConfig.DesktopGroupKind };
    config.Fences.Add(fallbackTarget);
    config.AutoOrganizeRules =
    [
        new AutoOrganizeRule
        {
            Name = "Large reports",
            Priority = 200,
            TargetFenceId = rulesTarget.Id,
            NamePattern = "report*",
            Extensions = "docx;pdf",
            MinimumSizeMb = 0
        }
    ];
    config.DefaultAutoOrganizeFenceId = fallbackTarget.Id;
    Assert(organizer.ResolveTargetFence(config, document)?.Id == rulesTarget.Id, "A matching custom rule should resolve by stable Fence id.");
    Assert(organizer.ResolveTargetFence(config, image)?.Title == "\u622a\u56fe\u56fe\u7247", "Automatic organization should prefer the matching type category over the fallback Fence.");
    var folderForRule = Path.Combine(desktop, "rule-folder");
    Directory.CreateDirectory(folderForRule);
    config.AutoOrganizeRules.Insert(0, new AutoOrganizeRule
    {
        Name = "Folders",
        Priority = 300,
        TargetFenceId = fallbackTarget.Id,
        FoldersOnly = true
    });
    Assert(organizer.ResolveTargetFence(config, folderForRule)?.Id == fallbackTarget.Id, "Folders-only rules should match directories.");

    var legacyFolder = Path.Combine(desktop, "MiniFences Organized", "\u538b\u7f29\u5305");
    Directory.CreateDirectory(legacyFolder);
    var legacyItem = Path.Combine(legacyFolder, "archive.zip");
    File.WriteAllText(legacyItem, "archive");
    var legacyFence = new FenceConfig { Title = "\u538b\u7f29\u5305", FolderPath = legacyFolder };
    config.Fences.Add(legacyFence);
    var migration = organizer.MigrateManagedFoldersToDesktopGroups(config);
    var restoredArchive = Path.Combine(desktop, "archive.zip");
    Assert(migration.MigratedFences == 1 && migration.RestoredItems == 1, "Legacy managed folder should migrate to one desktop group.");
    Assert(File.Exists(restoredArchive) && !File.Exists(legacyItem), "Migration should restore the file to the Desktop.");
    Assert(legacyFence.IsDesktopGroup && legacyFence.AssignedPaths.Contains(restoredArchive), "Migrated Fence should store restored item membership.");
}

static void TestMultiRootMetadataOrganizer(string root)
{
    var personal = Path.Combine(root, "multi-root-personal");
    var common = Path.Combine(root, "multi-root-common");
    Directory.CreateDirectory(personal);
    Directory.CreateDirectory(common);
    var personalDocument = Path.Combine(personal, "notes.docx");
    var commonShortcut = Path.Combine(common, "Shared App.lnk");
    File.WriteAllText(personalDocument, "document");
    File.WriteAllText(commonShortcut, "shortcut");
    var config = new AppConfig();
    var organizer = new AutoOrganizerService(desktopPath: personal,
        historyPath: Path.Combine(root, "multi-root-history.json"),
        additionalDesktopPaths: [common]);

    Assert(organizer.AssignDesktopItemsByType(config) == 2,
        "Metadata organization should include both personal and common desktop roots.");
    Assert(config.Fences.Single(fence => fence.Title == "\u5e38\u7528\u5feb\u6377\u65b9\u5f0f").AssignedPaths.Contains(commonShortcut),
        "A public desktop shortcut should be assigned without moving its source file.");
    Assert(File.Exists(commonShortcut), "Public desktop organization must preserve the original shortcut path.");

    var secondCommonShortcut = Path.Combine(common, "Second Shared App.lnk");
    File.WriteAllText(secondCommonShortcut, "shortcut");
    Assert(organizer.AssignUnassignedDesktopItemsByType(config) == 1,
        "Automatic reconciliation should assign only newly discovered unassigned desktop entries.");
    Assert(organizer.AssignUnassignedDesktopItemsByType(config) == 0,
        "Automatic reconciliation must not reassign items that already belong to a Fence.");

    var steamGame = Path.Combine(common, "Steam Game.url");
    var epicGame = Path.Combine(common, "Epic Game.url");
    var steamClient = Path.Combine(common, "Steam.url");
    File.WriteAllText(steamGame, "[InternetShortcut]\nURL=steam://rungameid/12345\n");
    File.WriteAllText(epicGame, "[InternetShortcut]\nURL=com.epicgames.launcher://apps/example?action=launch\n");
    File.WriteAllText(steamClient, "[InternetShortcut]\nURL=steam://open/main\n");
    organizer.AssignDesktopItemsByType(config);
    var gamesFence = config.Fences.Single(fence => fence.Title == "\u6e38\u620f");
    Assert(gamesFence.AssignedPaths.Contains(steamGame) && gamesFence.AssignedPaths.Contains(epicGame),
        "Steam and Epic game launch shortcuts should be assigned to Games.");
    Assert(!gamesFence.AssignedPaths.Contains(steamClient),
        "The Steam client shortcut itself must not be classified as a game.");
    Assert(File.Exists(steamGame) && File.Exists(epicGame),
        "Game classification must preserve shortcut files in their original desktop root.");
}

static bool HasOverlappingFences(IReadOnlyList<FenceConfig> fences)
{
    for (var outer = 0; outer < fences.Count; outer += 1)
    {
        for (var inner = outer + 1; inner < fences.Count; inner += 1)
        {
            if (Intersects(fences[outer], fences[inner]))
            {
                return true;
            }
        }
    }

    return false;
}

static bool Intersects(FenceConfig left, FenceConfig right)
{
    return left.Left < right.Left + right.Width &&
           left.Left + left.Width > right.Left &&
           left.Top < right.Top + right.Height &&
           left.Top + left.Height > right.Top;
}

static void TestConfigRoundTrip(string root)
{
    var configPath = Path.Combine(root, "config", "config.json");
    var service = new ConfigService(configPath);
    var folder = Path.Combine(root, "bound");
    Directory.CreateDirectory(folder);

    var config = new AppConfig
    {
        CurrentPage = 2,
        PageCount = 4,
        FencesHidden = true,
        EnableDesktopDoubleClick = false,
        EnableDesktopIconIntegration = false,
        EnableSnapToGrid = false,
        PreviousPageHotkey = "Ctrl+Shift+Left",
        NextPageHotkey = "Ctrl+Shift+Right",
        ToggleTopmostHotkey = "Win+Space",
        EnableAutoOrganizeNewDesktopItems = true,
        DefaultAutoOrganizeFenceId = "test",
        AutoOrganizeRules =
        [
            new AutoOrganizeRule
            {
                Id = "rule-test",
                Name = "Documents",
                Priority = 250,
                TargetFenceId = "test",
                Extensions = "docx;pdf",
                NamePattern = "report*",
                MinimumSizeMb = 1,
                MaximumSizeMb = 50
            }
        ],
        Language = LocalizationService.Chinese,
        DesktopIconPositions = new Dictionary<string, DesktopIconPositionConfig>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(folder, "positioned.txt")] = new() { Left = 321, Top = 147 }
        },
        DesktopIconOrder = [Path.Combine(folder, "second.txt"), Path.Combine(folder, "first.txt")],
        Fences =
        [
            new FenceConfig
            {
                Id = "test",
                Title = "Smoke",
                FolderPath = folder,
                Kind = FenceConfig.DesktopGroupKind,
                AssignedPaths = [Path.Combine(folder, "assigned.txt")],
                PageIndex = 2,
                Left = 12,
                Top = 34,
                Width = 300,
                Height = 260,
                ExpandedHeight = 340,
                BackgroundColor = "#CC112233",
                HeaderColor = "#AA445566",
                Opacity = 0.65,
                TitleAlignment = "Center",
                ShowPath = false,
                UseCleanStyle = true,
                SortMode = "Category",
                IsLocked = true,
                IsCollapsed = true,
                EnableHoverExpand = true
            },
            new FenceConfig
            {
                Id = "test",
                Title = "Other Page",
                FolderPath = folder,
                PageIndex = 0,
                Left = 48,
                Top = 56,
                Width = 100,
                Height = 120,
                Opacity = 0.1
            }
        ]
    };

    service.Save(config);
    Assert(File.Exists(configPath + ".bak"), "The first successful config save should immediately create a recovery backup.");
    var loaded = service.Load();
    Assert(loaded.Fences.Count == 2, "Config should restore multiple Fences.");
    Assert(loaded.CurrentPage == 2, "Config should restore current page.");
    Assert(loaded.PageCount == 4, "Config should restore empty pages.");
    Assert(loaded.FencesHidden, "Config should restore hidden state.");
    Assert(!loaded.EnableDesktopDoubleClick, "Config should restore the desktop double-click setting.");
    Assert(!loaded.EnableDesktopIconIntegration, "Config should restore the desktop icon integration switch.");
    Assert(!loaded.EnableSnapToGrid, "Config should restore the snap-to-grid setting.");
    Assert(loaded.PreviousPageHotkey == "Ctrl+Shift+Left" &&
           loaded.NextPageHotkey == "Ctrl+Shift+Right" &&
           loaded.ToggleTopmostHotkey == "Ctrl+Alt+Space",
        "Config should restore customizable keyboard shortcuts and migrate the Windows-reserved Win+Space binding.");
    Assert(loaded.EnableAutoOrganizeNewDesktopItems, "Config should restore automatic organization state.");
    Assert(loaded.DefaultAutoOrganizeFenceId == "test", "Config should restore the default automatic organization Fence.");
    Assert(loaded.AutoOrganizeRules.Count == 1 && loaded.AutoOrganizeRules[0].TargetFenceId == "test", "Config should restore automatic organization rules by Fence id.");
    Assert(loaded.AutoOrganizeRules[0].Extensions == "docx;pdf" && loaded.AutoOrganizeRules[0].Priority == 250, "Config should restore rule conditions and priority.");
    Assert(loaded.Language == LocalizationService.Chinese, "Config should restore language.");
    Assert(loaded.DesktopIconPositions.Values.Single() is { Left: 321, Top: 147 },
        "Config should persist loose desktop icon positions.");
    Assert(loaded.DesktopIconOrder.SequenceEqual(config.DesktopIconOrder, StringComparer.OrdinalIgnoreCase),
        "Config should persist the automatic desktop icon order.");
    Assert(loaded.DesktopIconOrder.All(path => loaded.Fences.Where(fence => fence.IsDesktopGroup)
            .All(fence => !fence.AssignedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))),
        "Loose desktop icons must remain unassigned across restart even when automatic organization of new items is enabled.");
    Assert(loaded.Fences[0].Title == "Smoke", "Config should restore title.");
    Assert(loaded.Fences[0].FolderPath == folder, "Config should restore bound folder.");
    Assert(loaded.Fences[0].IsDesktopGroup && loaded.Fences[0].AssignedPaths.Count == 1, "Config should restore desktop group membership metadata.");
    Assert(loaded.Fences[0].PageIndex == 2, "Config should restore Fence page.");
    Assert(loaded.Fences[1].PageIndex == 0, "Config should restore other Fence page.");
    Assert(!string.Equals(loaded.Fences[0].Id, loaded.Fences[1].Id, StringComparison.OrdinalIgnoreCase), "Config should repair duplicate Fence ids.");
    Assert(loaded.Fences[1].Width == 240 && loaded.Fences[1].Height == 180, "Config should clamp Fence size to usable minimums.");
    Assert(Math.Abs(loaded.Fences[1].Opacity - 0.1) < 0.01, "Config should preserve low numeric Fence opacity.");
    Assert(Math.Abs(loaded.Fences[0].Left - 12) < 0.01, "Config should restore left position.");
    Assert(loaded.Fences[0].BackgroundColor == "#CC112233", "Config should restore Fence background color.");
    Assert(loaded.Fences[0].HeaderColor == "#AA445566", "Config should restore Fence header color.");
    Assert(Math.Abs(loaded.Fences[0].Opacity - 0.65) < 0.01, "Config should restore Fence opacity.");
    Assert(loaded.Fences[0].TitleAlignment == "Center" && !loaded.Fences[0].ShowPath && loaded.Fences[0].UseCleanStyle,
        "Config should restore title alignment, path visibility, and frame style.");
    Assert(loaded.Fences[0].SortMode == "Category", "Config should restore the Fence sorting mode.");
    Assert(loaded.Fences[0].IsLocked, "Config should restore the Fence lock state.");
    Assert(Math.Abs((loaded.Fences[0].ExpandedHeight ?? 0) - 340) < 0.01, "Config should restore Fence expanded height.");
    Assert(loaded.Fences[0].IsCollapsed, "Config should restore Fence collapsed state.");
    Assert(loaded.Fences[0].EnableHoverExpand, "Config should restore Fence hover-expand setting.");

    File.WriteAllText(configPath, "{broken-json");
    var recovered = service.Load();
    Assert(recovered.Fences.Any(fence => fence.Title == "Smoke"), "A corrupt primary config should recover from the backup.");
    service.Save(loaded);

    var snapshotPath = service.SaveSnapshot(loaded);
    Assert(File.Exists(snapshotPath), "Layout snapshot should be written to disk.");
    Assert(service.TryLoadLatestSnapshot(out var snapshot, out var snapshotError), $"Latest layout snapshot should load: {snapshotError}");
    Assert(snapshot != null && snapshot.Fences.Any(fence => fence.Title == "Smoke"), "Layout snapshot should restore Fence configuration.");

    service.SaveNamedLayout(loaded, "Work layout");
    Assert(service.GetNamedLayouts().Contains("Work layout"), "Named layout should be listed after saving.");
    Assert(service.TryLoadNamedLayout("Work layout", out var namedLayout, out var namedLayoutError), $"Named layout should load: {namedLayoutError}");
    Assert(namedLayout != null && namedLayout.Fences.Any(fence => fence.Title == "Smoke"), "Named layout should restore Fence configuration.");

    var current = service.Load();
    var sameFence = current.Fences.Single(fence => fence.Title == "Smoke");
    sameFence.BackgroundColor = "#FFABCDEF";
    sameFence.HeaderColor = "#FF123456";
    sameFence.Opacity = 0.42;
    current.Language = LocalizationService.English;
    current.EnableDesktopIconIntegration = true;
    current.ClassificationScheme = "Simple";
    current.PreviousPageHotkey = "Alt+Left";
    var restored = service.ApplyLayout(current, namedLayout!, out var invalidPathCount);
    var restoredFence = restored.Fences.Single(fence => fence.Title == "Smoke");
    Assert(restored.CurrentPage == loaded.CurrentPage && restored.PageCount == loaded.PageCount,
        "Applying a layout should restore page state.");
    Assert(restored.DesktopIconOrder.SequenceEqual(loaded.DesktopIconOrder, StringComparer.OrdinalIgnoreCase),
        "Applying a layout should restore desktop icon order.");
    Assert(restored.Language == LocalizationService.English && restored.EnableDesktopIconIntegration &&
           restored.ClassificationScheme == "Simple" && restored.PreviousPageHotkey == "Alt+Left",
        "Applying a layout must preserve global settings.");
    Assert(restoredFence.BackgroundColor == "#FFABCDEF" && restoredFence.HeaderColor == "#FF123456" &&
           Math.Abs(restoredFence.Opacity - 0.42) < 0.01,
        "Applying a layout must preserve the current appearance of an existing Fence.");
    Assert(invalidPathCount > 0, "Unavailable saved paths should be reported without preventing restore.");

    service.SaveNamedLayout(loaded, "Unsafe:name");
    Assert(service.GetNamedLayouts().Contains("Unsafe_name"), "Invalid filename characters should be normalized.");
    Assert(service.RenameNamedLayout("Unsafe_name", "Renamed layout", false, out var renameError), $"Layout should rename: {renameError}");
    Assert(service.NamedLayoutExists("Renamed layout"), "Renamed layout should exist.");
    Assert(service.DeleteNamedLayout("Renamed layout", out var deleteError), $"Layout should delete: {deleteError}");
    Assert(!service.NamedLayoutExists("Renamed layout"), "Deleted layout should no longer exist.");

    for (var index = 0; index < 21; index++) service.SaveSnapshot(loaded);
    Assert(service.GetSnapshots().Count == 20, "Only the newest 20 automatic snapshots should be retained.");
    Assert(service.GetSnapshots().SequenceEqual(service.GetSnapshots().OrderByDescending(entry => entry.SavedAtUtc)),
        "Snapshots should be returned newest first.");

    Directory.CreateDirectory(service.NamedLayoutDirectory);
    var legacyLayoutPath = Path.Combine(service.NamedLayoutDirectory, "Legacy layout.json");
    File.WriteAllText(legacyLayoutPath, JsonSerializer.Serialize(loaded));
    Assert(service.TryLoadNamedLayout("Legacy layout", out var legacyLayout, out var legacyLayoutError),
        $"A 0.19.x full AppConfig layout should remain loadable: {legacyLayoutError}");
    Assert(legacyLayout?.Fences.Count == loaded.Fences.Count, "Legacy layout conversion should retain Fences.");

    File.WriteAllText(Path.Combine(service.NamedLayoutDirectory, "Broken.json"), "{not-json");
    Assert(!service.TryLoadNamedLayout("Broken", out _, out _), "A corrupt layout should fail without changing configuration.");
    Assert(!service.GetNamedLayoutEntries().Any(entry => entry.DisplayName == "Broken"),
        "Corrupt layouts should not break or pollute the management list.");

    for (var iteration = 0; iteration < 50; iteration++)
    {
        loaded.CurrentPage = iteration % loaded.PageCount;
        service.Save(loaded);
        var stressLoaded = service.Load();
        Assert(stressLoaded.CurrentPage == loaded.CurrentPage, "Repeated atomic config save/load should not lose the latest state.");
    }
    Assert(!File.Exists(configPath + ".tmp"), "Successful repeated config saves must not leave a temporary file behind.");

    var legacyCollapsedConfigPath = Path.Combine(root, "legacy-collapsed", "config.json");
    var legacyCollapsedService = new ConfigService(legacyCollapsedConfigPath);
    legacyCollapsedService.Save(new AppConfig
    {
        Fences =
        [
            new FenceConfig
            {
                Id = "legacy-collapsed",
                Title = "Legacy Collapsed",
                FolderPath = folder,
                Height = 180,
                IsCollapsed = true,
                ExpandedHeight = null
            }
        ]
    });
    var migratedCollapsed = legacyCollapsedService.Load().Fences.Single();
    Assert(Math.Abs((migratedCollapsed.ExpandedHeight ?? 0) - 420) < 0.01, "Legacy collapsed Fence with lost height should recover a usable expanded height.");
}

static void TestFenceLayout()
{
    var back = new FenceConfig { Id = "back", LayerOrder = 900_000 };
    var middle = new FenceConfig { Id = "middle", LayerOrder = 1_000_001 };
    var active = new FenceConfig { Id = "active", LayerOrder = 12 };
    var normalizedTop = MainWindow.NormalizeFenceLayers([back, middle, active], [active]);
    active.LayerOrder = normalizedTop;
    Assert(active.LayerOrder > back.LayerOrder && active.LayerOrder > middle.LayerOrder,
        "Layer normalization must keep the activated Fence above every sibling.");
    var existing = new[]
    {
        new FenceConfig
        {
            Left = 24,
            Top = 24,
            Width = 240,
            Height = 180
        }
    };
    var next = new FenceConfig
    {
        Left = 56,
        Top = 56,
        Width = 240,
        Height = 180
    };

    var position = FenceLayoutService.FindAvailablePosition(existing, next, 700, 500);
    Assert(position.Left >= 24 && position.Top >= 24, "New Fence position should stay in the workspace.");
    Assert(position.Left >= 280 || position.Top >= 220, "New Fence position should avoid overlapping existing Fences.");
}

static void TestDesktopDoubleClickTracker()
{
    var tracker = new DesktopDoubleClickTracker();
    Assert(!tracker.RegisterClick(true, 100, 100, 1_000), "First blank-desktop click should only start a candidate.");
    Assert(tracker.RegisterClick(true, 103, 104, 1_300), "Nearby blank-desktop clicks within the interval should trigger.");

    Assert(!tracker.RegisterClick(true, 100, 100, 2_000), "A new first click should start another candidate.");
    Assert(!tracker.RegisterClick(false, 100, 100, 2_100), "A desktop item click must never trigger hide/show.");
    Assert(!tracker.RegisterClick(true, 100, 100, 2_200), "An item click should reset the previous blank-desktop candidate.");

    tracker.Reset();
    Assert(!tracker.RegisterClick(true, 100, 100, 3_000), "First click before timeout test should not trigger.");
    Assert(!tracker.RegisterClick(true, 100, 100, 3_700), "Clicks outside the double-click interval should not trigger.");

    tracker.Reset();
    Assert(!tracker.RegisterClick(true, 100, 100, 4_000), "First click before distance test should not trigger.");
    Assert(!tracker.RegisterClick(true, 120, 120, 4_200), "Clicks too far apart should not trigger.");
}

static void TestLocalization()
{
    var localization = new LocalizationService
    {
        Language = "zh"
    };

    Assert(localization.Language == LocalizationService.Chinese, "Chinese aliases should normalize.");
    Assert(localization.T("Language") == "\u8bed\u8a00", "Chinese language text should load.");
    Assert(localization.T("OpenConfigFolder") == "\u6253\u5f00\u914d\u7f6e\u6587\u4ef6\u5939", "Tray maintenance text should localize.");
    Assert(localization.T("TitleCannotBeEmpty") == "\u6807\u9898\u4e0d\u80fd\u4e3a\u7a7a\u3002", "Rename dialog text should localize.");
    Assert(LocalizationService.GetMissingChineseKeys().Count == 0, "Every English localization key should have Chinese text.");

    localization.Language = "en";
    Assert(localization.T("OrganizeConfirm").Contains("{0}", StringComparison.Ordinal), "Formatted English text should keep placeholders.");
}

static void TestPageDeletion(string root)
{
    var folder = Path.Combine(root, "page-delete-bound");
    Directory.CreateDirectory(folder);
    var config = new AppConfig
    {
        CurrentPage = 1,
        PageCount = 4,
        Fences =
        [
            new FenceConfig
            {
                Id = "first",
                Title = "First",
                FolderPath = folder,
                PageIndex = 0
            },
            new FenceConfig
            {
                Id = "third",
                Title = "Third",
                FolderPath = folder,
                PageIndex = 2
            }
        ]
    };

    Assert(PageService.TryDeleteEmptyPage(config, 1, out var deleteError), $"Empty page should delete: {deleteError}");
    Assert(config.PageCount == 3, "Deleting an empty page should reduce page count.");
    Assert(config.CurrentPage == 0, "Deleting the current page should move selection to a valid page.");
    Assert(config.Fences.Single(fence => fence.Id == "third").PageIndex == 1, "Fences after the deleted page should shift left.");
    Assert(!PageService.TryDeleteEmptyPage(config, 1, out deleteError), "Non-empty page should not delete.");
    Assert(!string.IsNullOrWhiteSpace(deleteError), "Failed page deletion should explain why.");
}

static void TestFolderItemLoadingAndMove(string root)
{
    var source = Path.Combine(root, "source");
    var destination = Path.Combine(root, "destination");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(destination);

    var textPath = Path.Combine(source, "note.txt");
    var folderPath = Path.Combine(source, "folder");
    var managedRootPath = Path.Combine(source, "MiniFences Organized");
    var managedCategoryPath = Path.Combine(managedRootPath, "\u4e34\u65f6\u6587\u4ef6");
    File.WriteAllText(textPath, "hello");
    Directory.CreateDirectory(folderPath);
    Directory.CreateDirectory(managedCategoryPath);
    File.WriteAllText(Path.Combine(managedCategoryPath, "inside.txt"), "inside");

    var service = new FolderItemService();
    var items = service.LoadItems(source);
    Assert(items.Any(item => item.FullPath == textPath && item.Kind == "TXT"), "Folder items should include text file with FullPath.");
    Assert(items.Any(item => item.FullPath == folderPath && item.Kind == "Folder"), "Folder items should include folder with FullPath.");
    Assert(items.All(item => item.FullPath != managedRootPath), "Folder items should hide the MiniFences managed root folder.");
    var assignedOrder = service.LoadAssignedItems([textPath, folderPath]);
    Assert(assignedOrder.Select(item => item.FullPath).SequenceEqual([textPath, folderPath], StringComparer.OrdinalIgnoreCase),
        "Assigned desktop items must preserve the persisted manual order instead of sorting again by type or name.");
    var invalidSavedPath = "invalid\0desktop-item";
    Assert(service.LoadAssignedItems([invalidSavedPath, textPath]).Single().FullPath == textPath,
        "An invalid saved assignment path must be ignored without preventing valid items from loading.");
    Assert(FolderItemService.CollapseDesktopEntries([invalidSavedPath, textPath]).Contains(textPath),
        "Invalid saved paths must not crash desktop duplicate collapsing.");
    var wordLockPath = Path.Combine(source, "~$draft.docx");
    File.WriteAllText(wordLockPath, "lock");
    var hiddenDocumentPath = Path.Combine(source, "hidden.docx");
    File.WriteAllText(hiddenDocumentPath, "hidden");
    File.SetAttributes(hiddenDocumentPath, File.GetAttributes(hiddenDocumentPath) | FileAttributes.Hidden);
    items = service.LoadItems(source);
    Assert(items.All(item => item.FullPath != wordLockPath), "Word lock files should not appear in a Fence.");
    Assert(items.All(item => item.FullPath != hiddenDocumentPath), "Hidden files should not appear in a Fence.");
    var categoryItems = service.LoadItems(managedCategoryPath);
    Assert(categoryItems.Any(item => item.Name == "inside"), "Managed category folders should still show their own contents.");
    Assert(!service.TryLoadItems(Path.Combine(root, "missing-folder"), out var missingItems, out var loadError), "Missing folder should fail to load.");
    Assert(missingItems.Count == 0, "Missing folder should return no items.");
    Assert(loadError?.StartsWith("Path does not exist:", StringComparison.Ordinal) == true, "Missing folder load should report path not found.");

    var missing = new FolderItem { Name = "missing", FullPath = Path.Combine(source, "missing.txt") };
    Assert(!service.TryOpen(missing, out var openError), "Missing item should not open.");
    Assert(openError?.StartsWith("Path does not exist:", StringComparison.Ordinal) == true, "Missing item should report path not found.");

    var textItem = new FolderItem { Name = "note", FullPath = textPath };
    Assert(service.TryRenameItem(textItem, "renamed-note", out var renamedTextPath, out var renameError), $"File rename should succeed: {renameError}");
    if (renamedTextPath == null)
    {
        throw new InvalidOperationException("Renamed file path should be returned.");
    }
    Assert(File.Exists(renamedTextPath), "Renamed file should exist.");
    Assert(renamedTextPath.EndsWith("renamed-note.txt", StringComparison.OrdinalIgnoreCase), "File rename should preserve extension when omitted.");
    Assert(!File.Exists(textPath), "Original file path should be gone after rename.");
    textPath = renamedTextPath;

    var folderItem = new FolderItem { Name = "folder", FullPath = folderPath };
    Assert(service.TryRenameItem(folderItem, "renamed-folder", out var renamedFolderPath, out renameError), $"Folder rename should succeed: {renameError}");
    Assert(renamedFolderPath != null && Directory.Exists(renamedFolderPath), "Renamed folder should exist.");
    Assert(!Directory.Exists(folderPath), "Original folder path should be gone after rename.");
    File.WriteAllText(Path.Combine(source, "conflict.txt"), "existing");
    Assert(!service.TryRenameItem(new FolderItem { Name = "renamed-note", FullPath = textPath }, "conflict", out _, out renameError), "Rename to an existing file name should fail.");
    Assert(renameError == "Destination already exists.", "Existing destination should be reported.");

    Assert(service.TryCreateFolder(source, "New Folder", out var createdFolder, out var createError), $"Create folder should succeed: {createError}");
    Assert(createdFolder != null && Directory.Exists(createdFolder), "Created folder should exist.");
    Assert(service.TryCreateFolder(source, "New Folder", out var duplicateFolder, out createError), $"Duplicate folder create should succeed: {createError}");
    Assert(duplicateFolder != null && Directory.Exists(duplicateFolder), "Duplicate folder should exist.");
    Assert(!string.Equals(createdFolder, duplicateFolder, StringComparison.OrdinalIgnoreCase), "Duplicate folder should use a new name.");

    Assert(service.TryMoveIntoFolder([textPath], destination, out var moveError), $"Move should succeed: {moveError}");
    Assert(File.Exists(Path.Combine(destination, "renamed-note.txt")), "Moved file should exist in destination.");
    Assert(!File.Exists(textPath), "Moved file should leave source.");

    var batchOne = Path.Combine(source, "batch-one.txt");
    var batchTwo = Path.Combine(source, "batch-two.txt");
    File.WriteAllText(batchOne, "one");
    File.WriteAllText(batchTwo, "two");
    var moveResult = service.MoveIntoFolder([batchOne, batchTwo], destination);
    Assert(moveResult.Moved == 2, "Batch move should report moved items.");
    Assert(moveResult.Skipped == 0, "Batch move should not report skipped items.");
    Assert(moveResult.Errors.Count == 0, "Batch move should have no errors.");
    Assert(File.Exists(Path.Combine(destination, "batch-one.txt")), "First batch file should move.");
    Assert(File.Exists(Path.Combine(destination, "batch-two.txt")), "Second batch file should move.");
    Assert(!File.Exists(batchOne) && !File.Exists(batchTwo), "Batch move should leave source.");

    var duplicateFileSource = Path.Combine(source, "report.txt");
    var duplicateFileExisting = Path.Combine(destination, "report.txt");
    File.WriteAllText(duplicateFileSource, "new report");
    File.WriteAllText(duplicateFileExisting, "existing report");
    moveResult = service.MoveIntoFolder([duplicateFileSource], destination);
    Assert(moveResult.Moved == 1 && moveResult.Errors.Count == 0, "Same-name file should move using an available name.");
    Assert(File.ReadAllText(duplicateFileExisting) == "existing report", "Same-name file move must not overwrite the existing file.");
    Assert(File.ReadAllText(Path.Combine(destination, "report (1).txt")) == "new report", "Same-name file should receive a numbered suffix before its extension.");

    var dottedFolderSource = Path.Combine(source, "project.v1");
    var dottedFolderExisting = Path.Combine(destination, "project.v1");
    Directory.CreateDirectory(Path.Combine(dottedFolderSource, "nested"));
    Directory.CreateDirectory(dottedFolderExisting);
    File.WriteAllText(Path.Combine(dottedFolderSource, "nested", "data.txt"), "new folder data");
    File.WriteAllText(Path.Combine(dottedFolderExisting, "keep.txt"), "existing folder data");
    moveResult = service.MoveIntoFolder([dottedFolderSource], destination);
    var numberedFolder = Path.Combine(destination, "project.v1 (1)");
    Assert(moveResult.Moved == 1 && moveResult.Errors.Count == 0, "Same-name directory should move using an available name.");
    Assert(File.ReadAllText(Path.Combine(dottedFolderExisting, "keep.txt")) == "existing folder data", "Same-name directory move must not modify the existing directory.");
    Assert(File.ReadAllText(Path.Combine(numberedFolder, "nested", "data.txt")) == "new folder data", "Directory move should preserve nested contents.");
    Assert(!Directory.Exists(dottedFolderSource), "Moved directory should leave its source location.");

    var alreadyMoved = Path.Combine(destination, "batch-one.txt");
    moveResult = service.MoveIntoFolder([alreadyMoved, Path.Combine(source, "missing-batch.txt")], destination);
    Assert(moveResult.Moved == 0, "Already-in-destination batch should not move items.");
    Assert(moveResult.Skipped == 2, "Already-in-destination and missing items should be skipped.");
    Assert(moveResult.Errors.Count == 0, "Skipped batch should not be a hard error.");

    var managedDestination = Path.Combine(root, "desktop", "MiniFences Organized", "\u4e34\u65f6\u6587\u4ef6");
    Directory.CreateDirectory(managedDestination);
    Directory.Delete(managedDestination);
    var managedMoveSource = Path.Combine(source, "managed-move.txt");
    File.WriteAllText(managedMoveSource, "managed");
    moveResult = service.MoveIntoFolder([managedMoveSource], managedDestination);
    Assert(moveResult.Errors.Count == 0, "Managed missing destination should be recreated before move.");
    Assert(moveResult.Moved == 1, "Managed missing destination move should report moved item.");
    Assert(File.Exists(Path.Combine(managedDestination, "managed-move.txt")), "Managed missing destination should receive moved file.");

    var ordinaryMissingDestination = Path.Combine(root, "ordinary-missing", "\u4e34\u65f6\u6587\u4ef6");
    var ordinaryMoveSource = Path.Combine(source, "ordinary-move.txt");
    File.WriteAllText(ordinaryMoveSource, "ordinary");
    moveResult = service.MoveIntoFolder([ordinaryMoveSource], ordinaryMissingDestination);
    Assert(moveResult.Errors.Count == 1, "Ordinary missing destination should still fail.");
    Assert(File.Exists(ordinaryMoveSource), "Ordinary missing destination should leave source file in place.");

    var deletePath = Path.Combine(source, "delete-me.txt");
    File.WriteAllText(deletePath, "delete");
    Assert(service.TryDeleteItem(new FolderItem { Name = "delete-me", FullPath = deletePath }, out var deleteError), $"Delete should succeed: {deleteError}");
    Assert(!File.Exists(deletePath), "Deleted file should leave source folder.");
}

static void TestFenceControlBindingAndLayout(string root)
{
    var folder = Path.Combine(root, "fence-control");
    var filePath = Path.Combine(folder, "bound-item.txt");
    var childFolder = Path.Combine(folder, "bound-folder");
    var unassignedPath = Path.Combine(folder, "not-assigned.txt");
    Directory.CreateDirectory(childFolder);
    File.WriteAllText(filePath, "bound");
    File.WriteAllText(unassignedPath, "unassigned");

    Exception? threadFailure = null;
    var thread = new Thread(() =>
    {
        FenceControl? control = null;
        FenceControl? restoredControl = null;
        FenceControl? collapsedControl = null;
        try
        {
            var config = new FenceConfig
            {
                Id = "control-test",
                Title = "Control Test",
                FolderPath = folder,
                Left = 20,
                Top = 30,
                Width = 300,
                Height = 220
            };
            control = new FenceControl(config);
            control.SetLocalization(new LocalizationService());
            control.LoadFolderItems();

            var loadedItems = control.LoadedItemsForTesting;
            Assert(loadedItems.Any(item => item.FullPath == filePath), "Fence control should bind the real file FullPath.");
            Assert(loadedItems.Any(item => item.FullPath == childFolder), "Fence control should bind the real folder FullPath.");

            var desktopGroupConfig = new FenceConfig
            {
                Id = "desktop-group-test",
                Title = "Desktop Group Test",
                Kind = FenceConfig.DesktopGroupKind,
                FolderPath = folder,
                AssignedPaths = [filePath],
                TitleAlignment = "Right",
                ShowPath = false,
                UseCleanStyle = true
            };
            var desktopGroup = new FenceControl(desktopGroupConfig);
            desktopGroup.SetLocalization(new LocalizationService());
            desktopGroup.LoadFolderItems();
            Assert(desktopGroup.LoadedItemsForTesting.Count == 1, "Desktop group should render only assigned desktop items.");
            Assert(desktopGroup.LoadedItemsForTesting.Single().FullPath == filePath, "Desktop group should render its assigned item.");
            Assert(!desktopGroup.HasFolderWatcherForTesting,
                "Desktop groups must use the shared desktop watcher instead of allocating one watcher per Fence.");
            Assert(File.Exists(filePath) && File.Exists(unassignedPath), "Desktop grouping must not move or delete desktop files.");
            Assert(desktopGroup.TitleAlignmentForTesting == System.Windows.HorizontalAlignment.Right, "Fence should apply right title alignment.");
            Assert(!desktopGroup.IsPathVisibleForTesting, "Fence should hide its path when configured.");
            Assert(desktopGroup.BorderThicknessForTesting.Left == 0, "Clean Fence style should remove the outer border.");
            Assert(desktopGroup.IsInnerPanelTransparentForTesting, "Clean Fence style should remove the inner content panel.");
            Assert(!desktopGroup.IsFooterVisibleForTesting, "Clean Fence style should remove the footer.");
            Assert(!desktopGroup.IsResizeHandleVisibleForTesting, "Resize handle should stay hidden until pointer hover.");
            desktopGroup.SetTabStatus(3, 1);
            Assert(desktopGroup.IsTabNavigationVisibleForTesting, "Stacked Fences should expose direct previous/next tab navigation.");
            desktopGroup.SetTabStatus(3, 1, ["One", "Two", "Three"], useTabStrip: true, equalTabWidths: false);
            Assert(desktopGroup.TabColumnWidthsForTesting.Count == 4 &&
                   desktopGroup.TabColumnWidthsForTesting.Take(3).All(width => width.IsAuto) &&
                   desktopGroup.TabColumnWidthsForTesting[3].IsStar,
                "Content-width tabs should use automatic columns and leave the remaining title bar flexible.");
            desktopGroup.SetTabStatus(3, 1, ["One", "Two", "Three"], useTabStrip: true, equalTabWidths: true);
            Assert(desktopGroup.TabColumnWidthsForTesting.Count == 3 &&
                   desktopGroup.TabColumnWidthsForTesting.All(width => width.IsStar),
                "Equal-width tabs should divide the complete title bar into equal columns.");
            desktopGroup.StopForTesting();

            var secondFence = new FenceControl(new FenceConfig
            {
                Id = "selection-test",
                Title = "Selection Test",
                FolderPath = folder,
                Width = 300,
                Height = 220
            });
            secondFence.LoadFolderItems();
            control.SelectItemForTesting(0);
            secondFence.SelectItemForTesting(0);
            MainWindow.ClearOtherFenceSelections([control, secondFence], secondFence);
            Assert(control.SelectedItemCountForTesting == 0 && secondFence.SelectedItemCountForTesting == 1,
                "Selecting an item in one Fence must clear item selections in every other Fence.");
            secondFence.StopForTesting();

            var looseIcon = new DesktopLooseIconControl(new FolderItem
            {
                Name = "bound-folder",
                FullPath = childFolder,
                Kind = "Folder"
            });
            looseIcon.SetSelected(true);
            looseIcon.BeginInlineRenameForTesting();
            Assert(looseIcon.IsInlineRenamingForTesting,
                "A selected loose desktop icon should expose an inline text editor for renaming.");
            looseIcon.EndInlineRenameForTesting();
            Assert(!looseIcon.IsInlineRenamingForTesting,
                "Canceling inline rename should restore the desktop icon label.");

            var shortRenameEditor = new System.Windows.Controls.TextBox { FontSize = 12 };
            InlineRenameAppearance.Apply(shortRenameEditor, "tools");
            var longRenameEditor = new System.Windows.Controls.TextBox { FontSize = 12 };
            InlineRenameAppearance.Apply(longRenameEditor, "a very long desktop item name");
            Assert(shortRenameEditor.Width < longRenameEditor.Width &&
                   shortRenameEditor.Width >= InlineRenameAppearance.MinimumWidth &&
                   longRenameEditor.Width == InlineRenameAppearance.MaximumWidth,
                "Inline rename width should fit short names and cap long names at the desktop icon cell width.");
            Assert(shortRenameEditor.SelectionOpacity < 1 &&
                   shortRenameEditor.TextAlignment == System.Windows.TextAlignment.Left &&
                   shortRenameEditor.BorderThickness.Left == 1,
                "Inline rename must keep selected text readable and use a compact desktop-style border.");

            var watchedFilePath = Path.Combine(folder, "watcher-created.txt");
            File.WriteAllText(watchedFilePath, "watcher");
            Assert(
                PumpDispatcherUntil(
                    () => control.LoadedItemsForTesting.Any(item => item.FullPath == watchedFilePath),
                    TimeSpan.FromSeconds(5)),
                "Fence control should automatically show a file created in its bound folder.");

            File.Delete(watchedFilePath);
            Assert(
                PumpDispatcherUntil(
                    () => control.LoadedItemsForTesting.All(item => item.FullPath != watchedFilePath),
                    TimeSpan.FromSeconds(5)),
                "Fence control should automatically remove a deleted file from its item list.");

            var canvas = new System.Windows.Controls.Canvas();
            canvas.Children.Add(control);
            System.Windows.Controls.Canvas.SetLeft(control, 137);
            System.Windows.Controls.Canvas.SetTop(control, 91);
            control.Width = 410;
            control.Height = 330;
            control.SyncConfigFromLayout();

            Assert(Math.Abs(config.Left - 137) < 0.01, "Fence control should sync its left position to config.");
            Assert(Math.Abs(config.Top - 91) < 0.01, "Fence control should sync its top position to config.");
            Assert(Math.Abs(config.Width - 410) < 0.01, "Fence control should sync its width to config.");
            Assert(Math.Abs(config.Height - 330) < 0.01, "Fence control should sync its height to config.");

            var configPath = Path.Combine(root, "fence-control-config", "config.json");
            var configService = new ConfigService(configPath);
            configService.Save(new AppConfig
            {
                CurrentPage = 0,
                PageCount = 1,
                Language = LocalizationService.Chinese,
                Fences = [config]
            });

            var restoredAppConfig = configService.Load();
            var restoredConfig = restoredAppConfig.Fences.Single();
            restoredControl = new FenceControl(restoredConfig);
            restoredControl.SetLocalization(new LocalizationService { Language = restoredAppConfig.Language });
            restoredControl.LoadFolderItems();
            var restoredCanvas = new System.Windows.Controls.Canvas();
            restoredCanvas.Children.Add(restoredControl);
            System.Windows.Controls.Canvas.SetLeft(restoredControl, restoredConfig.Left);
            System.Windows.Controls.Canvas.SetTop(restoredControl, restoredConfig.Top);

            Assert(restoredControl.DisplayedTitleForTesting == "Control Test", "Restarted Fence should restore its displayed title.");
            Assert(restoredConfig.FolderPath == folder, "Restarted Fence should restore its bound folder.");
            Assert(Math.Abs(System.Windows.Controls.Canvas.GetLeft(restoredControl) - 137) < 0.01, "Restarted Fence should restore its left position.");
            Assert(Math.Abs(System.Windows.Controls.Canvas.GetTop(restoredControl) - 91) < 0.01, "Restarted Fence should restore its top position.");
            Assert(Math.Abs(restoredControl.Width - 410) < 0.01, "Restarted Fence should restore its width.");
            Assert(Math.Abs(restoredControl.Height - 330) < 0.01, "Restarted Fence should restore its height.");
            Assert(restoredControl.LoadedItemsForTesting.Any(item => item.FullPath == filePath), "Restarted Fence should reload files from its bound folder.");
            Assert(restoredControl.LoadedItemsForTesting.Any(item => item.FullPath == childFolder), "Restarted Fence should reload folders from its bound folder.");

            restoredControl.ToggleCollapsedForTesting();
            Assert(restoredControl.IsCollapsedForTesting, "Fence should enter its collapsed state.");
            Assert(!restoredControl.IsContentVisibleForTesting, "Collapsed Fence should hide its content area.");
            Assert(Math.Abs(restoredControl.Height - 34) < 0.01, "Collapsed Fence should only keep the title bar height.");
            restoredControl.SyncConfigFromLayout();
            Assert(Math.Abs(restoredConfig.Height - 330) < 0.01, "Collapsing a Fence should preserve its expanded height.");
            Assert(Math.Abs((restoredConfig.ExpandedHeight ?? 0) - 330) < 0.01, "Collapsed Fence should save its expanded height separately.");

            // Simulate a layout notification arriving with compact height after collapse.
            restoredConfig.Height = 34;

            configService.Save(restoredAppConfig);
            var collapsedAppConfig = configService.Load();
            var collapsedConfig = collapsedAppConfig.Fences.Single();
            collapsedControl = new FenceControl(collapsedConfig);
            collapsedControl.SetLocalization(new LocalizationService { Language = collapsedAppConfig.Language });
            Assert(collapsedControl.IsCollapsedForTesting, "Collapsed Fence state should persist across restart.");
            Assert(Math.Abs(collapsedControl.Height - 34) < 0.01, "Restarted collapsed Fence should remain compact.");
            collapsedControl.SetHoverExpandedForTesting(true);
            Assert(Math.Abs(collapsedControl.Height - 34) < 0.01, "Collapsed Fence should not hover-expand until the option is enabled.");
            collapsedConfig.EnableHoverExpand = true;
            Assert(collapsedControl.IsCollapsedForTesting, "Hover preview should not change the saved collapsed state.");
            var hoverLayoutNotifications = 0;
            collapsedControl.Changed += (_, _) => hoverLayoutNotifications++;
            collapsedControl.SetHoverExpandedForTesting(true);
            Assert(Math.Abs(collapsedControl.Height - 330) < 0.01, "Hovering a collapsed Fence should temporarily show its content.");
            Assert(collapsedControl.IsContentVisibleForTesting, "Hover expansion should restore the content area.");
            Assert(hoverLayoutNotifications == 1, "Hover expansion should notify the desktop host to refresh its hit-test region.");
            collapsedControl.SetHoverExpandedForTesting(false);
            Assert(Math.Abs(collapsedControl.Height - 34) < 0.01, "Leaving a hover-expanded Fence should collapse it again.");
            collapsedControl.DoubleClickTitleBarForTesting();
            Assert(!collapsedControl.IsCollapsedForTesting, "A title-bar double-click should expand a collapsed Fence.");
            Assert(Math.Abs(collapsedControl.Height - 330) < 0.01, "Expanded Fence should restore its previous height.");
            Assert(collapsedControl.IsContentVisibleForTesting, "Double-click expansion should restore the content area immediately.");
            collapsedControl.DoubleClickTitleBarForTesting();
            Assert(collapsedControl.IsCollapsedForTesting, "A second title-bar double-click should collapse the Fence again.");
            Assert(Math.Abs(collapsedControl.Height - 34) < 0.01, "Second title-bar double-click should restore compact height.");
        }
        catch (Exception ex)
        {
            threadFailure = ex;
        }
        finally
        {
            control?.StopForTesting();
            restoredControl?.StopForTesting();
            collapsedControl?.StopForTesting();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(15)))
    {
        throw new TimeoutException("Fence control UI smoke test timed out.");
    }

    if (threadFailure != null)
    {
        throw new InvalidOperationException("Fence control UI smoke test failed.", threadFailure);
    }
}

static bool PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (condition())
        {
            return true;
        }

        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    return condition();
}

static void TestShellOpenRequests(string root)
{
    var folder = Path.Combine(root, "shell-open");
    var childFolder = Path.Combine(folder, "folder-item");
    var textPath = Path.Combine(folder, "document.txt");
    var shortcutPath = Path.Combine(folder, "application.lnk");
    Directory.CreateDirectory(childFolder);
    File.WriteAllText(textPath, "document");
    File.WriteAllText(shortcutPath, "shortcut-placeholder");

    var requests = new List<ProcessStartInfo>();
    var service = new FolderItemService(startInfo => requests.Add(startInfo));
    var paths = new[] { childFolder, textPath, shortcutPath };
    foreach (var path in paths)
    {
        var item = new FolderItem
        {
            Name = Path.GetFileNameWithoutExtension(path),
            FullPath = path
        };
        Assert(service.TryOpen(item, out var error), $"Shell open request should succeed for {path}: {error}");
    }

    Assert(requests.Count == paths.Length, "Folder, file, and shortcut should each issue one Shell open request.");
    Assert(requests[0].FileName == "explorer.exe" && requests[0].Arguments.Contains(childFolder, StringComparison.Ordinal),
        "Folders must open explicitly in Explorer instead of resolving to a same-named application.");
    Assert(requests[1].FileName == textPath && requests[2].FileName == shortcutPath,
        "Files and shortcuts should use their exact FullPath for Shell execution.");
    Assert(requests.All(request => request.UseShellExecute), "All open requests must use Windows Shell execution.");

    requests.Clear();
    var explorerItem = new FolderItem { Name = "document", FullPath = textPath };
    Assert(service.TryShowInExplorer(explorerItem, out var explorerError), $"Show in Explorer request should succeed: {explorerError}");
    Assert(requests.Count == 1 && requests[0].FileName == "explorer.exe", "Show in Explorer should launch Explorer.");
    Assert(requests[0].UseShellExecute, "Show in Explorer should use Shell execution.");
    Assert(requests[0].Arguments.Contains(textPath, StringComparison.Ordinal), "Show in Explorer should select the exact item FullPath.");

    var failingService = new FolderItemService(_ => throw new InvalidOperationException("simulated shell failure"));
    Assert(!failingService.TryOpen(explorerItem, out var failureError), "Shell launch exception should be reported as an open failure.");
    Assert(failureError?.Contains("simulated shell failure", StringComparison.Ordinal) == true, "Shell launch failure should preserve the exception message.");
}

#pragma warning disable CS8321 // Kept as a legacy file-move regression during config migration.
static void TestAutoOrganizerPlanApplyUndo(string root)
{
    var desktop = Path.Combine(root, "organizer-desktop");
    var historyPath = Path.Combine(root, "history", "organize-history.json");
    Directory.CreateDirectory(desktop);

    var boundFolder = Path.Combine(desktop, "BoundFence");
    var appFolder = Path.Combine(desktop, "AppContainer");
    var customDocumentFolder = Path.Combine(root, "custom-document-fence");
    Directory.CreateDirectory(boundFolder);
    Directory.CreateDirectory(appFolder);
    Directory.CreateDirectory(customDocumentFolder);
    var docPath = Path.Combine(desktop, "report.pdf");
    var configDocPath = Path.Combine(desktop, "settings.json");
    var shortcutPath = Path.Combine(desktop, "tool.lnk");
    var installerPath = Path.Combine(desktop, "mobile-app.apk");
    var mediaPath = Path.Combine(desktop, "lecture.mp4");
    var temporaryPath = Path.Combine(desktop, "unfinished-download.crdownload");
    var hiddenPath = Path.Combine(desktop, "hidden-note.txt");
    var unknownPath = Path.Combine(desktop, "mystery.customtype");
    var looseFolder = Path.Combine(desktop, "LooseFolder");
    var dottedLooseFolder = Path.Combine(desktop, "project.v1");
    Directory.CreateDirectory(looseFolder);
    Directory.CreateDirectory(Path.Combine(dottedLooseFolder, "nested"));
    File.WriteAllText(docPath, "pdf");
    File.WriteAllText(configDocPath, "{}");
    File.WriteAllText(shortcutPath, "shortcut");
    File.WriteAllText(installerPath, "apk");
    File.WriteAllText(mediaPath, "mp4");
    File.WriteAllText(temporaryPath, "partial download");
    File.WriteAllText(hiddenPath, "hidden");
    File.SetAttributes(hiddenPath, File.GetAttributes(hiddenPath) | FileAttributes.Hidden);
    File.WriteAllText(unknownPath, "unknown");
    File.WriteAllText(Path.Combine(dottedLooseFolder, "nested", "data.txt"), "folder data");

    var config = new AppConfig
    {
        CurrentPage = 1,
        Fences =
        [
            new FenceConfig
            {
                Id = "bound",
                Title = "Bound",
                FolderPath = boundFolder
            },
            new FenceConfig
            {
                Id = "custom-same-title",
                Title = "\u6587\u6863\u8d44\u6599",
                FolderPath = customDocumentFolder
            }
        ]
    };

    var organizer = new AutoOrganizerService(
        desktopPath: desktop,
        historyPath: historyPath,
        currentExecutablePath: Path.Combine(appFolder, "MiniFences.exe"),
        currentDirectory: appFolder);
    var starterFences = organizer.CreateStarterCategoryFences(config);
    Assert(starterFences.Count == 9, "Starter category Fences should be created.");
    Assert(config.Fences.Count(fence => fence.Title == "\u6587\u6863\u8d44\u6599") == 2, "A custom same-title Fence must not replace the managed document Fence.");
    Assert(config.Fences.Any(fence => fence.Title == "\u6587\u6863\u8d44\u6599" && fence.FolderPath == customDocumentFolder), "Custom same-title Fence should remain unchanged.");
    Assert(Directory.Exists(Path.Combine(desktop, "MiniFences Organized", "\u4e34\u65f6\u6587\u4ef6")), "Starter temporary folder should exist.");
    Assert(config.Fences.Any(fence => fence.Title == "\u6587\u4ef6\u5939"), "Starter category Fences should include folders.");
    Assert(organizer.CreateStarterCategoryFences(config).Count == 0, "Starter category Fences should not duplicate existing categories.");

    var plan = organizer.BuildPlan(config);

    Assert(plan.Moves.Any(move => move.SourcePath == docPath && move.Category == "\u6587\u6863\u8d44\u6599"), "PDF should be planned as document material.");
    Assert(plan.Moves.Single(move => move.SourcePath == docPath).TargetFolder == Path.Combine(desktop, "MiniFences Organized", "\u6587\u6863\u8d44\u6599"), "Document organization must target the managed category folder, not a same-title custom Fence.");
    Assert(plan.Moves.Any(move => move.SourcePath == configDocPath && move.Category == "\u6587\u6863\u8d44\u6599"), "JSON should be planned as document material.");
    Assert(plan.Moves.Any(move => move.SourcePath == shortcutPath && move.Category == "\u5e38\u7528\u5feb\u6377\u65b9\u5f0f"), "LNK should be planned as shortcut.");
    Assert(plan.Moves.Any(move => move.SourcePath == installerPath && move.Category == "\u5b89\u88c5\u7a0b\u5e8f"), "APK should be planned as installer.");
    Assert(plan.Moves.Any(move => move.SourcePath == mediaPath && move.Category == "\u97f3\u89c6\u9891"), "MP4 should be planned as media.");
    Assert(plan.Moves.Any(move => move.SourcePath == temporaryPath && move.Category == "\u4e34\u65f6\u6587\u4ef6"), "Incomplete download should be planned as a temporary file.");
    Assert(plan.Moves.Any(move => move.SourcePath == unknownPath && move.Category == "\u5176\u4ed6"), "Unknown extension should be planned as other.");
    Assert(plan.Moves.Any(move => move.SourcePath == looseFolder && move.Category == "\u6587\u4ef6\u5939"), "Folder should be planned as folder category.");
    Assert(plan.Moves.Any(move => move.SourcePath == dottedLooseFolder && move.Category == "\u6587\u4ef6\u5939"), "Folder containing a dot should still be planned as a folder.");
    Assert(plan.Moves.All(move => move.SourcePath != boundFolder), "Configured Fence folder must not be moved.");
    Assert(plan.Moves.All(move => move.SourcePath != appFolder), "Current application folder must not be moved.");
    Assert(plan.Moves.All(move => move.SourcePath != hiddenPath), "Hidden desktop files must not be moved by automatic organization.");
    Assert(plan.CreatedFences.All(fence => fence.PageIndex == config.CurrentPage), "Created category Fences should stay on the current page.");

    var documentCategoryFolder = Path.Combine(desktop, "MiniFences Organized", "\u6587\u6863\u8d44\u6599");
    Directory.Delete(documentCategoryFolder);
    var result = organizer.ApplyPlan(config, plan);
    Assert(result.Errors.Count == 0, "Organizer apply should have no errors.");
    Assert(result.Moved == 9, $"Organizer should move seven files and two folders. Moved={result.Moved}, Skipped={result.Skipped}, Planned={plan.Moves.Count}: {string.Join(" | ", plan.Moves.Select(move => Path.GetFileName(move.SourcePath)))}.");
    Assert(File.Exists(Path.Combine(documentCategoryFolder, "report.pdf")), "Document should move to recreated document material folder.");
    Assert(File.Exists(Path.Combine(documentCategoryFolder, "settings.json")), "JSON should move to document material folder.");
    Assert(!Directory.EnumerateFileSystemEntries(customDocumentFolder).Any(), "Automatic organization must not put files into a same-title custom Fence.");
    Assert(File.Exists(Path.Combine(desktop, "MiniFences Organized", "\u5e38\u7528\u5feb\u6377\u65b9\u5f0f", "tool.lnk")), "Shortcut should move to shortcuts.");
    Assert(File.Exists(Path.Combine(desktop, "MiniFences Organized", "\u5b89\u88c5\u7a0b\u5e8f", "mobile-app.apk")), "APK should move to installers.");
    Assert(File.Exists(Path.Combine(desktop, "MiniFences Organized", "\u97f3\u89c6\u9891", "lecture.mp4")), "MP4 should move to media.");
    Assert(File.Exists(Path.Combine(desktop, "MiniFences Organized", "\u4e34\u65f6\u6587\u4ef6", "unfinished-download.crdownload")), "Incomplete download should move to temporary files.");
    Assert(File.Exists(Path.Combine(desktop, "MiniFences Organized", "\u5176\u4ed6", "mystery.customtype")), "Unknown extension should move to other.");
    Assert(Directory.Exists(Path.Combine(desktop, "MiniFences Organized", "\u6587\u4ef6\u5939", "LooseFolder")), "Folder should move to folder category.");
    var organizedDottedFolder = Path.Combine(desktop, "MiniFences Organized", "\u6587\u4ef6\u5939", "project.v1");
    Assert(File.Exists(Path.Combine(organizedDottedFolder, "nested", "data.txt")), "Folder containing a dot should preserve nested contents during organization.");
    Assert(File.Exists(hiddenPath), "Hidden desktop file should remain in place after organization.");
    Assert(organizer.HasUndoHistory(), "Organizer should save undo history.");

    Directory.CreateDirectory(dottedLooseFolder);
    File.WriteAllText(Path.Combine(dottedLooseFolder, "keep.txt"), "occupied original path");

    var undo = organizer.UndoLastOrganization(config);
    Assert(undo.Errors.Count == 0, "Organizer undo should have no errors.");
    Assert(undo.Moved == 9, "Organizer undo should move files and folders back.");
    Assert(File.Exists(docPath), "Document should be restored.");
    Assert(File.Exists(configDocPath), "JSON document should be restored.");
    Assert(File.Exists(shortcutPath), "Shortcut should be restored.");
    Assert(File.Exists(installerPath), "Installer should be restored.");
    Assert(File.Exists(mediaPath), "Media file should be restored.");
    Assert(File.Exists(temporaryPath), "Temporary file should be restored.");
    Assert(File.Exists(unknownPath), "Unknown extension file should be restored.");
    Assert(Directory.Exists(looseFolder), "Folder should be restored.");
    Assert(File.ReadAllText(Path.Combine(dottedLooseFolder, "keep.txt")) == "occupied original path", "Undo must not overwrite a same-name folder created after organization.");
    Assert(File.Exists(Path.Combine(desktop, "project.v1 (1)", "nested", "data.txt")), "Undo should restore a dotted folder with a directory-style numbered suffix.");
    Assert(File.Exists(hiddenPath), "Hidden desktop file should remain untouched after undo.");

    var automaticSource = Path.Combine(desktop, "new-note.txt");
    File.WriteAllText(automaticSource, "automatic rule test");
    Assert(organizer.TryBuildAutomaticMove(config, automaticSource, out var automaticMove) && automaticMove != null, "Automatic rules should route a new desktop document to its existing category Fence.");
    Assert(automaticMove!.TargetFolder == Path.Combine(desktop, "MiniFences Organized", "\u6587\u6863\u8d44\u6599"), "Automatic document rule should target the managed document Fence.");
    var automaticResult = organizer.ApplyPlan(config, new OrganizationPlan([automaticMove], []));
    Assert(automaticResult.Moved == 1, "Automatic rule should move the queued desktop item.");
    Assert(File.Exists(Path.Combine(automaticMove.TargetFolder, "new-note.txt")), "Automatically organized item should appear in its category folder.");
}
#pragma warning restore CS8321

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
