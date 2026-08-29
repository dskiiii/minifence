using System.IO;

namespace MiniFences;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\MiniFences.SingleInstance";
    private const string WakeEventName = @"Local\MiniFences.WakeExistingInstance";
    private const string ExitEventName = @"Local\MiniFences.ExitExistingInstance";
    private const string NewFenceEventName = @"Local\MiniFences.NewFence";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _wakeEvent;
    private EventWaitHandle? _exitEvent;
    private EventWaitHandle? _newFenceEvent;
    private Thread? _wakeThread;
    private bool _ownsSingleInstanceMutex;
    private volatile bool _isExiting;
    private bool _dispatcherErrorShown;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        if (Services.UpdateInstaller.TryRunFromArguments(e.Args))
        {
            return;
        }
        if (TryRunDesktopIconWatchdog(e.Args))
        {
            return;
        }
        WaitForPreviousInstance(e.Args);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            if (e.Args.Any(argument => string.Equals(argument, "--exit-existing", StringComparison.OrdinalIgnoreCase)))
            {
                SignalExistingInstanceExit();
            }
            else if (e.Args.Any(argument => string.Equals(argument, "--new-fence", StringComparison.OrdinalIgnoreCase)))
            {
                SignalExistingInstanceNewFence();
            }
            else
            {
                SignalExistingInstance();
            }
            Shutdown();
            Environment.Exit(0);
            return;
        }

        _ownsSingleInstanceMutex = true;
        base.OnStartup(e);
        _wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _newFenceEvent = new EventWaitHandle(false, EventResetMode.AutoReset, NewFenceEventName);
        StartWakeListener();
        try
        {
            new Services.StartupService().RefreshEnabledPath();
        }
        catch (Exception ex)
        {
            Services.AppLogger.LogException("Failed to refresh Windows startup path", ex);
        }
        var openSettings = !e.Args.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        MainWindow = new MainWindow(openSettings);
        MainWindow.Show();
        WriteUpdateHealthMarker(e.Args);
        new Services.DesktopIntegrationService().EnsureDesktopContextMenu();
        if (e.Args.Any(argument => string.Equals(argument, "--new-fence", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.BeginInvoke(() => ((MainWindow)MainWindow).CreateFenceFromDesktopContext());
    }

    private static void WriteUpdateHealthMarker(IReadOnlyList<string> args)
    {
        var index = Array.FindIndex(args.ToArray(), argument =>
            string.Equals(argument, Services.UpdateInstaller.HealthFileArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Count) return;
        try
        {
            var path = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Services.AppLogger.LogException("Failed to write update startup health marker", ex);
        }
    }

    private static void WaitForPreviousInstance(IReadOnlyList<string> args)
    {
        var markerIndex = Array.FindIndex(args.ToArray(), argument =>
            string.Equals(argument, "--restart-after-exit", StringComparison.OrdinalIgnoreCase));
        if (markerIndex < 0 || markerIndex + 1 >= args.Count ||
            !int.TryParse(args[markerIndex + 1], out var processId) || processId == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(processId);
            if (!previous.WaitForExit(2000))
            {
                Services.AppLogger.Log($"Restart watchdog is force-terminating the previous MiniFences process {processId}.");
                try
                {
                    previous.Kill(entireProcessTree: true);
                    previous.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    Services.AppLogger.LogException("Restart watchdog could not force-terminate the previous MiniFences process", ex);
                }
            }
        }
        catch
        {
            // The previous instance already exited; continue startup.
        }
    }

    private static bool TryRunDesktopIconWatchdog(IReadOnlyList<string> args)
    {
        var markerIndex = Array.FindIndex(args.ToArray(), argument =>
            string.Equals(argument, "--desktop-icon-watchdog", StringComparison.OrdinalIgnoreCase));
        if (markerIndex < 0 || markerIndex + 1 >= args.Count ||
            !int.TryParse(args[markerIndex + 1], out var processId))
        {
            return false;
        }

        try
        {
            using var owner = System.Diagnostics.Process.GetProcessById(processId);
            owner.WaitForExit();
        }
        catch
        {
            // The owner already exited; restoration is still required.
        }

        var service = new Services.DesktopIconLayoutService();
        var restored = false;
        for (var attempt = 0; attempt < 20 && !restored; attempt += 1)
        {
            restored = service.SetVisible(true);
            if (!restored) Thread.Sleep(100);
        }
        Services.AppLogger.Log($"Desktop icon recovery watchdog completed. Restored={restored}.");
        Environment.Exit(0);
        return true;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _isExiting = true;
        // OnClosing is the primary cleanup path. This second, idempotent call
        // covers shutdown paths where WPF reaches App.OnExit directly.
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.RestoreNativeDesktopIconsOnExit();
        }
        _wakeEvent?.Set();
        _exitEvent?.Set();
        _wakeThread?.Join(TimeSpan.FromMilliseconds(500));
        _wakeEvent?.Dispose();
        _wakeEvent = null;
        _exitEvent?.Dispose();
        _exitEvent = null;
        _newFenceEvent?.Dispose();
        _newFenceEvent = null;
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Services.AppLogger.LogException("Unhandled UI exception", e.Exception);
        e.Handled = true;
        if (_dispatcherErrorShown) return;
        _dispatcherErrorShown = true;
        System.Windows.MessageBox.Show("MiniFences 遇到意外错误，但已继续运行。详细信息已写入日志。\n\nMiniFences encountered an unexpected error and kept running. Details were written to the log.",
            "MiniFences", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        Dispatcher.BeginInvoke(() => _dispatcherErrorShown = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) Services.AppLogger.LogException("Fatal application exception", exception);
        else Services.AppLogger.Log($"Fatal application exception: {e.ExceptionObject}");
    }

    private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Services.AppLogger.LogException("Unobserved background task exception", e.Exception);
        e.SetObserved();
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var wakeEvent = EventWaitHandle.OpenExisting(WakeEventName);
            wakeEvent.Set();
        }
        catch
        {
            // If the existing instance has not created its event yet, exiting is still safer than opening a second UI.
        }
    }

    private static void SignalExistingInstanceExit()
    {
        try
        {
            using var exitEvent = EventWaitHandle.OpenExisting(ExitEventName);
            exitEvent.Set();
        }
        catch
        {
            // Compatibility fallback for an older instance that predates the named exit event.
            var window = FindWindow(null, "MiniFences");
            if (window != IntPtr.Zero) SendMessage(window, WmExitMiniFences, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static void SignalExistingInstanceNewFence()
    {
        try
        {
            using var newFenceEvent = EventWaitHandle.OpenExisting(NewFenceEventName);
            newFenceEvent.Set();
        }
        catch { SignalExistingInstance(); }
    }

    private void StartWakeListener()
    {
        _wakeThread = new Thread(() =>
        {
            while (!_isExiting)
            {
                try
                {
                    var signaled = WaitHandle.WaitAny([_wakeEvent!, _exitEvent!, _newFenceEvent!]);
                    if (_isExiting)
                    {
                        return;
                    }

                    if (signaled == 1)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (MainWindow is MainWindow mainWindow)
                            {
                                mainWindow.RequestExitFromAnotherInstance();
                            }
                        });
                        return;
                    }

                    if (signaled == 2)
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (MainWindow is MainWindow mainWindow) mainWindow.CreateFenceFromDesktopContext();
                        });
                        continue;
                    }

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is MainWindow mainWindow)
                        {
                            mainWindow.ShowFromTray();
                        }
                    });
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "MiniFences wake listener"
        };
        _wakeThread.Start();
    }

    internal const int WmExitMiniFences = 0x8000 + 91;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
