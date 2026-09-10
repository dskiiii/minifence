using System.Runtime.InteropServices;

namespace MiniFences.Services;

// Obtain the desktop view through ShellWindows, as documented by the Shell team:
// https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933
internal static class ShellDesktopView
{
    private const uint NoIcons = 0x1000; // FWF_NOICONS

    internal static bool TryGetVisible(out bool visible) => TryAccess(null, out visible);
    internal static bool TrySetVisible(bool visible) => TryAccess(visible, out _);

    private static bool TryAccess(bool? requestedVisibility, out bool visible)
    {
        visible = false;
        object? windows = null;
        object? desktop = null;
        IntPtr unknown = IntPtr.Zero, provider = IntPtr.Zero, browser = IntPtr.Zero;
        IntPtr view = IntPtr.Zero, folderView = IntPtr.Zero;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!);
            object location = 0; // CSIDL_DESKTOP
            object root = null!;
            desktop = ((dynamic)windows!).FindWindowSW(ref location, ref root,
                8 /* SWC_DESKTOP */, out int _, 1 /* SWFO_NEEDDISPATCH */);
            if (desktop == null) return false;
            unknown = Marshal.GetIUnknownForObject(desktop);
            var providerId = new Guid("6D5140C1-7436-11CE-8034-00AA006009FA");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, ref providerId, out provider));
            var browserService = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var browserId = new Guid("000214E2-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Method<QueryService>(provider, 3)(
                provider, ref browserService, ref browserId, out browser));
            // IShellBrowser inherits IOleWindow; QueryActiveShellView is slot 15.
            Marshal.ThrowExceptionForHR(Method<GetInterface>(browser, 15)(browser, out view));
            var folderViewId = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(view, ref folderViewId, out folderView));
            // IFolderView2 inherits IFolderView. These slots are defined by shobjidl_core.h.
            var getFlags = Method<GetFlags>(folderView, 25);
            if (requestedVisibility is { } requested)
            {
                // Let Explorer update both its internal view state and its window.
                // ShowWindow(SW_HIDE) alone leaves Shell's New/rename machinery
                // believing an invisible desktop view is still displaying icons.
                Marshal.ThrowExceptionForHR(Method<SetFlags>(folderView, 24)(
                    folderView, NoIcons, requested ? 0 : NoIcons));
            }
            Marshal.ThrowExceptionForHR(getFlags(folderView, out var flags));
            visible = (flags & NoIcons) == 0;
            return !requestedVisibility.HasValue || visible == requestedVisibility.Value;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not access Explorer desktop visibility through Shell", ex);
            return false;
        }
        finally
        {
            foreach (var pointer in new[] { folderView, view, browser, provider, unknown })
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            if (desktop != null && Marshal.IsComObject(desktop)) Marshal.ReleaseComObject(desktop);
            if (windows != null && Marshal.IsComObject(windows)) Marshal.ReleaseComObject(windows);
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryService(IntPtr self, ref Guid service, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterface(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFlags(IntPtr self, out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFlags(IntPtr self, uint mask, uint flags);
}
