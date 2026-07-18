using System.IO;

namespace MiniFences.Services;

public static class AppLogger
{
    private static readonly object LockObject = new();
    private const long MaxLogBytes = 1_000_000;
    private const int MaxArchiveCount = 3;

    public static string LogPath { get; } = ResolveLogPath();

    private static string ResolveLogPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("MINIFENCES_LOG_PATH");
        return !string.IsNullOrWhiteSpace(overridePath)
            ? Path.GetFullPath(overridePath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniFences", "logs", "app.log");
    }

    public static void Log(string message)
    {
        try
        {
            lock (LockObject)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded();
                File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}{Environment.NewLine}", System.Text.Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break the desktop UI.
        }
    }

    public static void LogException(string message, Exception exception)
    {
        Log($"{message}: {exception}");
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(LogPath))
        {
            return;
        }

        var info = new FileInfo(LogPath);
        if (info.Length < MaxLogBytes)
        {
            return;
        }

        for (var index = MaxArchiveCount - 1; index >= 1; index -= 1)
        {
            var source = GetArchivePath(index);
            var destination = GetArchivePath(index + 1);
            if (File.Exists(source))
            {
                File.Move(source, destination, overwrite: true);
            }
        }

        File.Move(LogPath, GetArchivePath(1), overwrite: true);
    }

    private static string GetArchivePath(int index)
    {
        return Path.Combine(
            Path.GetDirectoryName(LogPath) ?? "",
            $"app.{index}.log");
    }
}
