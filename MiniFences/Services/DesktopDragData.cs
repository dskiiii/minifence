using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MiniFences.Services;

internal static class DesktopDragData
{
    private static readonly ConditionalWeakTable<System.Windows.IDataObject, DragDataCache> Cache = new();
    internal const string PathsFormat = "MiniFences.DesktopItemPaths";
    internal const string LooseIconFormat = "MiniFences.LooseDesktopIcon";
    internal const string AnchorPathFormat = "MiniFences.DesktopItemAnchorPath";
    internal const string SourceFormat = "MiniFences.DragSource";
    internal const string NativeShellImageFormat = "MiniFences.NativeShellDragImage";
    internal const string PreferredDropEffectFormat = "Preferred DropEffect";

    internal static void Set(System.Windows.IDataObject data, IReadOnlyList<string> paths, bool looseIcon, string? anchorPath = null)
    {
        Cache.Remove(data);
        var filePaths = paths.ToArray();
        data.SetData(SourceFormat, true);
        data.SetData(PathsFormat, filePaths);
        // External applications such as browsers and chat clients only understand
        // the standard Shell file-drop format. MiniFences keeps its private formats
        // as well so internal drops can change membership/order without moving files.
        var physicalPaths = filePaths.Where(path => !FolderItemService.IsShellNamespacePath(path)).ToArray();
        if (physicalPaths.Length > 0) SetFileDropList(data, physicalPaths);
        data.SetData(LooseIconFormat, looseIcon);
        if (!string.IsNullOrWhiteSpace(anchorPath)) data.SetData(AnchorPathFormat, anchorPath);
    }

    internal static void SetFileDropList(System.Windows.IDataObject data, IReadOnlyCollection<string> paths)
    {
        Cache.Remove(data);
        data.SetData(System.Windows.DataFormats.FileDrop, paths.ToArray());
    }

    internal static void MarkMiniFencesSource(System.Windows.IDataObject data) =>
        data.SetData(SourceFormat, true);

    internal static bool HasNativeShellImage(System.Windows.IDataObject data) =>
        data.GetDataPresent(NativeShellImageFormat, autoConvert: false) &&
        data.GetData(NativeShellImageFormat, autoConvert: false) is true;


    internal static bool ShouldCancelExplorerDesktopDrop(
        System.Windows.DragDropKeyStates keyStates,
        bool overExplorerDesktop,
        bool overMiniFencesSurface = false)
    {
        const System.Windows.DragDropKeyStates mouseButtons =
            System.Windows.DragDropKeyStates.LeftMouseButton |
            System.Windows.DragDropKeyStates.RightMouseButton |
            System.Windows.DragDropKeyStates.MiddleMouseButton;
        return !overMiniFencesSurface && overExplorerDesktop && (keyStates & mouseButtons) == 0;
    }

    internal static bool ShouldReleaseDesktopMembershipAfterDrag(
        System.Windows.DragDropEffects result,
        bool overExplorerDesktop,
        bool canceledByEscape = false) =>
        !canceledByEscape && result == System.Windows.DragDropEffects.None && overExplorerDesktop;

    internal static bool TryGetPaths(System.Windows.IDataObject data, out string[] paths)
    {
        var cached = Cache.GetOrCreateValue(data);
        if (cached.PathsEvaluated)
        {
            paths = cached.Paths ?? [];
            return paths.Length > 0;
        }

        try
        {
            if (data.GetDataPresent(PathsFormat) &&
                ExtractPaths(data.GetData(PathsFormat)) is { Length: > 0 } internalPaths)
                cached.Paths = internalPaths;
            else if (data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
                     ExtractPaths(data.GetData(System.Windows.DataFormats.FileDrop)) is { Length: > 0 } filePaths)
                cached.Paths = filePaths;
        }
        catch
        {
            cached.Paths = null;
        }
        // Explorer can delay-render CF_HDROP. A first GetData call may fail or
        // return nothing and become available on a later DragOver packet. Only
        // internal MiniFences objects are stable enough to cache a negative;
        // retry external objects until paths appear instead of making one COM
        // timing hiccup blank the drag image for the rest of the operation.
        cached.PathsEvaluated = cached.Paths is { Length: > 0 } || IsMiniFencesSource(data);
        paths = cached.Paths ?? [];
        return paths.Length > 0;
    }

    private static string[]? ExtractPaths(object? value) => value switch
    {
        string[] array => array.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
        System.Collections.Specialized.StringCollection collection =>
            collection.Cast<string>().Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
        IEnumerable<string> enumerable =>
            enumerable.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
        _ => null
    };

    internal static bool IsLooseIconDrag(System.Windows.IDataObject data) =>
        data.GetDataPresent(LooseIconFormat) && data.GetData(LooseIconFormat) is true;

    internal static bool IsMiniFencesSource(System.Windows.IDataObject data) =>
        GetCachedData(data, SourceFormat) is true;

    /// <summary>
    /// Resolves the operation requested by the drag source without ever silently
    /// upgrading an ambiguous external drop to Move. Explorer breadcrumb/address
    /// drags advertise Link through Preferred DropEffect; ordinary cross-volume
    /// file drags advertise Copy. Move is accepted only when the source explicitly
    /// requests it (or Shift is held), because guessing Move can destroy the source.
    /// </summary>
    internal static System.Windows.DragDropEffects GetRequestedDropEffect(
        System.Windows.IDataObject data,
        System.Windows.DragDropKeyStates keyStates,
        System.Windows.DragDropEffects allowedEffects)
    {
        var allowed = allowedEffects & (System.Windows.DragDropEffects.Copy |
                                        System.Windows.DragDropEffects.Move |
                                        System.Windows.DragDropEffects.Link);
        if (allowed == System.Windows.DragDropEffects.None) return System.Windows.DragDropEffects.None;

        var control = (keyStates & System.Windows.DragDropKeyStates.ControlKey) != 0;
        var shift = (keyStates & System.Windows.DragDropKeyStates.ShiftKey) != 0;
        if (control && shift && allowed.HasFlag(System.Windows.DragDropEffects.Link))
            return System.Windows.DragDropEffects.Link;
        if (control && allowed.HasFlag(System.Windows.DragDropEffects.Copy))
            return System.Windows.DragDropEffects.Copy;
        if (shift && allowed.HasFlag(System.Windows.DragDropEffects.Move))
            return System.Windows.DragDropEffects.Move;

        var preferred = ReadDropEffect(GetCachedData(data, PreferredDropEffectFormat));
        if (preferred != System.Windows.DragDropEffects.None && allowed.HasFlag(preferred))
            return preferred;

        // A single advertised effect is unambiguous (notably Explorer address-bar Link drags).
        if (allowed is System.Windows.DragDropEffects.Copy or
            System.Windows.DragDropEffects.Move or
            System.Windows.DragDropEffects.Link)
            return allowed;

        // Internal MiniFences drags move membership, not filesystem content.
        if (IsMiniFencesSource(data) && allowed.HasFlag(System.Windows.DragDropEffects.Move))
            return System.Windows.DragDropEffects.Move;

        // Safe default for an external source that did not state an operation.
        if (allowed.HasFlag(System.Windows.DragDropEffects.Copy)) return System.Windows.DragDropEffects.Copy;
        if (allowed.HasFlag(System.Windows.DragDropEffects.Link)) return System.Windows.DragDropEffects.Link;
        return System.Windows.DragDropEffects.None;
    }

    private static System.Windows.DragDropEffects ReadDropEffect(object? value)
    {
        try
        {
            var raw = value switch
            {
                byte[] bytes when bytes.Length >= sizeof(int) => BitConverter.ToInt32(bytes, 0),
                MemoryStream stream when stream.Length >= sizeof(int) => ReadDropEffectStream(stream),
                int number => number,
                uint number => unchecked((int)number),
                _ => 0
            };
            return (System.Windows.DragDropEffects)raw & (System.Windows.DragDropEffects.Copy |
                                                          System.Windows.DragDropEffects.Move |
                                                          System.Windows.DragDropEffects.Link);
        }
        catch
        {
            return System.Windows.DragDropEffects.None;
        }
    }

    private static int ReadDropEffectStream(MemoryStream stream)
    {
        var position = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            return stream.Read(bytes) == bytes.Length ? BitConverter.ToInt32(bytes) : 0;
        }
        finally
        {
            stream.Position = position;
        }
    }

    internal static bool SetDropDescription(
        System.Windows.IDataObject data,
        System.Windows.DragDropEffects effect,
        string? target,
        bool chinese)
    {
        if (data is not System.Runtime.InteropServices.ComTypes.IDataObject native) return false;
        var description = new NativeDropDescription
        {
            Type = string.IsNullOrWhiteSpace(target)
                ? DropImageType.Invalid
                : effect == System.Windows.DragDropEffects.Move
                    ? DropImageType.Move
                    : effect == System.Windows.DragDropEffects.Copy
                        ? DropImageType.Copy
                        : DropImageType.Link,
            Message = BuildDropDescriptionMessage(effect, target, chinese), /* obsolete corrupted template
                ? string.Empty
                : chinese ? "移动到 %1" : "Move to %1",
            */ Insert = target ?? string.Empty
        };
        var size = Marshal.SizeOf<NativeDropDescription>();
        var memory = GlobalAlloc(0x0042, (UIntPtr)(uint)size);
        if (memory == IntPtr.Zero) return false;
        var locked = GlobalLock(memory);
        if (locked == IntPtr.Zero) { GlobalFree(memory); return false; }
        try { Marshal.StructureToPtr(description, locked, false); }
        finally { GlobalUnlock(memory); }
        var format = new FORMATETC
        {
            cfFormat = unchecked((short)RegisterClipboardFormat("DropDescription")),
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL
        };
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
        try
        {
            native.SetData(ref format, ref medium, true);
            return true;
        }
        catch
        {
            GlobalFree(memory);
            return false;
        }
    }

    internal static bool HasShellDragImage(System.Windows.IDataObject data)
    {
        var cached = Cache.GetOrCreateValue(data);
        if (cached.HasShellDragImage is bool value) return value;
        value = ProbeShellDragImage(data);
        // This method is called only after OLE has entered our target. At that
        // point the source data object is stable, so cache negative COM format
        // probes as well; repeating them for every mouse packet causes severe
        // lag with Explorer data objects that omit these private formats.
        cached.HasShellDragImage = value;
        return value;
    }

    private static string BuildDropDescriptionMessage(System.Windows.DragDropEffects effect, string? target, bool chinese) =>
        string.IsNullOrWhiteSpace(target)
            ? string.Empty
            : effect == System.Windows.DragDropEffects.Link
                ? chinese ? "创建链接到 %1" : "Create link in %1"
                : effect == System.Windows.DragDropEffects.Copy
                    ? chinese ? "复制到 %1" : "Copy to %1"
                    : chinese ? "移动到 %1" : "Move to %1";

    internal static bool RefreshShellDragImageState(System.Windows.IDataObject data)
    {
        var value = ProbeShellDragImage(data);
        Cache.GetOrCreateValue(data).HasShellDragImage = value;
        return value;
    }

    private static bool ProbeShellDragImage(System.Windows.IDataObject data)
    {
        try
        {
            return HasNativeShellImage(data) ||
                   data.GetDataPresent("DragWindow", autoConvert: false) ||
                   data.GetDataPresent("IsShowingLayered", autoConvert: false);
        }
        catch
        {
            return false;
        }
    }

    internal static object? GetCachedData(System.Windows.IDataObject data, string format)
    {
        var cached = Cache.GetOrCreateValue(data);
        cached.CustomData ??= new Dictionary<string, object?>(StringComparer.Ordinal);
        if (cached.CustomData.TryGetValue(format, out var value)) return value;
        try
        {
            value = data.GetDataPresent(format, autoConvert: false)
                ? data.GetData(format, autoConvert: false)
                : null;
        }
        catch
        {
            value = null;
        }
        cached.CustomData[format] = value;
        return value;
    }

    internal static string? GetAnchorPath(System.Windows.IDataObject data) =>
        data.GetDataPresent(AnchorPathFormat) ? data.GetData(AnchorPathFormat) as string : null;

    private sealed class DragDataCache
    {
        internal bool PathsEvaluated;
        internal string[]? Paths;
        internal bool? HasShellDragImage;
        internal Dictionary<string, object?>? CustomData;
    }

    private enum DropImageType { Invalid = -1, None = 0, Copy = 1, Move = 2, Link = 4 }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDropDescription
    {
        internal DropImageType Type;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Message;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Insert;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
