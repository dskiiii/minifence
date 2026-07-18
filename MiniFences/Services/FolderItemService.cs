using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniFences.Models;
using Microsoft.VisualBasic.FileIO;

namespace MiniFences.Services;

public sealed class FolderItemService
{
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
            if (string.IsNullOrWhiteSpace(item.FullPath) ||
                (!File.Exists(item.FullPath) && !Directory.Exists(item.FullPath)))
            {
                error = $"Path does not exist: {item.FullPath}";
                AppLogger.Log($"Open failed because path does not exist: {item.FullPath}");
                return false;
            }

            _startProcess(new ProcessStartInfo
            {
                FileName = item.FullPath,
                UseShellExecute = true
            });
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
                return new FolderMoveResult(0, 0, ["Destination folder does not exist."]);
            }

            var fullDestination = NormalizeDirectoryPath(destinationFolder);
            AppLogger.Log($"Move into folder requested. Destination={fullDestination}");
            var moved = 0;
            var skipped = 0;
            var errors = new List<string>();

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
                    AppLogger.Log($"Move succeeded: {fullSource} -> {destinationPath}");
                }
                catch (Exception itemEx)
                {
                    errors.Add($"{Path.GetFileName(fullSource)}: {itemEx.Message}");
                    AppLogger.LogException($"Move failed for item: {fullSource}", itemEx);
                }
            }

            return new FolderMoveResult(moved, skipped, errors);
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Move into folder failed. Destination={destinationFolder}", ex);
            return new FolderMoveResult(0, 0, [ex.Message]);
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
            Icon = ShellIconProvider.GetIcon(path)
        };
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
        var targetName = Path.HasExtension(newName)
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

        private static ImageSource? LoadIcon(string path)
        {
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
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    iconHandle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(48, 48));
                source.Freeze();
                return source;
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
            try
            {
                var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                return $"{Path.GetFullPath(path)}|{info.LastWriteTimeUtc.Ticks}|{(info is FileInfo file ? file.Length : 0)}";
            }
            catch { return Path.GetFullPath(path); }
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
                var source = Imaging.CreateBitmapSourceFromHIcon(iconHandle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(48, 48));
                source.Freeze();
                return source;
            }
            catch { return null; }
            finally { if (iconHandle != IntPtr.Zero) DestroyIcon(iconHandle); }
        }

        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

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
}

public sealed record FolderMoveResult(int Moved, int Skipped, IReadOnlyList<string> Errors);
