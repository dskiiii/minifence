using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.IO;

namespace MiniFences.Services;

public sealed class ShellContextMenuService
{
    public bool Show(
        string rightClickedPath,
        IEnumerable<string> selectedPaths,
        IntPtr owner,
        System.Drawing.Point screenPoint,
        out bool commandInvoked,
        out string? commandVerb,
        out string? error)
    {
        commandInvoked = false;
        commandVerb = null;
        error = null;
        var paths = SelectPathsForContextMenu(rightClickedPath, selectedPaths);
        if (paths.Length == 0) return false;

        var fullPidls = new List<IntPtr>();
        var childPidls = new List<IntPtr>();
        IShellFolder? shellFolder = null;
        IContextMenu? contextMenu = null;
        IContextMenu2? contextMenu2 = null;
        IContextMenu3? contextMenu3 = null;
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        var menu = IntPtr.Zero;
        try
        {
            foreach (var path in paths)
            {
                var parseResult = SHParseDisplayName(path, IntPtr.Zero, out var fullPidl, 0, out _);
                if (parseResult != 0) Marshal.ThrowExceptionForHR(parseResult);
                fullPidls.Add(fullPidl);
                var folderId = typeof(IShellFolder).GUID;
                var bindResult = SHBindToParent(fullPidl, ref folderId, out var currentFolder, out var childPidl);
                if (bindResult != 0) Marshal.ThrowExceptionForHR(bindResult);
                shellFolder ??= currentFolder;
                if (!ReferenceEquals(shellFolder, currentFolder)) Marshal.FinalReleaseComObject(currentFolder);
                childPidls.Add(childPidl);
            }
            if (shellFolder == null) return false;
            var contextMenuId = typeof(IContextMenu).GUID;
            shellFolder.GetUIObjectOf(owner, (uint)childPidls.Count, childPidls.ToArray(), ref contextMenuId, IntPtr.Zero, out var contextMenuPointer);
            contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPointer);
            Marshal.Release(contextMenuPointer);

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            contextMenu.QueryContextMenu(menu, 0, CommandFirst, CommandLast, CmfNormal | CmfExplore | CmfCanRename);

            try { contextMenu3 = contextMenu as IContextMenu3; } catch { contextMenu3 = null; }
            if (contextMenu3 == null)
            {
                try { contextMenu2 = contextMenu as IContextMenu2; } catch { contextMenu2 = null; }
            }
            source = HwndSource.FromHwnd(owner);
            if (source != null && (contextMenu3 != null || contextMenu2 != null))
            {
                hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (contextMenu3 != null && message is WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar)
                    {
                        var result = contextMenu3.HandleMenuMsg2((uint)message, wParam, lParam, out var menuResult);
                        handled = result == 0;
                        return menuResult;
                    }
                    if (contextMenu2 != null && message is WmInitMenuPopup or WmDrawItem or WmMeasureItem)
                    {
                        var result = contextMenu2.HandleMenuMsg((uint)message, wParam, lParam);
                        handled = result == 0;
                    }
                    return IntPtr.Zero;
                };
                source.AddHook(hook);
            }

            var command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, screenPoint.X, screenPoint.Y, owner, IntPtr.Zero);
            if (command >= CommandFirst)
            {
                var commandOffset = command - CommandFirst;
                commandVerb = TryGetCanonicalVerb(contextMenu, commandOffset);
                if (ShouldHandleCommandInHost(commandVerb))
                {
                    commandInvoked = true;
                    return true;
                }

                var invoke = new CommandInfo
                {
                    Size = Marshal.SizeOf<CommandInfo>(),
                    Mask = CmicUnicode | CmicPtInvoke,
                    Owner = owner,
                    Verb = new IntPtr(commandOffset),
                    VerbW = new IntPtr(commandOffset),
                    Show = SwShowNormal,
                    InvokePoint = new NativePoint { X = screenPoint.X, Y = screenPoint.Y }
                };
                contextMenu.InvokeCommand(ref invoke);
                commandInvoked = true;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLogger.LogException("Windows Shell context menu failed", ex);
            return false;
        }
        finally
        {
            if (source != null && hook != null) source.RemoveHook(hook);
            if (menu != IntPtr.Zero) DestroyMenu(menu);
            if (contextMenu != null && Marshal.IsComObject(contextMenu)) Marshal.FinalReleaseComObject(contextMenu);
            if (shellFolder != null && Marshal.IsComObject(shellFolder)) Marshal.FinalReleaseComObject(shellFolder);
            foreach (var pidl in fullPidls) Marshal.FreeCoTaskMem(pidl);
        }
    }

    internal static bool ShouldHandleCommandInHost(string? commandVerb) =>
        string.Equals(commandVerb, "rename", StringComparison.OrdinalIgnoreCase);

    private static string? TryGetCanonicalVerb(IContextMenu contextMenu, uint commandOffset)
    {
        const int capacity = 260;
        var buffer = Marshal.AllocCoTaskMem(capacity * sizeof(char));
        try
        {
            for (var index = 0; index < capacity; index += 1)
            {
                Marshal.WriteInt16(buffer, index * sizeof(char), 0);
            }

            var result = contextMenu.GetCommandString((UIntPtr)commandOffset, GcsVerbW, IntPtr.Zero, buffer, capacity);
            if (result != 0) return null;
            var verb = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(verb) ? null : verb;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not read Shell context-menu command verb", ex);
            return null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    internal static string[] SelectPathsForContextMenu(string rightClickedPath, IEnumerable<string> selectedPaths)
    {
        if (string.IsNullOrWhiteSpace(rightClickedPath) ||
            (!File.Exists(rightClickedPath) && !Directory.Exists(rightClickedPath)))
        {
            return [];
        }

        var targetPath = Path.GetFullPath(rightClickedPath);
        var targetParent = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetParent)) return [];

        return new[] { targetPath }
            .Concat(selectedPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Where(path => string.Equals(Path.GetDirectoryName(path), targetParent, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private const uint CommandFirst = 1;
    private const uint CommandLast = 0x7FFF;
    private const uint CmfNormal = 0;
    private const uint CmfExplore = 0x4;
    private const uint CmfCanRename = 0x10;
    private const uint TpmRightButton = 0x2;
    private const uint TpmReturnCmd = 0x100;
    private const uint CmicUnicode = 0x4000;
    private const uint CmicPtInvoke = 0x20000000;
    private const uint GcsVerbW = 0x4;
    private const int SwShowNormal = 1;
    private const int WmDrawItem = 0x002B;
    private const int WmMeasureItem = 0x002C;
    private const int WmInitMenuPopup = 0x0117;
    private const int WmMenuChar = 0x0120;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CommandInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner;
        public IntPtr Verb;
        public IntPtr Parameters;
        public IntPtr Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
        public IntPtr Title;
        public IntPtr VerbW;
        public IntPtr ParametersW;
        public IntPtr DirectoryW;
        public IntPtr TitleW;
        public NativePoint InvokePoint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr bindContext, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr itemId, ref uint attributes);
        void EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumIdList);
        void BindToObject(IntPtr itemId, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);
        void BindToStorage(IntPtr itemId, IntPtr bindContext, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr param, IntPtr first, IntPtr second);
        void CreateViewObject(IntPtr hwnd, ref Guid interfaceId, out IntPtr result);
        void GetAttributesOf(uint count, IntPtr[] itemIds, ref uint attributes);
        void GetUIObjectOf(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] itemIds,
            ref Guid interfaceId, IntPtr reserved, out IntPtr result);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint commandFirst, uint commandLast, uint flags);
        void InvokeCommand(ref CommandInfo commandInfo);
        [PreserveSig] int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, int maxName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F4-0000-0000-C000-000000000046")]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint commandFirst, uint commandLast, uint flags);
        void InvokeCommand(ref CommandInfo commandInfo);
        [PreserveSig] int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, int maxName);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint commandFirst, uint commandLast, uint flags);
        void InvokeCommand(ref CommandInfo commandInfo);
        [PreserveSig] int GetCommandString(UIntPtr command, uint flags, IntPtr reserved, IntPtr name, int maxName);
        [PreserveSig] int HandleMenuMsg(uint message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr itemId, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHBindToParent(IntPtr itemId, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellFolder parent, out IntPtr childId);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
}
