using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiniFences;
using MiniFences.Models;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var testFolder = Path.Combine(Path.GetTempPath(), "MiniFences-VisualHarness-Exact");
        Directory.CreateDirectory(testFolder);
        var testFile = Path.Combine(testFolder, "VMware Workstation Pro.txt");
        if (!File.Exists(testFile)) File.WriteAllText(testFile, "visual verification");
        for (var index = 1; index <= 12; index++)
        {
            var filler = Path.Combine(testFolder, $"ZZ verification item {index:00}.txt");
            if (!File.Exists(filler)) File.WriteAllText(filler, "scroll verification");
        }

        var config = new FenceConfig
        {
            Title = "Long name verification",
            FolderPath = testFolder,
            Width = 360,
            Height = 280,
            ShowPath = false
        };
        var fence = new FenceControl(config)
        {
            Width = config.Width,
            Height = config.Height,
            Margin = new Thickness(20)
        };
        fence.LoadFolderItems();

        var root = new Grid { Background = new SolidColorBrush(Color.FromRgb(38, 43, 49)) };
        root.Children.Add(fence);
        var window = new Window
        {
            Title = "MiniFences Visual Verification",
            Width = 430,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = root
        };
        var mode = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "normal";
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() =>
        {
            if (mode is "selected" or "rename" or "scrolled" or "cleared") fence.SelectItemForTesting(0);
            if (mode == "rename")
            {
                window.Dispatcher.BeginInvoke(() => fence.BeginItemInlineRenameForTesting(0));
            }
            if (mode == "scrolled")
            {
                window.Dispatcher.BeginInvoke(() =>
                {
                    fence.ScrollItemsForTesting(36);
                    window.Title = fence.ExpandedOverlayTracksSelectedItemForTesting()
                        ? "MiniFences Scroll Verification - PASS"
                        : "MiniFences Scroll Verification - FAIL";
                });
            }
            if (mode == "cleared")
            {
                window.Dispatcher.BeginInvoke(() =>
                {
                    fence.RaiseBlankAreaLeftClickForTesting();
                    window.Title = fence.SelectedItemCountForTesting == 0
                        ? "MiniFences Blank Click Verification - PASS"
                        : "MiniFences Blank Click Verification - FAIL";
                });
            }
        });
        var app = new Application();
        app.Run(window);
    }
}
