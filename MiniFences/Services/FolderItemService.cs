using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Drawing.Imaging;
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
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(ShouldShowItem)
            .Select(CreateItem)
            .ToList();
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
    {
        try
        {
            AutoOrganizerService.TryEnsureManagedCategoryFolder(destinationFolder, out _, out _);
            if (string.IsNullOrWhiteSpace(destinationFolder) || !Directory.Exists(destinationFolder))
            {
                AppLogger.Log($"Move failed because destination folder does not exist: {destinationFolder}");
                return new FolderMoveResult(0, 0, ["Destination folder does not exist."], []);
            }

            var fullDestination = NormalizeDirectoryPath(destinationFolder);
            AppLogger.Log($"Move into folder requested. Destination={fullDestination}");
            var moved = 0;
            var skipped = 0;
            var errors = new List<string>();
            var movedPaths = new List<string>();
            var moves = new List<FolderMoveEntry>();

            foreach (var sourcePath in sourcePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                var fullSource = Path.GetFullPath(sourcePath);
                if (!File.Exists(fullSource) && !Directory.Exists(fullSource))
                {
                    AppLogger.Log($"Move skipped because source does not exist: {fullSource}");
                    skipped += 1;
                    continue;
                }

                if (Directory.Exists(fullSource) && IsSameOrParentOf(fullSource, fullDestination))
                {
                    AppLogger.Log($"Move skipped because source folder contains destination. Source={fullSource}; Destination={fullDestination}");
                    skipped += 1;
                    continue;
                }

                var sourceParent = NormalizeDirectoryPath(Path.GetDirectoryName(fullSource) ?? "");
                if (string.Equals(sourceParent, fullDestination, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Log($"Move skipped because source is already in destination: {fullSource}");
                    skipped += 1;
                    continue;
                }

                try
                {
                    var destinationPath = GetAvailableDestinationPath(fullSource, fullDestination);
                    MoveFileSystemEntry(fullSource, destinationPath);
                    moved += 1;
                    movedPaths.Add(destinationPath);
                    moves.Add(new FolderMoveEntry(fullSource, destinationPath));
                    AppLogger.Log($"Move succeeded: {fullSource} -> {destinationPath}");
                }
                catch (Exception itemEx)
                {
                    errors.Add($"{Path.GetFileName(fullSource)}: {itemEx.Message}");
                    AppLogger.LogException($"Move failed for item: {fullSource}", itemEx);
                }
            }

            return new FolderMoveResult(moved, skipped, errors, movedPaths, moves);
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Move into folder failed. Destination={destinationFolder}", ex);
            return new FolderMoveResult(0, 0, [ex.Message], []);
        }
    }

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
        return new FolderItem
        {
            Name = ShellIconProvider.GetDisplayName(parsingName) ?? fallbackName,
            FullPath = parsingName,
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

    private static void MoveFileSystemEntry(string sourcePath, string destinationPath)
    {
        if (Directory.Exists(sourcePath))
        {
            try
            {
                Directory.Move(sourcePath, destinationPath);
                return;
            }
            catch (IOException ex)
            {
                AppLogger.LogException($"Directory.Move failed; trying copy/delete fallback. Source={sourcePath}; Destination={destinationPath}", ex);
                CopyDirectory(sourcePath, destinationPath);
                Directory.Delete(sourcePath, recursive: true);
                return;
            }
        }

        try
        {
            File.Move(sourcePath, destinationPath);
        }
        catch (IOException ex)
        {
            AppLogger.LogException($"File.Move failed; trying copy/delete fallback. Source={sourcePath}; Destination={destinationPath}", ex);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            File.Delete(sourcePath);
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(filePath));
            File.Copy(filePath, destinationPath, overwrite: false);
        }

        foreach (var directoryPath in Directory.EnumerateDirectories(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(directoryPath));
            CopyDirectory(directoryPath, destinationPath);
        }
    }

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
                return GetShellNamespaceInfo(path, includeIcon: true).Icon;
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
            GetShellNamespaceInfo(parsingName, includeIcon: false).DisplayName;

        private static (ImageSource? Icon, string? DisplayName) GetShellNamespaceInfo(
            string parsingName,
            bool includeIcon)
        {
            IntPtr pidl = IntPtr.Zero;
            IntPtr iconHandle = IntPtr.Zero;
            try
            {
                if (SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _) != 0 ||
                    pidl == IntPtr.Zero)
                {
                    return (null, null);
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
                if (result == IntPtr.Zero) return (null, null);
                iconHandle = info.hIcon;
                var icon = includeIcon && iconHandle != IntPtr.Zero
                    ? CreateShellBitmapSource(iconHandle)
                    : null;
                return (icon, string.IsNullOrWhiteSpace(info.szDisplayName) ? null : info.szDisplayName);
            }
            catch
            {
                return (null, null);
            }
            finally
            {
                if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
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

public sealed record FolderMoveEntry(string SourcePath, string DestinationPath);
