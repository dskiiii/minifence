using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using System.Runtime.InteropServices;
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
        if (mode == "renameinputauto")
        {
            var marker = Path.Combine(Path.GetTempPath(), "MiniFences-RenameInputAuto.txt");
            if (File.Exists(marker)) File.Delete(marker);
            var renameWindow = new DesktopRenameWindow(
                "original.zip",
                new System.Windows.Int32Rect(500, 350, 110, 30),
                InlineRenameAppearance.MaximumWidth,
                InlineRenameAppearance.EditorHeight,
                "original".Length);
            renameWindow.Loaded += (_, _) => renameWindow.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(350);
                System.Windows.Forms.SendKeys.SendWait("12{BACKSPACE}");
                await Task.Delay(150);
                File.WriteAllText(marker,
                    renameWindow.Editor.IsKeyboardFocusWithin && renameWindow.Editor.Text == "1.zip"
                        ? "PASS"
                        : $"FAIL Focus={renameWindow.Editor.IsKeyboardFocusWithin}; Text={renameWindow.Editor.Text}");
                renameWindow.CloseWithoutCommit();
            }, DispatcherPriority.ApplicationIdle);
            var renameInputApp = new Application();
            renameInputApp.Run(renameWindow);
            return;
        }
        if (mode == "listviewauto")
        {
            var listFence = new FenceControl(new FenceConfig
            {
                Title = "下载列表",
                FolderPath = testFolder,
                PortalViewMode = "List",
                Width = 620,
                Height = 360,
                ShowPath = false
            }) { Width = 620, Height = 360 };
            var listWindow = new Window
            {
                Title = "MiniFences List View Verification",
                Width = 660,
                Height = 410,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = listFence
            };
            listWindow.Loaded += (_, _) => listWindow.Dispatcher.BeginInvoke(async () =>
            {
                listFence.LoadFolderItems();
                await Task.Delay(700);
                listFence.SelectItemsForTesting(0, 1);
                await Task.Delay(150);
                listWindow.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(listWindow.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(listWindow.ActualHeight)),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(listWindow);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(Path.GetTempPath(), "MiniFences-ListView-Capture.png"));
                encoder.Save(output);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "MiniFences-ListViewAuto.txt"),
                    listFence.IsListViewModeForTesting && !listFence.ListTemplateContainsIconsForTesting ? "PASS" : "FAIL");
                listWindow.Close();
            }, DispatcherPriority.ApplicationIdle);
            var listApp = new Application();
            listApp.Run(listWindow);
            return;
        }
        if (mode == "listswitchiconsauto")
        {
            var switchFence = new FenceControl(new FenceConfig
            {
                Title = "列表切回图标",
                FolderPath = testFolder,
                PortalViewMode = "List",
                Width = 620,
                Height = 360,
                ShowPath = false
            }) { Width = 620, Height = 360 };
            var switchWindow = new Window
            {
                Title = "MiniFences List To Icons Verification",
                Width = 660,
                Height = 410,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = switchFence
            };
            switchWindow.Loaded += (_, _) => switchWindow.Dispatcher.BeginInvoke(async () =>
            {
                switchFence.LoadFolderItems();
                await Task.Delay(300);
                switchFence.SelectItemForTesting(0);
                switchFence.ApplyPortalViewForTesting();
                var overlayStayedHidden = switchFence.IsExpandedOverlayHiddenForTesting;
                switchFence.SelectViewModeForTesting("Icons");
                await Task.Delay(1200);
                switchWindow.UpdateLayout();
                var items = switchFence.LoadedItemsForTesting;
                var iconsLoaded = items.Count > 0 && items.All(item => item.Icon != null);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(switchWindow.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(switchWindow.ActualHeight)),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(switchWindow);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(Path.GetTempPath(), "MiniFences-ListSwitchIcons-Capture.png"));
                encoder.Save(output);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "MiniFences-ListSwitchIconsAuto.txt"),
                    overlayStayedHidden && iconsLoaded ? "PASS" : $"FAIL OverlayHidden={overlayStayedHidden}; IconsLoaded={iconsLoaded}");
                switchWindow.Close();
            }, DispatcherPriority.ApplicationIdle);
            var switchApp = new Application();
            switchApp.Run(switchWindow);
            return;
        }
        if (mode is "looselabel" or "looseselected")
        {
            var labelItem = new FolderItem
            {
                Name = "MiniFences-win-x64-0.23.80-source-verification",
                FullPath = testFile,
                Kind = "TXT",
                Icon = MiniFences.Services.FolderItemService.GetTypeIcon(".txt")
            };
            var looseIcon = new DesktopLooseIconControl(labelItem)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (mode == "looseselected") looseIcon.SetSelected(true);
            var labelRoot = new Grid { Background = new SolidColorBrush(Color.FromRgb(56, 59, 66)) };
            labelRoot.Children.Add(looseIcon);
            var labelWindow = new Window
            {
                Title = "MiniFences Two Line Label Verification",
                Width = 260,
                Height = 220,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = labelRoot
            };
            labelWindow.Loaded += (_, _) => labelWindow.Dispatcher.BeginInvoke(() =>
            {
                labelWindow.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(labelWindow.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(labelWindow.ActualHeight)),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(labelWindow);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(Path.GetTempPath(),
                    mode == "looseselected"
                        ? "MiniFences-SelectedLooseLabel-Capture.png"
                        : "MiniFences-TwoLineLabel-Capture.png"));
                encoder.Save(output);
                labelWindow.Dispatcher.BeginInvoke(() => labelWindow.Close(), DispatcherPriority.Background);
            }, DispatcherPriority.Loaded);
            var labelApp = new Application();
            labelApp.Run(labelWindow);
            return;
        }
        if (mode == "renamefocus")
        {
            var host = new Window
            {
                Title = "MiniFences Rename Focus Verification - RUNNING",
                Width = 520,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = new TextBlock
                {
                    Text = "The rename editor should commit when this window becomes active.",
                    Foreground = System.Windows.Media.Brushes.White,
                    Background = new SolidColorBrush(Color.FromRgb(42, 46, 52)),
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var renameWindow = new DesktopRenameWindow(
                "desktop-item",
                new System.Windows.Int32Rect(700, 420, 103, 25),
                InlineRenameAppearance.MaximumWidth,
                InlineRenameAppearance.EditorHeight);
            var dismissed = false;
            renameWindow.TryCommitRequested = _ =>
            {
                host.Title = "MiniFences Rename Focus Verification - FAIL (unchanged name was committed)";
                return false;
            };
            renameWindow.CancelRequested = () =>
            {
                dismissed = true;
                host.Title = "MiniFences Rename Focus Verification - PASS";
            };
            host.Loaded += (_, _) => host.Dispatcher.BeginInvoke(async () =>
            {
                renameWindow.Show();
                await Task.Delay(500);
                host.Activate();
                await Task.Delay(700);
                if (!dismissed)
                    host.Title = "MiniFences Rename Focus Verification - FAIL";
            }, DispatcherPriority.Input);
            var focusApp = new Application();
            focusApp.Run(host);
            return;
        }
        if (mode == "looserename")
        {
            var renameItem = new FolderItem
            {
                Name = "MiniFences-win-x64-0.23.53-verification",
                FullPath = testFile,
                Kind = "TXT",
                Icon = MiniFences.Services.FolderItemService.GetTypeIcon(".txt")
            };
            var looseIcon = new DesktopLooseIconControl(renameItem)
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            looseIcon.SetSelected(true);
            var renameRoot = new Grid { Background = new SolidColorBrush(Color.FromRgb(56, 59, 66)) };
            renameRoot.Children.Add(looseIcon);
            var renameWindow = new Window
            {
                Title = "MiniFences Loose Rename Verification",
                Width = 300,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = renameRoot
            };
            renameWindow.Loaded += (_, _) => renameWindow.Dispatcher.BeginInvoke(() =>
            {
                looseIcon.BeginInlineRenameForTesting();
                renameWindow.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(renameWindow.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(renameWindow.ActualHeight)),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(renameWindow);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(Path.GetTempPath(), "MiniFences-LooseRename-Capture.png"));
                encoder.Save(output);
                renameWindow.Title = looseIcon.IsRenameEditorCenteredForTesting &&
                                     looseIcon.IsRenameEditorCompactForTesting &&
                                     looseIcon.HasSelectionChromeForTesting
                    ? "MiniFences Loose Rename Verification - PASS"
                    : "MiniFences Loose Rename Verification - FAIL";
            }, DispatcherPriority.Input);
            var renameApp = new Application();
            renameApp.Run(renameWindow);
            return;
        }
        if (mode is "dragdrop" or "dragdropauto")
        {
            var dragRoot = Path.Combine(Path.GetTempPath(), "MiniFences-VisualHarness-DragDrop");
            var sourceFolder = Path.Combine(dragRoot, "Source");
            var targetFolder = Path.Combine(dragRoot, "Target");
            Directory.CreateDirectory(sourceFolder);
            Directory.CreateDirectory(targetFolder);
            var sourcePath = Path.Combine(sourceFolder, "Drag me.txt");
            var targetPath = Path.Combine(targetFolder, "Drag me.txt");
            if (File.Exists(targetPath) && !File.Exists(sourcePath)) File.Move(targetPath, sourcePath);
            if (!File.Exists(sourcePath)) File.WriteAllText(sourcePath, "drag/drop verification");

            var dragGrid = new Grid { Background = new SolidColorBrush(Color.FromRgb(38, 43, 49)) };
            dragGrid.ColumnDefinitions.Add(new ColumnDefinition());
            dragGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var sourceFence = new FenceControl(new FenceConfig
            {
                Title = "SOURCE - drag the icon",
                FolderPath = sourceFolder,
                Width = 330,
                Height = 280,
                ShowPath = false
            }) { Width = 330, Height = 280, Margin = new Thickness(12) };
            var targetFence = new FenceControl(new FenceConfig
            {
                Title = "TARGET - drop here",
                FolderPath = targetFolder,
                Width = 330,
                Height = 280,
                ShowPath = false
            }) { Width = 330, Height = 280, Margin = new Thickness(12) };
            sourceFence.LoadFolderItems();
            targetFence.LoadFolderItems();
            targetFence.ItemsChanged += (_, _) =>
            {
                sourceFence.LoadFolderItems();
                targetFence.LoadFolderItems();
            };
            Grid.SetColumn(sourceFence, 0);
            Grid.SetColumn(targetFence, 1);
            dragGrid.Children.Add(sourceFence);
            dragGrid.Children.Add(targetFence);
            var dragWindow = new Window
            {
                Title = "MiniFences Drag Drop Verification - READY",
                Width = 760,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content = dragGrid
            };
            var verifyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            verifyTimer.Tick += (_, _) =>
            {
                if (!File.Exists(targetPath)) return;
                verifyTimer.Stop();
                dragWindow.Title = "MiniFences Drag Drop Verification - PASS";
                dragWindow.UpdateLayout();
                var completed = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(dragWindow.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(dragWindow.ActualHeight)),
                    96, 96, PixelFormats.Pbgra32);
                completed.Render(dragWindow);
                var completedEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                completedEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(completed));
                using var completedOutput = File.Create(Path.Combine(Path.GetTempPath(), "MiniFences-DragDrop-Completed.png"));
                completedEncoder.Save(completedOutput);
                if (mode == "dragdropauto")
                    dragWindow.Dispatcher.BeginInvoke(() => dragWindow.Close(), DispatcherPriority.Background);
            };
            dragWindow.Loaded += (_, _) => dragWindow.Dispatcher.BeginInvoke(() =>
            {
                dragWindow.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(dragWindow.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(dragWindow.ActualHeight)),
                    96, 96, PixelFormats.Pbgra32);
                bitmap.Render(dragWindow);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(Path.GetTempPath(), "MiniFences-DragDrop-Capture.png"));
                encoder.Save(output);
                verifyTimer.Start();
                if (mode == "dragdropauto")
                {
                    var from = sourceFence.PointToScreen(new System.Windows.Point(60, 135));
                    var to = targetFence.PointToScreen(new System.Windows.Point(120, 150));
                    _ = Task.Run(async () =>
                    {
                        SetCursorPos((int)Math.Round(from.X), (int)Math.Round(from.Y));
                        await Task.Delay(150);
                        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
                        const int steps = 80;
                        for (var step = 1; step <= steps; step++)
                        {
                            var x = from.X + (to.X - from.X) * step / steps;
                            var y = from.Y + (to.Y - from.Y) * step / steps;
                            SetCursorPos((int)Math.Round(x), (int)Math.Round(y));
                            await Task.Delay(8);
                        }
                        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
                    });
                }
            }, DispatcherPriority.Loaded);
            var dragApp = new Application();
            dragApp.Run(dragWindow);
            return;
        }
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
            if (mode is "selected" or "selectedcapture" or "rename" or "scrolled" or "cleared") fence.SelectItemForTesting(0);
            if (mode == "selectedcapture")
            {
                fence.SelectItemForTesting(1);
                window.Dispatcher.BeginInvoke(() =>
                {
                    window.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                        Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                        96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(Path.GetTempPath(), "MiniFences-Selected-Capture.png"));
                    encoder.Save(output);
                    window.Close();
                }, DispatcherPriority.ContextIdle);
            }
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

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [DllImport("User32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("User32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
