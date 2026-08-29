using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiniFences.Services;

public static class UpdateInstaller
{
    private const string ApplyArgument = "--apply-update";
    private const string RestartBackgroundArgument = "--restart-background";
    internal const string HealthFileArgument = "--update-health-file";
    private const string ManifestName = ".minifences-manifest.json";
    private const int MaximumArchiveEntries = 20_000;
    private const long MaximumEntrySize = 512L * 1024 * 1024;
    private const long MaximumExtractedSize = 2L * 1024 * 1024 * 1024;

    public static bool Launch(string updateArchive, string targetDirectory,
        int ownerProcessId, bool restartInBackground)
    {
        if (!File.Exists(updateArchive) || !Directory.Exists(targetDirectory)) return false;
        try
        {
            CleanupOldUpdaterHosts();
            var host = CreateIndependentUpdaterHost();
            var executable = Path.Combine(host, "MiniFences.exe");
            if (!File.Exists(executable)) return false;
            var startInfo = HiddenStartInfo(executable, host);
            startInfo.ArgumentList.Add(ApplyArgument);
            startInfo.ArgumentList.Add(Path.GetFullPath(updateArchive));
            startInfo.ArgumentList.Add(Path.GetFullPath(targetDirectory));
            startInfo.ArgumentList.Add(ownerProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (restartInBackground) startInfo.ArgumentList.Add(RestartBackgroundArgument);
            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Failed to launch independent MiniFences updater", ex);
            return false;
        }
    }

    internal static bool TryRunFromArguments(IReadOnlyList<string> args)
    {
        var index = Array.FindIndex(args.ToArray(), value =>
            string.Equals(value, ApplyArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 3 >= args.Count) return false;
        if (!int.TryParse(args[index + 3], out var ownerProcessId))
        {
            Environment.Exit(2);
            return true;
        }
        var background = args.Any(value =>
            string.Equals(value, RestartBackgroundArgument, StringComparison.OrdinalIgnoreCase));
        var success = ApplyAndRestart(args[index + 1], args[index + 2], ownerProcessId, background);
        Environment.Exit(success ? 0 : 1);
        return true;
    }

    internal static bool IsSafeArchiveEntry(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || Path.IsPathRooted(entryName)) return false;
        var normalized = entryName.Replace('/', Path.DirectorySeparatorChar);
        return !normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
    }

    internal static string ResolveContainedPathForTesting(string root, string relative) =>
        ResolveContainedPath(root, relative);

    private static bool ApplyAndRestart(string archive, string targetDirectory,
        int ownerProcessId, bool restartInBackground)
    {
        var staging = Path.Combine(Path.GetTempPath(), "MiniFences", "Install", Guid.NewGuid().ToString("N"));
        var healthFile = Path.Combine(Path.GetTempPath(), "MiniFences", "Health", Guid.NewGuid().ToString("N") + ".ok");
        UpdateTransaction? transaction = null;
        try
        {
            if (!WaitForProcessExit(ownerProcessId, TimeSpan.FromSeconds(30)))
                throw new TimeoutException("MiniFences did not close within 30 seconds; the update was cancelled.");
            Directory.CreateDirectory(staging);
            ValidateArchive(archive, staging);
            ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);
            if (!File.Exists(Path.Combine(staging, "MiniFences.exe")))
                throw new InvalidDataException("The update package does not contain MiniFences.exe.");

            transaction = ReplaceFiles(staging, targetDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(healthFile)!);
            var updatedProcess = StartInstalledApplication(targetDirectory, restartInBackground, healthFile);
            if (updatedProcess is null || !WaitForHealth(updatedProcess, healthFile, TimeSpan.FromSeconds(30)))
            {
                TryKill(updatedProcess);
                throw new InvalidOperationException("The updated MiniFences did not report a successful startup.");
            }
            transaction.Commit();
            transaction = null;
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("MiniFences update installation failed", ex);
            if (transaction is not null)
            {
                transaction.Rollback();
                _ = StartInstalledApplication(targetDirectory, restartInBackground, null);
            }
            return false;
        }
        finally
        {
            transaction?.Dispose();
            TryDeleteDirectory(staging);
            TryDeleteFile(healthFile);
            TryDeleteFile(archive);
        }
    }

    private static string CreateIndependentUpdaterHost()
    {
        var sourceRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var host = Path.Combine(Path.GetTempPath(), "MiniFences", "Updater", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(host);
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var destination = ResolveContainedPath(host, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
        return host;
    }

    private static void ValidateArchive(string archive, string extractionRoot)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("The update archive contains too many entries.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!IsSafeArchiveEntry(entry.FullName))
                throw new InvalidDataException("The update archive contains an unsafe path.");
            _ = ResolveContainedPath(extractionRoot, entry.FullName);
            if (entry.Length > MaximumEntrySize)
                throw new InvalidDataException("The update archive contains an oversized file.");
            total = checked(total + entry.Length);
            if (total > MaximumExtractedSize)
                throw new InvalidDataException("The update archive is too large when extracted.");
        }
    }

    private static UpdateTransaction ReplaceFiles(string staging, string targetDirectory)
    {
        var targetRoot = Path.GetFullPath(targetDirectory);
        var newFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .Select(path => NormalizeRelative(Path.GetRelativePath(staging, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var declaredNewFiles = LoadManifestFiles(Path.Combine(staging, ManifestName));
        if (declaredNewFiles.Count > 0 && !declaredNewFiles.All(newFiles.Contains))
            throw new InvalidDataException("The update manifest references a missing file.");
        var oldFiles = LoadManifestFiles(Path.Combine(targetRoot, ManifestName));
        var staleFiles = oldFiles.Except(newFiles, StringComparer.OrdinalIgnoreCase).ToArray();
        var affected = newFiles.Concat(staleFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var backup = Path.Combine(Path.GetTempPath(), "MiniFences", "Backup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var transaction = new UpdateTransaction(targetRoot, backup, affected);
        try
        {
            transaction.CreateBackup();
            foreach (var relative in newFiles)
            {
                var source = ResolveContainedPath(staging, relative);
                var target = ResolveContainedPath(targetRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }
            foreach (var relative in staleFiles)
            {
                var target = ResolveContainedPath(targetRoot, relative);
                if (File.Exists(target)) File.Delete(target);
            }
            return transaction;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static HashSet<string> LoadManifestFiles(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(path));
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in manifest?.Files ?? [])
            {
                if (!IsSafeArchiveEntry(value)) throw new InvalidDataException("Unsafe path in update manifest.");
                result.Add(NormalizeRelative(value));
            }
            return result;
        }
        catch (JsonException ex) { throw new InvalidDataException("The update manifest is invalid.", ex); }
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException) { return true; }
    }

    private static Process? StartInstalledApplication(string targetDirectory,
        bool background, string? healthFile)
    {
        try
        {
            var executable = Path.Combine(targetDirectory, "MiniFences.exe");
            if (!File.Exists(executable)) return null;
            var startInfo = HiddenStartInfo(executable, targetDirectory);
            if (background) startInfo.ArgumentList.Add("--background");
            if (!string.IsNullOrWhiteSpace(healthFile))
            {
                startInfo.ArgumentList.Add(HealthFileArgument);
                startInfo.ArgumentList.Add(healthFile);
            }
            return Process.Start(startInfo);
        }
        catch (Exception ex) { AppLogger.LogException("Failed to restart MiniFences after update", ex); return null; }
    }

    private static bool WaitForHealth(Process process, string healthFile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(healthFile)) return true;
            if (process.HasExited) return false;
            Thread.Sleep(200);
        }
        return File.Exists(healthFile);
    }

    private static ProcessStartInfo HiddenStartInfo(string executable, string workingDirectory) => new()
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = workingDirectory
    };

    private static string ResolveContainedPath(string root, string relative)
    {
        if (!IsSafeArchiveEntry(relative)) throw new InvalidDataException("Unsafe update path.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update path escapes its target directory.");
        return candidate;
    }

    private static string NormalizeRelative(string value) =>
        value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static void CleanupOldUpdaterHosts()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "MiniFences", "Updater");
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.EnumerateDirectories(root))
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-7)) TryDeleteDirectory(directory);
        }
        catch { }
    }

    private static void TryKill(Process? process)
    {
        try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
        finally { process?.Dispose(); }
    }
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }

    private sealed record UpdateManifest(
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("files")] string[] Files);

    private sealed class UpdateTransaction : IDisposable
    {
        private readonly string _targetRoot;
        private readonly string _backupRoot;
        private readonly string[] _affected;
        private bool _finished;
        public UpdateTransaction(string targetRoot, string backupRoot, string[] affected)
        {
            _targetRoot = targetRoot; _backupRoot = backupRoot; _affected = affected;
        }
        public void CreateBackup()
        {
            foreach (var relative in _affected)
            {
                var target = ResolveContainedPath(_targetRoot, relative);
                if (!File.Exists(target)) continue;
                var backup = ResolveContainedPath(_backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, true);
            }
        }
        public void Rollback()
        {
            if (_finished) return;
            foreach (var relative in _affected)
            {
                try
                {
                    var target = ResolveContainedPath(_targetRoot, relative);
                    var backup = ResolveContainedPath(_backupRoot, relative);
                    if (File.Exists(backup))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(backup, target, true);
                    }
                    else if (File.Exists(target)) File.Delete(target);
                }
                catch (Exception ex) { AppLogger.LogException($"Update rollback failed for {relative}", ex); }
            }
            _finished = true;
            TryDeleteDirectory(_backupRoot);
        }
        public void Commit() { _finished = true; TryDeleteDirectory(_backupRoot); }
        public void Dispose() { if (!_finished) Rollback(); }
    }
}
