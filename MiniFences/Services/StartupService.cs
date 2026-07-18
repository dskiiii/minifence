using Microsoft.Win32;
using System.Diagnostics;

namespace MiniFences.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "MiniFences";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (key == null)
        {
            throw new InvalidOperationException("Could not open Windows startup registry key.");
        }

        if (enabled)
        {
            var executablePath = GetExecutablePath();
            key.SetValue(AppName, $"\"{executablePath}\" --background");
            AppLogger.Log($"Startup enabled: {executablePath}");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            AppLogger.Log("Startup disabled.");
        }
    }

    private static string GetExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        var modulePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(modulePath))
        {
            return modulePath;
        }

        throw new InvalidOperationException("Could not determine MiniFences executable path.");
    }
}
