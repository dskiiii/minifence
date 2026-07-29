using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
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
        var mode = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "normal";
        var childFolder = Path.Combine(testFolder, "Portal Child");
        Directory.CreateDirectory(childFolder);
        if (mode == "large")
        {
            for (var index = 0; index < 800; index++)
            {
                var largeItem = Path.Combine(testFolder, $"Large item {index:0000}.txt");
                if (!File.Exists(largeItem)) File.WriteAllText(largeItem, "virtualized");
            }
        }

        var config = new FenceConfig
        {
            Title = "Long name verification",
            FolderPath = testFolder,
            Width = 360,
            Height = 280,
            ShowPath = false
        };
        if (mode == "portal") config.PortalViewMode = "List";
        var fence = new FenceControl(config)
        {
            Width = config.Width,
            Height = config.Height,
            Margin = new Thickness(20)
        };
        if (mode == "tabdrag")
        {
            config.TabGroupId = "visual-tab-drag";
            fence.AllowTabDetachWithoutShiftForTesting = true;
            fence.SetTabStatus(2, 0, ["Desktop", "Downloads"], useTabStrip: true);
            fence.TabDetachRequested += (_, point) =>
                Application.Current.Dispatcher.BeginInvoke(() =>
                    Application.Current.MainWindow!.Title =
                        $"MiniFences Tab Drag Verification - PASS ({point.X},{point.Y})");
        }
        if (mode == "colorcapturezh")
            fence.SetLocalization(new MiniFences.Services.LocalizationService { Language = MiniFences.Services.LocalizationService.Chinese });
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
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() =>
        {
            if (mode == "portal")
            {
                fence.NavigatePortalForTesting(childFolder);
                window.Title = fence.IsPortalNavigationVisibleForTesting &&
                               string.Equals(fence.PortalPathForTesting, childFolder, StringComparison.OrdinalIgnoreCase)
                    ? "MiniFences Portal Navigation Verification - PASS"
                    : "MiniFences Portal Navigation Verification - FAIL";
            }
            if (mode == "large")
            {
                window.UpdateLayout();
                window.Title = fence.LoadedItemsForTesting.Count >= 800 &&
                               fence.RealizedItemCountForTesting is > 0 and < 100
                    ? "MiniFences Large Folder Verification - PASS"
                    : $"MiniFences Large Folder Verification - FAIL ({fence.LoadedItemsForTesting.Count}/{fence.RealizedItemCountForTesting})";
            }
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
            if (mode is "color" or "colorhold" or "colorcapture" or "colorcapturezh")
            {
                fence.ContextMenu.IsOpen = true;
                var verifyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                verifyTimer.Tick += (_, _) =>
                {
                    var picker = Application.Current.Windows.OfType<ColorPickerDialog>().FirstOrDefault();
                    if (picker == null) return;
                    verifyTimer.Stop();
                    window.Title = picker.IsVisible
                        ? "MiniFences Color Picker Verification - PASS"
                        : "MiniFences Color Picker Verification - FAIL";
                    if (mode is "colorcapture" or "colorcapturezh")
                    {
                        picker.UpdateLayout();
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            Math.Max(1, (int)Math.Ceiling(picker.ActualWidth)),
                            Math.Max(1, (int)Math.Ceiling(picker.ActualHeight)),
                            96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(picker);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(Path.GetTempPath(), "MiniFences-ColorPicker-Capture.png"));
                        encoder.Save(output);
                    }
                    if (mode == "color") picker.Close();
                };
                verifyTimer.Start();
                fence.ChooseBackgroundColorMenuItem.RaiseEvent(new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                });
            }
        });
        var app = new Application();
        app.Run(window);
    }
}
