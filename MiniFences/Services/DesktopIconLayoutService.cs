using System.Runtime.InteropServices;
using System.IO;
using MiniFences.Models;

namespace MiniFences.Services;

public sealed class DesktopIconLayoutService
{
    public bool IsVisible()
    {
        var listView = FindDesktopListView();
        return listView != IntPtr.Zero && IsWindowVisible(listView);
    }

    public bool SetVisible(bool visible)
    {
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero) return false;
        ShowWindow(listView, visible ? SwShowNoActivate : SwHide);
        return IsWindowVisible(listView) == visible;
    }

    public bool TryGetScreenPositions(out IReadOnlyList<DesktopIconPosition> positions, out string? error)
    {
        positions = [];
        error = null;
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero) { error = "Explorer desktop icon view was not found."; return false; }
        try
        {
            var names = ReadItemNames(listView);
            GetWindowThreadProcessId(listView, out var processId);
            var process = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, processId);
            if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var remotePoint = VirtualAllocEx(process, IntPtr.Zero, (nuint)Marshal.SizeOf<NativePoint>(), MemCommit | MemReserve, PageReadWrite);
            try
            {
                var result = new List<DesktopIconPosition>();
                for (var index = 0; index < names.Count; index += 1)
                {
                    if (SendMessage(listView, LvmGetItemPosition, new IntPtr(index), remotePoint) == IntPtr.Zero) continue;
                    var bytes = new byte[Marshal.SizeOf<NativePoint>()];
                    ReadProcessMemory(process, remotePoint, bytes, (nuint)bytes.Length, out _);
                    var point = new NativePoint(BitConverter.ToInt32(bytes, 0), BitConverter.ToInt32(bytes, 4));
                    ClientToScreen(listView, ref point);
                    result.Add(new DesktopIconPosition(names[index], new System.Windows.Point(point.X, point.Y)));
                }
                positions = result;
            }
            finally
            {
                if (remotePoint != IntPtr.Zero) VirtualFreeEx(process, remotePoint, 0, MemRelease);
                CloseHandle(process);
            }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public bool TryLayout(IReadOnlyList<FenceConfig> fences, ISet<string> visibleFenceIds, Func<System.Windows.Point, System.Windows.Point> toScreen, out int positionedCount, out string? error)
    {
        positionedCount = 0;
        error = null;
        var listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            error = "Explorer desktop icon view was not found.";
            return false;
        }

        try
        {
            var style = GetWindowLongPtr(listView, GwlStyle).ToInt64();
            if ((style & LvsAutoArrange) != 0)
            {
                SetWindowLongPtr(listView, GwlStyle, new IntPtr(style & ~LvsAutoArrange));
            }
            var items = ReadItemNames(listView);
            var matchedCount = 0;
            var requestedCount = fences.Sum(fence => fence.AssignedPaths.Count);
            foreach (var fence in fences.Where(fence => fence.IsDesktopGroup))
            {
                for (var itemIndex = 0; itemIndex < fence.AssignedPaths.Count; itemIndex += 1)
                {
                    var path = fence.AssignedPaths[itemIndex];
                    var displayName = Path.GetFileNameWithoutExtension(path);
                    var fullName = Path.GetFileName(path);
                    var desktopIndex = items.FindIndex(name =>
                        string.Equals(name, displayName, StringComparison.CurrentCultureIgnoreCase) ||
                        string.Equals(name, fullName, StringComparison.CurrentCultureIgnoreCase));
                    if (desktopIndex < 0) continue;
                    matchedCount += 1;

                    NativePoint nativePoint;
                    if (visibleFenceIds.Contains(fence.Id))
                    {
                        var columns = Math.Max(1, (int)((fence.Width - 20) / 88));
                        var column = itemIndex % columns;
                        var row = itemIndex / columns;
                        var screenPoint = toScreen(new System.Windows.Point(fence.Left + 14 + column * 88, fence.Top + 46 + row * 94));
                        nativePoint = new NativePoint((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y));
                        ScreenToClient(listView, ref nativePoint);
                    }
                    else
                    {
                        nativePoint = new NativePoint(30000 + itemIndex, 30000);
                    }
                    var packed = (nativePoint.X & 0xffff) | (nativePoint.Y << 16);
                    if (SendMessage(listView, LvmSetItemPosition, new IntPtr(desktopIndex), new IntPtr(packed)) != IntPtr.Zero) positionedCount += 1;
                }
            }
            if (requestedCount > 0 && positionedCount == 0)
            {
                var requestedSample = string.Join(" | ", fences.SelectMany(fence => fence.AssignedPaths).Take(4).Select(Path.GetFileNameWithoutExtension));
                var desktopSample = string.Join(" | ", items.Take(4));
                error = $"Explorer icon positioning failed. Requested={requestedCount}, DesktopItems={items.Count}, NamedItems={items.Count(name => !string.IsNullOrWhiteSpace(name))}, Matched={matchedCount}, RequestedSample=[{requestedSample}], DesktopSample=[{desktopSample}].";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<string> ReadItemNames(IntPtr listView)
    {
        GetWindowThreadProcessId(listView, out var processId);
        var process = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, processId);
        if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var result = new List<string>();
        var itemSize = Marshal.SizeOf<ListViewItem>();
        var textBytes = 1024;
        var remoteItem = VirtualAllocEx(process, IntPtr.Zero, (nuint)itemSize, MemCommit | MemReserve, PageReadWrite);
        var remoteText = VirtualAllocEx(process, IntPtr.Zero, (nuint)textBytes, MemCommit | MemReserve, PageReadWrite);
        try
        {
            var count = SendMessage(listView, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero).ToInt32();
            for (var index = 0; index < count; index += 1)
            {
                var item = new ListViewItem { Mask = LvifText, Item = index, Text = remoteText, TextMax = textBytes / 2 };
                var local = Marshal.AllocHGlobal(itemSize);
                try
                {
                    Marshal.StructureToPtr(item, local, false);
                    WriteProcessMemory(process, remoteItem, local, (nuint)itemSize, out _);
                    SendMessage(listView, LvmGetItemTextW, new IntPtr(index), remoteItem);
                    var buffer = new byte[textBytes];
                    ReadProcessMemory(process, remoteText, buffer, (nuint)buffer.Length, out _);
                    var text = System.Text.Encoding.Unicode.GetString(buffer);
                    var terminator = text.IndexOf('\0');
                    result.Add(terminator >= 0 ? text[..terminator] : text);
                }
                finally
                {
                    Marshal.FreeHGlobal(local);
                }
            }
        }
        finally
        {
            if (remoteItem != IntPtr.Zero) VirtualFreeEx(process, remoteItem, 0, MemRelease);
            if (remoteText != IntPtr.Zero) VirtualFreeEx(process, remoteText, 0, MemRelease);
            CloseHandle(process);
        }
        return result;
    }

    private static IntPtr FindDesktopListView()
    {
        var programManager = FindWindow("Progman", null);
        var programView = FindWindowEx(programManager, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (programView != IntPtr.Zero)
        {
            var programList = FindWindowEx(programView, IntPtr.Zero, "SysListView32", "FolderView");
            if (programList != IntPtr.Zero) return programList;
        }

        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            var className = new System.Text.StringBuilder(64);
            GetClassName(window, className, className.Capacity);
            if (!string.Equals(className.ToString(), "WorkerW", StringComparison.Ordinal)) return true;
            var view = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view == IntPtr.Zero) return true;
            result = FindWindowEx(view, IntPtr.Zero, "SysListView32", "FolderView");
            return result == IntPtr.Zero;
        }, IntPtr.Zero);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ListViewItem
    {
        public uint Mask; public int Item; public int SubItem; public uint State; public uint StateMask;
        public IntPtr Text; public int TextMax; public int Image; public IntPtr Parameter; public int Indent;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x, int y) { public int X = x; public int Y = y; }
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    private const uint LvifText = 1, ProcessVmOperation = 8, ProcessVmRead = 16, ProcessVmWrite = 32, ProcessQueryInformation = 1024;
    private const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageReadWrite = 4;
    private const int LvmFirst = 0x1000, LvmGetItemCount = LvmFirst + 4, LvmGetItemTextW = LvmFirst + 115, LvmSetItemPosition = LvmFirst + 15, LvmGetItemPosition = LvmFirst + 16;
    private const int GwlStyle = -16, LvsAutoArrange = 0x0100;
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maxCount);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, nuint size, out nuint written);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private const int SwHide = 0, SwShowNoActivate = 4;
}

public sealed record DesktopIconPosition(string Name, System.Windows.Point ScreenPosition);
