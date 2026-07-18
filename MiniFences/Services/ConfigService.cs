using System.IO;
using System.Text.Json;
using MiniFences.Models;

namespace MiniFences.Services;

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ConfigPath { get; }

    public string SnapshotDirectory => Path.Combine(Path.GetDirectoryName(ConfigPath) ?? AppContext.BaseDirectory, "snapshots");
    public string NamedLayoutDirectory => Path.Combine(Path.GetDirectoryName(ConfigPath) ?? AppContext.BaseDirectory, "layouts");

    public ConfigService(string? configPath = null)
    {
        ConfigPath = string.IsNullOrWhiteSpace(configPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniFences", "config.json")
            : configPath;
    }

    public AppConfig Load()
    {
        var backupPath = ConfigPath + ".bak";
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return File.Exists(backupPath)
                    ? LoadFromFile(backupPath)
                    : CreateDefaultAppConfig();
            }

            return LoadFromFile(ConfigPath);
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Failed to load config from {ConfigPath}", ex);
            if (File.Exists(backupPath))
            {
                try
                {
                    AppLogger.Log($"Trying backup config: {backupPath}");
                    return LoadFromFile(backupPath);
                }
                catch (Exception backupEx)
                {
                    AppLogger.LogException($"Failed to load backup config from {backupPath}", backupEx);
                }
            }

            return CreateDefaultAppConfig();
        }
    }

    public void Save(AppConfig config)
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = ConfigPath + ".tmp";
        var backupPath = ConfigPath + ".bak";
        try
        {
            var json = JsonSerializer.Serialize(Normalize(config), JsonOptions);
            File.WriteAllText(tempPath, json, System.Text.Encoding.UTF8);

            if (File.Exists(ConfigPath))
            {
                File.Replace(tempPath, ConfigPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, ConfigPath, overwrite: true);
                File.Copy(ConfigPath, backupPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException($"Failed to save config to {ConfigPath}", ex);
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // The original exception is the useful one.
            }

            throw;
        }
    }

    public string SaveSnapshot(AppConfig config)
    {
        Directory.CreateDirectory(SnapshotDirectory);
        var filename = $"layout-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json";
        var path = Path.Combine(SnapshotDirectory, filename);
        WriteLayout(path, CreateLayout(config));
        PruneSnapshots(20);
        return path;
    }

    public IReadOnlyList<LayoutEntry> GetSnapshots() => GetLayoutEntries(SnapshotDirectory, "layout-*.json", false)
        .OrderByDescending(entry => entry.SavedAtUtc)
        .ToArray();

    public bool TryLoadLatestSnapshot(out LayoutDocument? layout, out string? error)
    {
        var latest = GetSnapshots().FirstOrDefault();
        if (latest == null) { layout = null; error = "No layout snapshots are available."; return false; }
        return TryLoadSnapshot(latest.Id, out layout, out error);
    }

    public bool TryLoadSnapshot(string id, out LayoutDocument? layout, out string? error) =>
        TryLoadLayout(Path.Combine(SnapshotDirectory, Path.GetFileName(id)), out layout, out error);

    public string SaveNamedLayout(AppConfig config, string name)
    {
        var normalizedName = NormalizeLayoutName(name);
        Directory.CreateDirectory(NamedLayoutDirectory);
        var path = Path.Combine(NamedLayoutDirectory, $"{normalizedName}.json");
        WriteLayout(path, CreateLayout(config));
        return path;
    }

    public IReadOnlyList<string> GetNamedLayouts() => GetNamedLayoutEntries().Select(entry => entry.DisplayName).ToArray();

    public IReadOnlyList<LayoutEntry> GetNamedLayoutEntries() => GetLayoutEntries(NamedLayoutDirectory, "*.json", true)
        .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    public bool NamedLayoutExists(string name) => File.Exists(GetNamedLayoutPath(name));

    public bool TryLoadNamedLayout(string name, out LayoutDocument? layout, out string? error) =>
        TryLoadLayout(GetNamedLayoutPath(name), out layout, out error);

    public bool RenameNamedLayout(string oldName, string newName, bool overwrite, out string? error)
    {
        error = null;
        try
        {
            var source = GetNamedLayoutPath(oldName);
            var destination = GetNamedLayoutPath(newName);
            if (!File.Exists(source)) { error = "The saved layout no longer exists."; return false; }
            if (!overwrite && File.Exists(destination) && !string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            { error = "A layout with that name already exists."; return false; }
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return true;
            File.Move(source, destination, overwrite);
            return true;
        }
        catch (Exception ex) { AppLogger.LogException("Failed to rename layout", ex); error = ex.Message; return false; }
    }

    public bool DeleteNamedLayout(string name, out string? error)
    {
        error = null;
        try
        {
            var path = GetNamedLayoutPath(name);
            if (!File.Exists(path)) { error = "The saved layout no longer exists."; return false; }
            File.Delete(path);
            return true;
        }
        catch (Exception ex) { AppLogger.LogException("Failed to delete layout", ex); error = ex.Message; return false; }
    }

    public AppConfig ApplyLayout(AppConfig current, LayoutDocument layout, out int invalidPathCount)
    {
        invalidPathCount = 0;
        var currentById = current.Fences.ToDictionary(fence => fence.Id, StringComparer.OrdinalIgnoreCase);
        var restored = new List<FenceConfig>();
        foreach (var saved in layout.Fences)
        {
            var fence = CloneFence(saved);
            if (currentById.TryGetValue(fence.Id, out var existing)) CopyAppearance(existing, fence);
            if (fence.IsDesktopGroup)
                invalidPathCount += fence.AssignedPaths.Count(path => !File.Exists(path) && !Directory.Exists(path));
            else if (!Directory.Exists(fence.FolderPath)) invalidPathCount++;
            restored.Add(fence);
        }

        current.PageCount = Math.Max(1, layout.PageCount);
        current.CurrentPage = Math.Clamp(layout.CurrentPage, 0, current.PageCount - 1);
        current.DesktopIconOrder = layout.DesktopIconOrder.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        current.Fences = restored;
        return Normalize(current);
    }

    private string GetNamedLayoutPath(string name) => Path.Combine(NamedLayoutDirectory, $"{NormalizeLayoutName(name)}.json");

    private IReadOnlyList<LayoutEntry> GetLayoutEntries(string directory, string pattern, bool named)
    {
        if (!Directory.Exists(directory)) return [];
        var entries = new List<LayoutEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, pattern))
        {
            if (!TryLoadLayout(path, out var layout, out _) || layout == null) continue;
            var id = Path.GetFileName(path);
            var name = named ? Path.GetFileNameWithoutExtension(path) : layout.SavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            entries.Add(new LayoutEntry(id, name, layout.SavedAtUtc, layout.Fences.Count, layout.PageCount));
        }
        return entries;
    }

    private bool TryLoadLayout(string path, out LayoutDocument? layout, out string? error)
    {
        layout = null; error = null;
        try
        {
            if (!File.Exists(path)) { error = "The saved layout no longer exists."; return false; }
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            layout = document.RootElement.TryGetProperty(nameof(LayoutDocument.FormatVersion), out _)
                ? JsonSerializer.Deserialize<LayoutDocument>(json, JsonOptions)
                : CreateLayout(JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? throw new InvalidDataException("Invalid legacy layout."));
            if (layout == null || layout.FormatVersion != 1) throw new InvalidDataException("Unsupported layout format.");
            layout.PageCount = Math.Max(1, layout.PageCount);
            layout.Fences ??= [];
            layout.DesktopIconOrder ??= [];
            return true;
        }
        catch (Exception ex) { AppLogger.LogException($"Failed to load layout '{path}'", ex); error = ex.Message; return false; }
    }

    private void WriteLayout(string path, LayoutDocument layout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(layout, JsonOptions), System.Text.Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tempPath, path, null, true);
            else File.Move(tempPath, path);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch (Exception ex) { AppLogger.LogException($"Failed to clean temporary layout file '{tempPath}'", ex); }
        }
    }

    private void PruneSnapshots(int maximum)
    {
        foreach (var path in Directory.EnumerateFiles(SnapshotDirectory, "layout-*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(maximum))
            try { File.Delete(path); } catch (Exception ex) { AppLogger.LogException("Failed to prune layout snapshot", ex); }
    }

    private static LayoutDocument CreateLayout(AppConfig config) => new()
    {
        SavedAtUtc = DateTime.UtcNow,
        CurrentPage = config.CurrentPage,
        PageCount = config.PageCount,
        DesktopIconOrder = config.DesktopIconOrder.ToList(),
        Fences = config.Fences.Select(CloneFence).ToList()
    };

    private static FenceConfig CloneFence(FenceConfig fence) => new()
    {
        Id = fence.Id,
        Title = fence.Title,
        FolderPath = fence.FolderPath,
        Kind = fence.Kind,
        AssignedPaths = fence.AssignedPaths.ToList(),
        PageIndex = fence.PageIndex,
        Left = fence.Left,
        Top = fence.Top,
        Width = fence.Width,
        Height = fence.Height,
        LayerOrder = fence.LayerOrder,
        ExpandedHeight = fence.ExpandedHeight,
        BackgroundColor = fence.BackgroundColor,
        HeaderColor = fence.HeaderColor,
        Opacity = fence.Opacity,
        TitleAlignment = fence.TitleAlignment,
        ShowPath = fence.ShowPath,
        UseCleanStyle = fence.UseCleanStyle,
        SortMode = fence.SortMode,
        IsLocked = fence.IsLocked,
        IsCollapsed = fence.IsCollapsed,
        EnableHoverExpand = fence.EnableHoverExpand,
        EdgeDock = fence.EdgeDock,
        TabGroupId = fence.TabGroupId
    };

    private static void CopyAppearance(FenceConfig source, FenceConfig target)
    {
        target.BackgroundColor = source.BackgroundColor;
        target.HeaderColor = source.HeaderColor;
        target.Opacity = source.Opacity;
        target.TitleAlignment = source.TitleAlignment;
        target.ShowPath = source.ShowPath;
        target.UseCleanStyle = source.UseCleanStyle;
    }

    private static string NormalizeLayoutName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) throw new ArgumentException("Layout name cannot be empty.", nameof(name));
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var safeName = new string(trimmed.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return safeName.Length > 80 ? safeName[..80] : safeName;
    }

    public static FenceConfig CreateNewFence(int index)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return CreateNewFence(index, desktop);
    }

    public static FenceConfig CreateNewFence(int index, string folderPath)
    {
        var title = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = $"Fence {index + 1}";
        }

        return new FenceConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            FolderPath = folderPath,
            Left = 80 + index * 32,
            Top = 80 + index * 32,
            BackgroundColor = DefaultBackgroundColor,
            HeaderColor = DefaultHeaderColor,
            Opacity = 1.0
        };
    }

    public static AppConfig CreateDefaultAppConfig()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return CreateDefaultAppConfig(desktop);
    }

    public static AppConfig CreateDefaultAppConfig(string desktopPath)
    {
        var desktopFence = CreateDefaultFence("\u684c\u9762", desktopPath, 24, 24, 360, 420, "#CC3F7FA8");
        desktopFence.Kind = FenceConfig.DesktopGroupKind;
        desktopFence.AssignedPaths = FolderItemService.EnumerateFileSystemEntriesSafe([desktopPath])
            .Where(IsVisibleDesktopItem).ToList();
        return new AppConfig { Fences = [desktopFence] };
    }

    public static AppConfig CreateDefaultAppConfig(string desktopPath, string documentsPath, string downloadsPath)
    {
        var config = CreateDefaultAppConfig(desktopPath);
        AddExistingFolderDefaultFence(config, "\u6587\u6863", documentsPath);
        AddExistingFolderDefaultFence(config, "\u4e0b\u8f7d", downloadsPath);
        return config;
    }

    private static void AddExistingFolderDefaultFence(AppConfig config, string title, string folderPath)
    {
        if (!Directory.Exists(folderPath) || config.Fences.Any(fence => IsSamePath(fence.FolderPath, folderPath)))
        {
            return;
        }

        var index = config.Fences.Count;
        var fence = CreateDefaultFence(title, folderPath, 80, 80, DefaultStarterFenceWidth, DefaultStarterFenceHeight, GetDefaultHeaderColor(index));
        PlaceDefaultFence(config.Fences, fence);
        config.Fences.Add(fence);
    }

    private static void PlaceDefaultFence(IReadOnlyCollection<FenceConfig> existingFences, FenceConfig fence)
    {
        var position = FenceLayoutService.FindAvailablePosition(existingFences, fence, DefaultWorkspaceWidth, DefaultWorkspaceHeight);
        fence.Left = position.Left;
        fence.Top = position.Top;
    }

    private static FenceConfig CreateDefaultFence()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var fence = CreateDefaultFence("Desktop", desktop, 80, 80, 360, 420, DefaultHeaderColor);
        fence.Kind = FenceConfig.DesktopGroupKind;
        return fence;
    }

    private static FenceConfig CreateDefaultFence(string title, string folderPath, double left, double top, double width, double height, string headerColor)
    {
        return new FenceConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            FolderPath = folderPath,
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            BackgroundColor = DefaultBackgroundColor,
            HeaderColor = headerColor,
            Opacity = 1.0
        };
    }

    private static bool IsSamePath(string left, string right)
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
            return string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsVisibleDesktopItem(string path)
    {
        var name = Path.GetFileName(path);
        if (string.Equals(name, "desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Hidden) && !attributes.HasFlag(FileAttributes.System);
        }
        catch
        {
            return false;
        }
    }

    private static string GetDefaultHeaderColor(int index)
    {
        var colors = new[]
        {
            "#CC3F7FA8",
            "#CC6C8F3F",
            "#CC8F6C3F",
            "#CC8B5FA8",
            "#CCA85F6C",
            "#CC5F8FA8",
            "#CCA89A5F",
            "#CC6F7A87"
        };

        return colors[Math.Abs(index) % colors.Length];
    }

    private static AppConfig Normalize(AppConfig config)
    {
        if (config.Fences.Count == 0)
        {
            config.Fences.Add(CreateDefaultFence());
        }

        var usedFenceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < config.Fences.Count; index += 1)
        {
            config.Fences[index] = Normalize(config.Fences[index], index);
            if (!usedFenceIds.Add(config.Fences[index].Id))
            {
                var duplicateId = config.Fences[index].Id;
                config.Fences[index].Id = Guid.NewGuid().ToString("N");
                usedFenceIds.Add(config.Fences[index].Id);
                AppLogger.Log($"Duplicate Fence id replaced during config normalization: {duplicateId}");
            }
        }

        var maxFencePage = Math.Max(0, config.Fences.Max(fence => fence.PageIndex));
        config.PageCount = Math.Max(1, Math.Max(config.PageCount, Math.Max(maxFencePage, config.CurrentPage) + 1));
        config.CurrentPage = Math.Clamp(config.CurrentPage, 0, config.PageCount - 1);
        config.Language = LocalizationService.NormalizeLanguage(config.Language);
        config.PreviousPageHotkey = string.IsNullOrWhiteSpace(config.PreviousPageHotkey) ? "Ctrl+Alt+Left" : config.PreviousPageHotkey;
        config.NextPageHotkey = string.IsNullOrWhiteSpace(config.NextPageHotkey) ? "Ctrl+Alt+Right" : config.NextPageHotkey;
        config.ToggleTopmostHotkey = string.IsNullOrWhiteSpace(config.ToggleTopmostHotkey) ||
                                     string.Equals(config.ToggleTopmostHotkey, "Win+Space", StringComparison.OrdinalIgnoreCase)
            ? "Ctrl+Alt+Space"
            : config.ToggleTopmostHotkey;
        config.AutoOrganizeRules ??= [];
        config.AutoOrganizeRules = config.AutoOrganizeRules
            .Where(rule => rule != null)
            .Select(rule =>
            {
                rule.Id = string.IsNullOrWhiteSpace(rule.Id) ? Guid.NewGuid().ToString("N") : rule.Id;
                rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "New rule" : rule.Name.Trim();
                rule.Priority = Math.Clamp(rule.Priority, -10000, 10000);
                rule.TargetFenceId ??= "";
                rule.NamePattern ??= "";
                rule.Extensions ??= "";
                if (rule.MinimumSizeMb is < 0) rule.MinimumSizeMb = 0;
                if (rule.MaximumSizeMb is < 0) rule.MaximumSizeMb = null;
                return rule;
            })
            .GroupBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        config.DefaultAutoOrganizeFenceId ??= "";
        config.DesktopIconPositions ??= new Dictionary<string, DesktopIconPositionConfig>(StringComparer.OrdinalIgnoreCase);
        config.DesktopIconPositions = config.DesktopIconPositions
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value != null)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        config.DesktopIconOrder ??= [];
        config.DesktopIconOrder = config.DesktopIconOrder
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return config;
    }

    private static AppConfig LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        if (json.Contains("\"Fences\"", StringComparison.OrdinalIgnoreCase))
        {
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return Normalize(config ?? CreateDefaultAppConfig());
        }

        var legacyFence = JsonSerializer.Deserialize<FenceConfig>(json, JsonOptions);
        return Normalize(new AppConfig
        {
            Fences = [legacyFence ?? CreateDefaultFence()]
        });
    }

    private static FenceConfig Normalize(FenceConfig config, int index)
    {
        config.Id = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString("N") : config.Id;
        config.Title = string.IsNullOrWhiteSpace(config.Title) ? "Mini Fence" : config.Title;
        config.FolderPath = string.IsNullOrWhiteSpace(config.FolderPath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : config.FolderPath;
        config.Kind = string.Equals(config.Kind, FenceConfig.DesktopGroupKind, StringComparison.OrdinalIgnoreCase)
            ? FenceConfig.DesktopGroupKind
            : FenceConfig.FolderPortalKind;
        config.AssignedPaths ??= [];
        config.AssignedPaths = config.AssignedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        config.PageIndex = Math.Max(0, config.PageIndex);
        config.Width = Math.Max(240, config.Width);
        config.Height = Math.Max(180, config.Height);
        config.ExpandedHeight = config.ExpandedHeight is null && config.IsCollapsed && config.Height <= 180
            ? 420
            : Math.Max(180, config.ExpandedHeight ?? config.Height);
        config.BackgroundColor = string.IsNullOrWhiteSpace(config.BackgroundColor) ? DefaultBackgroundColor : config.BackgroundColor;
        config.HeaderColor = string.IsNullOrWhiteSpace(config.HeaderColor) ? DefaultHeaderColor : config.HeaderColor;
        config.Opacity = double.IsNaN(config.Opacity) || double.IsInfinity(config.Opacity)
            ? 1.0
            : Math.Clamp(config.Opacity, 0.0, 1.0);
        config.TitleAlignment = config.TitleAlignment is "Center" or "Right" ? config.TitleAlignment : "Left";
        config.SortMode = config.SortMode is "Name" or "Size" or "ItemType" or "Modified" or "Created" or "Category"
            ? config.SortMode
            : "None";
        if (double.IsNaN(config.Left) || double.IsInfinity(config.Left)) config.Left = 80 + index * 32;
        if (double.IsNaN(config.Top) || double.IsInfinity(config.Top)) config.Top = 80 + index * 32;
        return config;
    }

    private const string DefaultBackgroundColor = "#DD20242A";
    private const string DefaultHeaderColor = "#CC3F7FA8";
    private const double DefaultWorkspaceWidth = 1280;
    private const double DefaultWorkspaceHeight = 720;
    private const double DefaultStarterFenceWidth = 240;
    private const double DefaultStarterFenceHeight = 180;
}
