namespace MiniFences.Services;

internal static class DesktopDragData
{
    internal const string PathsFormat = "MiniFences.DesktopItemPaths";
    internal const string LooseIconFormat = "MiniFences.LooseDesktopIcon";
    internal const string AnchorPathFormat = "MiniFences.DesktopItemAnchorPath";

    internal static void Set(System.Windows.DataObject data, IReadOnlyList<string> paths, bool looseIcon, string? anchorPath = null)
    {
        var filePaths = paths.ToArray();
        data.SetData(PathsFormat, filePaths);
        // External applications such as browsers and chat clients only understand
        // the standard Shell file-drop format. MiniFences keeps its private formats
        // as well so internal drops can change membership/order without moving files.
        SetFileDropList(data, filePaths);
        data.SetData(LooseIconFormat, looseIcon);
        if (!string.IsNullOrWhiteSpace(anchorPath)) data.SetData(AnchorPathFormat, anchorPath);
    }

    internal static void SetFileDropList(System.Windows.DataObject data, IReadOnlyCollection<string> paths)
    {
        var files = new System.Collections.Specialized.StringCollection();
        files.AddRange(paths.ToArray());
        data.SetFileDropList(files);
    }

    internal static bool ShouldCancelExplorerDesktopDrop(System.Windows.DragDropKeyStates keyStates, bool overExplorerDesktop)
    {
        const System.Windows.DragDropKeyStates mouseButtons =
            System.Windows.DragDropKeyStates.LeftMouseButton |
            System.Windows.DragDropKeyStates.RightMouseButton |
            System.Windows.DragDropKeyStates.MiddleMouseButton;
        return overExplorerDesktop && (keyStates & mouseButtons) == 0;
    }

    internal static bool ShouldReleaseDesktopMembershipAfterDrag(
        System.Windows.DragDropEffects result,
        bool overExplorerDesktop) =>
        result == System.Windows.DragDropEffects.None && overExplorerDesktop;

    internal static bool TryGetPaths(System.Windows.IDataObject data, out string[] paths)
    {
        if (data.GetDataPresent(PathsFormat) && data.GetData(PathsFormat) is string[] internalPaths && internalPaths.Length > 0)
        {
            paths = internalPaths;
            return true;
        }
        if (data.GetDataPresent(System.Windows.DataFormats.FileDrop) &&
            data.GetData(System.Windows.DataFormats.FileDrop) is string[] filePaths && filePaths.Length > 0)
        {
            paths = filePaths;
            return true;
        }
        paths = [];
        return false;
    }

    internal static bool IsLooseIconDrag(System.Windows.IDataObject data) =>
        data.GetDataPresent(LooseIconFormat) && data.GetData(LooseIconFormat) is true;

    internal static string? GetAnchorPath(System.Windows.IDataObject data) =>
        data.GetDataPresent(AnchorPathFormat) ? data.GetData(AnchorPathFormat) as string : null;
}
