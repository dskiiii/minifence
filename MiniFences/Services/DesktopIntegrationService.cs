using System.Diagnostics;
using Microsoft.Win32;

namespace MiniFences.Services;

public sealed class DesktopIntegrationService
{
    private const string MenuPath = @"Software\Classes\Directory\Background\shell\MiniFences";

    public void EnsureDesktopContextMenu()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            using var menu = Registry.CurrentUser.CreateSubKey(MenuPath);
            menu.SetValue("", "Create MiniFences Fence");
            menu.SetValue("MUIVerb", "Create MiniFences Fence");
            menu.SetValue("Icon", executable);
            using var command = menu.CreateSubKey("command");
            command.SetValue("", BuildCommand(executable));
        }
        catch (Exception ex) { AppLogger.LogException("Could not register the desktop context-menu entry.", ex); }
    }

    internal static string BuildCommand(string executable) => $"\"{executable}\" --new-fence";
}
