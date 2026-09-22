using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.IO.Compression;
using System.Windows.Threading;
using MiniFences;
using MiniFences.Models;
using MiniFences.Services;

var root = Path.Combine(Path.GetTempPath(), "MiniFencesSmokeTests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("MINIFENCES_LOG_PATH", Path.Combine(root, "logs", "app.log"));

try
{
    if (args.Contains("--shell-desktop-check"))
    {
        Exception? failure = null;
        var probe = new Thread(() =>
        {
            try
            {
                Assert(ShellDesktopView.TryGetVisible(out var visible), "Desktop Shell visibility must be readable.");
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    Assert(ShellDesktopView.TrySetVisible(visible), "Repeated Shell visibility updates must succeed.");
                    Assert(ShellDesktopView.TryGetVisible(out var current) && current == visible,
                        "Repeated Shell updates must preserve desktop visibility.");
                }
                Console.WriteLine($"Desktop Shell COM check passed. IconsVisible={visible}");
            }
            catch (Exception ex) { failure = ex; }
        });
        probe.SetApartmentState(ApartmentState.STA);
        probe.Start();
        probe.Join();
        if (failure != null) throw failure;
        return 0;
    }
    Assert(AppLogger.LogPath.StartsWith(root, StringComparison.OrdinalIgnoreCase),
        "Smoke-test diagnostics must be isolated from the user's production log.");
    TestConfigRoundTrip(root);
    TestDefaultConfig(root);
    TestManualDropOwnership();
    TestLocalization();
    TestStartupExecutableResolution();
    TestGitHubUpdateParsing();
    TestUpdateInstallerSafety();
    TestDesktopDoubleClickTracker();
    TestDesktopDragData();
    TestNativeShellDragSourceHelper(root);
    TestSeparatedInternalDragFeedback();
    TestDragHintPlacement();
    TestSettingsNavigation();
    TestHotkeyCaptureFormatting();
    TestColorPickerDialog();
    TestTabMergeRules();
    TestTabDropAcceptance();
    TestTabGroupPresentationState();
    TestHeaderDragEdgeTracking();
    TestCustomGridAndPageHotkeys();
    TestFenceItemMultiSelectionPresentation();
    TestTaskbarWorkAreaClamp();
    TestHostedDesktopDpiBounds();
    TestDesktopHostCompatibilityMode();
    TestShowDesktopCompatibilityRecovery();
    Assert(MainWindow.GetFenceLayoutHeight(new FenceConfig { Height = 600, IsCollapsed = true }) == 34,
        "Startup layout clamping must use the visible title height for a collapsed Fence.");
    TestFenceLayout();
    TestPageDeletion(root);
    TestFolderItemLoadingAndMove(root);
    TestShellIconLoading(root);
    TestSystemDesktopShellItems();
    TestShellOpenRequests(root);
    TestShellContextMenuPathSelection(root);
    TestShellContextMenuHostCommands();
    TestFenceControlBindingAndLayout(root);
    TestMetadataOrganizer(root);
    TestMultiRootMetadataOrganizer(root);
    TestActionHistory(root);
    TestDisplayLayouts(root);
    TestLargeFolderVirtualization(root);
    TestAdvancedAutoOrganizeRules(root);
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

static void TestStartupExecutableResolution()
{
    const string entryAssembly = @"C:\MiniFences\MiniFences.dll";
    const string appHost = @"C:\MiniFences\MiniFences.exe";
    var resolved = StartupService.ResolveStartupCommand(
        @"C:\dotnet\dotnet.exe",
        entryAssembly,
        @"C:\dotnet\dotnet.exe",
        path => string.Equals(path, entryAssembly, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, appHost, StringComparison.OrdinalIgnoreCase));
    Assert(resolved.Contains("powershell.exe", StringComparison.OrdinalIgnoreCase) &&
           resolved.Contains("-WindowStyle Hidden", StringComparison.OrdinalIgnoreCase) &&
           resolved.Contains(@"C:\dotnet\dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
           resolved.Contains(entryAssembly, StringComparison.OrdinalIgnoreCase),
        "Framework-dependent startup must launch the dotnet host and MiniFences DLL without a visible console.");

    var quotedCommand = StartupService.BuildHiddenFrameworkCommand(
        @"C:\it's\dotnet.exe", entryAssembly, @"C:\Windows\powershell.exe");
    Assert(quotedCommand.Contains(@"C:\it''s\dotnet.exe", StringComparison.Ordinal),
        "Hidden startup command must safely escape apostrophes in paths.");

    resolved = StartupService.ResolveStartupCommand(
        @"C:\Apps\MiniFences.exe",
        entryAssembly,
        @"C:\Apps\MiniFences.exe",
        _ => false);
    Assert(string.Equals(resolved, "\"C:\\Apps\\MiniFences.exe\" --background", StringComparison.OrdinalIgnoreCase),
        "Startup must retain the direct executable path for published builds.");
}

static void TestGitHubUpdateParsing()
{
    const string json = """
    {
      "tag_name": "v0.25.0",
      "name": "MiniFences 0.25.0",
      "body": "Bug fixes",
      "html_url": "https://github.com/dskiiii/minifence/releases/tag/v0.25.0",
      "draft": false,
      "prerelease": false,
      "assets": [
        { "name": "MiniFences-win-x64-0.25.0.zip", "browser_download_url": "https://github.com/dskiiii/minifence/releases/download/v0.25.0/MiniFences-win-x64-0.25.0.zip", "digest": "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" },
        { "name": "MiniFences-win-x64-0.25.0.zip.sha256", "browser_download_url": "https://github.com/dskiiii/minifence/releases/download/v0.25.0/MiniFences-win-x64-0.25.0.zip.sha256" },
        { "name": "MiniFences-win-x64-0.25.0-slim.zip", "browser_download_url": "https://github.com/dskiiii/minifence/releases/download/v0.25.0/MiniFences-win-x64-0.25.0-slim.zip" }
      ]
    }
    """;
    var update = GitHubUpdateService.ParseReleaseForTesting(json, new Version(0, 24, 37));
    Assert(update is not null && update.Version.Equals(new Version(0, 25, 0)) &&
           update.AssetName == "MiniFences-win-x64-0.25.0.zip" &&
           update.Sha256?.Length == 64 && update.ChecksumUri is not null,
        "GitHub update parsing must choose the full self-contained asset and its checksum.");
    Assert(GitHubUpdateService.ParseReleaseForTesting(json, new Version(0, 25, 0)) is null,
        "GitHub update checks must not offer an equal or older release.");
    var uppercaseHash = new string('A', 64);
    Assert(GitHubUpdateService.NormalizeSha256("sha256:" + uppercaseHash) == new string('a', 64) &&
           GitHubUpdateService.NormalizeSha256("sha256:ABCDEF") == "",
        "GitHub release digests must be a complete 64-character SHA-256 value.");

    const string headedNotes = """
    ## 中文

    修复中文更新说明。

    ## English

    Fixed the English update notes.
    """;
    Assert(GitHubUpdateService.LocalizeReleaseNotes(headedNotes, chinese: true) == "修复中文更新说明。" &&
           GitHubUpdateService.LocalizeReleaseNotes(headedNotes, chinese: false) == "Fixed the English update notes.",
        "Update notes with language headings must follow the configured MiniFences language.");

    const string legacyNotes = """
    MiniFences 中文正式版说明。

    ---

    MiniFences stable release notes for English users.
    """;
    Assert(GitHubUpdateService.LocalizeReleaseNotes(legacyNotes, chinese: true) == "MiniFences 中文正式版说明。" &&
           GitHubUpdateService.LocalizeReleaseNotes(legacyNotes, chinese: false) == "MiniFences stable release notes for English users.",
        "Legacy bilingual update notes must be split at their Markdown divider.");
    Assert(GitHubUpdateService.LocalizeReleaseNotes("Security and reliability fixes.", chinese: true) ==
           "Security and reliability fixes.",
        "Unsectioned release notes must remain available instead of being discarded.");
}

static void TestUpdateInstallerSafety()
{
    Assert(UpdateInstaller.IsSafeArchiveEntry("MiniFences.exe") &&
           UpdateInstaller.IsSafeArchiveEntry("runtimes/win-x64/native/a.dll") &&
           !UpdateInstaller.IsSafeArchiveEntry("..\\outside.txt") &&
           !UpdateInstaller.IsSafeArchiveEntry("C:\\outside.txt"),
        "Update archives must reject rooted and parent-traversal entries before extraction.");
    var containmentRoot = Path.Combine(Path.GetTempPath(), "MiniFences-containment-test");
    Assert(UpdateInstaller.ResolveContainedPathForTesting(containmentRoot, "runtimes/win-x64/a.dll")
               .StartsWith(Path.GetFullPath(containmentRoot), StringComparison.OrdinalIgnoreCase),
        "Resolved update paths must remain inside the target directory.");
    var adaptiveWidths = FenceControl.GetAdaptiveTabWidths(["常用", "AI", "下载和卸载"], 600)!;
    Assert(Math.Abs(adaptiveWidths.Sum() - 600) < 0.01 &&
           adaptiveWidths[2] > adaptiveWidths[0] && adaptiveWidths[0] > adaptiveWidths[1] &&
           adaptiveWidths[2] - adaptiveWidths[1] < 90,
        "Adaptive tabs must fill the strip while sharing spare width evenly around their text widths.");
}

static void TestShellIconLoading(string root)
{
    var area = Path.Combine(root, "shell-icon");
    Directory.CreateDirectory(area);
    var archive = Path.Combine(area, "archive.zip");
    File.WriteAllBytes(archive, []);
    var service = new FolderItemService();
    var item = service.LoadAssignedItems([archive]).Single();
    System.Windows.Media.ImageSource? loadedIcon = null;
    service.LoadIconsAsync([item], (_, icon) => loadedIcon = icon, CancellationToken.None)
        .GetAwaiter().GetResult();
    Assert(loadedIcon is System.Windows.Media.Imaging.BitmapSource,
        "Shell file-association icons must load as a real bitmap on the STA icon worker.");
}

static void TestDragHintPlacement()
{
    Assert(!MainWindow.ShouldUseFullDesktopWindowRegion(false, false, true) &&
           MainWindow.ShouldUseFullDesktopWindowRegion(true, false, false) &&
           !MainWindow.ShouldUseFullDesktopWindowRegion(false, false, false),
        "The independent drag image must not expand the Explorer-hosted desktop input region.");
    Assert(MainWindow.ShouldShowCustomDragFeedback(false) &&
           !MainWindow.ShouldShowCustomDragFeedback(true) &&
           MainWindow.ShouldIgnoreIntermediateDragHide(true) &&
           !MainWindow.ShouldIgnoreIntermediateDragHide(false) &&
           MainWindow.ShouldClearDragFeedback(true, true) &&
           MainWindow.ShouldClearDragFeedback(false, false) &&
           !MainWindow.ShouldClearDragFeedback(false, true),
        "MiniFences must defer to a Shell drag image, use custom feedback only as a fallback, and clear it on Escape.");
    Assert(!DragFeedbackWindow.RealtimeTrackingCanShowWindowForTesting,
        "The native cursor-tracking thread must never re-show a feedback HWND after Escape hides it.");
    Assert(MainWindow.ShouldUseCustomDragImage(true, false) &&
           MainWindow.ShouldUseCustomDragImage(true, true) &&
           MainWindow.ShouldUseCustomDragImage(false, false) &&
           !MainWindow.ShouldUseCustomDragImage(false, true),
        "Internal drags use MiniFences feedback; external Explorer drags retain their single native image.");
    Assert(MainWindow.ShouldUpdateDragFeedbackFromMouseMessage(true, 0x0200) &&
           !MainWindow.ShouldUpdateDragFeedbackFromMouseMessage(false, 0x0200) &&
           !MainWindow.ShouldUpdateDragFeedbackFromMouseMessage(true, 0x0201),
        "Every real mouse-move hook message must synchronously reposition the active drag image.");
    Assert(!MainWindow.ShouldEndShellDragForScreenPoint(true) &&
           MainWindow.ShouldEndShellDragForScreenPoint(false),
        "Child-control DragLeave events must not clear feedback; a real MiniFences surface exit must clear it immediately.");

    foreach (var dpi in new uint[] { 96, 120, 144, 168, 192 })
    {
        var scale = dpi / 96d;
        var cursor = new System.Windows.Point(1462, 461);
        var placement = DragFeedbackWindow.CalculatePhysicalPlacement(
            cursor,
            new System.Windows.Size(200 * scale, 32 * scale),
            dpi,
            iconVisible: true);
        Assert(Math.Abs(placement.X + DragFeedbackWindow.SourceVisualWidthDips * scale / 2 - cursor.X) < 0.01 &&
               Math.Abs(placement.Y + DragFeedbackWindow.IconCenterYDips * scale - cursor.Y) < 0.01,
            $"Drag image hotspot must remain on the physical pointer at {dpi * 100 / 96}% display scaling.");
    }

    var secondaryMonitorPlacement = DragFeedbackWindow.CalculatePhysicalPlacement(
        new System.Windows.Point(-900, 700),
        new System.Windows.Size(300, 48),
        144,
        iconVisible: true);
    Assert(Math.Abs(secondaryMonitorPlacement.X + DragFeedbackWindow.SourceVisualWidthDips * 1.5 / 2 + 900) < 0.01 &&
           Math.Abs(secondaryMonitorPlacement.Y + DragFeedbackWindow.IconCenterYDips * 1.5 - 700) < 0.01 &&
           MainWindow.DragHintRefreshIntervalMilliseconds <= 16,
        "Drag image positioning must retain negative virtual-screen coordinates on a secondary monitor.");

    Exception? windowFailure = null;
    var windowThread = new Thread(() =>
    {
        try
        {
            var feedbackWindow = new DragFeedbackWindow();
            feedbackWindow.SetTargetText("Move to Files");
            Assert(!feedbackWindow.ShowActivated && !feedbackWindow.ShowInTaskbar &&
                   feedbackWindow.IsHitTestVisible == false &&
                   feedbackWindow.TargetText?.Replace("\n", " ").StartsWith("Move to", StringComparison.Ordinal) == true &&
                   DragFeedbackWindow.LabelWidthDips == 86,
                "Drag feedback must use one non-activating, click-through top-level window.");
            feedbackWindow.Close();
        }
        catch (Exception ex)
        {
            windowFailure = ex;
        }
    });
    windowThread.SetApartmentState(ApartmentState.STA);
    windowThread.Start();
    windowThread.Join();
    if (windowFailure != null) throw windowFailure;
}

static void TestNativeShellDragSourceHelper(string root)
{
    Assert(ShellDragSourceImage.DragLabelWidth == 86 && ShellDragSourceImage.DragLabelMaxLines == 2,
        "The native drag label must match the desktop cell width and use at most two lines.");
    Exception? failure = null;
    var initialized = false;
    var thread = new Thread(() =>
    {
        try
        {
            var file = Path.Combine(root, "native-shell-drag.txt");
            File.WriteAllText(file, "native Shell drag image");
            using var data = new ShellCompatibleDataObject();
            DesktopDragData.Set(data, [file], looseIcon: false, file);
            var pixels = new byte[] { 255, 160, 40, 255 };
            var icon = System.Windows.Media.Imaging.BitmapSource.Create(
                1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 4);
            icon.Freeze();
            initialized = ShellDragSourceImage.TryInitialize(data, icon, "native-shell-drag.txt") &&
                          DesktopDragData.HasNativeShellImage(data);
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure != null) throw new InvalidOperationException("Native Shell drag helper test failed.", failure);
    Assert(initialized, "IDragSourceHelper must store its private formats in MiniFences' COM data object.");
}

/* Obsolete combined-window regression retained for history.
static void TestCombinedInternalDragFeedback()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var feedback = new DragFeedbackWindow();
            feedback.SetIcon(MiniFences.Services.FolderItemService.GetTypeIcon(".txt"));
            feedback.SetTargetText("MiniFences-win-x64-0.23.100-showdesktop-zorder-fix", expanded: true);
            feedback.EnsureShown();
            feedback.UpdateLayout();
            var sourceOnlyWidth = feedback.FeedbackContentWidthForTesting;
            feedback.SetDropTargetText("移动到 新横向");
            feedback.UpdateLayout();
            feedback.UpdatePosition();
            Assert(feedback.HasSingleCombinedFeedbackVisualForTesting,
                "An internal drag must show its source and move target in one feedback window.");
            Assert(feedback.UsesGeometricallyCenteredDropArrowForTesting,
                "The move-target arrow must use a geometric midline instead of a font baseline.");
            Assert(feedback.IsSourceNameVisibleWithDropTargetForTesting,
                "A visible move target must supplement the dragged filename, never replace it.");
            Assert(feedback.IsSourceIconVisibleForTesting &&
                   Math.Abs(feedback.FeedbackContentWidthForTesting - sourceOnlyWidth) < 0.01,
                "Entering a folder target must retain the source icon without restructuring the feedback window.");
            Assert(feedback.NativeBoundsCoverContentForTesting,
                "The native drag-feedback window must expand to contain the source icon and folder-target prompt.");
            feedback.SetDropTargetText(null);
            Assert(feedback.DropTargetText == null && feedback.TargetText != null &&
                   !feedback.IsSourceNameVisibleWithDropTargetForTesting,
                "Leaving a folder hot zone must synchronously remove only the move target and retain source feedback.");
            feedback.Close();
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure != null) throw new InvalidOperationException("Combined internal drag feedback test failed.", failure);
}

*/
static void TestSeparatedInternalDragFeedback()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var sourceFeedback = new DragFeedbackWindow();
            sourceFeedback.SetIcon(FolderItemService.GetTypeIcon(".txt"));
            sourceFeedback.SetTargetText("Dragged item", expanded: false);
            sourceFeedback.EnsureShown();
            sourceFeedback.UpdateLayout();
            var sourceHandle = sourceFeedback.NativeHandleForTesting;
            var sourceWidth = sourceFeedback.FeedbackContentWidthForTesting;

            var targetFeedback = new DragTargetHintWindow();
            targetFeedback.ShowTarget("Move to target folder");
            Assert(sourceHandle != IntPtr.Zero && targetFeedback.NativeHandleForTesting != IntPtr.Zero &&
                   sourceHandle != targetFeedback.NativeHandleForTesting,
                "The folder target prompt must use a separate native window from the dragged source icon.");
            Assert(sourceFeedback.IsSourceIconVisibleForTesting &&
                   Math.Abs(sourceFeedback.FeedbackContentWidthForTesting - sourceWidth) < 0.01,
                "Showing a folder target must not resize or hide the dragged source icon window.");

            targetFeedback.HideTarget();
            Assert(sourceFeedback.IsSourceIconVisibleForTesting &&
                   sourceFeedback.NativeHandleForTesting == sourceHandle,
                "Leaving a folder target must preserve the original source icon HWND.");
            targetFeedback.Close();
            sourceFeedback.Close();
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure != null) throw new InvalidOperationException("Separated internal drag feedback test failed.", failure);
}

static void TestSystemDesktopShellItems()
{
    const string thisPc = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    const string recycleBin = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";
    Assert(FolderItemService.IsShellNamespacePath(thisPc) &&
           !FolderItemService.IsShellNamespacePath(@"C:\Users\Public\Desktop"),
        "System desktop icons must be distinguished from file-system entries.");
    Assert(FolderItemService.IsRecycleBinNamespacePath(recycleBin) &&
           !FolderItemService.IsRecycleBinNamespacePath(thisPc),
        "The Recycle Bin must remain distinguishable as a Shell drop target.");
    Assert(FolderItemService.ShouldShowDesktopShellItem(0, false) &&
           !FolderItemService.ShouldShowDesktopShellItem(1, true) &&
           FolderItemService.ShouldShowDesktopShellItem(null, true),
        "Windows HideDesktopIcons values must control which system icons MiniFences recreates.");

    var requests = new List<ProcessStartInfo>();
    var service = new FolderItemService(startInfo => requests.Add(startInfo));
    Assert(service.LoadAssignedItems([thisPc]).Single().FullPath == thisPc,
        "System desktop icons assigned to a Fence must survive content reloads.");
    Assert(service.TryOpen(new FolderItem { Name = "This PC", FullPath = thisPc }, out var error),
        $"System desktop icon should open through Explorer: {error}");
    Assert(requests.Single().FileName == "explorer.exe" &&
           requests.Single().Arguments == thisPc &&
           requests.Single().UseShellExecute,
        "System desktop icons must launch their exact Shell namespace.");
    Assert(!service.TryRenameItem(
            new FolderItem { Name = "This PC", FullPath = thisPc },
            "Renamed",
            out _,
            out _),
        "System desktop icons must not enter the file-system rename path.");
}

static void TestActionHistory(string root)
{
    var area = Path.Combine(root, "action-history");
    Directory.CreateDirectory(area);
    var historyPath = Path.Combine(area, "history", "actions.json");
    var configService = new ConfigService(Path.Combine(area, "config.json"));
    var service = new ActionHistoryService(historyPath);
    var file = Path.Combine(area, "desktop-item.txt");
    File.WriteAllText(file, "safe");
    var fence = new FenceConfig
    {
        Id = "history-fence",
        Title = "History",
        Kind = FenceConfig.DesktopGroupKind,
        AssignedPaths = [file],
        Left = 123,
        Top = 234,
        HeaderColor = "#FF123456",
        TabGroupId = "tabs"
    };
    var config = new AppConfig { PageCount = 2, Fences = [fence] };
    service.RecordFenceDeleted(fence, 0);
    config.Fences.Clear();
    var undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
    Assert(undo.Succeeded && config.Fences.Count == 1 &&
           config.Fences[0].AssignedPaths.SequenceEqual([file]) &&
           config.Fences[0].Left == 123 && config.Fences[0].TabGroupId == "tabs",
        "Fence delete undo must restore appearance, layout, tab membership and item assignments without moving files.");
    Assert(File.Exists(file), "Fence delete and undo must not move or delete the real file.");

    var secondFence = new FenceConfig
    {
        Id = "history-second",
        Title = "Second",
        Kind = FenceConfig.DesktopGroupKind,
        AssignedPaths = []
    };
    config.Fences.Add(secondFence);
    var membershipsBefore = service.CaptureMemberships(config);
    config.Fences[0].AssignedPaths.Clear();
    secondFence.AssignedPaths.Add(file);
    service.RecordMembershipChange("assign", membershipsBefore, config);
    undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
    Assert(undo.Succeeded && config.Fences[0].AssignedPaths.SequenceEqual([file]) &&
           secondFence.AssignedPaths.Count == 0,
        "Desktop assignment undo must restore original Fence ownership and order without duplicates.");

    var changedFence = config.Fences[0];
    Assert(service.ExecuteFenceChange("change folder", config, changedFence, () =>
    {
        changedFence.Kind = FenceConfig.FolderPortalKind;
        changedFence.FolderPath = area;
        changedFence.AssignedPaths.Clear();
        return true;
    }), "Fence type changes must enter the unified action history.");
    undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
    Assert(undo.Succeeded && config.Fences[0].IsDesktopGroup &&
           config.Fences[0].AssignedPaths.SequenceEqual([file]) &&
           config.Fences[0].HeaderColor == "#FF123456",
        "Undoing a Folder Portal conversion must restore the complete Fence and its desktop assignments.");

    var source = Path.Combine(area, "source.txt");
    var destination = Path.Combine(area, "destination.txt");
    File.WriteAllText(destination, "moved");
    service.RecordFileMove("move", [(source, destination)]);
    undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
    Assert(undo.Succeeded && File.Exists(source) && !File.Exists(destination),
        "File move undo must restore the original path.");

    if (Directory.Exists(@"D:\") &&
        !string.Equals(Path.GetPathRoot(area), @"D:\", StringComparison.OrdinalIgnoreCase))
    {
        var crossVolumeRoot = Path.Combine(@"D:\", "MiniFencesSmokeTests-" + Guid.NewGuid().ToString("N"));
        var crossVolumeOriginal = Path.Combine(crossVolumeRoot, "restored-folder");
        var crossVolumeCurrent = Path.Combine(area, "cross-volume-current");
        try
        {
            Directory.CreateDirectory(crossVolumeRoot);
            var realTransferSource = Path.Combine(crossVolumeRoot, "actual-transfer-source");
            var realTransferDestinationFolder = Path.Combine(area, "actual-transfer-destination");
            Directory.CreateDirectory(realTransferSource);
            Directory.CreateDirectory(realTransferDestinationFolder);
            File.WriteAllText(Path.Combine(realTransferSource, "verified.txt"), "verified cross-volume move");
            var realTransfer = service.ExecuteFileTransfer("actual cross-volume move",
                new FolderItemService(), [realTransferSource], realTransferDestinationFolder,
                FolderTransferOperation.Move);
            var publishedPath = Path.Combine(realTransferDestinationFolder, "actual-transfer-source");
            Assert(realTransfer.Moved == 1 && !Directory.Exists(realTransferSource) &&
                   File.ReadAllText(Path.Combine(publishedPath, "verified.txt")) == "verified cross-volume move" &&
                   !Directory.EnumerateFileSystemEntries(crossVolumeRoot)
                       .Any(path => Path.GetFileName(path).Contains(".minifences-move-", StringComparison.OrdinalIgnoreCase)),
                "A cross-volume move must publish a verified destination and remove only its quarantined source.");
            undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
            Assert(undo.Succeeded && Directory.Exists(realTransferSource) && !Directory.Exists(publishedPath),
                "A journaled cross-volume move must remain undoable after verified publication.");

            Directory.CreateDirectory(crossVolumeCurrent);
            File.WriteAllText(Path.Combine(crossVolumeCurrent, "payload.txt"), "cross-volume undo");
            service.RecordFileMove("cross-volume undo", [(crossVolumeOriginal, crossVolumeCurrent)]);
            undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
            Assert(undo.Succeeded && Directory.Exists(crossVolumeOriginal) &&
                   File.ReadAllText(Path.Combine(crossVolumeOriginal, "payload.txt")) == "cross-volume undo" &&
                   !Directory.Exists(crossVolumeCurrent),
                "Undo must restore a completed folder move across drive letters using verified staging.");
        }
        finally
        {
            if (Directory.Exists(crossVolumeRoot)) Directory.Delete(crossVolumeRoot, recursive: true);
        }
    }

    var conflictSource = Path.Combine(area, "conflict.txt");
    var conflictDestination = Path.Combine(area, "conflict-moved.txt");
    File.WriteAllText(conflictSource, "existing");
    File.WriteAllText(conflictDestination, "moved");
    service.RecordFileMove("conflict", [(conflictSource, conflictDestination)]);
    undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
    Assert(!undo.Succeeded && File.ReadAllText(conflictSource) == "existing" &&
           File.ReadAllText(conflictDestination) == "moved",
        "Undo conflicts must never overwrite either file.");

    service.Clear();
    var transferService = new FolderItemService();
    var copySource = Path.Combine(area, "history-copy-source.txt");
    var copyDestinationFolder = Path.Combine(area, "history-copy-destination");
    Directory.CreateDirectory(copyDestinationFolder);
    File.WriteAllText(copySource, "copy remains at source");
    var copyResult = service.ExecuteFileTransfer("copy", transferService, [copySource],
        copyDestinationFolder, FolderTransferOperation.Copy);
    var copiedPath = copyResult.MovedPaths.Single();
    Assert(File.Exists(copySource) && File.Exists(copiedPath) &&
           service.GetNextUndo()?.ActionType == "FileCopy",
        "Copy operations must be journaled before execution and offered as undoable created outputs.");
    undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
    Assert(undo.Succeeded && File.Exists(copySource) && !File.Exists(copiedPath),
        "Undoing a copy must remove only the unchanged destination and preserve the source.");

    var linkTarget = Path.Combine(area, "history-link-target");
    Directory.CreateDirectory(linkTarget);
    var linkResult = service.ExecuteFileTransfer("link", transferService, [linkTarget],
        copyDestinationFolder, FolderTransferOperation.Link);
    var shortcutPath = linkResult.MovedPaths.Single();
    Assert(File.Exists(shortcutPath) && service.GetNextUndo()?.ActionType == "LinkCreate",
        "Link creation must use a valid staged .lnk and enter action history.");
    undo = service.Undo(service.GetNextUndo()!.Id, config, configService);
    Assert(undo.Succeeded && Directory.Exists(linkTarget) && !File.Exists(shortcutPath),
        "Undoing link creation must remove only the shortcut.");

    var recoverySource = Path.Combine(area, "recovery-source");
    var recoveryQuarantine = Path.Combine(area,
        ".recovery-source.minifences-move-1234567890abcdef1234567890abcdef.source");
    var recoveryDestination = Path.Combine(copyDestinationFolder, "recovery-source");
    var recoveryPartial = recoveryDestination + ".minifences-1234567890abcdef1234567890abcdef.partial";
    Directory.CreateDirectory(recoveryQuarantine);
    File.WriteAllText(Path.Combine(recoveryQuarantine, "payload.txt"), "recover me");
    Directory.CreateDirectory(recoveryPartial);
    File.WriteAllText(Path.Combine(recoveryPartial, "incomplete.txt"), "partial");
    var interruptedDocument = new ActionJournalDocument
    {
        Transactions =
        [
            new ActionTransaction
            {
                ActionType = "FileMove",
                DisplayName = "interrupted move",
                Status = "InProgress",
                IsUndoable = false,
                Entries =
                [
                    new ActionEntry
                    {
                        SourcePath = recoverySource,
                        DestinationPath = recoveryDestination,
                        SourceStagingPath = recoveryQuarantine,
                        DestinationStagingPath = recoveryPartial,
                        Operation = "Move",
                        Status = "InProgress"
                    }
                ]
            }
        ]
    };
    File.WriteAllText(historyPath, JsonSerializer.Serialize(interruptedDocument));
    var crashRecovered = new ActionHistoryService(historyPath);
    Assert(Directory.Exists(recoverySource) &&
           File.ReadAllText(Path.Combine(recoverySource, "payload.txt")) == "recover me" &&
           !Directory.Exists(recoveryQuarantine) && !Directory.Exists(recoveryPartial) &&
           crashRecovered.GetTransactions().Single().Status == "Failed" &&
           crashRecovered.LastRecoveryReport is { RestoredSources: 1, CleanedPartials: 1 },
        "Startup recovery must remove only recognized partial output and atomically restore quarantined source data.");

    crashRecovered.Clear();
    for (var i = 0; i < 60; i++)
        crashRecovered.Record(new ActionTransaction { DisplayName = $"item-{i}", ActionType = "Membership" });
    Assert(crashRecovered.GetTransactions().Count == 50, "Action history must retain at most 50 transactions.");

    var oldDocument = new ActionJournalDocument
    {
        Transactions =
        [
            new ActionTransaction { DisplayName = "expired", CreatedAtUtc = DateTime.UtcNow.AddDays(-31) },
            new ActionTransaction { DisplayName = "recent", CreatedAtUtc = DateTime.UtcNow }
        ]
    };
    File.WriteAllText(historyPath, JsonSerializer.Serialize(oldDocument));
    var pruned = new ActionHistoryService(historyPath);
    Assert(pruned.GetTransactions().Count == 1 && pruned.GetTransactions()[0].DisplayName == "recent",
        "Action history must remove entries older than 30 days during startup, not only after a new action.");

    File.WriteAllText(historyPath, "{broken");
    var recovered = new ActionHistoryService(historyPath);
    Assert(recovered.GetTransactions().Count == 0 && File.Exists(historyPath) &&
           Directory.EnumerateFiles(Path.GetDirectoryName(historyPath)!, "*.corrupt").Any(),
        "Corrupt action history must be isolated and must not block startup.");
    recovered.RecordRecycleDelete([file]);
    Assert(recovered.GetNextUndo() is null, "Recycle Bin history must be visible but not offer unreliable automatic restore.");
    recovered.Clear();
    Assert(File.Exists(file), "Clearing operation history must not alter files or the current layout.");
}

static void TestDisplayLayouts(string root)
{
    var service = new DisplayLayoutService(Path.Combine(root, "display-layouts.json"));
    var topologyA = DisplayLayoutService.CreateTopologyKey(
    [
        new DisplayDescriptor("DISPLAY1", 0, 0, 1920, 1080, 96, 96, true),
        new DisplayDescriptor("DISPLAY2", 1920, 0, 2560, 1440, 144, 144, false)
    ]);
    var topologyB = DisplayLayoutService.CreateTopologyKey(
    [
        new DisplayDescriptor("DISPLAY1", 0, 0, 1920, 1080, 96, 96, true)
    ]);
    Assert(topologyA != topologyB, "Display topology key must include monitor combination, bounds and DPI.");
    var config = new AppConfig
    {
        Fences =
        [
            new FenceConfig { Id = "display-fence", Left = 2400, Top = 300, Width = 400, Height = 420, PageIndex = 1 }
        ]
    };
    service.SaveProfile(topologyA, config, 4480, 1440);
    config.Fences[0].Left = 20;
    config.Fences[0].Top = 20;
    Assert(service.TryRestoreProfile(topologyA, config, 4480, 1440) &&
           config.Fences[0].Left == 2400 && config.Fences[0].PageIndex == 1,
        "Returning to a known monitor topology must restore its exact Fence layout.");
    config.Fences[0].IsCollapsed = true;
    config.Fences[0].Top = 1400;
    service.SaveProfile(topologyA, config, 4480, 1440);
    config.Fences[0].Top = 0;
    Assert(service.TryRestoreProfile(topologyA, config, 4480, 1440) &&
           Math.Abs(config.Fences[0].Top - 1400) < 0.01,
        "A collapsed Fence layout must be restored using its visible title height, not its expanded height.");
    config.Fences[0].IsCollapsed = false;
    DisplayLayoutService.RemapToWorkspace(config, 4480, 1440, 1920, 1080);
    Assert(config.Fences[0].Left >= 0 && config.Fences[0].Left + config.Fences[0].Width <= 1920 &&
           config.Fences[0].Top >= 0 && config.Fences[0].Top + config.Fences[0].Height <= 1080,
        "Unknown monitor topology fallback must keep every Fence visible.");
    config.Fences[0].Left = 100;
    config.Fences[0].Top = 100;
    var cycled = DisplayLayoutService.CycleMonitorContents(config,
    [
        new System.Windows.Rect(0, 0, 1920, 1080),
        new System.Windows.Rect(1920, 0, 2560, 1440)
    ]);
    Assert(cycled == 1 && config.Fences[0].Left >= 1920,
        "Swap monitor contents must preserve relative placement while cycling Fences to the next monitor.");
}

static void TestHostedDesktopDpiBounds()
{
    var scaled4K = MainWindow.GetHostedLogicalSize(3840, 2160, 1.5, 1.5);
    Assert(Math.Abs(scaled4K.Width - 2560) < 0.01 &&
           Math.Abs(scaled4K.Height - 1440) < 0.01,
        "A 4K Explorer desktop at 150% scaling must retain its complete logical workspace.");

    var invalidDpi = MainWindow.GetHostedLogicalSize(3840, 2160, double.NaN, 0);
    Assert(invalidDpi.Width == 3840 && invalidDpi.Height == 2160,
        "Invalid DPI values must safely fall back to a 1:1 desktop workspace.");

    var hostedPoint = MainWindow.ConvertClientPhysicalPixelsToWorkspaceDips(
        new System.Windows.Point(62, 693),
        1.25,
        1.25,
        new System.Windows.Point(0, 0));
    Assert(Math.Abs(hostedPoint.X - 49.6) < 0.01 &&
           Math.Abs(hostedPoint.Y - 554.4) < 0.01,
        "Explorer-child hit testing must convert physical ScreenToClient pixels to WPF DIPs at 125% scaling.");
}

static void TestDesktopHostCompatibilityMode()
{
    Assert(!MainWindow.ShouldUseTopLevelDesktopFallback(new Version(10, 0, 26100, 1)),
        "Stable Windows 11 builds before 26200 should retain Explorer child hosting.");
    Assert(MainWindow.ShouldUseTopLevelDesktopFallback(new Version(10, 0, 26200, 5516)),
        "Windows 11 build 26200 and later must use the top-level layered-window compatibility path.");
    Assert(MainWindow.ShouldUseTopLevelDesktopFallback(new Version(10, 0, 19045), "top-level") &&
           !MainWindow.ShouldUseTopLevelDesktopFallback(new Version(10, 0, 26200), "explorer"),
        "The diagnostic desktop-host override must take precedence over automatic OS detection.");
    Assert(MainWindow.NeedsTopLevelDesktopOwnerAttachment(true, false, new IntPtr(1), new IntPtr(2)) &&
           !MainWindow.NeedsTopLevelDesktopOwnerAttachment(true, false, new IntPtr(2), new IntPtr(2)) &&
           !MainWindow.NeedsTopLevelDesktopOwnerAttachment(true, true, IntPtr.Zero, new IntPtr(2)) &&
           !MainWindow.NeedsTopLevelDesktopOwnerAttachment(false, false, IntPtr.Zero, new IntPtr(2)),
        "Top-level compatibility mode must attach Explorer ownership once and preserve it while healthy.");
    Assert(MainWindow.IsPointAtTaskbarShowDesktopEdge(3835, 2100, 0, 2070, 3840, 2160, 32) &&
           !MainWindow.IsPointAtTaskbarShowDesktopEdge(3700, 2100, 0, 2070, 3840, 2160, 32) &&
           MainWindow.IsPointAtTaskbarShowDesktopEdge(10, 1075, 0, 0, 64, 1080, 32),
        "The taskbar Show Desktop edge must be recognized on horizontal and vertical taskbars.");
    Assert(MainWindow.ShouldHandleDesktopDoubleClick(true, true) &&
           !MainWindow.ShouldHandleDesktopDoubleClick(true, false) &&
           !MainWindow.ShouldHandleDesktopDoubleClick(false, true),
        "Closing MiniFences must disable desktop double-click restoration until desktop integration is reopened.");
    Assert(MainWindow.ShouldEnterShowDesktopFromDesktopForeground(false, false, true) &&
           MainWindow.ShouldEnterShowDesktopFromDesktopForeground(false, false, false) &&
           !MainWindow.ShouldEnterShowDesktopFromDesktopForeground(false, true, false),
        "Explorer foreground must enter protection without changing the stable owner relationship.");
    Assert(!MainWindow.ShouldExitShowDesktopForNormalForeground(true, 799, 800) &&
           MainWindow.ShouldExitShowDesktopForNormalForeground(true, 800, 800),
        "Stale normal-window events must be ignored during the initial Shell animation only.");
    Assert(MainWindow.ShouldRepairDesktopLayerAfterNormalForeground(false, false) &&
           !MainWindow.ShouldRepairDesktopLayerAfterNormalForeground(true, false) &&
           !MainWindow.ShouldRepairDesktopLayerAfterNormalForeground(false, true),
        "Every normal foreground activation must repair post-restore z-order drift once no transition owns the layer.");
    Assert(MainWindow.ShouldCancelRenameForForegroundWindow("Chrome_WidgetWin_1", "Codex") &&
           !MainWindow.ShouldCancelRenameForForegroundWindow("CandidateWindow", "TextInputHost") &&
           !MainWindow.ShouldCancelRenameForForegroundWindow("MSCTFIME UI", "unknown"),
        "Other applications should cancel rename editors without disrupting input-method candidate windows.");
}

static void TestShowDesktopCompatibilityRecovery()
{
    Assert(!MainWindow.ShouldRecoverTopLevelDesktopWindow(false, false, false, true, 2) &&
           !MainWindow.ShouldRecoverTopLevelDesktopWindow(true, true, false, true, 2),
        "Explorer-child mode and application shutdown must not run top-level desktop recovery.");
    Assert(MainWindow.ShouldRecoverTopLevelDesktopWindow(true, false, false, false, 0) &&
           MainWindow.ShouldRecoverTopLevelDesktopWindow(true, false, true, true, 0) &&
           MainWindow.ShouldRecoverTopLevelDesktopWindow(true, false, true, false, 2),
        "The Win11 compatibility window must recover after hide, minimize, or Shell DWM cloak.");
    Assert(!MainWindow.ShouldRecoverTopLevelDesktopWindow(true, false, true, false, 0),
        "A healthy visible compatibility window must not be unnecessarily restored.");
    Assert(MainWindow.ShouldBlockTopLevelDesktopHide(true, false, 0x0080) &&
           !MainWindow.ShouldBlockTopLevelDesktopHide(false, false, 0x0080) &&
           !MainWindow.ShouldBlockTopLevelDesktopHide(true, true, 0x0080) &&
           !MainWindow.ShouldBlockTopLevelDesktopHide(true, false, 0x0010),
        "Show Desktop hide requests must be blocked before the compatibility window disappears.");
    Assert(MainWindow.ShouldBlockUnsolicitedDesktopZOrderChange(true, false, false, false, 0) &&
           !MainWindow.ShouldBlockUnsolicitedDesktopZOrderChange(true, false, false, true, 0) &&
           !MainWindow.ShouldBlockUnsolicitedDesktopZOrderChange(true, false, true, false, 0) &&
           !MainWindow.ShouldBlockUnsolicitedDesktopZOrderChange(true, false, false, false, 0x0004),
        "Shell z-order notifications must be suppressed unless they come from MiniFences or an explicit topmost mode.");
    Assert(MainWindow.IsDesktopLayerPlacementStable(new IntPtr(2), new IntPtr(2)) &&
           !MainWindow.IsDesktopLayerPlacementStable(IntPtr.Zero, new IntPtr(2)) &&
           !MainWindow.IsDesktopLayerPlacementStable(new IntPtr(2), new IntPtr(3)),
        "Desktop adjacency checks must be idempotent before a native z-order update.");
}

static void TestLargeFolderVirtualization(string root)
{
    var folder = Path.Combine(root, "large-folder");
    Directory.CreateDirectory(folder);
    for (var index = 0; index < 600; index++)
        File.WriteAllText(Path.Combine(folder, $"item-{index:0000}.txt"), "x");

    Exception? failure = null;
    var thread = new Thread(() =>
    {
        System.Windows.Window? window = null;
        FenceControl? fence = null;
        try
        {
            fence = new FenceControl(new FenceConfig
            {
                Id = "large-folder",
                Title = "Large folder",
                FolderPath = folder,
                Width = 380,
                Height = 320
            });
            window = new System.Windows.Window { Width = 400, Height = 350, Content = fence, ShowInTaskbar = false };
            window.Show();
            fence.LoadFolderItems();
            window.UpdateLayout();
            Assert(fence.LoadedItemsForTesting.Count == 600, "Large Folder Portal must load every item.");
            Assert(fence.RealizedItemCountForTesting > 0 && fence.RealizedItemCountForTesting < 80,
                "Large Folder Portal must virtualize off-screen item containers.");
            var offsetBeforeDragWheel = fence.VerticalScrollOffsetForTesting;
            Assert(fence.ScrollItemsDuringDrag(-120),
                "A Fence with a realized ScrollViewer must accept wheel input during an OLE drag.");
            window.UpdateLayout();
            Assert(fence.VerticalScrollOffsetForTesting > offsetBeforeDragWheel,
                "Dragging an item must not prevent the mouse wheel from scrolling Fence contents.");
            fence.ScrollItemsForTesting(400);
            var offsetBeforeRefresh = fence.VerticalScrollOffsetForTesting;
            Assert(offsetBeforeRefresh > 0, "Scroll preservation test must start below the top.");
            File.WriteAllText(Path.Combine(folder, "newly-dropped.txt"), "new item");
            fence.LoadFolderItems();
            window.UpdateLayout();
            Assert(fence.LoadedItemsForTesting.Count == 601 &&
                   Math.Abs(fence.VerticalScrollOffsetForTesting - offsetBeforeRefresh) < 1,
                "Refreshing after a dropped file must update contents without resetting scroll position.");
            File.Delete(Path.Combine(folder, "newly-dropped.txt"));
            fence.LoadFolderItems();
            window.UpdateLayout();
            Assert(fence.LoadedItemsForTesting.Count == 600 &&
                   Math.Abs(fence.VerticalScrollOffsetForTesting - offsetBeforeRefresh) < 1,
                "Refreshing after moving a file out must preserve the source Fence scroll position.");
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            fence?.StopForTesting();
            window?.Close();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Large-folder virtualization test failed.", failure);
}

static void TestAdvancedAutoOrganizeRules(string root)
{
    Assert(DesktopIntegrationService.BuildCommand(@"C:\Program Files\MiniFences\MiniFences.exe") ==
           "\"C:\\Program Files\\MiniFences\\MiniFences.exe\" --new-fence",
        "Windows desktop context-menu command must quote executable paths and request a new Fence.");
    var folder = Path.Combine(root, "advanced-rules");
    Directory.CreateDirectory(folder);
    var exact = Path.Combine(folder, "Budget.xlsx");
    File.WriteAllText(exact, "budget");
    Assert(!AutoOrganizerService.RuleMatches(new AutoOrganizeRule(), exact),
        "An enabled rule with no criteria must not capture every new desktop item.");
    Assert(!new AutoOrganizeRule().IsEnabled,
        "New auto-organize rules must remain disabled until the user configures them.");
    Assert(AutoOrganizerService.RuleMatches(new AutoOrganizeRule { ExactNames = "README; Budget.xlsx" }, exact),
        "Auto-organize rules must support exact file names.");
    Assert(!AutoOrganizerService.RuleMatches(new AutoOrganizeRule { ExactNames = "Other.xlsx" }, exact),
        "Exact-name rules must reject other files.");
    var shortcut = Path.Combine(folder, "Company.url");
    File.WriteAllText(shortcut, "[InternetShortcut]\nURL=https://company.example/dashboard\n");
    Assert(AutoOrganizerService.RuleMatches(
            new AutoOrganizeRule { ShortcutTargetPattern = "https://company.example/*" }, shortcut),
        "Auto-organize rules must match Internet shortcut targets.");
    Assert(AutoOrganizerService.RuleMatches(new AutoOrganizeRule { FoldersOnly = true }, folder) &&
           !AutoOrganizerService.RuleMatches(new AutoOrganizeRule { FoldersOnly = true }, exact),
        "A folders-only rule may use that option as its sole effective criterion.");
}

static void TestColorPickerDialog()
{
    var red = ColorPickerDialog.ColorFromHsvForTesting(0, 1, 1);
    var cyan = ColorPickerDialog.ColorFromHsvForTesting(180, 1, 1);
    Assert(red.R == 255 && red.G == 0 && red.B == 0 &&
           cyan.R == 0 && cyan.G == 255 && cyan.B == 255,
        "Continuous HSV picking must cover primary and secondary colors accurately.");
    var sourceColor = System.Windows.Media.Color.FromArgb(204, 63, 127, 168);
    var hsv = ColorPickerDialog.HsvFromColorForTesting(sourceColor);
    var roundTrip = ColorPickerDialog.ColorFromHsvForTesting(hsv.Hue, hsv.Saturation, hsv.Value, sourceColor.A);
    Assert(Math.Abs(roundTrip.R - sourceColor.R) <= 1 &&
           Math.Abs(roundTrip.G - sourceColor.G) <= 1 &&
           Math.Abs(roundTrip.B - sourceColor.B) <= 1 &&
           roundTrip.A == sourceColor.A,
        "HSV conversion must preserve the selected Fence color and opacity.");

    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var localization = new LocalizationService { Language = LocalizationService.Chinese };
            foreach (var chooseHeader in new[] { false, true })
            {
                var becameVisible = false;
                var dialog = new ColorPickerDialog(chooseHeader ? "#CC3F7FA8" : "#DD20242A", localization, chooseHeader);
                dialog.Loaded += (_, _) =>
                {
                    becameVisible = dialog.IsVisible;
                    dialog.Dispatcher.BeginInvoke(dialog.Close, DispatcherPriority.ApplicationIdle);
                };
                dialog.ShowDialog();
                Assert(becameVisible, $"The {(chooseHeader ? "title" : "background")} color picker must become visible.");
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure != null) throw new InvalidOperationException("Color picker window test failed.", failure);
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
    const string recycleBin = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";
    Assert(ShellContextMenuService.SelectPathsForContextMenu(recycleBin, []).SequenceEqual([recycleBin]),
        "Shell namespace desktop icons must be passed to the native context-menu provider.");
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
    Assert(SettingsWindow.GetWelcomeVisibilityActionKey(true) == "DisableMiniFences" &&
           SettingsWindow.GetWelcomeVisibilityActionKey(false) == "EnableMiniFences",
        "The Welcome action must retain its open/close wording while controlling complete Fence visibility.");
    Assert(MainWindow.IsMiniFencesEnabledState(false, true) &&
           !MainWindow.IsMiniFencesEnabledState(true, true) &&
           !MainWindow.IsMiniFencesEnabledState(false, false),
        "MiniFences is open only when Fences are visible and desktop-icon integration is enabled.");
    Assert(SettingsWindow.GetBottomRollupOptionAvailability(true, false, true) ==
           (false, false, false),
        "Bottom roll-up options must all be disabled when automatic edge roll-up is off.");
    Assert(SettingsWindow.GetBottomRollupOptionAvailability(true, true, false) ==
           (true, false, true) &&
           SettingsWindow.GetBottomRollupOptionAvailability(true, true, true) ==
           (true, true, true),
        "Bottom-title placement must only be available when bottom-edge roll-up is allowed.");
    Assert(SettingsWindow.GetTabOptionAvailability(false, false) == (false, false, false) &&
           SettingsWindow.GetTabOptionAvailability(true, true) == (true, true, true),
        "Tab preview controls must only expose options that affect the selected tab mode.");
    Assert(SettingsWindow.ApplyRollupPreviewDoubleClick(true, true) == (true, false) &&
           SettingsWindow.ApplyRollupPreviewDoubleClick(true, false) == (false, false),
        "Double-clicking a hover-expanded preview must collapse it without losing its rolled-up base state.");
    Assert(SettingsWindow.GetRollupPreviewDock(0, 210, 116) == "Top" &&
           SettingsWindow.GetRollupPreviewDock(94, 210, 116) == "Bottom" &&
           SettingsWindow.GetRollupPreviewDock(47, 210, 116) == "Standard",
        "Dragging the roll-up preview must distinguish the top edge, bottom edge, and free-standing area.");
    Assert(SettingsWindow.GetRollupPreviewTop("Top", 210, 34) == 0 &&
           SettingsWindow.GetRollupPreviewTop("Bottom", 210, 34) == 176 &&
           SettingsWindow.GetRollupPreviewTop("Standard", 210, 116) == 47,
        "The roll-up preview must snap to either screen edge and otherwise remain centered.");
    Assert(SettingsWindow.ShouldRollupPreviewAtDock(true, true, false, "Top") &&
           !SettingsWindow.ShouldRollupPreviewAtDock(true, true, false, "Bottom") &&
           SettingsWindow.ShouldRollupPreviewAtDock(true, true, true, "Bottom") &&
           !SettingsWindow.ShouldRollupPreviewAtDock(true, false, true, "Top"),
        "The draggable preview must follow the real top- and bottom-edge roll-up settings.");
}

static void TestHotkeyCaptureFormatting()
{
    Assert(SettingsWindow.FormatCapturedHotkey(
               System.Windows.Input.Key.Left,
               System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt) == "Ctrl+Alt+Left",
        "Shortcut capture must turn a key combination into the canonical saved form.");
    Assert(SettingsWindow.FormatCapturedHotkey(
               System.Windows.Input.Key.A,
               System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift) == "Ctrl+Shift+A",
        "Shortcut capture must record letters as part of the pressed combination instead of free-form text.");
    Assert(SettingsWindow.FormatCapturedHotkey(
               System.Windows.Input.Key.NumPad7,
               System.Windows.Input.ModifierKeys.Windows) == "Win+7" &&
           SettingsWindow.FormatCapturedHotkey(
               System.Windows.Input.Key.F12,
               System.Windows.Input.ModifierKeys.None) == "F12",
        "Shortcut capture must normalize number-pad digits and support direct-page function keys.");
    Assert(SettingsWindow.FormatCapturedHotkey(
               System.Windows.Input.Key.LeftCtrl,
               System.Windows.Input.ModifierKeys.Control) == null,
        "Pressing only a modifier must wait for the actual shortcut key.");
    Assert(SettingsWindow.GetCapturedModifier(System.Windows.Input.Key.LeftCtrl) ==
               System.Windows.Input.ModifierKeys.Control &&
           SettingsWindow.GetCapturedModifier(System.Windows.Input.Key.RightAlt) ==
               System.Windows.Input.ModifierKeys.Alt &&
           SettingsWindow.GetCapturedModifier(System.Windows.Input.Key.Left) ==
               System.Windows.Input.ModifierKeys.None,
        "Left/right modifier keys must be recognized independently from the final shortcut key.");
    Assert(SettingsWindow.FormatModifierPreview(
               System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt) == "Ctrl+Alt+…",
        "Sequential shortcut capture must visibly retain modifiers until the final key is pressed.");
    Assert(SettingsWindow.FormatCapturedMouseHotkey(
               "MouseRight",
               System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt) == "Ctrl+Alt+MouseRight",
        "Shortcut capture must distinguish the mouse right button from the keyboard Right Arrow key.");
    Assert(MainWindow.GetAdjacentPageIndex(0, 2, -1) == 1 &&
           MainWindow.GetAdjacentPageIndex(1, 2, 1) == 0 &&
           MainWindow.GetAdjacentPageIndex(1, 3, -1) == 0,
        "Previous/next shortcuts must cycle at the first and last page so every press has a visible result.");
}

static void TestFenceItemMultiSelectionPresentation()
{
    Assert(new FolderItem { Kind = "Folder", Size = 0 }.DisplaySize == "—" &&
           new FolderItem { Kind = "TXT", Size = 512 * 1024 }.DisplaySize == "512 KB" &&
           new FolderItem { Kind = "TXT", Size = 1536 }.DisplaySize == "1.5 KB" &&
           new FolderItem { Kind = "ZIP", Size = 128L * 1024 * 1024 }.DisplaySize == "128 MB" &&
           new FolderItem { Kind = "ISO", Size = 1280L * 1024 * 1024 }.DisplaySize == "1.25 GB",
        "List sizes must use KB below one MB, MB below one GB, GB at and above one GB, and a dash for folders.");
    Assert(!MainWindow.ShouldCloseWpfContextMenuFromLowLevelMouseHook(),
        "The low-level desktop hook must not close a WPF context menu before a submenu Click event is delivered.");
    Assert(!MainWindow.ShouldClearKeyboardFocusForGlobalClick(true) &&
           MainWindow.ShouldClearKeyboardFocusForGlobalClick(false),
        "The desktop mouse hook must never clear keyboard focus for Settings, dialogs, ComboBox popups, or other MiniFences windows.");
    Assert(FenceControl.ShouldShowExpandedSelectionLabel(1) &&
           !FenceControl.ShouldShowExpandedSelectionLabel(0) &&
           !FenceControl.ShouldShowExpandedSelectionLabel(2),
        "The rename overlay is only eligible for one selected item; ordinary selection keeps the compact filename layout.");
    Assert(FenceControl.GetItemPresentationTag(false, true, 1) == "SingleSelectedIcon" &&
           FenceControl.GetItemPresentationTag(false, true, 2) == "Icons" &&
           FenceControl.GetItemPresentationTag(true, true, 2) == "List",
        "Multi-selected icon cells must retain their own visible selection chrome.");
    Assert(FenceControl.ShouldScheduleInlineRename(true, true, System.Windows.Input.ModifierKeys.None),
        "A second unmodified click on a selected filename may start inline rename.");
    Assert(!FenceControl.ShouldScheduleInlineRename(true, false, System.Windows.Input.ModifierKeys.Control) &&
           !FenceControl.ShouldScheduleInlineRename(true, true, System.Windows.Input.ModifierKeys.Control) &&
           !FenceControl.ShouldScheduleInlineRename(true, true, System.Windows.Input.ModifierKeys.Shift),
        "Ctrl or Shift selection clicks must never start inline rename.");
    Exception? listViewFailure = null;
    var listViewThread = new Thread(() =>
    {
        try
        {
            var listFence = new FenceControl(new FenceConfig
            {
                Title = "Downloads",
                FolderPath = Path.GetTempPath(),
                Kind = FenceConfig.DesktopGroupKind,
                PortalViewMode = "List"
            });
            listFence.ApplyPortalViewForTesting();
            Assert(listFence.IsListViewModeForTesting && !listFence.ListTemplateContainsIconsForTesting,
                "Fence list view must use a persisted, genuinely icon-free row template.");
            listFence.SelectViewModeForTesting("Icons");
            Assert(listFence.Config.PortalViewMode == "Icons" && !listFence.IsListViewModeForTesting,
                "The View > Icons submenu click must update the Fence configuration and template.");
            listFence.SelectViewModeForTesting("List");
            Assert(listFence.Config.PortalViewMode == "List" && listFence.IsListViewModeForTesting,
                "The View > List submenu click must update the Fence configuration and template.");
            listFence.SetListColumnVisibility(showType: false, showSize: true, showTime: false);
            Assert(!listFence.Config.ListShowType && listFence.Config.ListShowSize && !listFence.Config.ListShowTime,
                "List column settings must update the existing Fence in place without an application restart.");
            var compactLayout = FenceControl.GetPortalItemLayout(true, 700, 42, 2);
            var comfortableLayout = FenceControl.GetPortalItemLayout(true, 700, 42, 6);
            var spaciousLayout = FenceControl.GetPortalItemLayout(true, 700, 42, 10);
            Assert(compactLayout.PanelHeight < comfortableLayout.PanelHeight &&
                   comfortableLayout.PanelHeight < spaciousLayout.PanelHeight,
                "Compact, comfortable, and spacious list spacing must produce visibly different row extents.");
            listFence.SetTabStatus(2, 0, ["First", "Second"], useTabStrip: true);
            Assert(listFence.FirstTabCornerRadiusForTesting.TopLeft == 8,
                "The first title tab must follow the Fence's top-left corner instead of protruding through it.");
            listFence.StopForTesting();
        }
        catch (Exception ex) { listViewFailure = ex; }
    });
    listViewThread.SetApartmentState(ApartmentState.STA);
    listViewThread.Start();
    listViewThread.Join();
    if (listViewFailure != null) throw new InvalidOperationException("Fence list-view test failed.", listViewFailure);
}

static void TestDesktopDragData()
{
    Assert(MainWindow.GetMouseWheelDelta(120u << 16) == 120 &&
           MainWindow.GetMouseWheelDelta(unchecked((uint)(-120 << 16))) == -120,
        "The low-level drag hook must preserve upward and downward mouse-wheel deltas.");
    var formattedLabel = DesktopIconLabelConverter.FormatTwoLines(
        "MiniFences-win-x64-0.23.80-source-verification", 76);
    var expandedLabel = DesktopIconLabelConverter.FormatAllLines(
        "MiniFences-win-x64-0.23.80-source-verification", 76);
    var compactLines = formattedLabel.Split('\n');
    var expandedLines = expandedLabel.Split('\n');
    Assert(expandedLines.Length > 2 && compactLines[0] == expandedLines[0],
        "Selected and compact labels must share the same first-line layout; selection may only reveal later lines.");
    var hyphenatedName = "MiniFences-source-0";
    var hyphenatedExpanded = DesktopIconLabelConverter.FormatAllLines(hyphenatedName, 76);
    Assert(hyphenatedExpanded.Replace("\n", string.Empty, StringComparison.Ordinal) == hyphenatedName,
        "Wrapping a desktop filename must preserve every hyphen and underscore at line boundaries.");
    var hyphenatedCompact = DesktopIconLabelConverter.FormatTwoLines(hyphenatedName, 76);
    Assert(hyphenatedCompact.StartsWith("MiniFences-\n", StringComparison.Ordinal),
        "A preferred wrap after a hyphen must keep the hyphen visible on the first line.");
    Assert(formattedLabel.Count(character => character == '\n') == 1 && formattedLabel.EndsWith('…'),
        "Long desktop labels must be deterministically formatted into exactly two lines with a final ellipsis.");
    var suppressedRenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Desktop\Renamed.txt" };
    Assert(!MainWindow.ShouldQueueAutoOrganization(@"c:\desktop\renamed.txt", suppressedRenames) &&
           !MainWindow.ShouldQueueAutoOrganization(@"C:\Desktop\Renamed.txt", suppressedRenames),
        "A user-initiated desktop rename must suppress duplicate automatic-organization watcher events during the rename window.");
    suppressedRenames.Clear();
    Assert(MainWindow.ShouldQueueAutoOrganization(@"C:\Desktop\Renamed.txt", suppressedRenames),
        "Automatic organization must resume after the desktop rename suppression window expires.");
    var paths = new[] { @"C:\Desktop\one.txt", @"C:\Desktop\two.txt" };
    var data = new System.Windows.DataObject();
    DesktopDragData.Set(data, paths, looseIcon: true, paths[0]);
    Assert(DesktopDragData.TryGetPaths(data, out var restored) && restored.SequenceEqual(paths),
        "Internal desktop drags should preserve item paths.");
    Assert(data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
           data.GetData(System.Windows.DataFormats.FileDrop) is string[] fileDrop && fileDrop.SequenceEqual(paths),
        "Desktop drags should expose standard Shell FileDrop data for browser and chat uploads.");
    var systemIconPath = "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    var systemIconData = new System.Windows.DataObject();
    DesktopDragData.Set(systemIconData, [systemIconPath], looseIcon: true, systemIconPath);
    Assert(DesktopDragData.TryGetPaths(systemIconData, out var systemIconPaths) &&
           systemIconPaths.SequenceEqual([systemIconPath]) &&
           !systemIconData.GetDataPresent(System.Windows.DataFormats.FileDrop),
        "System desktop icons must support internal positioning without exposing invalid Shell FileDrop paths.");
    var collectionData = new System.Windows.DataObject();
    var collection = new System.Collections.Specialized.StringCollection { paths[0], paths[1] };
    collectionData.SetData(System.Windows.DataFormats.FileDrop, collection);
    Assert(DesktopDragData.TryGetPaths(collectionData, out var collectionPaths) &&
           collectionPaths.SequenceEqual(paths),
        "Explorer file drops must accept both array and StringCollection path representations.");
    var linkData = new System.Windows.DataObject();
    linkData.SetData(DesktopDragData.PreferredDropEffectFormat,
        BitConverter.GetBytes((int)System.Windows.DragDropEffects.Link));
    Assert(DesktopDragData.GetRequestedDropEffect(linkData, System.Windows.DragDropKeyStates.None,
               System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Link) ==
           System.Windows.DragDropEffects.Link,
        "Explorer address-bar/breadcrumb drags must preserve their requested Link operation.");
    var ambiguousExternalData = new System.Windows.DataObject();
    Assert(DesktopDragData.GetRequestedDropEffect(ambiguousExternalData, System.Windows.DragDropKeyStates.None,
               System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move | System.Windows.DragDropEffects.Link) ==
           System.Windows.DragDropEffects.Copy &&
           DesktopDragData.GetRequestedDropEffect(ambiguousExternalData, System.Windows.DragDropKeyStates.ShiftKey,
               System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move) ==
           System.Windows.DragDropEffects.Move,
        "Ambiguous external drops must default to Copy; only an explicit Move request may remove the source.");
    Assert(DesktopDragData.IsLooseIconDrag(data) && DesktopDragData.GetAnchorPath(data) == paths[0],
        "Loose icon drags should preserve their origin and anchor item.");
    Assert(data.GetDataPresent(DesktopDragData.SourceFormat),
        "Every MiniFences drag must identify its source consistently.");
    Assert(DesktopDragData.IsMiniFencesSource(data),
        "Internal drags must be identifiable without repeatedly querying the COM data object.");
    Assert(!DesktopDragData.HasShellDragImage(data),
        "A data object without Shell metadata must remain eligible for fallback feedback.");
    var shellData = new System.Windows.DataObject();
    shellData.SetData("DragWindow", new byte[] { 1, 0, 0, 0 });
    Assert(DesktopDragData.HasShellDragImage(shellData),
        "Shell drag-image metadata must be detected so Windows can own native drag feedback.");
    var nativeData = new System.Windows.DataObject();
    nativeData.SetData(DesktopDragData.NativeShellImageFormat, true);
    Assert(DesktopDragData.HasShellDragImage(nativeData),
        "A successful internal native Shell image must suppress custom follower feedback.");
    var guaranteedIcon = FolderItemService.GetGuaranteedPathIcon(@"Z:\path-that-does-not-exist\unknown.weird");
    Assert(guaranteedIcon != null,
        "Every drag path must have a visible fallback icon even when the Shell icon handler returns nothing.");
    var targetData = new System.Windows.DataObject();
    Assert(!DesktopDragData.RefreshShellDragImageState(targetData) &&
           !DesktopDragData.HasShellDragImage(targetData),
        "A Shell target-helper session without drag-image formats must keep fallback feedback available.");
    Assert(MainWindow.ShouldPreserveLooseSelectionOnPointerDown(true, 2, System.Windows.Input.ModifierKeys.None) &&
           !MainWindow.ShouldPreserveLooseSelectionOnPointerDown(true, 2, System.Windows.Input.ModifierKeys.Control) &&
           !MainWindow.ShouldPreserveLooseSelectionOnPointerDown(false, 2, System.Windows.Input.ModifierKeys.None),
        "Starting a drag on a selected loose icon must preserve multi-selection only without a new selection modifier.");
    Assert(!DesktopDragData.ShouldCancelExplorerDesktopDrop(System.Windows.DragDropKeyStates.LeftMouseButton, true) &&
           DesktopDragData.ShouldCancelExplorerDesktopDrop(System.Windows.DragDropKeyStates.None, true) &&
           !DesktopDragData.ShouldCancelExplorerDesktopDrop(System.Windows.DragDropKeyStates.None, true, overMiniFencesSurface: true) &&
           !DesktopDragData.ShouldCancelExplorerDesktopDrop(System.Windows.DragDropKeyStates.None, false),
        "A completed drop on Explorer should be canceled without blocking valid drops on a MiniFences surface.");
    Assert(MainWindow.IsPointInsideWorkspace(new System.Windows.Point(0, 0), 1920, 1080) &&
           MainWindow.IsPointInsideWorkspace(new System.Windows.Point(1919, 1079), 1920, 1080) &&
           !MainWindow.IsPointInsideWorkspace(new System.Windows.Point(1920, 1079), 1920, 1080) &&
           !MainWindow.IsPointInsideWorkspace(new System.Windows.Point(-1, 0), 1920, 1080),
        "The full MiniFences workspace should remain a valid desktop drag-and-drop surface.");
    Assert(DesktopDragData.ShouldReleaseDesktopMembershipAfterDrag(System.Windows.DragDropEffects.None, true) &&
           !DesktopDragData.ShouldReleaseDesktopMembershipAfterDrag(System.Windows.DragDropEffects.None, true, canceledByEscape: true) &&
           !DesktopDragData.ShouldReleaseDesktopMembershipAfterDrag(System.Windows.DragDropEffects.Copy, false),
        "Only an intentional Explorer-desktop drop may release Fence membership; Escape and external uploads must keep it.");
    Assert(MainWindow.ShouldTreatShellWindowAsDesktop("SysListView32", belongsToDesktopHost: true) &&
           !MainWindow.ShouldTreatShellWindowAsDesktop("SysListView32", belongsToDesktopHost: false) &&
           !MainWindow.ShouldTreatShellWindowAsDesktop("CabinetWClass", belongsToDesktopHost: true),
        "Explorer folder views must not be classified as desktop solely because they use a desktop-like child class.");
    var reordered = MainWindow.ReorderDesktopIcons(["one", "two", "three", "four"], ["three", "four"], 1);
    Assert(reordered.SequenceEqual(["one", "three", "four", "two"]),
        "Manual desktop icon dragging should change only the automatic grid order and keep multi-selection contiguous.");
}

static void TestDefaultConfig(string root)
{
    Assert(MainWindow.ShouldShowFence(false, true, true),
        "Desktop Fences must remain visible while MiniFences intentionally hides Explorer icons.");
    Assert(MainWindow.ShouldShowFence(false, false, false),
        "Folder Portal Fences must remain visible when desktop icon integration is disabled.");
    Assert(!MainWindow.ShouldShowFence(false, false, true) &&
           !MainWindow.ShouldShowLooseDesktopIcons(false, false),
        "Desktop groups and MiniFences loose icons must defer to Explorer when integration is disabled.");
    Assert(!MainWindow.ShouldShowFence(true, true, false),
        "The explicit hide-Fences state must hide every Fence.");

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
    Assert(FolderItemService.IsDesktopRootPath(personalDesktop) &&
           !FolderItemService.IsDesktopRootPath(Path.Combine(root, "not-the-desktop")),
        "Desktop-root detection must distinguish the real Windows desktop from ordinary folders.");

    var duplicateGuardService = new ConfigService(Path.Combine(root, "desktop-portal-guard.json"));
    duplicateGuardService.Save(new AppConfig
    {
        Fences =
        [
            new FenceConfig
            {
                Id = "desktop-portal",
                Kind = FenceConfig.FolderPortalKind,
                FolderPath = personalDesktop
            }
        ]
    });
    var guardedDesktopFence = duplicateGuardService.Load().Fences.Single();
    Assert(guardedDesktopFence.IsDesktopGroup && guardedDesktopFence.AssignedPaths.Count == 0,
        "Binding the desktop root must create an empty desktop group instead of duplicating every desktop icon.");

    var linkedSource = new FenceConfig
    {
        Id = "linked-source",
        Title = "Common apps",
        Kind = FenceConfig.DesktopGroupKind,
        AssignedPaths = ["wechat.lnk", "qq.lnk"],
        PageIndex = 0,
        ContentLinkId = "common-apps-link"
    };
    var linkedCopy = MainWindow.CreateLinkedFenceCopy(linkedSource, 1);
    var linkedConfig = new AppConfig { PageCount = 2, Fences = [linkedSource, linkedCopy] };
    Assert(linkedCopy.Id != linkedSource.Id && linkedCopy.PageIndex == 1 &&
           linkedCopy.ContentLinkId == linkedSource.ContentLinkId &&
           linkedCopy.AssignedPaths.SequenceEqual(linkedSource.AssignedPaths) &&
           linkedCopy.Left == linkedSource.Left && linkedCopy.Top == linkedSource.Top &&
           linkedCopy.Width == linkedSource.Width && linkedCopy.Height == linkedSource.Height,
        "A synchronized copy must have independent identity while initially matching content and layout exactly.");
    Assert(MainWindow.GetContentLinkedFences(linkedConfig, linkedSource).Count == 2,
        "Content-linked Fences must resolve every synchronized instance.");
    linkedCopy.Left = 246;
    linkedCopy.Top = 135;
    linkedCopy.Width = 480;
    linkedCopy.Height = 320;
    linkedCopy.ExpandedHeight = 320;
    Assert(MainWindow.SynchronizeLinkedFenceLayout(linkedConfig, linkedCopy) &&
           linkedSource.Left == 246 && linkedSource.Top == 135 &&
           linkedSource.Width == 480 && linkedSource.Height == 320,
        "Moving or resizing any synchronized copy must keep every page at the exact same position and size.");
    linkedSource.SynchronizeLinkedLayout = false;
    linkedCopy.SynchronizeLinkedLayout = false;
    linkedCopy.Left = 512;
    Assert(!MainWindow.SynchronizeLinkedFenceLayout(linkedConfig, linkedCopy) && linkedSource.Left == 246,
        "Disabling linked-layout synchronization must allow each page copy to keep an independent position.");

    var pinnedFence = new FenceConfig { PageIndex = 0, ShowOnAllPages = true };
    Assert(MainWindow.IsFenceVisibleOnPage(pinnedFence, 0) &&
           MainWindow.IsFenceVisibleOnPage(pinnedFence, 7) &&
           !MainWindow.IsFenceVisibleOnPage(new FenceConfig { PageIndex = 0 }, 1),
        "A pinned Fence must be visible on every page without changing normal page visibility.");

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
    Assert(config.GridSize == 16 && !config.SnapWhileDragging,
        "The default grid should be 16 pixels and snap only when the drag finishes.");
    Assert(config.DirectPageHotkeys.SequenceEqual(Enumerable.Range(1, 12).Select(index => $"F{index}")),
        "Direct page shortcuts should default to F1 through F12.");
    Assert(config.EnableTabCreation, "Tab creation should be enabled by default.");
    Assert(config.TabViewMode == "Compact", "The compact tab view should remain the default for compatibility.");
    Assert(!config.ConfirmTabCreation && !config.HoverSwitchTabs, "Confirmation and hover switching should be opt-in.");
    Assert(config.EnableRollup && config.DoubleClickTitleRollup, "Roll-up and title double-click should remain enabled by default.");
    Assert(config.AllowBottomEdgeRollup && config.BottomDockTitleAtBottom && !config.TopDockTitleAtBottomOnExpand,
        "Bottom docking should keep its title at the bottom by default, while the mirrored top-dock layout remains optional.");
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

static void TestTabDropAcceptance()
{
    var ownTab = new FenceConfig { Id = "source" };
    Assert(!FenceControl.CanAcceptTabMerge("source", "source", null),
        "A tab dropped on its own standalone Fence must detach instead of being falsely accepted.");
    Assert(!FenceControl.CanAcceptTabMerge("source", "target", [ownTab]),
        "A tab already in the target group must not be treated as a new merge.");
    Assert(FenceControl.CanAcceptTabMerge("source", "target", [new FenceConfig { Id = "other" }]),
        "A tab dropped over a different Fence must be eligible for merging.");
    Assert(FenceControl.IsTabMergeDropPoint(new System.Windows.Point(150, 17), 450),
        "The middle third of the target title bar must accept a tab merge.");
    Assert(!FenceControl.IsTabMergeDropPoint(new System.Windows.Point(150, 100), 450),
        "The target Fence content area must detach the tab instead of merging it.");
    Assert(!FenceControl.IsTabMergeDropPoint(new System.Windows.Point(30, 17), 450),
        "The sides of the target title bar must not count as the merge zone.");
    var sourceFenceBounds = new System.Windows.Rect(100, 100, 450, 320);
    Assert(!FenceControl.ShouldDetachTabDrop(sourceFenceBounds, new System.Drawing.Point(320, 250)),
        "Dropping a tab anywhere inside its combined Fence must keep it in the group.");
    Assert(!FenceControl.ShouldDetachTabDrop(sourceFenceBounds, new System.Drawing.Point(558, 250)),
        "A small pointer overshoot near the combined Fence edge must not detach a tab.");
    Assert(FenceControl.ShouldDetachTabDrop(sourceFenceBounds, new System.Drawing.Point(590, 250)),
        "A tab must detach only after it is dropped clearly outside the combined Fence.");
    Assert(FenceControl.BuildTabReorderPreviewOrder(4, 0, 2).SequenceEqual([1, 2, 0, 3]) &&
           FenceControl.BuildTabReorderPreviewOrder(4, 3, 1).SequenceEqual([0, 3, 1, 2]),
        "Browser-style tab dragging must calculate the live gap order in both directions before drop.");
    Assert(FenceControl.CalculateTabReorderTargetIndex(20, 1, [0, 90, 180], [90, 90, 90]) == 0 &&
           FenceControl.CalculateTabReorderTargetIndex(100, 1, [0, 90, 180], [90, 90, 90]) == 1 &&
           FenceControl.CalculateTabReorderTargetIndex(250, 1, [0, 90, 180], [90, 90, 90]) == 2,
        "A middle tab must reach the left, middle, and right insertion positions without relying on its transparent source slot receiving drag events.");
    Assert(FenceControl.CalculateTabReorderTargetIndex(140, 0, [90, 0, 180], [90, 90, 90]) == 1 &&
           FenceControl.CalculateTabReorderTargetIndex(80, 2, [0, 90, 180], [90, 90, 90]) == 1,
        "Edge tabs must be able to enter the middle insertion position using pointer geometry.");
    Assert(!FenceControl.ShouldAllowExternalTabMove(false) &&
           FenceControl.ShouldAllowExternalTabMove(true),
        "Dragging a tab out of its group or into another group must require Shift from drag start.");
    Assert(!FenceControl.ShouldStartTabOperation(System.Windows.Input.ModifierKeys.None) &&
           FenceControl.ShouldStartTabOperation(System.Windows.Input.ModifierKeys.Shift),
        "An ordinary tab-header drag must move the complete Fence; only Shift may start tab sorting or detaching.");
    Assert(FenceControl.ShouldConstrainTabDragToHeader(externalMoveAllowed: false) &&
           !FenceControl.ShouldConstrainTabDragToHeader(externalMoveAllowed: true),
        "An ordinary tab reorder preview must remain locked to the source header while Shift-drag may leave it.");
    var sourceHeader = new System.Windows.Rect(20, 30, 500, 34);
    Assert(!FenceControl.ShouldCollapseDraggedTabSlot(true, sourceHeader, new System.Windows.Point(250, 47)) &&
           !FenceControl.ShouldCollapseDraggedTabSlot(true, sourceHeader, new System.Windows.Point(500, 47)) &&
           FenceControl.ShouldCollapseDraggedTabSlot(true, sourceHeader, new System.Windows.Point(540, 47)) &&
           !FenceControl.ShouldCollapseDraggedTabSlot(false, sourceHeader, new System.Windows.Point(540, 47)),
        "The complete large title bar must remain a three-position reorder zone, while crossing its horizontal edge must stop reordering and collapse the source slot.");
    Assert(FenceControl.ShouldCompactTabDragPreview(true, true, false) &&
           FenceControl.ShouldCompactTabDragPreview(true, false, true) &&
           FenceControl.ShouldCompactTabDragPreview(false, true, false) &&
           !FenceControl.ShouldCompactTabDragPreview(false, false, true),
        "A dragged tab must remain compact anywhere in its original group and compact only in another Fence's middle merge zone.");
    Assert(MainWindow.GetTabDragRemainderIndex(0, 0, 3) == 1 &&
           MainWindow.GetTabDragRemainderIndex(2, 2, 3) == 1,
        "Dragging the active tab must immediately reveal a remaining tab in the original group.");
    Assert(MainWindow.GetTabDragRemainderIndex(1, 0, 3) == 1,
        "Dragging an inactive tab must keep the original group's active content unchanged.");
    Assert(MainWindow.GetTabDragRemainderIndex(0, 0, 1) == -1,
        "A standalone Fence cannot expose a remaining tab while dragging.");
}

static void TestTabGroupPresentationState()
{
    var lockedTarget = new FenceConfig { TabGroupId = "locked", IsLocked = true };
    var lockedSibling = new FenceConfig { TabGroupId = "locked", IsLocked = true };
    var incoming = new FenceConfig { TabGroupId = "locked", IsLocked = false,
        PreTabWidth = 280, PreTabHeight = 320 };
    MainWindow.SynchronizeTabGroupPresentationState(lockedTarget, [lockedTarget, lockedSibling, incoming]);
    Assert(incoming.IsLocked && lockedSibling.IsLocked,
        "An unlocked tab merged into a locked target must inherit the target lock.");
    incoming.IsCollapsed = true;
    incoming.EdgeDock = "Top";
    MainWindow.PrepareDetachedFence(incoming);
    Assert(!incoming.IsLocked && incoming.TabGroupId == null && !incoming.IsCollapsed &&
        incoming.EdgeDock == null && incoming.Width == 280 && incoming.Height == 320,
        "Both drag and menu detach must produce an unlocked, expanded standalone Fence.");
    Assert(lockedTarget.IsLocked && lockedSibling.IsLocked,
        "Detaching a tab must leave the remaining group's lock unchanged.");
    incoming.TabGroupId = "locked";
    MainWindow.SynchronizeTabGroupPresentationState(lockedTarget, [lockedTarget, lockedSibling, incoming]);
    Assert(incoming.IsLocked, "Reattaching a detached tab must restore the target group's lock.");
    lockedSibling.IsLocked = false;
    MainWindow.SynchronizeTabGroupPresentationState(lockedSibling, [lockedTarget, lockedSibling, incoming]);
    Assert(!lockedTarget.IsLocked && !incoming.IsLocked,
        "Unlocking any active tab must unlock the complete group before switching tabs.");

    var activeTab = new FenceConfig
    {
        Left = 120,
        Top = 80,
        Width = 420,
        Height = 360,
        ExpandedHeight = 360,
        IsCollapsed = true,
        EdgeDock = "Top"
    };
    var otherTab = new FenceConfig
    {
        Left = 10,
        Top = 20,
        Width = 240,
        Height = 200,
        ExpandedHeight = 200,
        IsCollapsed = false
    };

    MainWindow.SynchronizeTabGroupPresentationState(activeTab, [activeTab, otherTab]);
    Assert(otherTab.IsCollapsed &&
           otherTab.EdgeDock == "Top" &&
           otherTab.ExpandedHeight == 360 &&
           otherTab.Left == 120 &&
           otherTab.Top == 80 &&
           otherTab.Width == 420 &&
           otherTab.Height == 360,
        "Every tab in a group must share the active tab's roll-up and layout state.");

    activeTab.IsCollapsed = false;
    activeTab.EdgeDock = null;
    activeTab.ExpandedHeight = 400;
    MainWindow.SynchronizeTabGroupPresentationState(activeTab, [activeTab, otherTab]);
    Assert(!otherTab.IsCollapsed &&
           otherTab.EdgeDock == null &&
           otherTab.ExpandedHeight == 400,
        "Expanding one tab must expand the complete tab group.");
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
    Assert(MainWindow.IsPointInMergeZone(new System.Windows.Point(450, 94), target, 10),
        "A dragged header visually overlapping the target edge should use a small vertical tolerance.");
    Assert(!MainWindow.IsPointInMergeZone(new System.Windows.Point(450, 88), target, 10),
        "The merge tolerance must not accept a visibly separated header.");

    var config = new AppConfig
    {
        Fences =
        [
            new FenceConfig { Id = "removed", TabGroupId = null },
            new FenceConfig { Id = "remaining", TabGroupId = "group", Width = 500, Height = 500, PreTabWidth = 320, PreTabHeight = 280 }
        ]
    };
    Assert(MainWindow.DissolveSingleItemTabGroup(config, "group"), "A one-item tab group should be dissolved.");
    Assert(config.Fences[1].TabGroupId == null, "The remaining Fence should no longer expose tab-group actions.");
    Assert(config.Fences[1].Width == 320 && config.Fences[1].Height == 280,
        "A Fence leaving a tab group should regain its pre-merge size.");

    var firstTab = new FenceConfig { Id = "tab-a", TabGroupId = "shared-tabs", PageIndex = 0 };
    var secondTab = new FenceConfig { Id = "tab-b", TabGroupId = "shared-tabs", PageIndex = 0 };
    var independent = new FenceConfig { Id = "single", PageIndex = 0 };
    var pageConfig = new AppConfig { Fences = [firstTab, secondTab, independent] };
    var groupMoveTargets = MainWindow.GetFencePageMoveTargets(pageConfig, firstTab);
    Assert(groupMoveTargets.Count == 2 &&
           groupMoveTargets.Contains(firstTab) &&
           groupMoveTargets.Contains(secondTab) &&
           !groupMoveTargets.Contains(independent),
        "Changing the page of a merged Fence must move the complete tab group without turning page changes into tab switches.");
    Assert(MainWindow.GetFencePageMoveTargets(pageConfig, independent).SequenceEqual([independent]),
        "Changing the page of an independent Fence must affect only that Fence.");
    Assert(MainWindow.GetDirectPageIndex(0x70) == 0 &&
           MainWindow.GetDirectPageIndex(0x71) == 1 &&
           MainWindow.GetDirectPageIndex(0x7B) == 11 &&
           MainWindow.GetDirectPageIndex(0x6F) == -1,
        "F1-F12 must map directly to pages 1-12 and reject unrelated keys.");
}

static void TestCustomGridAndPageHotkeys()
{
    var snapped = FenceControl.SnapPositionToGrid(new System.Windows.Point(23, 27), 10);
    Assert(snapped.X == 20 && snapped.Y == 30,
        "A custom grid size must control Fence position snapping.");
    Assert(MainWindow.ParseHotkeyKey("F1") == 0x70 &&
           MainWindow.ParseHotkeyKey("F12") == 0x7B &&
           MainWindow.ParseHotkeyKey("MouseLeft") == 0x01 &&
           MainWindow.ParseHotkeyKey("MouseRight") == 0x02 &&
           MainWindow.ParseHotkeyKey("F13") == 0,
        "Custom shortcuts must parse function keys and explicit mouse buttons while rejecting unsupported function keys.");
    Assert(MainWindow.ShouldApplyForegroundShortcutContext(7, 7, new IntPtr(100), new IntPtr(100)) &&
           !MainWindow.ShouldApplyForegroundShortcutContext(6, 7, new IntPtr(100), new IntPtr(100)) &&
           !MainWindow.ShouldApplyForegroundShortcutContext(7, 7, new IntPtr(100), new IntPtr(200)),
        "A delayed foreground notification must not overwrite a newer mouse-derived desktop shortcut context.");
    Assert(MainWindow.IsMouseTransparentOverlay(0x00080020L) &&
           MainWindow.IsMouseTransparentOverlay(0x080800A0L) &&
           !MainWindow.IsMouseTransparentOverlay(0x00080000L) &&
           !MainWindow.IsMouseTransparentOverlay(0x00000020L) &&
           !MainWindow.IsMouseTransparentOverlay(0x08000080L) &&
           !MainWindow.IsMouseTransparentOverlay(0),
        "Only layered click-through overlays may be skipped; ordinary, layered interactive, and no-activate windows must still block desktop shortcuts.");
    Assert(MainWindow.ShouldSkipNvidiaOverlay("NVIDIA Share", 0x8080080, true, false) &&
           !MainWindow.ShouldSkipNvidiaOverlay("NVIDIA Share", 0x8080080, true, true) &&
           !MainWindow.ShouldSkipNvidiaOverlay("NVIDIA Share", 0x8080080, false, false) &&
           !MainWindow.ShouldSkipNvidiaOverlay("Other application", 0x8080080, true, false) &&
           !MainWindow.ShouldSkipNvidiaOverlay("NVIDIA Share", 0x80000, true, false),
        "The reported NVIDIA HUD may be skipped only when native hit testing finds another window; interactive overlays and unknown hits must block shortcuts.");
}

static void TestManualDropOwnership()
{
    var target = new FenceConfig { Id = "a", Kind = FenceConfig.DesktopGroupKind };
    var other = new FenceConfig { Id = "b", Kind = FenceConfig.DesktopGroupKind };
    var config = new AppConfig { Fences = [target, other] };
    const string path = @"C:\Desktop\Dropped.txt";
    Assert(MainWindow.ShouldAutomaticallyAssignDesktopItem(config, path),
        "New unassigned desktop items must remain eligible for automatic classification.");
    // The watcher queued Created before the asynchronous manual drop assigned A.
    target.AssignedPaths.Add(path);
    Assert(!MainWindow.ShouldAutomaticallyAssignDesktopItem(config, path.ToLowerInvariant()),
        "A delayed Created notification must not move a manually assigned item from A to B.");
    target.AssignedPaths.Clear();
    other.AssignedPaths.Add(path);
    Assert(!MainWindow.ShouldAutomaticallyAssignDesktopItem(config, path),
        "Duplicate watcher notifications must preserve existing ownership in any Fence.");
    other.AssignedPaths.Clear();
    Assert(MainWindow.ShouldAutomaticallyAssignDesktopItem(config, path),
        "Released items must not be suppressed permanently.");
}

static void TestHeaderDragEdgeTracking()
{
    var position = FenceControl.ClampHeaderDragPosition(
        new System.Windows.Point(900, 100),
        new System.Windows.Size(1000, 700),
        new System.Windows.Size(360, 300));
    Assert(position.X == 640 && position.Y == 100,
        "An ordinary Fence must remain completely inside the desktop while it is dragged.");

    var compactPosition = FenceControl.ClampHeaderDragPosition(
        new System.Windows.Point(900, 100),
        new System.Windows.Size(1000, 700),
        new System.Windows.Size(360, 300),
        new System.Windows.Rect(60, 0, 180, 34));
    Assert(compactPosition.X == 760 && compactPosition.Y == 100,
        "A compact merge preview should move until its visible title reaches the edge, not until its hidden full width does.");

    var taskbarSafePosition = FenceControl.ClampHeaderDragPosition(
        new System.Windows.Point(900, 700),
        new System.Windows.Rect(0, 0, 1000, 660),
        new System.Windows.Size(360, 300));
    Assert(taskbarSafePosition.X == 640 && taskbarSafePosition.Y == 360,
        "Dragging must keep the Fence footer above the taskbar work-area boundary.");

    var taskbarSafeCompactPosition = FenceControl.ClampHeaderDragPosition(
        new System.Windows.Point(900, 700),
        new System.Windows.Rect(0, 0, 1000, 660),
        new System.Windows.Size(360, 300),
        new System.Windows.Rect(60, 0, 180, 34));
    Assert(taskbarSafeCompactPosition.X == 760 && taskbarSafeCompactPosition.Y == 626,
        "The compact merge title must remain movable to a screen edge while staying above the taskbar.");
}

static void TestTaskbarWorkAreaClamp()
{
    var position = MainWindow.ClampFencePositionToWorkArea(
        new System.Windows.Point(1500, 950),
        new System.Windows.Size(360, 300),
        new System.Windows.Rect(0, 0, 1920, 1040));
    Assert(position.X == 1500 && position.Y == 740,
        "Dropping a Fence over a bottom taskbar must move it back above the taskbar.");

    var leftTaskbarPosition = MainWindow.ClampFencePositionToWorkArea(
        new System.Windows.Point(0, 100),
        new System.Windows.Size(360, 300),
        new System.Windows.Rect(48, 0, 1872, 1080));
    Assert(leftTaskbarPosition.X == 48,
        "The usable work-area clamp must also respect a taskbar docked to a side.");

    var usableArea = new System.Windows.Rect(0, 0, 1920, 1040);
    Assert(MainWindow.GetAutoRollupEdge(new System.Windows.Rect(200, 5, 360, 300), usableArea) == "Top",
        "Top-edge roll-up must not depend on grid snapping.");
    Assert(MainWindow.GetAutoRollupEdge(new System.Windows.Rect(200, 740, 360, 300), usableArea) == "Bottom",
        "Bottom-edge roll-up must use the taskbar-safe work-area boundary.");
    Assert(MainWindow.GetAutoRollupEdge(
               new System.Windows.Rect(200, 740, 360, 300), usableArea, allowBottom: false) == null,
        "Bottom-edge roll-up must be independently disableable.");
    Assert(MainWindow.GetAutoRollupEdge(new System.Windows.Rect(200, 300, 360, 300), usableArea) == null,
        "A Fence away from an edge must remain expanded.");
    Assert(!FenceControl.ShouldUndockDuringHeaderDrag(
               "Bottom", new System.Windows.Rect(200, 740, 360, 300), usableArea) &&
           FenceControl.ShouldUndockDuringHeaderDrag(
               "Bottom", new System.Windows.Rect(200, 720, 360, 300), usableArea),
        "A bottom-docked Fence must keep its inverted layout at the edge and restore its ordinary layout after being dragged clear.");
    Assert(FenceControl.ShouldUndockDuringHeaderDrag(
               "Top", new System.Windows.Rect(200, 20, 360, 300), usableArea),
        "Dragging a top-docked Fence away from the edge must release its docked state.");
    Assert(FenceControl.GetFenceContextMenuPlacement("Bottom") ==
               System.Windows.Controls.Primitives.PlacementMode.Top &&
           FenceControl.GetFenceContextMenuPlacement("Top") ==
               System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        "A bottom-docked Fence menu must open upward instead of entering the taskbar.");
    Assert(FenceControl.GetDockedFenceTop("Bottom", 34, usableArea) == 1006 &&
           FenceControl.GetDockedFenceTop("Bottom", 300, usableArea) == 740,
        "Bottom docking must use the taskbar-safe work-area bottom for both rolled-up and expanded layouts.");
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
        GridSize = 24,
        SnapWhileDragging = true,
        AllowBottomEdgeRollup = false,
        BottomDockTitleAtBottom = false,
        TopDockTitleAtBottomOnExpand = true,
        DirectPageHotkeys = ["Ctrl+1", "Ctrl+2", "", "", "", "", "", "", "", "", "", ""],
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
                ShowOnAllPages = true,
                ContentLinkId = "shared-content-test",
                Left = 12,
                Top = 34,
                Width = 300,
                Height = 260,
                ExpandedHeight = 340,
                BackgroundColor = "#CC112233",
                HeaderColor = "#AA445566",
                HeaderGradientEnabled = true,
                HeaderGradientColor = "#AA778899",
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
    Assert(loaded.GridSize == 24 && loaded.SnapWhileDragging,
        "Config should restore the custom grid size and real-time snapping mode.");
    Assert(!loaded.AllowBottomEdgeRollup &&
           !loaded.BottomDockTitleAtBottom && loaded.TopDockTitleAtBottomOnExpand,
        "Config should restore the selectable bottom-dock title behavior.");
    Assert(loaded.DirectPageHotkeys[0] == "Ctrl+1" && loaded.DirectPageHotkeys[1] == "Ctrl+2" &&
           string.IsNullOrEmpty(loaded.DirectPageHotkeys[2]),
        "Config should restore customized and disabled direct-page shortcuts.");
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
    Assert(loaded.Fences[0].ShowOnAllPages && loaded.Fences[0].ContentLinkId == "shared-content-test",
        "Config should persist all-page visibility and synchronized-content identity.");
    Assert(loaded.Fences[1].PageIndex == 0, "Config should restore other Fence page.");
    Assert(!string.Equals(loaded.Fences[0].Id, loaded.Fences[1].Id, StringComparison.OrdinalIgnoreCase), "Config should repair duplicate Fence ids.");
    Assert(loaded.Fences[1].Width == 240 && loaded.Fences[1].Height == 180, "Config should clamp Fence size to usable minimums.");
    Assert(Math.Abs(loaded.Fences[1].Opacity - 0.1) < 0.01, "Config should preserve low numeric Fence opacity.");
    Assert(Math.Abs(loaded.Fences[0].Left - 12) < 0.01, "Config should restore left position.");
    Assert(loaded.Fences[0].BackgroundColor == "#CC112233", "Config should restore Fence background color.");
    Assert(loaded.Fences[0].HeaderColor == "#AA445566", "Config should restore Fence header color.");
    Assert(loaded.Fences[0].HeaderGradientEnabled &&
           loaded.Fences[0].HeaderGradientColor == "#AA778899" &&
           FenceAppearanceBrush.CreateHeaderBrush(loaded.Fences[0]) is System.Windows.Media.LinearGradientBrush,
        "Config and rendering should preserve the optional title-bar gradient.");
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
    Assert(service.GetAutomaticBackupPaths().Count == 20,
        "Automatic full-config backup rotation must retain the newest 20 distinct versions.");

    var diagnosticsPath = Path.Combine(root, "diagnostics", "bundle.zip");
    DiagnosticBundleService.Create(diagnosticsPath, loaded,
    [
        new ActionTransaction
        {
            ActionType = "FileMove",
            DisplayName = "diagnostic move",
            Entries = [new ActionEntry { SourcePath = Path.Combine(folder, "private", "source.txt") }]
        }
    ]);
    using (var diagnostics = ZipFile.OpenRead(diagnosticsPath))
    {
        Assert(diagnostics.GetEntry("system.txt") != null &&
               diagnostics.GetEntry("config-redacted.json") != null &&
               diagnostics.GetEntry("history-redacted.json") != null,
            "The diagnostic bundle must include system, redacted config, and redacted history evidence.");
        using var reader = new StreamReader(diagnostics.GetEntry("history-redacted.json")!.Open());
        var redactedHistory = reader.ReadToEnd();
        Assert(!redactedHistory.Contains(Path.Combine(folder, "private"), StringComparison.OrdinalIgnoreCase) &&
               redactedHistory.Contains("…", StringComparison.Ordinal),
            "Diagnostic history must not expose full user paths.");
    }

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
    Assert(MainWindow.IsDesktopBlankClick(true, false, false, true) &&
           !MainWindow.IsDesktopBlankClick(true, false, true, true) &&
           !MainWindow.IsDesktopBlankClick(true, true, false, true),
        "The global mouse hook must not clear loose-icon selection when the click is on that desktop icon.");
    Assert(MainWindow.ShouldClearSelectionForGlobalClick(false, false, false) &&
           MainWindow.ShouldClearSelectionForGlobalClick(false, true, false) &&
           !MainWindow.ShouldClearSelectionForGlobalClick(false, true, true) &&
           !MainWindow.ShouldClearSelectionForGlobalClick(true, false, false),
        "Clicks in another window or blank desktop must clear selection, while real MiniFences/desktop items must retain it.");
    Assert(!MainWindow.IsDesktopBlankClick(true, false, true, true),
        "A managed loose desktop icon hit must never be reclassified as blank desktop by the global mouse hook.");
    Assert(MainWindow.IsPointInsideLooseIconBounds(new System.Windows.Point(50, 60), 8, 8, 86, 92) &&
           !MainWindow.IsPointInsideLooseIconBounds(new System.Windows.Point(120, 60), 8, 8, 86, 92),
        "The global click classifier must recognize the WPF loose icon's own workspace bounds.");
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
            },
            new FenceConfig
            {
                Id = "global",
                Title = "Global",
                FolderPath = folder,
                PageIndex = 1,
                ShowOnAllPages = true
            }
        ]
    };

    Assert(PageService.TryDeleteEmptyPage(config, 1, out var deleteError), $"Empty page should delete: {deleteError}");
    Assert(config.PageCount == 3, "Deleting an empty page should reduce page count.");
    Assert(config.CurrentPage == 0, "Deleting the current page should move selection to a valid page.");
    Assert(config.Fences.Single(fence => fence.Id == "third").PageIndex == 1, "Fences after the deleted page should shift left.");
    Assert(config.Fences.Single(fence => fence.Id == "global").PageIndex == 1,
        "An all-page Fence must not block page deletion and must retain a valid base page.");
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
    System.Windows.Media.ImageSource? loadedShellIcon = null;
    var iconLoad = service.LoadIconsAsync([assignedOrder[0]], (_, icon) => loadedShellIcon = icon, CancellationToken.None);
    Assert(Task.WhenAny(iconLoad, Task.Delay(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult() == iconLoad,
        "Background Shell icon loading must not leave the settings item list on placeholders indefinitely.");
    iconLoad.GetAwaiter().GetResult();
    Assert(loadedShellIcon is not null,
        "Background Shell icon loading must replace the settings placeholder with a real file icon.");
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
    var versionedArchivePath = Path.Combine(source, "MiniFences-0.23.12.zip");
    File.WriteAllText(versionedArchivePath, "zip-test");
    var versionedArchive = new FolderItem { Name = "MiniFences-0.23.12", FullPath = versionedArchivePath };
    Assert(service.TryRenameItem(versionedArchive, "MiniFences-0.23.13", out var renamedArchivePath, out renameError) &&
           renamedArchivePath != null && renamedArchivePath.EndsWith("MiniFences-0.23.13.zip", StringComparison.OrdinalIgnoreCase) &&
           File.Exists(renamedArchivePath),
        "Renaming a versioned file label must preserve its hidden .zip extension; version dots are not an extension.");

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

    var copySource = Path.Combine(source, "copy-source");
    Directory.CreateDirectory(Path.Combine(copySource, "nested"));
    File.WriteAllText(Path.Combine(copySource, "nested", "payload.txt"), "verified copy");
    var transferProgress = new List<FolderTransferProgress>();
    EventHandler<FolderTransferProgress> progressHandler = (_, progress) => transferProgress.Add(progress);
    FolderItemService.TransferProgressChanged += progressHandler;
    var copyResult = service.CopyIntoFolder([copySource], destination);
    FolderItemService.TransferProgressChanged -= progressHandler;
    var copiedFolder = Path.Combine(destination, "copy-source");
    Assert(copyResult.Moved == 1 && Directory.Exists(copySource) &&
           File.ReadAllText(Path.Combine(copiedFolder, "nested", "payload.txt")) == "verified copy" &&
           !Directory.EnumerateFileSystemEntries(destination, "*.partial", SearchOption.AllDirectories).Any(),
        "Copy drops must preserve the source and publish only a complete verified destination, never a partial folder.");
    Assert(transferProgress.Any(progress => progress.Stage == "Starting") &&
           transferProgress.Any(progress => progress.Stage == "Transferring" && progress.TotalBytes > 0) &&
           transferProgress.Any(progress => progress.Stage == "Completed"),
        "Verified transfers must publish visible start, item progress, and completion states.");

    var cancelSource = Path.Combine(source, "cancel-large.bin");
    using (var cancelFile = new FileStream(cancelSource, FileMode.CreateNew, FileAccess.Write))
        cancelFile.SetLength(8L * 1024 * 1024);
    EventHandler<FolderTransferProgress>? cancelHandler = null;
    cancelHandler = (_, progress) =>
    {
        if (progress.CurrentPath == cancelSource && progress.CompletedBytes > 0)
            FolderItemService.CancelActiveTransfers();
    };
    FolderItemService.TransferProgressChanged += cancelHandler;
    var canceledResult = service.CopyIntoFolder([cancelSource], destination);
    FolderItemService.TransferProgressChanged -= cancelHandler;
    Assert(canceledResult.Errors.Count > 0 && File.Exists(cancelSource) &&
           !File.Exists(Path.Combine(destination, "cancel-large.bin")) &&
           !Directory.EnumerateFileSystemEntries(destination)
               .Any(path => Path.GetFileName(path).Contains(".partial", StringComparison.OrdinalIgnoreCase)),
        "Canceling a byte-progress transfer must preserve the source and remove its incomplete staging output.");

    Assert(FolderItemService.TryAssessDangerousMove(
        [new FolderTransferPlanEntry(copySource, copiedFolder, FolderTransferOperation.Move,
            Path.Combine(source, ".copy-source.minifences-move-test.source"), null)], out var danger) &&
           danger.CrossVolume,
        "Cross-volume moves must require an explicit preflight confirmation before any file-system change.");

    var linkSource = Path.Combine(source, "linked-folder");
    Directory.CreateDirectory(linkSource);
    var linkResult = service.CreateShortcutsIntoFolder([linkSource], destination);
    Assert(linkResult.Moved == 1 && Directory.Exists(linkSource) &&
           linkResult.MovedPaths.Count == 1 && File.Exists(linkResult.MovedPaths[0]) &&
           string.Equals(Path.GetExtension(linkResult.MovedPaths[0]), ".lnk", StringComparison.OrdinalIgnoreCase),
        "Link drops must create a .lnk while leaving the target folder untouched.");

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
    Assert(moveResult.MovedPaths.Contains(Path.Combine(destination, "batch-one.txt"), StringComparer.OrdinalIgnoreCase) &&
           moveResult.MovedPaths.Contains(Path.Combine(destination, "batch-two.txt"), StringComparer.OrdinalIgnoreCase),
        "Move results must expose destination paths so restored desktop items can be assigned to a Fence.");

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
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
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
            Assert(!control.IsPortalNavigationVisibleForTesting, "Folder Portal root should keep the redundant navigation bar hidden.");
            var previewDropSource = Path.Combine(root, "preview-drop-source.txt");
            var previewDropTarget = Path.Combine(folder, "preview-drop-source.txt");
            File.WriteAllText(previewDropSource, "preview drop routing");
            var previewDropData = new System.Windows.DataObject();
            previewDropData.SetData(System.Windows.DataFormats.FileDrop, new[] { previewDropSource });
            var itemsList = (System.Windows.Controls.ListView)control.FindName("ItemsList");
            control.Measure(new System.Windows.Size(420, 320));
            control.Arrange(new System.Windows.Rect(0, 0, 420, 320));
            control.UpdateLayout();
            var dragEventConstructor = typeof(System.Windows.DragEventArgs)
                .GetConstructors(System.Reflection.BindingFlags.Instance |
                                 System.Reflection.BindingFlags.NonPublic |
                                 System.Reflection.BindingFlags.Public)
                .Single();
            var previewDropEvent = (System.Windows.DragEventArgs)dragEventConstructor.Invoke([
                previewDropData,
                System.Windows.DragDropKeyStates.LeftMouseButton,
                System.Windows.DragDropEffects.Move,
                itemsList,
                new System.Windows.Point(250, 160)
            ]);
            previewDropEvent.RoutedEvent = System.Windows.DragDrop.PreviewDropEvent;
            var previewDropHandler = typeof(FenceControl).GetMethod(
                "FenceControl_PreviewDrop",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            previewDropHandler!.Invoke(control, [itemsList, previewDropEvent]);
            var previewDropCompleted = PumpDispatcherUntil(
                () => File.Exists(previewDropTarget) && !File.Exists(previewDropSource) &&
                      control.LoadedItemsForTesting.Any(item =>
                          string.Equals(item.FullPath, previewDropTarget, StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5));
            Assert(previewDropCompleted &&
                   control.LoadedItemsForTesting.Any(item =>
                       string.Equals(item.FullPath, previewDropTarget, StringComparison.OrdinalIgnoreCase)) &&
                   previewDropEvent.Handled,
                $"The Fence preview-drop handler must accept nested ListView file data and move it into the target folder. " +
                $"target={File.Exists(previewDropTarget)}, source={File.Exists(previewDropSource)}, handled={previewDropEvent.Handled}, effects={previewDropEvent.Effects}");
            var tabDragPreview = control.CreateTabDragPreviewForTesting();
            Assert(FenceControl.GetTabDragPreviewFenceForTesting(tabDragPreview) is FenceControl
                   {
                       Width: > 0,
                       Height: > 0,
                       IsHitTestVisible: false
                   } previewFence &&
                   previewFence.LoadedItemsForTesting.Count == control.LoadedItemsForTesting.Count,
                "Detached-tab dragging must create a real non-interactive Fence preview with its items and icons loaded.");
            tabDragPreview.Close();
            control.NavigatePortalForTesting(childFolder);
            Assert(string.Equals(control.PortalPathForTesting, childFolder, StringComparison.OrdinalIgnoreCase),
                "Opening a subfolder inside a Folder Portal must navigate in place.");
            control.NavigatePortalUpForTesting();
            Assert(string.Equals(control.PortalPathForTesting, folder, StringComparison.OrdinalIgnoreCase),
                "Folder Portal Up must return to its configured root without escaping it.");

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
            Assert(desktopGroup.VerticalScrollBarVisibilityForTesting == System.Windows.Controls.ScrollBarVisibility.Auto,
                "Fence scrollbar must keep a stable layout slot so hover does not reflow the icon grid.");
            Assert(!desktopGroup.IsResizeHandleVisibleForTesting, "Resize handle should stay hidden until pointer hover.");
            desktopGroup.SetMergePreview(true);
            Assert(desktopGroup.IsMergePreviewVisibleForTesting,
                "Hovering the middle merge zone must restore the visible target preview while the dragged Fence compacts.");
            desktopGroup.SetMergePreview(false);
            Assert(!desktopGroup.IsMergePreviewVisibleForTesting,
                "Leaving the merge zone must clear the target merge preview.");
            Assert(!FenceControl.IsNearResizeHandle(new System.Windows.Point(180, 120), 360, 420, handleAtTop: false),
                "Pointer in the middle of a Fence should not reveal the resize handle.");
            Assert(FenceControl.IsNearResizeHandle(new System.Windows.Point(350, 410), 360, 420, handleAtTop: false),
                "Pointer near the bottom-right corner should reveal the resize handle.");
            Assert(FenceControl.IsNearResizeHandle(new System.Windows.Point(350, 10), 360, 420, handleAtTop: true),
                "A bottom-docked Fence should reveal its resize handle near the top-right corner.");
            var renamedAssignedPath = Path.Combine(folder, "renamed-test.txt");
            Assert(FenceControl.ReplaceAssignedPathAfterRename(desktopGroupConfig, filePath, renamedAssignedPath) &&
                   desktopGroupConfig.AssignedPaths.SequenceEqual([renamedAssignedPath], StringComparer.OrdinalIgnoreCase),
                "Renaming an assigned desktop item must preserve its Fence membership using the new path.");
            desktopGroup.SetTabStatus(3, 1);
            Assert(desktopGroup.IsTabNavigationVisibleForTesting, "Stacked Fences should expose direct previous/next tab navigation.");
            Assert(desktopGroup.TabPickerItemCountForTesting == 3,
                "Clicking the compact page indicator must offer every tab for direct selection.");
            var originalGroupId = desktopGroupConfig.TabGroupId;
            desktopGroupConfig.TabGroupId = "compact-detach-test";
            var detachRequests = 0;
            desktopGroup.UnstackRequested += (_, _) => detachRequests++;
            var picker = desktopGroup.BuildTabPickerMenuForTesting();
            var detachCommand = picker.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => !item.IsCheckable);
            detachCommand.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Assert(detachRequests == 1, "Compact picker detach must invoke the active Fence's unstack action exactly once.");
            desktopGroupConfig.TabGroupId = null;
            Assert(desktopGroup.BuildTabPickerMenuForTesting().Items.OfType<System.Windows.Controls.MenuItem>().All(item => item.IsCheckable),
                "Standalone Fences must not offer a detach action.");
            desktopGroupConfig.TabGroupId = originalGroupId;
            Assert(desktopGroup.CompactTabNavigationExcludesRollupForTesting,
                "Rapid clicks on compact tab arrows and their page indicator must never be interpreted as title-bar roll-up double-clicks.");
            desktopGroup.SetTabStatus(3, 1, ["One", "Two", "Three"], useTabStrip: true, equalTabWidths: false);
            Assert(desktopGroup.TabStripHandlesRollupForTesting,
                "Double-clicking a full-width title tab should use the configured title-bar roll-up behavior.");
            Assert(desktopGroup.AreTabTitlesCenteredForTesting,
                "Tab titles should fill their tab and center text horizontally.");
            Assert(desktopGroup.TabColumnWidthsForTesting.Count == 4 &&
                   desktopGroup.TabColumnWidthsForTesting.Take(3).All(width => width.IsAuto) &&
                   desktopGroup.TabColumnWidthsForTesting[3].IsStar,
                "Content-width tabs should use automatic columns and leave the remaining title bar flexible.");
            desktopGroup.SetTabStatus(3, 1, ["One", "Two", "Three"], useTabStrip: true, equalTabWidths: true);
            Assert(desktopGroup.TabColumnWidthsForTesting.Count == 3 &&
                   desktopGroup.TabColumnWidthsForTesting.All(width => width.IsStar),
                "Equal-width tabs should divide the complete title bar into equal columns.");
            desktopGroup.SetTabStatus(3, 1, ["程序和快捷方式", "游戏", "工作区"], useTabStrip: true,
                adaptiveTabWidths: true);
            Assert(desktopGroup.TabColumnWidthsForTesting.Count == 3 &&
                   desktopGroup.TabColumnWidthsForTesting.All(width => width.IsStar) &&
                   desktopGroup.TabColumnWidthsForTesting[0].Value > desktopGroup.TabColumnWidthsForTesting[1].Value &&
                   FenceControl.GetAdaptiveTabWeight("程序和快捷方式") > FenceControl.GetAdaptiveTabWeight("游戏"),
                "Adaptive tabs should fill the title bar while assigning more width to longer titles.");
            desktopGroup.SetTabStatus(3, 0, ["One", "Two", "Three"], useTabStrip: true,
                equalTabWidths: true, selectionStyle: "Underline");
            Assert(desktopGroup.SelectedTabBorderThicknessForTesting.Bottom == 2 &&
                   desktopGroup.SelectedTabBorderThicknessForTesting.Top == 0 &&
                   desktopGroup.SelectedTabBackgroundForTesting.A == 48,
                "Underline selection should keep the normal tab background and draw only a bottom accent line.");
            desktopGroup.SetTabStatus(3, 0, ["One", "Two", "Three"], useTabStrip: true,
                equalTabWidths: true, selectionStyle: "DoubleLine",
                selectionLineColor: "Gold", selectionLineThickness: "Thick");
            Assert(desktopGroup.SelectedTabBorderThicknessForTesting.Top == 3 &&
                   desktopGroup.SelectedTabBorderThicknessForTesting.Bottom == 3 &&
                   desktopGroup.SelectedTabBorderColorForTesting == FenceControl.GetSelectionLineColor("Gold"),
                "Double-line selection should apply the chosen color and preset thickness to both edges.");
            Assert(FenceControl.NormalizeTabSelectionLineColor("#7F123456") == "#7F123456" &&
                   FenceControl.GetSelectionLineColor("#7F123456").A == 0x7F,
                "Tab accent colors should preserve arbitrary colors and opacity from the color picker.");
            desktopGroup.SetTabStatus(3, 0, ["One", "Two", "Three"], useTabStrip: true,
                equalTabWidths: true, selectionStyle: "Fill");
            Assert(desktopGroup.SelectedTabBackgroundForTesting.A == 210 &&
                   desktopGroup.SelectedTabBorderThicknessForTesting == new System.Windows.Thickness(0),
                "Fill selection should preserve the original white selected-tab appearance.");
            var dragTabs = new[]
            {
                new FenceConfig { Id = "drag-one" },
                new FenceConfig { Id = "drag-two" },
                new FenceConfig { Id = "drag-three" }
            };
            desktopGroup.SetTabStatus(3, 1, ["One", "Two", "Three"], useTabStrip: true,
                equalTabWidths: true, tabConfigs: dragTabs);
            desktopGroup.Measure(new System.Windows.Size(420, 320));
            desktopGroup.Arrange(new System.Windows.Rect(0, 0, 420, 320));
            desktopGroup.UpdateLayout();
            var tabStrip = (System.Windows.Controls.Grid)desktopGroup.FindName("TabStripPanel");
            var reorderTarget = tabStrip.Children.OfType<System.Windows.Controls.Border>().ElementAt(2);
            (int From, int To)? requestedReorder = null;
            desktopGroup.TabReorderRequested += (from, to) => requestedReorder = (from, to);
            var tabDropData = new System.Windows.DataObject();
            tabDropData.SetData("MiniFences.TabFenceId", "drag-one");
            tabDropData.SetData("MiniFences.TabIndex", 0);
            var tabPreviewDropEvent = (System.Windows.DragEventArgs)dragEventConstructor.Invoke([
                tabDropData,
                System.Windows.DragDropKeyStates.LeftMouseButton,
                System.Windows.DragDropEffects.Move,
                reorderTarget,
                new System.Windows.Point(10, 10)
            ]);
            tabPreviewDropEvent.RoutedEvent = System.Windows.DragDrop.PreviewDropEvent;
            reorderTarget.RaiseEvent(tabPreviewDropEvent);
            Assert(!tabPreviewDropEvent.Handled,
                "The outer Fence preview-drop handler must leave a tab-strip drop for the target tab.");
            var tabDropEvent = (System.Windows.DragEventArgs)dragEventConstructor.Invoke([
                tabDropData,
                System.Windows.DragDropKeyStates.LeftMouseButton,
                System.Windows.DragDropEffects.Move,
                reorderTarget,
                new System.Windows.Point(10, 10)
            ]);
            tabDropEvent.RoutedEvent = System.Windows.DragDrop.DropEvent;
            reorderTarget.RaiseEvent(tabDropEvent);
            Assert(requestedReorder == (0, 2) && tabDropEvent.Effects == System.Windows.DragDropEffects.Move,
                "Dropping a combined-Fence tab on another tab must request reordering instead of detaching it.");
            int? requestedItemTabSelection = null;
            desktopGroup.TabSelectedRequested += targetIndex => requestedItemTabSelection = targetIndex;
            var itemTabDropData = new System.Windows.DataObject();
            DesktopDragData.SetFileDropList(itemTabDropData, [filePath]);
            var itemTabDropEvent = (System.Windows.DragEventArgs)dragEventConstructor.Invoke([
                itemTabDropData,
                System.Windows.DragDropKeyStates.LeftMouseButton,
                System.Windows.DragDropEffects.Move,
                reorderTarget,
                new System.Windows.Point(10, 10)
            ]);
            itemTabDropEvent.RoutedEvent = System.Windows.DragDrop.DropEvent;
            reorderTarget.RaiseEvent(itemTabDropEvent);
            Assert(PumpDispatcherUntil(() => requestedItemTabSelection == 2, TimeSpan.FromSeconds(1)) &&
                   itemTabDropEvent.Effects == System.Windows.DragDropEffects.None,
                "Dragging files over an inactive combined-Fence tab must open that Fence without appending the files.");
            desktopGroup.HideTabForActiveDrag("drag-two");
            Assert(desktopGroup.VisibleTabCountForTesting == 3 &&
                   desktopGroup.TabColumnWidthsForTesting[1].Value > 0 &&
                   desktopGroup.IsTabDragSlotPreservedForTesting(1) &&
                   desktopGroup.FirstTabCornerRadiusForTesting.TopLeft == 8,
                "The dragged tab must leave a non-interactive browser-style insertion slot in the title strip.");
            desktopGroup.CollapseActiveTabDragSlotForTesting(true);
            Assert(desktopGroup.VisibleTabCountForTesting == 2 &&
                   desktopGroup.TabColumnWidthsForTesting[2].Value == 0 &&
                   desktopGroup.AreRemainingTabSlotsContiguousForTesting &&
                   desktopGroup.AreTabReorderTransformsResetForTesting,
                "Once a Shift-drag leaves the source title bar, its placeholder must collapse so the remaining tabs close the gap.");
            desktopGroup.CollapseActiveTabDragSlotForTesting(false);
            Assert(desktopGroup.VisibleTabCountForTesting == 3 &&
                   desktopGroup.IsTabDragSlotPreservedForTesting(1) &&
                   desktopGroup.AreTabReorderTransformsResetForTesting,
                "Returning the dragged tab to its source title bar must restore the insertion slot.");
            desktopGroup.HideTabForActiveDrag("drag-one");
            Assert(desktopGroup.FirstTabCornerRadiusForTesting.TopLeft == 8,
                "After the first tab is detached, the first remaining visible tab must inherit the Fence corner.");
            desktopGroup.SetTabStatus(3, 0, ["One", "Two", "Three"], useTabStrip: true,
                equalTabWidths: true, tabConfigs: dragTabs);
            desktopGroup.HideTabForActiveDrag("drag-one");
            desktopGroup.PreviewTabReorderForTesting(0, 2);
            Assert(desktopGroup.TabGridColumnsForTesting.SequenceEqual([2, 0, 1]),
                "Dragging the left tab over the right tab must move both remaining tabs left and place the insertion slot on the right, even without a child DragOver event.");
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
            control.UpdateLayout();
            Assert(control.ExpandedOverlayAllowsUnderlyingItemSelectionForTesting(),
                "An expanded long-name tile must be visual-only so items underneath remain selectable.");
            secondFence.SelectItemForTesting(0);
            MainWindow.ClearOtherFenceSelections([control, secondFence], secondFence);
            Assert(control.SelectedItemCountForTesting == 0 && secondFence.SelectedItemCountForTesting == 1,
                "Selecting an item in one Fence must clear item selections in every other Fence.");
            var borderBeforeDragHighlight = control.BorderThicknessForTesting;
            control.SetDragHighlightedForTesting();
            secondFence.SetDragHighlightedForTesting();
            Assert(control.BorderThicknessForTesting.Equals(borderBeforeDragHighlight),
                "Drag highlighting must be an overlay and must not shrink or reflow Fence contents.");
            MainWindow.ClearAllFenceDragHighlights([control, secondFence]);
            Assert(!control.IsDragHighlightedForTesting && !secondFence.IsDragHighlightedForTesting,
                "Canceling a drag must clear target highlighting from every Fence, including overlapping Fences.");
            secondFence.StopForTesting();

            var looseIcon = new DesktopLooseIconControl(new FolderItem
            {
                Name = "bound-folder",
                FullPath = childFolder,
                Kind = "Folder"
            });
            Assert(looseIcon.IsCompactNameTwoLinesForTesting,
                "An unselected loose desktop icon must reserve exactly two centered filename lines.");
            looseIcon.SetSelected(true);
            Assert(looseIcon.IsNameExpandedForTesting && looseIcon.HasSelectionChromeForTesting,
                "Selecting a loose desktop icon must expand its complete filename.");
            Assert(looseIcon.IsExpandedNameContainedForTesting,
                "Expanding a loose icon must grow one fixed-width selection tile around both the icon and complete filename.");
            looseIcon.SetSelected(true, expandName: false);
            Assert(looseIcon.IsCompactNameTwoLinesForTesting && looseIcon.HasSelectionChromeForTesting,
                "A selected icon in a multi-selection must keep the compact two-line label.");
            looseIcon.SetSelected(true);
            looseIcon.SetDraggingVisualForTesting(true);
            Assert(looseIcon.IsNameExpandedForTesting,
                "Starting a drag from an expanded icon must preserve its expanded geometry.");
            looseIcon.SetDraggingVisualForTesting(false);
            Assert(looseIcon.IsNameExpandedForTesting,
                "Canceling an expanded drag must restore exactly the pre-drag filename geometry.");
            looseIcon.SetSelected(false);
            looseIcon.SetDraggingVisualForTesting(true);
            Assert(looseIcon.IsCompactNameTwoLinesForTesting,
                "Starting an unexpanded drag must retain its compact two-line source label.");
            looseIcon.SetDraggingVisualForTesting(false);
            Assert(looseIcon.IsCompactNameTwoLinesForTesting,
                "Canceling an unexpanded drag must retain compact geometry.");
            looseIcon.SetSelected(true);
            Assert(DesktopLooseIconControl.WasActiveSelectionBeforePointerDown(true, true) &&
                   !DesktopLooseIconControl.WasActiveSelectionBeforePointerDown(true, false),
                "Returning from another window must require one click to reactivate selection before rename can start.");
            Assert(DesktopLooseIconControl.CanStartDragGesture(true, System.Windows.Input.MouseButtonState.Pressed) &&
                   !DesktopLooseIconControl.CanStartDragGesture(false, System.Windows.Input.MouseButtonState.Pressed) &&
                   !DesktopLooseIconControl.CanStartDragGesture(true, System.Windows.Input.MouseButtonState.Released),
                "A loose icon may start dragging only after its own mouse-down; Escape must not arm the icon under the cursor.");
            looseIcon.BeginInlineRenameForTesting();
            Assert(looseIcon.IsInlineRenamingForTesting,
                "A selected loose desktop icon should expose an inline text editor for renaming.");
            looseIcon.Measure(new System.Windows.Size(86, 200));
            looseIcon.Arrange(new System.Windows.Rect(0, 0, 86, looseIcon.DesiredSize.Height));
            Assert(looseIcon.IsRenameEditorCenteredForTesting,
                "The desktop inline rename editor and its text must be centered under the icon.");
            looseIcon.SetInlineRenameTextForTesting("a very long bound folder name that must wrap");
            Assert(looseIcon.IsRenameEditorCompactForTesting,
                "Desktop renaming must use only the wrapped height required to keep the complete name visible.");
            looseIcon.SetInlineRenameTextForTesting("bound-folder");
            looseIcon.CommitInlineRenameIfPointerOutside(new System.Windows.Point(-1000, -1000));
            Assert(!looseIcon.IsInlineRenamingForTesting,
                "Clicking outside an inline rename editor should commit it and restore the desktop icon label.");

            var noOpHistoryPath = Path.Combine(folder, "no-op-rename-history", "actions.json");
            var noOpHistory = new ActionHistoryService(noOpHistoryPath);
            var noOpRenameIcon = new DesktopLooseIconControl(new FolderItem
            {
                Name = "bound-folder",
                FullPath = childFolder,
                Kind = "Folder"
            }) { ActionHistory = noOpHistory };
            noOpRenameIcon.BeginInlineRenameForTesting();
            noOpRenameIcon.CommitInlineRenameIfPointerOutside(new System.Windows.Point(-1000, -1000));
            Assert(!noOpRenameIcon.IsInlineRenamingForTesting &&
                   noOpHistory.GetTransactions().Count == 0 &&
                   ActionHistoryService.PathsEqual(childFolder, childFolder),
                "Leaving a desktop rename unchanged must close immediately without touching the file system or action history.");

            var looseRenameSource = Path.Combine(folder, "loose-rename-source");
            var looseRenameTarget = Path.Combine(folder, "loose-rename-target");
            Directory.CreateDirectory(looseRenameSource);
            var renamedLooseIcon = new DesktopLooseIconControl(new FolderItem
            {
                Name = "loose-rename-source",
                FullPath = looseRenameSource,
                Kind = "Folder"
            });
            (string oldPath, string newPath)? renameEvent = null;
            renamedLooseIcon.ItemRenamed += (_, oldPath, newPath) => renameEvent = (oldPath, newPath);
            renamedLooseIcon.BeginInlineRenameForTesting();
            renamedLooseIcon.SetInlineRenameTextForTesting("loose-rename-target");
            renamedLooseIcon.CommitInlineRenameIfPointerOutside(new System.Windows.Point(-1000, -1000));
            Assert(Directory.Exists(looseRenameTarget) &&
                   !Directory.Exists(looseRenameSource) &&
                   string.Equals(renamedLooseIcon.Item.FullPath, looseRenameTarget, StringComparison.OrdinalIgnoreCase) &&
                   renamedLooseIcon.Item.Name == "loose-rename-target" &&
                   renameEvent is { } renamed &&
                   string.Equals(renamed.oldPath, looseRenameSource, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(renamed.newPath, looseRenameTarget, StringComparison.OrdinalIgnoreCase),
                "Renaming a loose desktop icon must retain its rendered item and expose the new path without a full desktop redraw.");

            var shortRenameEditor = new System.Windows.Controls.TextBox { FontSize = 12 };
            InlineRenameAppearance.Apply(shortRenameEditor, "tools");
            var longRenameEditor = new System.Windows.Controls.TextBox { FontSize = 12 };
            InlineRenameAppearance.Apply(longRenameEditor, "a very long desktop item name");
            Assert(shortRenameEditor.Width < longRenameEditor.Width &&
                   shortRenameEditor.Width >= InlineRenameAppearance.MinimumWidth &&
                   longRenameEditor.Width == InlineRenameAppearance.WrappedEditorWidth,
                "Inline rename width should compensate one-line labels and preserve the wrapped label column for long names.");
            Assert(shortRenameEditor.SelectionOpacity < 1 &&
                   shortRenameEditor.TextAlignment == System.Windows.TextAlignment.Center &&
                   shortRenameEditor.BorderThickness.Left == 1,
                "Inline rename must keep selected text readable and use a compact desktop-style border.");
            Assert(InlineRenameAppearance.MeasureWrappedHeight(longRenameEditor, "VMware Workstation Professional", InlineRenameAppearance.WrappedEditorWidth) >
                   InlineRenameAppearance.EditorHeight,
                "A long desktop item name should produce a rename editor taller than one line.");
            Assert(InlineRenameAppearance.MeasureWrappedHeight(longRenameEditor, "Workstation Pro", InlineRenameAppearance.WrappedEditorWidth) >
                   InlineRenameAppearance.EditorHeight,
                "A desktop item name that only slightly exceeds the editor width must still wrap to a second line.");
            Assert(InlineRenameAppearance.MeasureWrappedHeight(longRenameEditor, "VMware Workstation Pro", InlineRenameAppearance.WrappedEditorWidth) >
                   InlineRenameAppearance.MeasureWrappedHeight(longRenameEditor, "Workstation Pro", InlineRenameAppearance.WrappedEditorWidth),
                "Word-aware wrapping must allocate a third line for VMware Workstation Pro instead of scrolling its first word away.");
            Assert(InlineRenameAppearance.GetInitialSelectionLength(@"C:\Desktop\archive.zip", "archive") == "archive".Length &&
                   InlineRenameAppearance.GetInitialSelectionLength(@"C:\Desktop\MiniFences-0.23.12.zip", "MiniFences-0.23.12") == "MiniFences-0.23.12".Length,
                "A rename editor whose label hides the extension must select the complete visible base name without treating version dots as an extension.");
            var independentRenameWindow = new DesktopRenameWindow(
                "desktop-item",
                new System.Windows.Int32Rect(100, 100, 103, 25),
                InlineRenameAppearance.MaximumWidth,
                InlineRenameAppearance.EditorHeight);
            Assert(independentRenameWindow.WindowStyle == System.Windows.WindowStyle.None &&
                   !independentRenameWindow.ShowInTaskbar &&
                   independentRenameWindow.Topmost &&
                   independentRenameWindow.ShowActivated &&
                   independentRenameWindow.IsCenteredForTesting &&
                   !independentRenameWindow.CommitsOnWindowDeactivationForTesting &&
                   independentRenameWindow.Editor.TextWrapping == System.Windows.TextWrapping.Wrap &&
                   !independentRenameWindow.Editor.AcceptsReturn &&
                   independentRenameWindow.ContainsPhysicalScreenPoint(new System.Windows.Point(150, 120)) &&
                   !independentRenameWindow.ContainsPhysicalScreenPoint(new System.Windows.Point(50, 50)),
                "Desktop icon renaming must use a centered, independently activated editor window instead of activating the Explorer-hosted desktop surface.");
            var outsideClickDismissedRename = false;
            independentRenameWindow.CancelRequested = () => outsideClickDismissedRename = true;
            independentRenameWindow.TryCommitRequested = _ => throw new InvalidOperationException(
                "An unchanged outside-click rename must not execute a file-system rename.");
            independentRenameWindow.Show();
            Assert(!independentRenameWindow.RequestCommitIfPointerOutside(new System.Windows.Point(150, 120)) &&
                   independentRenameWindow.RequestCommitIfPointerOutside(new System.Windows.Point(50, 50)) &&
                   PumpDispatcherUntil(
                       () => outsideClickDismissedRename && !independentRenameWindow.IsVisible,
                       TimeSpan.FromSeconds(2)),
                "A click on non-activating desktop space must dismiss an unchanged rename without waiting for window deactivation.");
            Assert(!MainWindow.ShouldRepairDesktopLayerOnDeactivation(true) &&
                   MainWindow.ShouldRepairDesktopLayerOnDeactivation(false),
                "Opening the independent rename editor must not trigger a competing desktop z-order repair.");
            const long noActivateStyle = 0x08000000L;
            Assert(MainWindow.UpdateInlineRenameActivationStyle(noActivateStyle, enabled: true) == 0 &&
                   MainWindow.UpdateInlineRenameActivationStyle(0, enabled: false) == noActivateStyle,
                "Inline rename must temporarily enable window activation for keyboard input and restore it afterward.");
            Assert(MainWindow.ReorderFenceItems(["A", "B", "C", "D"], ["B"], 3)
                    .SequenceEqual(["A", "C", "B", "D"], StringComparer.OrdinalIgnoreCase) &&
                   MainWindow.ReorderFenceItems(["A", "B", "C"], ["C"], 0)
                    .SequenceEqual(["C", "A", "B"], StringComparer.OrdinalIgnoreCase),
                "Dragging Fence icons must persist their insertion order instead of snapping back to a fixed sort.");
            Assert(FenceControl.CanMoveIntoFolder([filePath], childFolder) &&
                   !FenceControl.CanMoveIntoFolder([childFolder], childFolder),
                "Folder icons should accept existing items but reject being dropped onto themselves.");
            Assert(FenceControl.IsFolderIconHotZone(new System.Windows.Point(21, 21), new System.Windows.Size(42, 42)) &&
                   !FenceControl.IsFolderIconHotZone(new System.Windows.Point(21, 70), new System.Windows.Size(42, 42)),
                "Only the folder image itself should accept file moves; its label and surrounding cell must remain available for manual ordering.");
            Assert(FenceControl.IsStableFolderDropZone(new System.Windows.Point(43, 25), new System.Windows.Size(86, 92)) &&
                   FenceControl.IsStableFolderDropZone(new System.Windows.Point(43, 78), new System.Windows.Size(86, 92)) &&
                   !FenceControl.IsStableFolderDropZone(new System.Windows.Point(90, 25), new System.Windows.Size(86, 92)),
                "A folder's full item cell must remain a stable target while the margin between item cells stays available for insertion.");

            var gridBounds = new[]
            {
                new System.Windows.Rect(0, 0, 86, 92),
                new System.Windows.Rect(94, 0, 86, 92),
                new System.Windows.Rect(0, 100, 86, 92),
                new System.Windows.Rect(94, 100, 86, 92)
            };
            Assert(FenceControl.GetGridInsertionIndex(gridBounds, new System.Windows.Point(400, 40)) == 2 &&
                   FenceControl.GetGridInsertionIndex(gridBounds, new System.Windows.Point(0, 140)) == 2 &&
                   FenceControl.GetGridInsertionIndex(gridBounds, new System.Windows.Point(20, 260)) == 4,
                "Dropping in right-side blank space must insert at the end of the nearest row, not at the end of the entire Fence.");

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

            var bottomDockedConfig = new FenceConfig
            {
                Width = 300,
                Height = 330,
                ExpandedHeight = 330,
                IsCollapsed = true,
                EdgeDock = "Bottom"
            };
            var bottomDockedControl = new FenceControl(bottomDockedConfig);
            Assert(bottomDockedControl.IsTitleAtBottomForTesting,
                "A bottom-docked Fence must keep its title bar at the bottom while rolled up.");
            bottomDockedControl.ToggleCollapsedForTesting();
            Assert(bottomDockedControl.IsTitleAtBottomForTesting &&
                   bottomDockedControl.IsResizeHandleAtTopForTesting &&
                   bottomDockedControl.ResizeGripOrientationForTesting == "Top" &&
                   !bottomDockedControl.IsCollapsedForTesting,
                "A bottom title must reveal content upward and mirror its resize grip into the upper-right corner.");
            bottomDockedControl.StopForTesting();

            var topDockedConfig = new FenceConfig
            {
                Width = 300,
                Height = 330,
                ExpandedHeight = 330,
                IsCollapsed = true,
                EdgeDock = "Top"
            };
            var topDockedControl = new FenceControl(topDockedConfig)
            {
                TopDockTitleAtBottomOnExpand = true
            };
            topDockedControl.ToggleCollapsedForTesting();
            Assert(topDockedControl.IsTitleAtBottomForTesting &&
                   topDockedControl.IsResizeHandleAtTopForTesting &&
                   !topDockedControl.IsCollapsedForTesting,
                "The mirrored top-dock option must expand downward with the title bar at the Fence bottom.");
            topDockedControl.StopForTesting();
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
