using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniFences.Models;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace MiniFences.Services;

public sealed class FolderItemService
{
    private const string RecycleBinClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";
    private static readonly SemaphoreSlim TransferGate = new(1, 1);
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> ActiveTransfers = new();
    internal static bool HasActiveTransfers => !ActiveTransfers.IsEmpty;
    public static event EventHandler<FolderTransferProgress>? TransferProgressChanged;
    public static event EventHandler<FolderTransferConfirmationEventArgs>? TransferConfirmationRequested;
    public static event EventHandler<FolderTransferCompletedEventArgs>? TransferCompleted;
    private const string DesktopShellIconSettingsPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
    private static readonly DesktopShellItemDescriptor[] DesktopShellItems =
    [
        new("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "This PC", false),
        new("{645FF040-5081-101B-9F08-00AA002F954E}", "Recycle Bin", true),
        new("{59031A47-3F72-44A7-89C5-5595FE6B30EE}", "User Files", false),
        new("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "Network", false),
        new("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "Control Panel", false)
    ];
    private static readonly ImageSource FilePlaceholderIcon = CreatePlaceholderIcon(isFolder: false);
    private static readonly ImageSource FolderPlaceholderIcon = CreatePlaceholderIcon(isFolder: true);
    private readonly Action<ProcessStartInfo> _startProcess;

    public FolderItemService()
        : this(startInfo =>
        {
            Process.Start(startInfo);
        })
    {
    }

    internal FolderItemService(Action<ProcessStartInfo> startProcess)
    {
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
    }

    public IReadOnlyList<FolderItem> LoadItems(string folderPath)
    {
        return TryLoadItems(folderPath, out var items, out _)
            ? items
            : Array.Empty<FolderItem>();
    }

    public static ImageSource? GetTypeIcon(string extension, bool isFolder = false) =>
        ShellIconProvider.GetTypeIcon(extension, isFolder);

    public static ImageSource? GetPathIcon(string path) =>
        string.IsNullOrWhiteSpace(path) ? null : ShellIconProvider.GetIcon(path);

    internal static ImageSource GetGuaranteedPathIcon(string path)
    {
        var isFolder = false;
        try { isFolder = Directory.Exists(path); } catch { }
        return GetPathIcon(path) ??
               GetTypeIcon(isFolder ? string.Empty : Path.GetExtension(path), isFolder) ??
               (isFolder ? FolderPlaceholderIcon : FilePlaceholderIcon);
    }

    internal static bool IsShellNamespacePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase);

    internal static bool IsRecycleBinNamespacePath(string? path) =>
        string.Equals(path, $"shell:::{RecycleBinClsid}", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDesktopRootPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var personal = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var common = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate, personal, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidate, common, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static bool ShouldShowDesktopShellItem(object? registryValue, bool visibleByDefault)
    {
        if (registryValue == null) return visibleByDefault;
        try { return Convert.ToInt32(registryValue) == 0; }
        catch { return visibleByDefault; }
    }

    internal static IReadOnlyList<FolderItem> LoadVisibleDesktopShellItems()
    {
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(DesktopShellIconSettingsPath);
            return DesktopShellItems
                .Where(descriptor => ShouldShowDesktopShellItem(
                    settings?.GetValue(descriptor.Clsid),
                    descriptor.VisibleByDefault))
                .Select(descriptor => CreateShellNamespaceItem(
                    $"shell:::{descriptor.Clsid}",
                    descriptor.FallbackName))
                .ToArray();
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to read Windows system desktop icon settings", ex);
            return DesktopShellItems
                .Where(descriptor => descriptor.VisibleByDefault)
                .Select(descriptor => CreateShellNamespaceItem(
                    $"shell:::{descriptor.Clsid}",
                    descriptor.FallbackName))
                .ToArray();
        }
    }

    public async Task LoadIconsAsync(IEnumerable<FolderItem> items,
        Action<FolderItem, ImageSource?> applyIcon, CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(items,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                ImageSource? icon = null;
                var retryDelays = new[] { 0, 400, 1200, 2500 };
                foreach (var delay in retryDelays)
                {
                    if (delay > 0) await Task.Delay(delay, token);
                    icon = await LoadShellIconOnStaAsync(item.FullPath, token);
                    if (icon != null) break;
                }
                token.ThrowIfCancellationRequested();
                if (icon == null) AppLogger.Log($"Shell icon remained unavailable after startup retries: {item.FullPath}");
                applyIcon(item, icon);
            });
    }

    private static async Task<ImageSource?> LoadShellIconOnStaAsync(string path, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(ShellIconProvider.GetIcon(path));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "MiniFences Shell icon loader"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            AppLogger.Log($"Shell icon loading timed out: {path}");
            return null;
        }
    }

    public IReadOnlyList<FolderItem> LoadAssignedItems(IEnumerable<string> paths)
    {
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           (IsShellNamespacePath(path) || File.Exists(path) || Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => IsShellNamespacePath(path) || ShouldShowItem(path))
            .Select(CreateAssignedItem)
            .ToList();
    }

    private static FolderItem CreateAssignedItem(string path)
    {
        if (!IsShellNamespacePath(path)) return CreateItem(path);
        var descriptor = DesktopShellItems.FirstOrDefault(candidate =>
            string.Equals(path, $"shell:::{candidate.Clsid}", StringComparison.OrdinalIgnoreCase));
        return CreateShellNamespaceItem(path, descriptor?.FallbackName ?? "Desktop item");
    }

    public static IReadOnlyList<string> CollapseDesktopEntries(IEnumerable<string> paths)
    {
        var personal = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        return paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(path =>
                IsInDirectory(path, personal) ? 0 : 1).First())
            .ToArray();
    }

    public static IReadOnlyList<string> EnumerateFileSystemEntriesSafe(IEnumerable<string> directories)
    {
        var entries = new List<string>();
        foreach (var directory in directories.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                if (Directory.Exists(directory)) entries.AddRange(Directory.EnumerateFileSystemEntries(directory));
            }
            catch (Exception ex)
            {
                AppLogger.LogException($"Failed to enumerate directory '{directory}'", ex);
            }
        }
        return entries;
    }

    private static bool IsInDirectory(string path, string directory)
    {
        try
        {
            return string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? ""), directory, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public bool TryLoadItems(string folderPath, out IReadOnlyList<FolderItem> items, out string? error)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            AppLogger.Log($"Folder skipped because path is missing: {folderPath}");
            items = Array.Empty<FolderItem>();
            error = $"Path does not exist: {folderPath}";
            return false;
        }

        try
        {
            items = Directory.EnumerateFileSystemEntries(folderPath)
                .Where(ShouldShowItem)
                .Select(CreateItem)
                .OrderByDescending(item => item.Kind == "Folder")
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            AppLogger.Log($"Loaded {items.Count} item(s) from folder: {folderPath}");

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Failed to load folder items from {folderPath}", ex);
            items = Array.Empty<FolderItem>();
            error = ex.Message;
            return false;
        }
    }

    private static bool ShouldShowItem(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Hidden) || attributes.HasFlag(FileAttributes.System))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return !Directory.Exists(path) || !AutoOrganizerService.IsManagedRootFolder(path);
    }

    public bool TryOpen(FolderItem item, out string? error)
    {
        AppLogger.Log($"Open requested: {item.FullPath}");
        try
        {
            if (IsShellNamespacePath(item.FullPath))
            {
                _startProcess(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = item.FullPath,
                    UseShellExecute = true
                });
                AppLogger.Log($"Shell namespace open succeeded: {item.FullPath}");
                error = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(item.FullPath) ||
                (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath)))
            {
                error = $"Path does not exist: {item.FullPath}";
                AppLogger.Log($"Open failed because path does not exist: {item.FullPath}");
                return false;
            }

            var startInfo = Directory.Exists(item.FullPath)
                ? new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{item.FullPath}\"",
                    UseShellExecute = true
                }
                : new ProcessStartInfo
                {
                    FileName = item.FullPath,
                    UseShellExecute = true
                };
            _startProcess(startInfo);
            AppLogger.Log($"Open succeeded: {item.FullPath}");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.LogException($"Open failed: {item.FullPath}", ex);
            return false;
        }
    }

    public bool TryShowInExplorer(FolderItem item, out string? error)
    {
        try
        {
            if (IsShellNamespacePath(item.FullPath))
            {
                error = "System desktop icons cannot be selected in a file-system folder.";
                return false;
            }
            if (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath))
            {
                error = "The item no longer exists.";
                return false;
            }

            _startProcess(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.FullPath}\"",
                UseShellExecute = true
            });
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public bool TryMoveIntoFolder(IEnumerable<string> sourcePaths, string destinationFolder, out string? error)
    {
        var result = MoveIntoFolder(sourcePaths, destinationFolder);
        error = result.Errors.FirstOrDefault();
        return result.Errors.Count == 0;
    }

    public FolderMoveResult MoveIntoFolder(IEnumerable<string> sourcePaths, string destinationFolder)
        => TransferIntoFolder(sourcePaths, destinationFolder, FolderTransferOperation.Move, null, CancellationToken.None);

    public FolderMoveResult CopyIntoFolder(IEnumerable<string> sourcePaths, string destinationFolder)
        => TransferIntoFolder(sourcePaths, destinationFolder, FolderTransferOperation.Copy, null, CancellationToken.None);

    public FolderMoveResult CreateShortcutsIntoFolder(IEnumerable<string> sourcePaths, string destinationFolder)
        => TransferIntoFolder(sourcePaths, destinationFolder, FolderTransferOperation.Link, null, CancellationToken.None);

    public FolderMoveResult TransferIntoFolder(IEnumerable<string> sourcePaths, string destinationFolder,
        FolderTransferOperation operation)
        => TransferIntoFolder(sourcePaths, destinationFolder, operation, null, CancellationToken.None);

    internal FolderMoveResult TransferIntoFolder(IEnumerable<string> sourcePaths, string destinationFolder,
        FolderTransferOperation operation, IFolderTransferObserver? observer, CancellationToken cancellationToken)
    {
        var transferId = Guid.NewGuid();
        var gateHeld = false;
        var skipped = 0;
        var errors = new List<string>();
        var movedPaths = new List<string>();
        var moves = new List<FolderMoveEntry>();
        var plans = new List<FolderTransferPlanEntry>();
        var fullDestination = destinationFolder;
        using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ActiveTransfers[transferId] = transferCancellation;
        ReportProgress(new FolderTransferProgress(transferId, operation, "Queued", 0, 0, null, true));
        try
        {
            TransferGate.Wait(transferCancellation.Token);
            gateHeld = true;
            AutoOrganizerService.TryEnsureManagedCategoryFolder(destinationFolder, out _, out _);
            if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
            {
                AppLogger.Log($"{operation} failed because destination folder does not exist: {destinationFolder}");
                return new FolderMoveResult(0, 0, ["Destination folder does not exist."], []);
            }

            fullDestination = NormalizeDirectoryPath(destinationFolder);
            AppLogger.Log($"{operation} into folder requested. Destination={fullDestination}");
            var reservedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var sourcePath in sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                transferCancellation.Token.ThrowIfCancellationRequested();
                var fullSource = Path.GetFullPath(sourcePath);
                if (!File.Exists(fullSource) && !Directory.Exists(fullSource))
                {
                    AppLogger.Log($"Move skipped because source does not exist: {fullSource}");
                    skipped += 1;
                    continue;
                }

                if (operation != FolderTransferOperation.Link && Directory.Exists(fullSource) &&
                    IsSameOrParentOf(fullSource, fullDestination))
                {
                    AppLogger.Log($"Move skipped because source folder contains destination. Source={fullSource}; Destination={fullDestination}");
                    skipped += 1;
                    continue;
                }

                var sourceParent = NormalizeDirectoryPath(Path.GetDirectoryName(fullSource) ?? "");
                if (operation != FolderTransferOperation.Link &&
                    string.Equals(sourceParent, fullDestination, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Log($"Move skipped because source is already in destination: {fullSource}");
                    skipped += 1;
                    continue;
                }

                var destinationPath = GetReservedDestinationPath(fullSource, fullDestination, operation,
                    reservedDestinations);
                reservedDestinations.Add(destinationPath);
                var crossVolumeMove = operation == FolderTransferOperation.Move &&
                                      !PathsUseSameRoot(fullSource, destinationPath);
                var stagingPath = operation == FolderTransferOperation.Move && !crossVolumeMove
                    ? null
                    : operation == FolderTransferOperation.Link
                        ? destinationPath + $".minifences-{Guid.NewGuid():N}.partial.lnk"
                        : destinationPath + $".minifences-{Guid.NewGuid():N}.partial";
                plans.Add(new FolderTransferPlanEntry(
                    fullSource,
                    destinationPath,
                    operation,
                    crossVolumeMove ? GetSourceQuarantinePath(fullSource) : null,
                    stagingPath));
            }

            if (operation == FolderTransferOperation.Move &&
                TryAssessDangerousMove(plans, out var confirmation) &&
                !RequestTransferConfirmation(confirmation))
            {
                const string message = "Move was canceled before any file-system changes were made.";
                ReportProgress(new FolderTransferProgress(transferId, operation, "Canceled", 0, plans.Count, null, false));
                return new FolderMoveResult(0, 0, [message], []);
            }

            observer?.Planned(plans);
            ReportProgress(new FolderTransferProgress(transferId, operation, "Starting", 0, plans.Count, null, true));
            var completedCount = 0;
            foreach (var plan in plans)
            {
                transferCancellation.Token.ThrowIfCancellationRequested();
                ReportProgress(new FolderTransferProgress(transferId, operation, "Transferring",
                    completedCount, plans.Count, plan.SourcePath, true));
                try
                {
                    var lastByteReport = Stopwatch.StartNew();
                    var fingerprint = ExecuteTransferPlan(plan, transferCancellation.Token,
                        (completedBytes, totalBytes, currentPath) =>
                        {
                            if (lastByteReport.ElapsedMilliseconds < 120 && completedBytes < totalBytes) return;
                            lastByteReport.Restart();
                            ReportProgress(new FolderTransferProgress(transferId, operation, "Transferring",
                                completedCount, plans.Count, currentPath, true, completedBytes, totalBytes));
                        });
                    movedPaths.Add(plan.DestinationPath);
                    moves.Add(new FolderMoveEntry(plan.SourcePath, plan.DestinationPath, plan.Operation, fingerprint));
                    observer?.Completed(plan, fingerprint);
                    completedCount++;
                    ReportProgress(new FolderTransferProgress(transferId, operation, "Transferring",
                        completedCount, plans.Count, plan.SourcePath, true));
                    AppLogger.Log($"{operation} succeeded: {plan.SourcePath} -> {plan.DestinationPath}");
                }
                catch (OperationCanceledException)
                {
                    TryRestoreQuarantinedSource(plan, out _);
                    observer?.Failed(plan, "Transfer canceled safely.");
                    throw;
                }
                catch (Exception itemEx)
                {
                    TryRestoreQuarantinedSource(plan, out var restoreError);
                    var message = restoreError is null ? itemEx.Message : $"{itemEx.Message}; {restoreError}";
                    errors.Add($"{Path.GetFileName(plan.SourcePath)}: {message}");
                    observer?.Failed(plan, message);
                    AppLogger.LogException($"{operation} failed for item: {plan.SourcePath}", itemEx);
                }
            }

            observer?.Finished(errors);
            ReportProgress(new FolderTransferProgress(transferId, operation,
                errors.Count == 0 ? "Completed" : "CompletedWithErrors",
                completedCount, plans.Count, null, false));
            var result = new FolderMoveResult(movedPaths.Count, skipped, errors, movedPaths, moves);
            ReportCompleted(new FolderTransferCompletedEventArgs(
                operation, plans.Select(plan => plan.SourcePath).ToArray(), fullDestination, result));
            return result;
        }
        catch (OperationCanceledException)
        {
            const string message = "Transfer was canceled; source items were left untouched or restored.";
            errors.Add(message);
            observer?.Finished([message]);
            ReportProgress(new FolderTransferProgress(transferId, operation, "Canceled", 0, 0, null, false));
            var canceledResult = new FolderMoveResult(movedPaths.Count, skipped, errors, movedPaths, moves);
            ReportCompleted(new FolderTransferCompletedEventArgs(
                operation, plans.Select(plan => plan.SourcePath).ToArray(), fullDestination, canceledResult, true));
            AppLogger.Log(message);
            return canceledResult;
        }
        catch (Exception ex)
        {
            observer?.Finished([ex.Message]);
            ReportProgress(new FolderTransferProgress(transferId, operation, "Failed", 0, 0, null, false));
            AppLogger.LogException($"{operation} into folder failed. Destination={destinationFolder}", ex);
            return new FolderMoveResult(0, 0, [ex.Message], []);
        }
        finally
        {
            if (gateHeld) TransferGate.Release();
            ActiveTransfers.TryRemove(transferId, out _);
        }
    }

    public static void CancelActiveTransfers()
    {
        foreach (var cancellation in ActiveTransfers.Values) cancellation.Cancel();
    }

    private static void ReportProgress(FolderTransferProgress progress)
    {
        try { TransferProgressChanged?.Invoke(null, progress); }
        catch (Exception ex) { AppLogger.LogException("Transfer progress listener failed.", ex); }
    }

    private static void ReportCompleted(FolderTransferCompletedEventArgs completed)
    {
        try { TransferCompleted?.Invoke(null, completed); }
        catch (Exception ex) { AppLogger.LogException("Transfer completion listener failed.", ex); }
    }

    private static bool RequestTransferConfirmation(FolderTransferConfirmationEventArgs request)
    {
        var handlers = TransferConfirmationRequested;
        if (handlers is null) return true;
        handlers.Invoke(null, request);
        return request.Approved;
    }

    internal static bool TryAssessDangerousMove(IReadOnlyList<FolderTransferPlanEntry> plans,
        out FolderTransferConfirmationEventArgs request)
    {
        var crossVolume = plans.Any(plan => !string.IsNullOrWhiteSpace(plan.SourceStagingPath));
        long totalBytes = 0;
        var fileCount = 0;
        foreach (var plan in plans)
        {
            try
            {
                if (File.Exists(plan.SourcePath))
                {
                    fileCount++;
                    totalBytes += new FileInfo(plan.SourcePath).Length;
                }
                else if (Directory.Exists(plan.SourcePath))
                {
                    var pending = new Stack<string>();
                    pending.Push(plan.SourcePath);
                    while (pending.Count > 0 && fileCount < 1000 && totalBytes < 1024L * 1024 * 1024)
                    {
                        var current = pending.Pop();
                        foreach (var file in Directory.EnumerateFiles(current))
                        {
                            fileCount++;
                            totalBytes += new FileInfo(file).Length;
                            if (fileCount >= 1000 || totalBytes >= 1024L * 1024 * 1024) break;
                        }
                        foreach (var directory in Directory.EnumerateDirectories(current))
                            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0) pending.Push(directory);
                    }
                }
            }
            catch
            {
                // An unreadable tree will be rejected by verified transfer. It
                // should not turn a preflight confirmation into a crash.
            }
        }
        request = new FolderTransferConfirmationEventArgs(
            plans.Select(plan => plan.SourcePath).ToArray(),
            plans.FirstOrDefault()?.DestinationPath is { } destination
                ? Path.GetDirectoryName(destination) ?? destination : string.Empty,
            crossVolume, fileCount, totalBytes);
        return crossVolume || fileCount >= 1000 || totalBytes >= 1024L * 1024 * 1024;
    }

    public static FolderTransferOperation? GetTransferOperation(System.Windows.DragDropEffects effect) => effect switch
    {
        System.Windows.DragDropEffects.Copy => FolderTransferOperation.Copy,
        System.Windows.DragDropEffects.Move => FolderTransferOperation.Move,
        System.Windows.DragDropEffects.Link => FolderTransferOperation.Link,
        _ => null
    };

    public static string GetOperationDisplayName(FolderTransferOperation operation, bool chinese = true) =>
        chinese
            ? operation switch
            {
                FolderTransferOperation.Copy => "复制",
                FolderTransferOperation.Link => "创建链接",
                _ => "移动"
            }
            : operation switch
            {
                FolderTransferOperation.Copy => "copying",
                FolderTransferOperation.Link => "creating shortcuts for",
                _ => "moving"
            };

    public bool TryCreateFolder(string parentFolder, string baseName, out string? createdPath, out string? error)
    {
        createdPath = null;
        try
        {
            AutoOrganizerService.TryEnsureManagedCategoryFolder(parentFolder, out _, out _);
            if (string.IsNullOrWhiteSpace(parentFolder) || !Directory.Exists(parentFolder))
            {
                error = "Destination folder does not exist.";
                AppLogger.Log($"Create folder failed because parent folder does not exist: {parentFolder}");
                return false;
            }

            var safeBaseName = SanitizeFolderName(baseName);
            var targetPath = GetAvailableFolderPath(parentFolder, safeBaseName);
            Directory.CreateDirectory(targetPath);
            createdPath = targetPath;
            error = null;
            AppLogger.Log($"Folder created: {targetPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.LogException($"Create folder failed. Parent={parentFolder}; BaseName={baseName}", ex);
            return false;
        }
    }

    public bool TryRenameItem(FolderItem item, string newName, out string? renamedPath, out string? error)
    {
        renamedPath = null;
        try
        {
            if (IsShellNamespacePath(item.FullPath))
            {
                error = "System desktop icons cannot be renamed.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(item.FullPath) ||
                (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath)))
            {
                error = $"Path does not exist: {item.FullPath}";
                AppLogger.Log($"Rename failed because path does not exist: {item.FullPath}");
                return false;
            }

            var sanitizedName = SanitizeFolderName(newName);
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                error = "Invalid item name.";
                return false;
            }

            var parentFolder = Path.GetDirectoryName(item.FullPath);
            if (string.IsNullOrWhiteSpace(parentFolder))
            {
                error = "Destination folder does not exist.";
                return false;
            }

            var targetPath = BuildRenameTargetPath(item.FullPath, sanitizedName);
            if (string.Equals(Path.GetFullPath(item.FullPath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                renamedPath = item.FullPath;
                error = null;
                return true;
            }

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                error = "Destination already exists.";
                return false;
            }

            if (Directory.Exists(item.FullPath))
            {
                Directory.Move(item.FullPath, targetPath);
            }
            else
            {
                File.Move(item.FullPath, targetPath);
            }

            renamedPath = targetPath;
            error = null;
            AppLogger.Log($"Rename succeeded: {item.FullPath} -> {targetPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.LogException($"Rename failed: {item.FullPath} -> {newName}", ex);
            return false;
        }
    }

    public bool TryDeleteItem(FolderItem item, out string? error)
    {
        try
        {
            if (IsShellNamespacePath(item.FullPath))
            {
                error = "System desktop icons cannot be deleted here.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(item.FullPath) ||
                (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath)))
            {
                error = $"Path does not exist: {item.FullPath}";
                AppLogger.Log($"Delete failed because path does not exist: {item.FullPath}");
                return false;
            }

            if (Directory.Exists(item.FullPath))
            {
                FileSystem.DeleteDirectory(
                    item.FullPath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
            else
            {
                FileSystem.DeleteFile(
                    item.FullPath,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }

            AppLogger.Log($"Delete sent to recycle bin: {item.FullPath}");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.LogException($"Delete failed: {item.FullPath}", ex);
            return false;
        }
    }

    private static FolderItem CreateItem(string path)
    {
        var isDirectory = Directory.Exists(path);
        var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        var extension = Path.GetExtension(path);
        return new FolderItem
        {
            Name = isDirectory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path),
            FullPath = path,
            ToolTip = path,
            Kind = isDirectory ? "Folder" : string.IsNullOrWhiteSpace(extension) ? "File" : extension.TrimStart('.').ToUpperInvariant(),
            ModifiedAt = info.LastWriteTime,
            CreatedAt = info.CreationTime,
            Size = isDirectory ? 0 : ((FileInfo)info).Length,
            // Shell icon handlers can block indefinitely. Use a managed placeholder for the
            // initial frame and replace it with the real icon from the bounded background pass.
            Icon = ShellIconProvider.TryGetCachedIcon(path) ??
                   (isDirectory ? FolderPlaceholderIcon : FilePlaceholderIcon)
        };
    }

    private static FolderItem CreateShellNamespaceItem(string parsingName, string fallbackName)
    {
        var name = ShellIconProvider.GetDisplayName(parsingName) ?? fallbackName;
        return new FolderItem
        {
            Name = name,
            FullPath = parsingName,
            ToolTip = ShellIconProvider.GetInfoTip(parsingName) ?? name,
            Kind = "System",
            Icon = ShellIconProvider.GetIcon(parsingName) ?? FolderPlaceholderIcon
        };
    }

    private static ImageSource CreatePlaceholderIcon(bool isFolder)
    {
        var drawing = new DrawingGroup();
        if (isFolder)
        {
            var geometry = Geometry.Parse("M 5,12 L 18,12 L 23,18 L 43,18 L 43,40 L 5,40 Z");
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 183, 67)),
                new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 215, 128)), 1.5),
                geometry));
        }
        else
        {
            var body = Geometry.Parse("M 10,5 L 31,5 L 42,16 L 42,43 L 10,43 Z");
            var fold = Geometry.Parse("M 31,5 L 31,16 L 42,16");
            drawing.Children.Add(new GeometryDrawing(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(104, 151, 190)),
                new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(181, 216, 241)), 1.5),
                body));
            drawing.Children.Add(new GeometryDrawing(
                null,
                new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(210, 232, 247)), 1.5),
                fold));
        }

        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static string GetAvailableDestinationPath(string sourcePath, string destinationFolder)
    {
        var name = Path.GetFileName(sourcePath);
        var destinationPath = Path.Combine(destinationFolder, name);
        if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
        {
            return destinationPath;
        }

        var isDirectory = Directory.Exists(sourcePath);
        var fileName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? "" : Path.GetExtension(name);
        for (var index = 1; index < 10_000; index += 1)
        {
            var candidateName = string.IsNullOrEmpty(extension)
                ? $"{fileName} ({index})"
                : $"{fileName} ({index}){extension}";
            var candidatePath = Path.Combine(destinationFolder, candidateName);
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new IOException($"Could not find an available name for {name}.");
    }

    private static string GetReservedDestinationPath(string sourcePath, string destinationFolder,
        FolderTransferOperation operation, ISet<string> reserved)
    {
        var candidate = operation == FolderTransferOperation.Link
            ? GetAvailableShortcutPath(sourcePath, destinationFolder)
            : GetAvailableDestinationPath(sourcePath, destinationFolder);
        if (!reserved.Contains(candidate)) return candidate;

        var name = Path.GetFileName(candidate);
        var isDirectory = operation != FolderTransferOperation.Link && Directory.Exists(sourcePath);
        var baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isDirectory ? string.Empty : Path.GetExtension(name);
        for (var index = 1; index < 10_000; index += 1)
        {
            candidate = Path.Combine(destinationFolder, $"{baseName} ({index}){extension}");
            if (!reserved.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
        throw new IOException($"Could not reserve an available name for {name}.");
    }

    private static string GetSourceQuarantinePath(string sourcePath)
    {
        var parent = Path.GetDirectoryName(sourcePath) ??
                     throw new IOException("Source has no parent directory.");
        var name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        for (var attempt = 0; attempt < 100; attempt += 1)
        {
            var candidate = Path.Combine(parent, $".{name}.minifences-move-{Guid.NewGuid():N}.source");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not reserve a safe source quarantine path.");
    }

    internal static bool TryRestoreQuarantinedSource(FolderTransferPlanEntry plan, out string? error)
    {
        error = null;
        var quarantine = plan.SourceStagingPath;
        if (string.IsNullOrWhiteSpace(quarantine) ||
            (!File.Exists(quarantine) && !Directory.Exists(quarantine))) return true;
        var sourceParent = Path.GetFullPath(Path.GetDirectoryName(plan.SourcePath) ?? string.Empty);
        var quarantineParent = Path.GetFullPath(Path.GetDirectoryName(quarantine) ?? string.Empty);
        var quarantineName = Path.GetFileName(quarantine);
        var sourceName = Path.GetFileName(plan.SourcePath.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.Equals(sourceParent, quarantineParent, StringComparison.OrdinalIgnoreCase) ||
            !quarantineName.StartsWith($".{sourceName}.minifences-move-", StringComparison.OrdinalIgnoreCase) ||
            !quarantineName.EndsWith(".source", StringComparison.OrdinalIgnoreCase))
        {
            error = "Refused to restore an unrecognized source quarantine path.";
            return false;
        }
        if (File.Exists(plan.SourcePath) || Directory.Exists(plan.SourcePath))
        {
            error = $"A new item now exists at the original path; preserved quarantined source at {quarantine}.";
            return false;
        }
        try
        {
            if (Directory.Exists(quarantine)) Directory.Move(quarantine, plan.SourcePath);
            else File.Move(quarantine, plan.SourcePath);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not restore quarantined source {quarantine}: {ex.Message}";
            return false;
        }
    }

    private static string GetAvailableFolderPath(string parentFolder, string baseName)
    {
        var candidatePath = Path.Combine(parentFolder, baseName);
        if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
        {
            return candidatePath;
        }

        for (var index = 1; index < 10_000; index += 1)
        {
            candidatePath = Path.Combine(parentFolder, $"{baseName} ({index})");
            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new IOException($"Could not find an available folder name for {baseName}.");
    }

    private static string SanitizeFolderName(string baseName)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "New Folder" : baseName.Trim();
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalidChar, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "New Folder" : name;
    }

    private static string BuildRenameTargetPath(string sourcePath, string newName)
    {
        var parentFolder = Path.GetDirectoryName(sourcePath) ?? "";
        if (Directory.Exists(sourcePath))
        {
            return Path.Combine(parentFolder, newName);
        }

        var extension = Path.GetExtension(sourcePath);
        // File item labels intentionally hide the real extension. Preserve it
        // unconditionally while renaming the displayed base name: dots in a
        // version such as "MiniFences-0.23.12" are not a replacement extension.
        var targetName = string.IsNullOrEmpty(extension) ||
                         newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? newName
            : newName + extension;
        return Path.Combine(parentFolder, targetName);
    }

    internal static void MoveFileSystemEntrySafely(string sourcePath, string destinationPath)
    {
        if (PathsUseSameRoot(sourcePath, destinationPath))
        {
            if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, destinationPath);
            else File.Move(sourcePath, destinationPath);
            return;
        }

        var plan = new FolderTransferPlanEntry(
            sourcePath,
            destinationPath,
            FolderTransferOperation.Move,
            GetSourceQuarantinePath(sourcePath),
            destinationPath + $".minifences-{Guid.NewGuid():N}.partial");
        ExecuteTransferPlan(plan, CancellationToken.None);
    }

    internal static void CopyFileSystemEntrySafely(string sourcePath, string destinationPath)
    {
        var plan = new FolderTransferPlanEntry(
            sourcePath,
            destinationPath,
            FolderTransferOperation.Copy,
            null,
            destinationPath + $".minifences-{Guid.NewGuid():N}.partial");
        ExecuteTransferPlan(plan, CancellationToken.None);
    }

    private static string? ExecuteTransferPlan(FolderTransferPlanEntry plan, CancellationToken cancellationToken,
        Action<long, long, string>? byteProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Operation == FolderTransferOperation.Link)
        {
            var staging = plan.DestinationStagingPath ?? throw new InvalidOperationException("Shortcut staging path is missing.");
            try
            {
                CreateShortcut(plan.SourcePath, staging);
                var fingerprint = ComputePathFingerprint(staging, cancellationToken);
                File.Move(staging, plan.DestinationPath);
                return fingerprint;
            }
            catch
            {
                TryDeleteStagingPath(staging);
                throw;
            }
        }

        if (plan.Operation == FolderTransferOperation.Copy)
            return CopyFileSystemEntrySafely(plan.SourcePath, plan.DestinationPath,
                plan.DestinationStagingPath!, cancellationToken, byteProgress);

        if (plan.SourceStagingPath is null)
        {
            if (Directory.Exists(plan.SourcePath)) Directory.Move(plan.SourcePath, plan.DestinationPath);
            else File.Move(plan.SourcePath, plan.DestinationPath);
            return null;
        }

        // Atomically remove the original name before a cross-volume copy. If a
        // producer recreates that name while copying, the new data is outside
        // our quarantine and can never be deleted by this transfer.
        if (Directory.Exists(plan.SourcePath)) Directory.Move(plan.SourcePath, plan.SourceStagingPath);
        else File.Move(plan.SourcePath, plan.SourceStagingPath);
        try
        {
            var fingerprint = CopyFileSystemEntrySafely(plan.SourceStagingPath, plan.DestinationPath,
                plan.DestinationStagingPath!, cancellationToken, byteProgress);
            if (Directory.Exists(plan.SourceStagingPath)) Directory.Delete(plan.SourceStagingPath, recursive: true);
            else File.Delete(plan.SourceStagingPath);
            return fingerprint;
        }
        catch
        {
            TryRestoreQuarantinedSource(plan, out _);
            throw;
        }
    }

    private static string CopyFileSystemEntrySafely(string sourcePath, string destinationPath,
        string stagingPath, CancellationToken cancellationToken,
        Action<long, long, string>? byteProgress = null)
    {
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
            throw new IOException($"Destination already exists: {destinationPath}");

        if (File.Exists(stagingPath) || Directory.Exists(stagingPath))
            throw new IOException($"Transfer staging path already exists: {stagingPath}");
        try
        {
            if (Directory.Exists(sourcePath))
            {
                var sourceRootAttributes = File.GetAttributes(sourcePath);
                if ((sourceRootAttributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reparse-point directory cannot be transferred safely: {sourcePath}");
                var before = CaptureDirectoryManifest(sourcePath, includeHashes: false, cancellationToken);
                Directory.CreateDirectory(stagingPath);
                File.SetAttributes(stagingPath, File.GetAttributes(stagingPath) | FileAttributes.Hidden);
                CopyDirectoryFromManifest(sourcePath, stagingPath, before, cancellationToken, byteProgress);
                var after = CaptureDirectoryManifest(sourcePath, includeHashes: true, cancellationToken);
                var copied = CaptureDirectoryManifest(stagingPath, includeHashes: true, cancellationToken);
                if (!ManifestMetadataMatches(before, after) || !ManifestContentMatches(after, copied))
                    throw new IOException("Source changed during copy or the verified copy is incomplete; source was left untouched.");
                var fingerprint = ComputeManifestFingerprint(copied);
                Directory.Move(stagingPath, destinationPath);
                TrySetAttributes(destinationPath, sourceRootAttributes);
                return fingerprint;
            }

            var sourceInfo = new FileInfo(sourcePath);
            if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse-point file cannot be transferred safely: {sourcePath}");
            var expectedLength = sourceInfo.Length;
            var expectedWriteTime = sourceInfo.LastWriteTimeUtc;
            var sourceAttributes = sourceInfo.Attributes;
            CopyFileContents(sourcePath, stagingPath, expectedLength, 0, expectedLength,
                byteProgress, cancellationToken);
            File.SetAttributes(stagingPath, File.GetAttributes(stagingPath) | FileAttributes.Hidden);
            var sourceHash = ComputeFileSha256(sourcePath, cancellationToken);
            var destinationHash = ComputeFileSha256(stagingPath, cancellationToken);
            sourceInfo.Refresh();
            if (!sourceInfo.Exists || sourceInfo.Length != expectedLength || sourceInfo.LastWriteTimeUtc != expectedWriteTime ||
                new FileInfo(stagingPath).Length != expectedLength ||
                !string.Equals(sourceHash, destinationHash, StringComparison.Ordinal))
                throw new IOException("Source changed during copy or the verified copy is incomplete; source was left untouched.");
            File.Move(stagingPath, destinationPath);
            TrySetAttributes(destinationPath, sourceAttributes);
            return destinationHash;
        }
        catch
        {
            TryDeleteStagingPath(stagingPath);
            throw;
        }
    }

    private static IReadOnlyList<DirectoryManifestEntry> CaptureDirectoryManifest(string root,
        bool includeHashes, CancellationToken cancellationToken)
    {
        var entries = new List<DirectoryManifestEntry>();
        CaptureDirectoryManifest(root, root, entries, includeHashes, cancellationToken);
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void CaptureDirectoryManifest(string root, string current,
        ICollection<DirectoryManifestEntry> entries, bool includeHashes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var directory in Directory.EnumerateDirectories(current))
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse-point directory cannot be transferred safely: {directory}");
            entries.Add(new DirectoryManifestEntry(Path.GetRelativePath(root, directory), true, 0, 0, null));
            CaptureDirectoryManifest(root, directory, entries, includeHashes, cancellationToken);
        }

        foreach (var file in Directory.EnumerateFiles(current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse-point file cannot be transferred safely: {file}");
            entries.Add(new DirectoryManifestEntry(
                Path.GetRelativePath(root, file),
                false,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                includeHashes ? ComputeFileSha256(file, cancellationToken) : null));
        }
    }

    private static void CopyDirectoryFromManifest(string sourceRoot, string destinationRoot,
        IReadOnlyList<DirectoryManifestEntry> manifest, CancellationToken cancellationToken,
        Action<long, long, string>? byteProgress)
    {
        var totalBytes = manifest.Where(entry => !entry.IsDirectory).Sum(entry => entry.Length);
        long completedBytes = 0;
        foreach (var entry in manifest.Where(entry => entry.IsDirectory))
            Directory.CreateDirectory(Path.Combine(destinationRoot, entry.RelativePath));
        foreach (var entry in manifest.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(destinationRoot, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var source = Path.Combine(sourceRoot, entry.RelativePath);
            CopyFileContents(source, destination, entry.Length, completedBytes, totalBytes,
                byteProgress, cancellationToken);
            completedBytes += entry.Length;
        }
    }

    private static void CopyFileContents(string sourcePath, string destinationPath, long expectedLength,
        long completedBefore, long totalBytes, Action<long, long, string>? byteProgress,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(sourcePath);
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan))
        using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                   FileShare.None, 1024 * 1024, FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                destination.Write(buffer, 0, read);
                copied += read;
                byteProgress?.Invoke(completedBefore + copied, Math.Max(1, totalBytes), sourcePath);
            }
            if (copied != expectedLength)
                throw new IOException($"Source length changed while copying: {sourcePath}");
            destination.Flush(flushToDisk: true);
        }
        File.SetCreationTimeUtc(destinationPath, sourceInfo.CreationTimeUtc);
        File.SetLastWriteTimeUtc(destinationPath, sourceInfo.LastWriteTimeUtc);
        File.SetAttributes(destinationPath, sourceInfo.Attributes);
    }

    private static bool ManifestMetadataMatches(IReadOnlyList<DirectoryManifestEntry> before,
        IReadOnlyList<DirectoryManifestEntry> after) =>
        before.Count == after.Count && before.Zip(after).All(pair =>
            string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            pair.First.IsDirectory == pair.Second.IsDirectory &&
            pair.First.Length == pair.Second.Length &&
            pair.First.LastWriteUtcTicks == pair.Second.LastWriteUtcTicks);

    private static bool ManifestContentMatches(IReadOnlyList<DirectoryManifestEntry> source,
        IReadOnlyList<DirectoryManifestEntry> destination) =>
        source.Count == destination.Count && source.Zip(destination).All(pair =>
            string.Equals(pair.First.RelativePath, pair.Second.RelativePath, StringComparison.OrdinalIgnoreCase) &&
            pair.First.IsDirectory == pair.Second.IsDirectory &&
            pair.First.Length == pair.Second.Length &&
            (pair.First.IsDirectory || string.Equals(pair.First.Sha256, pair.Second.Sha256, StringComparison.Ordinal)));

    private static string ComputeManifestFingerprint(IReadOnlyList<DirectoryManifestEntry> manifest)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in manifest)
        {
            var line = $"{entry.RelativePath}\0{entry.IsDirectory}\0{entry.Length}\0{entry.Sha256}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static string ComputePathFingerprint(string path, CancellationToken cancellationToken = default)
    {
        if (File.Exists(path)) return ComputeFileSha256(path, cancellationToken);
        if (Directory.Exists(path))
            return ComputeManifestFingerprint(CaptureDirectoryManifest(path, includeHashes: true, cancellationToken));
        throw new FileNotFoundException("Path does not exist for fingerprinting.", path);
    }

    private static string ComputeFileSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool PathsUseSameRoot(string first, string second) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(first)), Path.GetPathRoot(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteStagingPath(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Could not remove incomplete staging path: {path}", ex);
        }
    }

    internal static bool TryDeleteTrackedStagingPath(string? path, string? destinationPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(destinationPath) ||
            !Path.GetFileName(path).Contains(".minifences-", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).Contains(".partial", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? string.Empty),
                Path.GetFullPath(Path.GetDirectoryName(destinationPath) ?? string.Empty),
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith(
                Path.GetFileName(destinationPath) + ".minifences-", StringComparison.OrdinalIgnoreCase))
        {
            error = "Refused to clean an unrecognized transfer staging path.";
            return false;
        }
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TrySetAttributes(string path, FileAttributes attributes)
    {
        try
        {
            File.SetAttributes(path, attributes);
        }
        catch (Exception ex)
        {
            // The data is already fully verified and atomically published. An
            // attribute restoration failure must not misreport the transfer as
            // failed or trigger source deletion/copy retries.
            AppLogger.LogException($"Could not restore copied item attributes: {path}", ex);
        }
    }

    private static string GetAvailableShortcutPath(string sourcePath, string destinationFolder)
    {
        var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var baseName = Path.GetFileName(trimmed);
        if (!Directory.Exists(sourcePath)) baseName = Path.GetFileNameWithoutExtension(trimmed);
        return GetAvailableNamedPath(destinationFolder, SanitizeFolderName(baseName) + ".lnk");
    }

    private static string GetAvailableNamedPath(string destinationFolder, string fileName)
    {
        var candidate = Path.Combine(destinationFolder, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 10_000; index += 1)
        {
            candidate = Path.Combine(destinationFolder, $"{baseName} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException($"Could not find an available name for {fileName}.");
    }

    private static void CreateShortcut(string sourcePath, string shortcutPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ??
                        throw new InvalidOperationException("Windows Script Host is unavailable.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType) ??
                    throw new InvalidOperationException("Windows Script Host could not be started.");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = sourcePath;
            dynamicShortcut.WorkingDirectory = Directory.Exists(sourcePath)
                ? sourcePath
                : Path.GetDirectoryName(sourcePath) ?? string.Empty;
            dynamicShortcut.Save();
            if (!File.Exists(shortcutPath)) throw new IOException("The shortcut was not created.");
        }
        finally
        {
            if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    private sealed record DirectoryManifestEntry(
        string RelativePath,
        bool IsDirectory,
        long Length,
        long LastWriteUtcTicks,
        string? Sha256);

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSameOrParentOf(string sourceFolder, string destinationFolder)
    {
        var normalizedSource = NormalizeDirectoryPath(sourceFolder);
        var normalizedDestination = NormalizeDirectoryPath(destinationFolder);
        return string.Equals(normalizedSource, normalizedDestination, StringComparison.OrdinalIgnoreCase) ||
               normalizedDestination.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedDestination.StartsWith(normalizedSource + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static class ShellIconProvider
    {
        private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);
        private const int MaximumCachedIcons = 512;
        private const uint ShgfiIcon = 0x000000100;
        private const uint ShgfiLargeIcon = 0x000000000;
        private const uint ShgfiUseFileAttributes = 0x000000010;
        private const uint ShgfiPidl = 0x000000008;
        private const uint ShgfiDisplayName = 0x000000200;
        private const uint FileAttributeDirectory = 0x00000010;
        private const uint FileAttributeNormal = 0x00000080;

        public static ImageSource? GetIcon(string path)
        {
            var cacheKey = GetCacheKey(path);
            if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
            var icon = LoadIcon(path);
            if (IconCache.Count >= MaximumCachedIcons) IconCache.Clear();
            if (icon != null) IconCache[cacheKey] = icon;
            return icon;
        }

        public static ImageSource? TryGetCachedIcon(string path)
        {
            var cacheKey = GetCacheKey(path);
            return IconCache.TryGetValue(cacheKey, out var cached) ? cached : null;
        }

        private static ImageSource? LoadIcon(string path)
        {
            if (IsShellNamespacePath(path))
            {
                return GetShellNamespaceInfo(path, includeIcon: true, includeInfoTip: false).Icon;
            }

            var iconHandle = IntPtr.Zero;
            try
            {
                var info = new ShFileInfo();
                var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                {
                    return null;
                }

                iconHandle = info.hIcon;
                return CreateShellBitmapSource(iconHandle);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (iconHandle != IntPtr.Zero)
                {
                    DestroyIcon(iconHandle);
                }
            }
        }

        private static string GetCacheKey(string path)
        {
            if (IsShellNamespacePath(path)) return $"shell|{path}";
            try
            {
                var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                return $"{Path.GetFullPath(path)}|{info.LastWriteTimeUtc.Ticks}|{(info is FileInfo file ? file.Length : 0)}";
            }
            catch { return Path.GetFullPath(path); }
        }

        public static string? GetDisplayName(string parsingName) =>
            GetShellNamespaceInfo(parsingName, includeIcon: false, includeInfoTip: false).DisplayName;

        public static string? GetInfoTip(string parsingName) =>
            GetShellNamespaceInfo(parsingName, includeIcon: false, includeInfoTip: true).InfoTip;

        private static (ImageSource? Icon, string? DisplayName, string? InfoTip) GetShellNamespaceInfo(
            string parsingName,
            bool includeIcon,
            bool includeInfoTip)
        {
            IntPtr pidl = IntPtr.Zero;
            IntPtr iconHandle = IntPtr.Zero;
            try
            {
                if (SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0 ||
                    pidl == IntPtr.Zero)
                {
                    return (null, null, null);
                }

                var info = new ShFileInfo();
                var flags = ShgfiPidl | ShgfiDisplayName | ShgfiLargeIcon;
                if (includeIcon) flags |= ShgfiIcon;
                var result = SHGetFileInfoPidl(
                    pidl,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf<ShFileInfo>(),
                    flags);
                if (result == IntPtr.Zero) return (null, null, null);
                iconHandle = info.hIcon;
                var icon = includeIcon && iconHandle != IntPtr.Zero
                    ? CreateShellBitmapSource(iconHandle)
                    : null;
                var infoTip = includeInfoTip ? GetInfoTip(pidl) : null;
                return (
                    icon,
                    string.IsNullOrWhiteSpace(info.szDisplayName) ? null : info.szDisplayName,
                    infoTip);
            }
            catch
            {
                return (null, null, null);
            }
            finally
            {
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }

        private static string? GetInfoTip(IntPtr pidl)
        {
            if (pidl == IntPtr.Zero) return null;

            var interfaceId = typeof(IQueryInfo).GUID;
            var result = SHBindToObject(
                IntPtr.Zero,
                pidl,
                IntPtr.Zero,
                ref interfaceId,
                out var unknown);
            if (result != 0 || unknown == IntPtr.Zero) return null;

            try
            {
                var queryInfo = (IQueryInfo)Marshal.GetObjectForIUnknown(unknown);
                var tipResult = queryInfo.GetInfoTip(0, out var tipPointer);
                if (tipResult != 0 || tipPointer == IntPtr.Zero) return null;
                try
                {
                    var tip = Marshal.PtrToStringUni(tipPointer);
                    return string.IsNullOrWhiteSpace(tip) ? null : tip.Trim();
                }
                finally
                {
                    Marshal.FreeCoTaskMem(tipPointer);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public static ImageSource? GetTypeIcon(string extension, bool isFolder)
        {
            var path = isFolder ? "PreviewFolder" : "Preview" + (extension.StartsWith('.') ? extension : "." + extension);
            var attributes = isFolder ? FileAttributeDirectory : FileAttributeNormal;
            var cacheKey = $"type|{path}";
            if (IconCache.TryGetValue(cacheKey, out var cached)) return cached;
            var icon = GetIconCore(path, attributes, ShgfiIcon | ShgfiLargeIcon | ShgfiUseFileAttributes);
            if (icon != null) IconCache[cacheKey] = icon;
            return icon;
        }

        private static ImageSource? GetIconCore(string path, uint attributes, uint flags)
        {
            var iconHandle = IntPtr.Zero;
            try
            {
                var info = new ShFileInfo();
                var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), flags);
                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                iconHandle = info.hIcon;
                return CreateShellBitmapSource(iconHandle);
            }
            catch { return null; }
            finally { if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle); }
        }

        private static bool HasVisiblePixels(BitmapSource source)
        {
            try
            {
                var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var stride = converted.PixelWidth * 4;
                var pixels = new byte[stride * converted.PixelHeight];
                converted.CopyPixels(pixels, stride, 0);
                for (var index = 3; index < pixels.Length; index += 4)
                {
                    if (pixels[index] >= 8) return true;
                }
            }
            catch
            {
                // A readable shell bitmap is preferable to discarding it merely because
                // a pixel-format conversion is unsupported by a third-party icon handler.
                return true;
            }

            return false;
        }

        private static BitmapSource? CreateShellBitmapSource(IntPtr iconHandle)
        {
            try
            {
                // Several archive shell extensions expose an HICON whose RGB data is
                // correct but whose 32-bit alpha channel is empty. WPF's direct HICON
                // conversion consequently renders it transparent. Icon.ToBitmap applies
                // the legacy AND mask first, matching Explorer's rendering path.
                using var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(iconHandle).Clone();
                using var bitmap = icon.ToBitmap();
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;
                var source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.StreamSource = stream;
                source.EndInit();
                source.Freeze();
                return HasVisiblePixels(source) ? source : null;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

        [DllImport("Shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

        [DllImport("Shell32.dll")]
        private static extern int SHBindToObject(
            IntPtr pbc,
            IntPtr pidl,
            IntPtr pUnk,
            ref Guid riid,
            out IntPtr ppv);

        [ComImport, Guid("00021500-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IQueryInfo
        {
            [PreserveSig]
            int GetInfoTip(uint flags, out IntPtr tip);

            [PreserveSig]
            int GetInfoFlags(out uint flags);
        }

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(
            string name,
            IntPtr bindingContext,
            out IntPtr pidl,
            uint attributes,
            out uint attributesOut);

        [DllImport("User32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShFileInfo
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }
    }

    private sealed record DesktopShellItemDescriptor(
        string Clsid,
        string FallbackName,
        bool VisibleByDefault);
}

public sealed record FolderMoveResult(
    int Moved,
    int Skipped,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> MovedPaths,
    IReadOnlyList<FolderMoveEntry>? Moves = null);

public sealed record FolderMoveEntry(
    string SourcePath,
    string DestinationPath,
    FolderTransferOperation Operation = FolderTransferOperation.Move,
    string? DestinationFingerprint = null);

public sealed record FolderTransferPlanEntry(
    string SourcePath,
    string DestinationPath,
    FolderTransferOperation Operation,
    string? SourceStagingPath,
    string? DestinationStagingPath);

internal interface IFolderTransferObserver
{
    void Planned(IReadOnlyList<FolderTransferPlanEntry> plans);
    void Completed(FolderTransferPlanEntry plan, string? destinationFingerprint);
    void Failed(FolderTransferPlanEntry plan, string error);
    void Finished(IReadOnlyList<string> errors);
}

public enum FolderTransferOperation
{
    Copy,
    Move,
    Link
}

public sealed record FolderTransferProgress(
    Guid TransferId,
    FolderTransferOperation Operation,
    string Stage,
    int CompletedItems,
    int TotalItems,
    string? CurrentPath,
    bool CanCancel,
    long CompletedBytes = 0,
    long TotalBytes = 0);

public sealed class FolderTransferConfirmationEventArgs(
    IReadOnlyList<string> sourcePaths,
    string destinationFolder,
    bool crossVolume,
    int fileCount,
    long totalBytes) : EventArgs
{
    public IReadOnlyList<string> SourcePaths { get; } = sourcePaths;
    public string DestinationFolder { get; } = destinationFolder;
    public bool CrossVolume { get; } = crossVolume;
    public int FileCount { get; } = fileCount;
    public long TotalBytes { get; } = totalBytes;
    public bool Approved { get; set; }
}

public sealed record FolderTransferCompletedEventArgs(
    FolderTransferOperation Operation,
    IReadOnlyList<string> SourcePaths,
    string DestinationFolder,
    FolderMoveResult Result,
    bool WasCanceled = false);
