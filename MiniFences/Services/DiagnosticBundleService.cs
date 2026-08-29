using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MiniFences.Models;

namespace MiniFences.Services;

public static partial class DiagnosticBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Create(string destinationZip, AppConfig config,
        IReadOnlyList<ActionTransaction> transactions)
    {
        var parent = Path.GetDirectoryName(destinationZip);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        using var archive = ZipFile.Open(destinationZip, ZipArchiveMode.Create);

        WriteEntry(archive, "system.txt", BuildSystemSummary());
        WriteEntry(archive, "config-redacted.json",
            JsonSerializer.Serialize(CreateRedactedConfig(config), JsonOptions));
        WriteEntry(archive, "history-redacted.json",
            JsonSerializer.Serialize(CreateRedactedHistory(transactions), JsonOptions));
        if (File.Exists(AppLogger.LogPath))
        {
            var lines = File.ReadLines(AppLogger.LogPath).TakeLast(800);
            WriteEntry(archive, "recent-log-redacted.txt", RedactText(string.Join(Environment.NewLine, lines)));
        }
    }

    private static string BuildSystemSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"MiniFences version: {typeof(DiagnosticBundleService).Assembly.GetName().Version?.ToString(3)}");
        builder.AppendLine($"Generated UTC: {DateTime.UtcNow:O}");
        builder.AppendLine($"OS: {Environment.OSVersion.VersionString}");
        builder.AppendLine($"64-bit OS/process: {Environment.Is64BitOperatingSystem}/{Environment.Is64BitProcess}");
        builder.AppendLine($".NET: {Environment.Version}");
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
            builder.AppendLine($"Drive {drive.Name}: format={drive.DriveFormat}; total={drive.TotalSize}; free={drive.AvailableFreeSpace}");
        return builder.ToString();
    }

    private static AppConfig CreateRedactedConfig(AppConfig source)
    {
        var clone = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(source)) ?? new AppConfig();
        clone.DesktopIconOrder = clone.DesktopIconOrder.Select(path => RedactPath(path) ?? "").ToList();
        foreach (var fence in clone.Fences)
        {
            fence.FolderPath = RedactPath(fence.FolderPath) ?? "";
            fence.PortalCurrentPath = RedactPath(fence.PortalCurrentPath);
            fence.AssignedPaths = fence.AssignedPaths.Select(path => RedactPath(path) ?? "").ToList();
        }
        return clone;
    }

    private static IReadOnlyList<ActionTransaction> CreateRedactedHistory(
        IReadOnlyList<ActionTransaction> transactions)
    {
        var clone = JsonSerializer.Deserialize<List<ActionTransaction>>(
            JsonSerializer.Serialize(transactions)) ?? [];
        foreach (var transaction in clone)
        {
            transaction.Errors = transaction.Errors.Select(error => RedactText(error) ?? "").ToList();
            foreach (var entry in transaction.Entries)
            {
                entry.SourcePath = RedactPath(entry.SourcePath);
                entry.DestinationPath = RedactPath(entry.DestinationPath);
                entry.SourceStagingPath = RedactPath(entry.SourceStagingPath);
                entry.DestinationStagingPath = RedactPath(entry.DestinationStagingPath);
                entry.Error = RedactText(entry.Error);
            }
        }
        return clone;
    }

    internal static string? RedactPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || FolderItemService.IsShellNamespacePath(path)) return path;
        try
        {
            var root = Path.GetPathRoot(path) ?? "";
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(root) ? $"…\\{name}" : $"{root}…\\{name}";
        }
        catch { return "<redacted-path>"; }
    }

    internal static string? RedactText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var redacted = text.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(temp, "%TEMP%", StringComparison.OrdinalIgnoreCase);
        return AbsolutePathRegex().Replace(redacted, match =>
        {
            var path = match.Value;
            var name = Path.GetFileName(path.TrimEnd('\\'));
            return $"{path[..3]}…\\{name}";
        });
    }

    private static void WriteEntry(ZipArchive archive, string name, string? content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content ?? string.Empty);
    }

    [GeneratedRegex(@"(?i)[A-Z]:\\[^\r\n;\""<>|]*")]
    private static partial Regex AbsolutePathRegex();
}
