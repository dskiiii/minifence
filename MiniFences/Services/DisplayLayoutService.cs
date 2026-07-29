using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiniFences.Models;
using Forms = System.Windows.Forms;

namespace MiniFences.Services;

public sealed class DisplayLayoutService
{
    private const int MaximumProfiles = 12;
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private DisplayLayoutDocument _document;

    public DisplayLayoutService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MiniFences", "display-layouts.json");
        _document = Load();
    }

    public static IReadOnlyList<DisplayDescriptor> GetCurrentDisplays() =>
        Forms.Screen.AllScreens.Select(screen =>
        {
            var dpi = TryGetDpi(screen.Bounds.Left + screen.Bounds.Width / 2,
                screen.Bounds.Top + screen.Bounds.Height / 2);
            return new DisplayDescriptor(screen.DeviceName, screen.Bounds.Left, screen.Bounds.Top,
                screen.Bounds.Width, screen.Bounds.Height, dpi.X, dpi.Y, screen.Primary);
        }).OrderBy(display => display.Left).ThenBy(display => display.Top).ToArray();

    public static string CreateTopologyKey(IEnumerable<DisplayDescriptor> displays)
    {
        var canonical = string.Join("|", displays
            .OrderBy(display => display.Left).ThenBy(display => display.Top)
            .Select(display => $"{display.DeviceName}:{display.Left},{display.Top},{display.Width},{display.Height}:{display.DpiX},{display.DpiY}:{display.IsPrimary}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public void SaveProfile(string topologyKey, AppConfig config, double workspaceWidth, double workspaceHeight)
    {
        if (string.IsNullOrWhiteSpace(topologyKey) || workspaceWidth <= 0 || workspaceHeight <= 0) return;
        var profile = new DisplayLayoutProfile
        {
            TopologyKey = topologyKey,
            SavedAtUtc = DateTime.UtcNow,
            WorkspaceWidth = workspaceWidth,
            WorkspaceHeight = workspaceHeight,
            Fences = config.Fences.Select(fence => new FenceDisplayPlacement
            {
                FenceId = fence.Id,
                Left = fence.Left,
                Top = fence.Top,
                Width = fence.Width,
                Height = fence.Height,
                PageIndex = fence.PageIndex
            }).ToList()
        };
        _document.Profiles.RemoveAll(item => item.TopologyKey == topologyKey);
        _document.Profiles.Add(profile);
        _document.Profiles = _document.Profiles.OrderByDescending(item => item.SavedAtUtc).Take(MaximumProfiles).ToList();
        Save();
    }

    public bool TryRestoreProfile(string topologyKey, AppConfig config, double workspaceWidth, double workspaceHeight)
    {
        var profile = _document.Profiles.FirstOrDefault(item => item.TopologyKey == topologyKey);
        if (profile is null) return false;
        foreach (var placement in profile.Fences)
        {
            var fence = config.Fences.FirstOrDefault(item => item.Id == placement.FenceId);
            if (fence is null) continue;
            fence.Width = Math.Clamp(placement.Width, 240, Math.Max(240, workspaceWidth));
            fence.Height = Math.Clamp(placement.Height, 180, Math.Max(180, workspaceHeight));
            var visibleHeight = GetLayoutHeight(fence);
            fence.Left = Math.Clamp(placement.Left, 0, Math.Max(0, workspaceWidth - fence.Width));
            fence.Top = Math.Clamp(placement.Top, 0, Math.Max(0, workspaceHeight - visibleHeight));
            fence.PageIndex = placement.PageIndex;
        }
        return true;
    }

    public static void RemapToWorkspace(AppConfig config, double oldWidth, double oldHeight,
        double newWidth, double newHeight)
    {
        if (newWidth <= 0 || newHeight <= 0) return;
        foreach (var fence in config.Fences)
        {
            var availableOldX = Math.Max(1, oldWidth - fence.Width);
            var visibleHeight = GetLayoutHeight(fence);
            var availableOldY = Math.Max(1, oldHeight - visibleHeight);
            var ratioX = Math.Clamp(fence.Left / availableOldX, 0, 1);
            var ratioY = Math.Clamp(fence.Top / availableOldY, 0, 1);
            fence.Width = Math.Clamp(fence.Width, 240, Math.Max(240, newWidth));
            fence.Height = Math.Clamp(fence.Height, 180, Math.Max(180, newHeight));
            fence.Left = ratioX * Math.Max(0, newWidth - fence.Width);
            fence.Top = ratioY * Math.Max(0, newHeight - visibleHeight);
        }
    }

    public static int CycleMonitorContents(AppConfig config, IReadOnlyList<System.Windows.Rect> monitorBounds)
    {
        if (monitorBounds.Count < 2) return 0;
        var moved = 0;
        foreach (var fence in config.Fences)
        {
            var visibleHeight = GetLayoutHeight(fence);
            var center = new System.Windows.Point(fence.Left + fence.Width / 2, fence.Top + visibleHeight / 2);
            var sourceIndex = -1;
            for (var index = 0; index < monitorBounds.Count; index++)
            {
                if (monitorBounds[index].Contains(center)) { sourceIndex = index; break; }
            }
            if (sourceIndex < 0) continue;
            var source = monitorBounds[sourceIndex];
            var target = monitorBounds[(sourceIndex + 1) % monitorBounds.Count];
            var normalizedX = Math.Clamp((fence.Left - source.Left) / Math.Max(1, source.Width - fence.Width), 0, 1);
            var normalizedY = Math.Clamp((fence.Top - source.Top) / Math.Max(1, source.Height - visibleHeight), 0, 1);
            fence.Left = target.Left + normalizedX * Math.Max(0, target.Width - fence.Width);
            fence.Top = target.Top + normalizedY * Math.Max(0, target.Height - visibleHeight);
            moved++;
        }
        return moved;
    }

    internal static double GetLayoutHeight(FenceConfig fence) => fence.IsCollapsed ? 34 : fence.Height;

    private DisplayLayoutDocument Load()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var result = JsonSerializer.Deserialize<DisplayLayoutDocument>(File.ReadAllText(_path), _options);
            if (result?.FormatVersion != 1) throw new InvalidDataException("Unsupported display layout format.");
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Display layout profiles could not be loaded.", ex);
            return new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_document, _options));
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) { AppLogger.LogException("Display layout profile could not be saved.", ex); }
    }

    private static (uint X, uint Y) TryGetDpi(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint(x, y), 2);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0)
                return (dpiX, dpiY);
        }
        catch { }
        return (96, 96);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
