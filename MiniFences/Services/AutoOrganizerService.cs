using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using MiniFences.Models;

namespace MiniFences.Services;

public sealed class AutoOrganizerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _desktopPath;
    private readonly IReadOnlyList<string> _desktopRoots;
    private readonly string _historyPath;
    private readonly string _assignmentHistoryPath;
    private readonly string? _currentExecutablePath;
    private readonly string _currentDirectory;

    private readonly CategoryRule[] _rules =
    [
        new("\u4e34\u65f6\u6587\u4ef6", [".tmp", ".temp", ".bak", ".old", ".backup", ".crdownload", ".part", ".download"]),
        new("\u622a\u56fe\u56fe\u7247", [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg", ".tif", ".tiff", ".heic"]),
        new("\u6587\u6863\u8d44\u6599", [".doc", ".docx", ".pdf", ".txt", ".rtf", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".md", ".wps", ".et", ".dps", ".json", ".xml", ".log", ".ini", ".yml", ".yaml"]),
        new("\u97f3\u89c6\u9891", [".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v"]),
        new("\u538b\u7f29\u5305", [".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz", ".iso"]),
        new("\u5b89\u88c5\u7a0b\u5e8f", [".exe", ".msi", ".msix", ".appx", ".apk", ".bat", ".cmd", ".ps1"]),
        new("\u5e38\u7528\u5feb\u6377\u65b9\u5f0f", [".lnk", ".url"]),
        new("\u6587\u4ef6\u5939", [])
    ];

    private static readonly string[] OrganizerCategories =
    [
        "\u6e38\u620f",
        "\u622a\u56fe\u56fe\u7247",
        "\u6587\u6863\u8d44\u6599",
        "\u97f3\u89c6\u9891",
        "\u538b\u7f29\u5305",
        "\u5b89\u88c5\u7a0b\u5e8f",
        "\u5e38\u7528\u5feb\u6377\u65b9\u5f0f",
        "\u4e34\u65f6\u6587\u4ef6",
        "\u6587\u4ef6\u5939",
        "\u5176\u4ed6"
    ];

    public AutoOrganizerService(
        string? desktopPath = null,
        string? historyPath = null,
        string? currentExecutablePath = null,
        string? currentDirectory = null,
        IEnumerable<string>? additionalDesktopPaths = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _desktopPath = string.IsNullOrWhiteSpace(desktopPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : desktopPath;
        var extraRoots = additionalDesktopPaths ?? (string.IsNullOrWhiteSpace(desktopPath)
            ? [Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)]
            : Array.Empty<string>());
        _desktopRoots = new[] { _desktopPath }.Concat(extraRoots)
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _historyPath = string.IsNullOrWhiteSpace(historyPath)
            ? Path.Combine(appData, "MiniFences", "organize-history.json")
            : historyPath;
        _assignmentHistoryPath = string.IsNullOrWhiteSpace(historyPath)
            ? Path.Combine(appData, "MiniFences", "organize-membership-history.json")
            : historyPath + ".memberships";
        _currentExecutablePath = currentExecutablePath;
        _currentDirectory = string.IsNullOrWhiteSpace(currentDirectory)
            ? Environment.CurrentDirectory
            : currentDirectory;
    }

    public OrganizationPlan BuildPlan(AppConfig config)
    {
        var desktop = _desktopPath;
        var root = Path.Combine(desktop, "MiniFences Organized");
        var createdFences = new List<FenceConfig>();
        var moves = new List<OrganizationMove>();

        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(desktop))
        {
            var name = Path.GetFileName(sourcePath);
            if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ShouldSkipProtectedEntry(sourcePath))
            {
                AppLogger.Log($"Organizer skipped hidden, system, or reparse-point entry: {sourcePath}");
                continue;
            }

            if (IsSameDirectory(sourcePath, root))
            {
                continue;
            }

            if (IsConfiguredFenceFolder(config, sourcePath))
            {
                AppLogger.Log($"Organizer skipped configured Fence folder: {sourcePath}");
                continue;
            }

            if (IsCurrentApplicationContainer(sourcePath))
            {
                AppLogger.Log($"Organizer skipped current application container: {sourcePath}");
                continue;
            }

            var category = Categorize(sourcePath);
            var targetFence = FindOrCreateFence(config, createdFences, category, root);
            if (IsAlreadyInFolder(sourcePath, targetFence.FolderPath))
            {
                continue;
            }

            moves.Add(new OrganizationMove(sourcePath, targetFence.FolderPath, category));
        }

        return new OrganizationPlan(moves, createdFences);
    }

    public bool TryBuildAutomaticMove(AppConfig config, string sourcePath, out OrganizationMove? move)
    {
        move = null;
        var parentDirectory = string.IsNullOrWhiteSpace(sourcePath) ? null : Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(parentDirectory) ||
            !_desktopRoots.Any(root => IsSameDirectory(parentDirectory, root)) ||
            (!File.Exists(sourcePath) && !Directory.Exists(sourcePath)) ||
            ShouldSkipProtectedEntry(sourcePath) ||
            IsConfiguredFenceFolder(config, sourcePath) ||
            IsCurrentApplicationContainer(sourcePath))
        {
            return false;
        }

        var category = Categorize(sourcePath);
        var root = Path.Combine(_desktopPath, "MiniFences Organized");
        var targetFence = config.Fences.FirstOrDefault(fence =>
            IsSameFolderPath(fence.FolderPath, Path.Combine(root, category)));
        if (targetFence == null || IsAlreadyInFolder(sourcePath, targetFence.FolderPath))
        {
            return false;
        }

        move = new OrganizationMove(sourcePath, targetFence.FolderPath, category);
        return true;
    }

    public IReadOnlyList<FenceConfig> CreateStarterCategoryFences(AppConfig config)
    {
        var createdFences = new List<FenceConfig>();
        foreach (var category in GetStarterCategories())
        {
            var existing = config.Fences.Concat(createdFences)
                .FirstOrDefault(fence => fence.IsDesktopGroup && string.Equals(fence.Title, category, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                continue;
            }

            var fence = ConfigService.CreateNewFence(config.Fences.Count + createdFences.Count, _desktopPath);
            fence.Title = category;
            fence.Kind = FenceConfig.DesktopGroupKind;
            fence.PageIndex = config.CurrentPage;
            createdFences.Add(fence);
        }

        foreach (var fence in createdFences)
        {
            config.Fences.Add(fence);
        }

        return createdFences;
    }

    public int AssignDesktopItemsByType(AppConfig config)
    {
        var paths = FolderItemService.CollapseDesktopEntries(FolderItemService.EnumerateFileSystemEntriesSafe(_desktopRoots))
            .Where(path => !ShouldSkipProtectedEntry(path) && !IsCurrentApplicationContainer(path))
            .ToArray();
        var categorized = paths.GroupBy(path => Categorize(path, config.ClassificationScheme), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var categoryNames = GetAllOrganizerCategories().ToHashSet(StringComparer.OrdinalIgnoreCase);
        config.Fences.RemoveAll(fence => fence.IsDesktopGroup && categoryNames.Contains(fence.Title) &&
                                         fence.AssignedPaths.Count == 0 && !categorized.ContainsKey(fence.Title));
        foreach (var category in categorized.Keys)
        {
            if (config.Fences.Any(fence => fence.IsDesktopGroup && string.Equals(fence.Title, category, StringComparison.OrdinalIgnoreCase))) continue;
            var fence = ConfigService.CreateNewFence(config.Fences.Count, _desktopPath);
            fence.Title = category;
            fence.Kind = FenceConfig.DesktopGroupKind;
            fence.PageIndex = config.CurrentPage;
            fence.AssignedPaths = [];
            config.Fences.Add(fence);
        }
        var assigned = 0;
        foreach (var path in paths)
        {
            var target = config.Fences.FirstOrDefault(fence => fence.IsDesktopGroup &&
                string.Equals(fence.Title, Categorize(path, config.ClassificationScheme), StringComparison.OrdinalIgnoreCase));
            if (target == null) continue;

            foreach (var group in config.Fences.Where(fence => fence.IsDesktopGroup))
            {
                group.AssignedPaths.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            }
            target.AssignedPaths.Add(path);
            assigned += 1;
        }

        foreach (var group in config.Fences.Where(fence => fence.IsDesktopGroup))
        {
            group.AssignedPaths = group.AssignedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        config.Fences.RemoveAll(fence => fence.IsDesktopGroup && categoryNames.Contains(fence.Title) && fence.AssignedPaths.Count == 0);
        return assigned;
    }

    public int AssignDesktopItemsByTypeWithUndo(AppConfig config)
    {
        var before = config.Fences.Select(CloneFenceForHistory).ToArray();
        var existingIds = before.Select(fence => fence.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigned = AssignDesktopItemsByType(config);
        var createdIds = config.Fences.Where(fence => !existingIds.Contains(fence.Id)).Select(fence => fence.Id).ToArray();
        var beforeById = before.ToDictionary(fence => fence.Id, StringComparer.OrdinalIgnoreCase);
        var changed = createdIds.Length > 0 || beforeById.Count != config.Fences.Count || config.Fences.Any(fence =>
            !beforeById.TryGetValue(fence.Id, out var previous) ||
            !previous.AssignedPaths.SequenceEqual(fence.AssignedPaths, StringComparer.OrdinalIgnoreCase));
        if (changed) SaveAssignmentHistory(new DesktopAssignmentHistory(DateTimeOffset.Now, before, createdIds));
        return assigned;
    }

    public int AssignUnassignedDesktopItemsByType(AppConfig config)
    {
        var alreadyAssigned = config.Fences.Where(fence => fence.IsDesktopGroup)
            .SelectMany(fence => fence.AssignedPaths)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unassigned = FolderItemService.CollapseDesktopEntries(FolderItemService.EnumerateFileSystemEntriesSafe(_desktopRoots))
            .Where(path => !alreadyAssigned.Contains(Path.GetFullPath(path)) &&
                           !ShouldSkipProtectedEntry(path) && !IsCurrentApplicationContainer(path))
            .ToArray();
        var assigned = 0;
        foreach (var path in unassigned)
        {
            var category = Categorize(path, config.ClassificationScheme);
            var target = config.Fences.FirstOrDefault(fence => fence.IsDesktopGroup &&
                string.Equals(fence.Title, category, StringComparison.OrdinalIgnoreCase));
            target ??= ResolveTargetFence(config, path);
            if (target == null) continue;
            target.AssignedPaths.Add(path);
            assigned += 1;
        }
        return assigned;
    }

    public int ReassignPlatformGameShortcuts(AppConfig config)
    {
        var gamePaths = config.Fences.Where(fence => fence.IsDesktopGroup)
            .SelectMany(fence => fence.AssignedPaths)
            .Where(IsPlatformGameShortcut)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (gamePaths.Length == 0) return 0;

        var target = config.Fences.FirstOrDefault(fence => fence.IsDesktopGroup && fence.Title == "\u6e38\u620f");
        if (target == null)
        {
            target = ConfigService.CreateNewFence(config.Fences.Count, _desktopPath);
            target.Title = "\u6e38\u620f";
            target.Kind = FenceConfig.DesktopGroupKind;
            target.PageIndex = config.CurrentPage;
            target.AssignedPaths = [];
            config.Fences.Add(target);
        }
        foreach (var path in gamePaths)
        {
            foreach (var fence in config.Fences.Where(fence => fence.IsDesktopGroup && fence.Id != target.Id))
                fence.AssignedPaths.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            if (!target.AssignedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) target.AssignedPaths.Add(path);
        }
        return gamePaths.Length;
    }

    public string GetCategoryForPath(string path) => Categorize(path);

    public static string[] GetCategoriesForScheme(string scheme) => scheme is "Simple"
        ? ["\u6e38\u620f", "\u7a0b\u5e8f\u548c\u5feb\u6377\u65b9\u5f0f", "\u6587\u4ef6\u5939", "\u6587\u6863\u548c\u6587\u4ef6", "\u56fe\u7247\u548c\u5a92\u4f53", "\u538b\u7f29\u5305", "\u5176\u4ed6"]
        : GetStarterCategories();

    private static IEnumerable<string> GetAllOrganizerCategories() =>
        GetStarterCategories().Concat(GetCategoriesForScheme("Simple")).Distinct(StringComparer.OrdinalIgnoreCase);

    public FenceConfig? ResolveTargetFence(AppConfig config, string path)
    {
        if (ShouldSkipProtectedEntry(path)) return null;
        foreach (var rule in config.AutoOrganizeRules
                     .Where(rule => rule.IsEnabled)
                     .OrderByDescending(rule => rule.Priority)
                     .ThenBy(rule => rule.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!RuleMatches(rule, path)) continue;
            var target = config.Fences.FirstOrDefault(fence =>
                fence.IsDesktopGroup &&
                string.Equals(fence.Id, rule.TargetFenceId, StringComparison.OrdinalIgnoreCase));
            if (target != null) return target;
        }

        var categoryTarget = config.Fences.FirstOrDefault(fence => fence.IsDesktopGroup &&
            string.Equals(fence.Title, Categorize(path, config.ClassificationScheme), StringComparison.OrdinalIgnoreCase));
        if (categoryTarget != null) return categoryTarget;

        if (!string.IsNullOrWhiteSpace(config.DefaultAutoOrganizeFenceId))
        {
            var fallback = config.Fences.FirstOrDefault(fence =>
                fence.IsDesktopGroup &&
                string.Equals(fence.Id, config.DefaultAutoOrganizeFenceId, StringComparison.OrdinalIgnoreCase));
            if (fallback != null) return fallback;
        }

        return null;
    }

    public static bool RuleMatches(AutoOrganizeRule rule, string path)
    {
        var isFolder = Directory.Exists(path);
        if (rule.FoldersOnly && !isFolder) return false;
        if (!string.IsNullOrWhiteSpace(rule.NamePattern))
        {
            var name = Path.GetFileName(path);
            var patterns = rule.NamePattern.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!patterns.Any(pattern => WildcardMatch(name, pattern))) return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Extensions))
        {
            if (isFolder) return false;
            var extension = Path.GetExtension(path);
            var extensions = rule.Extensions.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.StartsWith('.') ? value : "." + value);
            if (!extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return false;
        }

        if (!isFolder && (rule.MinimumSizeMb.HasValue || rule.MaximumSizeMb.HasValue))
        {
            var sizeMb = new FileInfo(path).Length / 1024d / 1024d;
            if (rule.MinimumSizeMb.HasValue && sizeMb < rule.MinimumSizeMb.Value) return false;
            if (rule.MaximumSizeMb.HasValue && sizeMb > rule.MaximumSizeMb.Value) return false;
        }

        return true;
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        var expression = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(
            value,
            expression,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    public bool ConvertLegacyDesktopPortal(AppConfig config)
    {
        var changed = false;
        var alreadyAssigned = config.Fences.Where(fence => fence.IsDesktopGroup)
            .SelectMany(fence => fence.AssignedPaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var fence in config.Fences.Where(fence => !fence.IsDesktopGroup && IsSameFolderPath(fence.FolderPath, _desktopPath)))
        {
            fence.Kind = FenceConfig.DesktopGroupKind;
            fence.AssignedPaths = Directory.EnumerateFileSystemEntries(_desktopPath)
                .Where(path => !alreadyAssigned.Contains(path) && !ShouldSkipProtectedEntry(path))
                .ToList();
            changed = true;
        }
        return changed;
    }

    public DesktopGroupMigrationResult MigrateManagedFoldersToDesktopGroups(AppConfig config)
    {
        var migratedFences = 0;
        var restoredItems = 0;
        var errors = new List<string>();
        foreach (var fence in config.Fences.Where(fence => !fence.IsDesktopGroup && IsManagedCategoryFolder(fence.FolderPath)).ToList())
        {
            var assignedPaths = new List<string>();
            if (Directory.Exists(fence.FolderPath))
            {
                foreach (var sourcePath in Directory.EnumerateFileSystemEntries(fence.FolderPath).ToList())
                {
                    try
                    {
                        var destinationPath = GetAvailableDestinationPath(sourcePath, _desktopPath);
                        if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, destinationPath);
                        else File.Move(sourcePath, destinationPath);
                        assignedPaths.Add(destinationPath);
                        restoredItems += 1;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{Path.GetFileName(sourcePath)}: {ex.Message}");
                    }
                }
            }

            fence.Kind = FenceConfig.DesktopGroupKind;
            fence.FolderPath = _desktopPath;
            fence.AssignedPaths = assignedPaths;
            migratedFences += 1;
        }

        var assigned = config.Fences.Where(fence => fence.IsDesktopGroup)
            .SelectMany(fence => fence.AssignedPaths)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var desktopFence in config.Fences.Where(fence => !fence.IsDesktopGroup && IsSameFolderPath(fence.FolderPath, _desktopPath)).ToList())
        {
            desktopFence.Kind = FenceConfig.DesktopGroupKind;
            desktopFence.AssignedPaths = Directory.EnumerateFileSystemEntries(_desktopPath)
                .Where(path => !assigned.Contains(path) && !ShouldSkipProtectedEntry(path))
                .ToList();
            migratedFences += 1;
        }

        var root = Path.Combine(_desktopPath, "MiniFences Organized");
        try
        {
            foreach (var directory in Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : [])
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }

        return new DesktopGroupMigrationResult(migratedFences, restoredItems, errors);
    }

    public static string[] GetStarterCategories()
    {
        return
        [
            "\u6e38\u620f",
            "\u5e38\u7528\u5feb\u6377\u65b9\u5f0f",
            "\u6587\u6863\u8d44\u6599",
            "\u622a\u56fe\u56fe\u7247",
            "\u97f3\u89c6\u9891",
            "\u538b\u7f29\u5305",
            "\u5b89\u88c5\u7a0b\u5e8f",
            "\u4e34\u65f6\u6587\u4ef6",
            "\u6587\u4ef6\u5939",
            "\u5176\u4ed6"
        ];
    }

    public static IReadOnlyList<string> GetOrganizerCategories()
    {
        return OrganizerCategories;
    }

    public static bool TryEnsureManagedCategoryFolder(string folderPath, out bool created, out string? error)
    {
        created = false;
        error = null;
        if (!IsManagedCategoryFolder(folderPath))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
                created = true;
                AppLogger.Log($"Recreated managed category folder: {folderPath}");
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.LogException($"Failed to recreate managed category folder: {folderPath}", ex);
            return false;
        }
    }

    public static bool IsManagedCategoryFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return false;
        }

        var categoryName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!GetStarterCategories().Contains(categoryName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var parent = Path.GetDirectoryName(folderPath);
        return IsManagedRootFolder(parent);
    }

    public static bool IsManagedRootFolder(string? folderPath)
    {
        return !string.IsNullOrWhiteSpace(folderPath) &&
               string.Equals(
                   Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                   "MiniFences Organized",
                   StringComparison.OrdinalIgnoreCase);
    }

    public OrganizationResult ApplyPlan(AppConfig config, OrganizationPlan plan)
    {
        foreach (var fence in plan.CreatedFences)
        {
            if (config.Fences.All(existing => existing.Id != fence.Id))
            {
                config.Fences.Add(fence);
            }
        }

        var moved = 0;
        var skipped = 0;
        var errors = new List<string>();
        var undoEntries = new List<OrganizationUndoEntry>();
        foreach (var move in plan.Moves)
        {
            try
            {
                EnsureMoveTargetFolder(move.TargetFolder);
                if (!File.Exists(move.SourcePath) && !Directory.Exists(move.SourcePath))
                {
                    skipped += 1;
                    continue;
                }

                var destinationPath = GetAvailableDestinationPath(move.SourcePath, move.TargetFolder);
                if (Directory.Exists(move.SourcePath))
                {
                    Directory.Move(move.SourcePath, destinationPath);
                }
                else
                {
                    File.Move(move.SourcePath, destinationPath);
                }

                undoEntries.Add(new OrganizationUndoEntry(move.SourcePath, destinationPath));
                moved += 1;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(move.SourcePath)}: {ex.Message}");
            }
        }

        if (undoEntries.Count > 0)
        {
            SaveHistory(new OrganizationHistory(
                DateTimeOffset.Now,
                undoEntries,
                plan.CreatedFences.Select(fence => fence.Id).ToList()));
        }

        return new OrganizationResult(moved, skipped, errors);
    }

    private static void EnsureMoveTargetFolder(string targetFolder)
    {
        TryEnsureManagedCategoryFolder(targetFolder, out _, out _);
        Directory.CreateDirectory(targetFolder);
    }

    public bool HasUndoHistory()
    {
        return File.Exists(_assignmentHistoryPath) || File.Exists(_assignmentHistoryPath + ".bak") ||
               File.Exists(_historyPath) || File.Exists(_historyPath + ".bak");
    }

    public bool HasDesktopAssignmentUndoHistory() =>
        File.Exists(_assignmentHistoryPath) || File.Exists(_assignmentHistoryPath + ".bak");

    public OrganizationUndoResult UndoLastDesktopAssignment(AppConfig config)
    {
        var assignmentHistory = LoadAssignmentHistory();
        if (assignmentHistory == null)
            return new OrganizationUndoResult(0, 0, 0, ["No desktop assignment history is available."]);

        var restored = 0;
        foreach (var snapshot in assignmentHistory.Fences)
        {
            var current = config.Fences.FirstOrDefault(fence => string.Equals(fence.Id, snapshot.Id, StringComparison.OrdinalIgnoreCase));
            if (current == null)
            {
                config.Fences.Add(CloneFenceForHistory(snapshot));
                restored += snapshot.AssignedPaths.Count;
                continue;
            }
            restored += current.AssignedPaths.Except(snapshot.AssignedPaths, StringComparer.OrdinalIgnoreCase).Count();
            restored += snapshot.AssignedPaths.Except(current.AssignedPaths, StringComparer.OrdinalIgnoreCase).Count();
            current.AssignedPaths = snapshot.AssignedPaths.ToList();
        }
        var removed = config.Fences.RemoveAll(fence => assignmentHistory.CreatedFenceIds.Contains(fence.Id, StringComparer.OrdinalIgnoreCase));
        DeleteHistoryFile(_assignmentHistoryPath);
        return new OrganizationUndoResult(restored, 0, removed, []);
    }

    public OrganizationUndoResult UndoLastOrganization(AppConfig config)
    {
        var assignmentHistory = LoadAssignmentHistory();
        if (assignmentHistory != null)
        {
            return UndoLastDesktopAssignment(config);
        }

        var history = LoadHistory();
        if (history == null || history.Moves.Count == 0)
        {
            return new OrganizationUndoResult(0, 0, 0, ["No organization history is available."]);
        }

        var moved = 0;
        var skipped = 0;
        var errors = new List<string>();
        foreach (var entry in history.Moves.Reverse())
        {
            try
            {
                if (!File.Exists(entry.DestinationPath) && !Directory.Exists(entry.DestinationPath))
                {
                    skipped += 1;
                    continue;
                }

                var originalFolder = Path.GetDirectoryName(entry.SourcePath);
                if (string.IsNullOrWhiteSpace(originalFolder))
                {
                    skipped += 1;
                    continue;
                }

                Directory.CreateDirectory(originalFolder);
                var isDirectory = Directory.Exists(entry.DestinationPath);
                var restorePath = GetAvailableRestorePath(entry.SourcePath, isDirectory);
                if (isDirectory)
                {
                    Directory.Move(entry.DestinationPath, restorePath);
                }
                else
                {
                    File.Move(entry.DestinationPath, restorePath);
                }

                moved += 1;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(entry.DestinationPath)}: {ex.Message}");
            }
        }

        var removedFences = RemoveEmptyCreatedFences(config, history.CreatedFenceIds);
        if (moved > 0 && errors.Count == 0)
        {
            DeleteHistory();
        }

        return new OrganizationUndoResult(moved, skipped, removedFences, errors);
    }

    private FenceConfig FindOrCreateFence(AppConfig config, List<FenceConfig> createdFences, string category, string root)
    {
        var folderPath = Path.Combine(root, category);
        var existing = config.Fences.Concat(createdFences)
            .FirstOrDefault(fence => IsSameFolderPath(fence.FolderPath, folderPath));
        if (existing != null)
        {
            return existing;
        }

        var index = config.Fences.Count + createdFences.Count;
        var fence = ConfigService.CreateNewFence(index);
        fence.Title = category;
        fence.FolderPath = folderPath;
        fence.PageIndex = config.CurrentPage;
        createdFences.Add(fence);
        return fence;
    }

    private string Categorize(string path, string scheme = "Detailed")
    {
        if (Directory.Exists(path))
        {
            return "\u6587\u4ef6\u5939";
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (IsPlatformGameShortcut(path)) return "\u6e38\u620f";
        if (scheme is "Simple")
        {
            if (new[] { ".lnk", ".url", ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".ps1" }.Contains(extension)) return "\u7a0b\u5e8f\u548c\u5feb\u6377\u65b9\u5f0f";
            if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".mp3", ".wav", ".flac", ".mp4", ".mkv", ".avi", ".mov" }.Contains(extension)) return "\u56fe\u7247\u548c\u5a92\u4f53";
            if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".iso" }.Contains(extension)) return "\u538b\u7f29\u5305";
            if (new[] { ".doc", ".docx", ".pdf", ".txt", ".rtf", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".md", ".wps" }.Contains(extension)) return "\u6587\u6863\u548c\u6587\u4ef6";
            return "\u5176\u4ed6";
        }
        foreach (var rule in _rules)
        {
            if (rule.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return rule.Name;
            }
        }

        return "\u5176\u4ed6";
    }

    internal static bool IsPlatformGameShortcut(string path)
    {
        var descriptor = GetShortcutLaunchDescriptor(path);
        if (string.IsNullOrWhiteSpace(descriptor)) return false;
        return descriptor.Contains("steam://rungameid/", StringComparison.OrdinalIgnoreCase) ||
               descriptor.Contains("steam://run/", StringComparison.OrdinalIgnoreCase) ||
               (descriptor.Contains("steam.exe", StringComparison.OrdinalIgnoreCase) &&
                descriptor.Contains("-applaunch", StringComparison.OrdinalIgnoreCase)) ||
               descriptor.Contains("com.epicgames.launcher://apps/", StringComparison.OrdinalIgnoreCase) ||
               descriptor.Contains("epicgames://launch", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetShortcutLaunchDescriptor(string path)
    {
        try
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            {
                return File.ReadLines(path)
                    .FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..].Trim();
            }
            if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
                if (shortcut == null) return null;
                var shortcutType = shortcut.GetType();
                var targetPath = shortcutType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null)?.ToString();
                var arguments = shortcutType.InvokeMember("Arguments", BindingFlags.GetProperty, null, shortcut, null)?.ToString();
                return $"{targetPath} {arguments}";
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Could not inspect shortcut for game classification: {path}", ex);
            return null;
        }
    }

    private static bool ShouldSkipProtectedEntry(string path)
    {
        if (Path.GetFileName(path).StartsWith("~$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            const FileAttributes protectedAttributes =
                FileAttributes.Hidden |
                FileAttributes.System |
                FileAttributes.ReparsePoint;
            return (attributes & protectedAttributes) != 0;
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Organizer could not read entry attributes and skipped it: {path}", ex);
            return true;
        }
    }

    private static bool IsAlreadyInFolder(string sourcePath, string folderPath)
    {
        var sourceParent = Path.GetFullPath(Path.GetDirectoryName(sourcePath) ?? "");
        var target = Path.GetFullPath(folderPath);
        return string.Equals(sourceParent.TrimEnd(Path.DirectorySeparatorChar),
            target.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameDirectory(string left, string right)
    {
        return IsSameFolderPath(left, right);
    }

    private static bool IsSameFolderPath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(
                left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsConfiguredFenceFolder(AppConfig config, string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return false;
        }

        return config.Fences.Any(fence =>
            IsSameFolderPath(fence.FolderPath, sourcePath));
    }

    private bool IsCurrentApplicationContainer(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
        {
            return false;
        }

        var executablePath = _currentExecutablePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        var normalizedSource = NormalizeDirectoryPath(sourcePath);
        var currentDirectory = NormalizeDirectoryPath(_currentDirectory);
        if (currentDirectory.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            currentDirectory.StartsWith(normalizedSource + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedExecutable = Path.GetFullPath(executablePath);
        return normalizedExecutable.StartsWith(normalizedSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedExecutable.StartsWith(normalizedSource + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAvailableDestinationPath(string sourcePath, string destinationFolder, bool? sourceIsDirectory = null)
    {
        var name = Path.GetFileName(sourcePath);
        var destinationPath = Path.Combine(destinationFolder, name);
        if (!File.Exists(destinationPath) && !Directory.Exists(destinationPath))
        {
            return destinationPath;
        }

        var isDirectory = sourceIsDirectory ?? Directory.Exists(sourcePath);
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

    private static string GetAvailableRestorePath(string originalPath, bool isDirectory)
    {
        var destinationFolder = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrWhiteSpace(destinationFolder))
        {
            throw new IOException("The original folder is not available.");
        }

        return GetAvailableDestinationPath(originalPath, destinationFolder, isDirectory);
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private int RemoveEmptyCreatedFences(AppConfig config, IReadOnlyList<string> createdFenceIds)
    {
        var removed = 0;
        foreach (var fenceId in createdFenceIds)
        {
            var fence = config.Fences.FirstOrDefault(item => string.Equals(item.Id, fenceId, StringComparison.OrdinalIgnoreCase));
            if (fence == null || !Directory.Exists(fence.FolderPath))
            {
                continue;
            }

            if (Directory.EnumerateFileSystemEntries(fence.FolderPath).Any())
            {
                continue;
            }

            config.Fences.Remove(fence);
            removed += 1;
        }

        return removed;
    }

    private OrganizationHistory? LoadHistory()
    {
        var backupPath = _historyPath + ".bak";
        try
        {
            if (!File.Exists(_historyPath))
            {
                return File.Exists(backupPath) ? LoadHistoryFromFile(backupPath) : null;
            }

            return LoadHistoryFromFile(_historyPath);
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Failed to load organization history from {_historyPath}", ex);
            if (File.Exists(backupPath))
            {
                try
                {
                    AppLogger.Log($"Trying backup organization history: {backupPath}");
                    return LoadHistoryFromFile(backupPath);
                }
                catch (Exception backupEx)
                {
                    AppLogger.LogException($"Failed to load backup organization history from {backupPath}", backupEx);
                }
            }

            return null;
        }
    }

    private void SaveHistory(OrganizationHistory history)
    {
        var directory = Path.GetDirectoryName(_historyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _historyPath + ".tmp";
        var backupPath = _historyPath + ".bak";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(history, JsonOptions), System.Text.Encoding.UTF8);
            if (File.Exists(_historyPath))
            {
                File.Replace(tempPath, _historyPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _historyPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Failed to save organization history to {_historyPath}", ex);
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // The save failure above is the useful error.
            }

            throw;
        }
    }

    private void SaveAssignmentHistory(DesktopAssignmentHistory history)
    {
        try { SaveHistoryFile(_assignmentHistoryPath, history); }
        catch (Exception ex) { AppLogger.LogException($"Failed to save desktop assignment history to {_assignmentHistoryPath}", ex); }
    }

    private DesktopAssignmentHistory? LoadAssignmentHistory()
    {
        try
        {
            var path = File.Exists(_assignmentHistoryPath) ? _assignmentHistoryPath : _assignmentHistoryPath + ".bak";
            return File.Exists(path)
                ? JsonSerializer.Deserialize<DesktopAssignmentHistory>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Failed to load desktop assignment history from {_assignmentHistoryPath}", ex);
            return null;
        }
    }

    private static void SaveHistoryFile<T>(string path, T history)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(history, JsonOptions), System.Text.Encoding.UTF8);
        if (File.Exists(path)) File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
        else File.Move(tempPath, path, overwrite: true);
    }

    private static void DeleteHistoryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
        catch { }
    }

    private static FenceConfig CloneFenceForHistory(FenceConfig fence) =>
        JsonSerializer.Deserialize<FenceConfig>(JsonSerializer.Serialize(fence, JsonOptions), JsonOptions) ?? new FenceConfig();

    private void DeleteHistory()
    {
        try
        {
            if (File.Exists(_historyPath))
            {
                File.Delete(_historyPath);
            }

            var backupPath = _historyPath + ".bak";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch
        {
            // A stale history file is not fatal; a later undo attempt will report what can still be restored.
        }
    }

    private static OrganizationHistory? LoadHistoryFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<OrganizationHistory>(json, JsonOptions);
    }

    private sealed record CategoryRule(string Name, string[] Extensions);
}

public sealed record OrganizationMove(string SourcePath, string TargetFolder, string Category);
public sealed record DesktopGroupMigrationResult(int MigratedFences, int RestoredItems, IReadOnlyList<string> Errors);

public sealed record OrganizationPlan(IReadOnlyList<OrganizationMove> Moves, IReadOnlyList<FenceConfig> CreatedFences);

public sealed record OrganizationResult(int Moved, int Skipped, IReadOnlyList<string> Errors);

public sealed record OrganizationUndoEntry(string SourcePath, string DestinationPath);

public sealed record OrganizationHistory(DateTimeOffset CreatedAt, IReadOnlyList<OrganizationUndoEntry> Moves, IReadOnlyList<string> CreatedFenceIds);

public sealed record OrganizationUndoResult(int Moved, int Skipped, int RemovedFences, IReadOnlyList<string> Errors);

public sealed record DesktopAssignmentHistory(DateTimeOffset CreatedAt, IReadOnlyList<FenceConfig> Fences, IReadOnlyList<string> CreatedFenceIds);
