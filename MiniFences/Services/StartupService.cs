using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Reflection;

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

    public void RefreshEnabledPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(AppName) is not string currentValue || string.IsNullOrWhiteSpace(currentValue))
        {
            return;
        }

        var updatedValue = BuildStartupCommand();
        if (string.Equals(currentValue, updatedValue, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        key.SetValue(AppName, updatedValue);
        AppLogger.Log($"Startup path refreshed: {updatedValue}");
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
            var startupCommand = BuildStartupCommand();
            key.SetValue(AppName, startupCommand);
            AppLogger.Log($"Startup enabled: {startupCommand}");
        }
        else
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            AppLogger.Log("Startup disabled.");
        }
    }

    private static string BuildStartupCommand()
    {
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        var modulePath = Process.GetCurrentProcess().MainModule?.FileName;
        return ResolveStartupCommand(Environment.ProcessPath, entryAssemblyPath, modulePath, File.Exists);
    }

    internal static string ResolveStartupCommand(
        string? processPath,
        string? entryAssemblyPath,
        string? modulePath,
        Func<string, bool> fileExists)
    {
        // Framework-dependent development builds run under dotnet.exe. Keep both
        // the host and DLL in the command because the generated apphost may require
        // a globally installed runtime that is not present on the machine.
        if (IsDotnetHost(processPath) && !string.IsNullOrWhiteSpace(entryAssemblyPath) &&
            string.Equals(Path.GetExtension(entryAssemblyPath), ".dll", StringComparison.OrdinalIgnoreCase) &&
            fileExists(entryAssemblyPath))
        {
            return BuildHiddenFrameworkCommand(
                processPath!,
                entryAssemblyPath,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"WindowsPowerShell\v1.0\powershell.exe"));
        }

        if (IsUsableApplicationPath(processPath)) return $"\"{processPath}\" --background";
        if (IsUsableApplicationPath(modulePath)) return $"\"{modulePath}\" --background";

        if (!string.IsNullOrWhiteSpace(entryAssemblyPath) &&
            string.Equals(Path.GetExtension(entryAssemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            var appHostPath = Path.ChangeExtension(entryAssemblyPath, ".exe");
            if (!string.IsNullOrWhiteSpace(appHostPath) && fileExists(appHostPath))
            {
                return $"\"{appHostPath}\" --background";
            }
        }

        throw new InvalidOperationException("Could not determine MiniFences executable path.");
    }

    private static bool IsDotnetHost(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsUsableApplicationPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !string.Equals(Path.GetFileName(path), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    internal static string BuildHiddenFrameworkCommand(
        string dotnetPath,
        string applicationDllPath,
        string powershellPath)
    {
        static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

        var escapedHost = EscapePowerShellLiteral(dotnetPath);
        var escapedDll = EscapePowerShellLiteral(applicationDllPath);
        var script = $"Start-Process -WindowStyle Hidden -FilePath '{escapedHost}' " +
                     $"-ArgumentList @('{escapedDll}','--background')";
        return $"\"{powershellPath}\" -NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"";
    }
}
