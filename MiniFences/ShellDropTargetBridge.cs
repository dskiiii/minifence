using System.Runtime.InteropServices;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using MiniFences.Services;

namespace MiniFences;

/// <summary>
/// Forwards the complete OLE target sequence to Windows' Shell drag-image
/// manager. Explorer owns and positions the image; MiniFences never chases it.
/// </summary>
internal sealed class ShellDropTargetBridge : IDisposable
{
    private IDropTargetHelper? _helper;
    private bool _entered;
    private bool _loggedUnsupportedData;
    private System.Windows.IDataObject? _rejectedData;

    internal bool IsActive => _entered;

    internal bool DragEnter(IntPtr target, System.Windows.IDataObject data, System.Drawing.Point point,
        System.Windows.DragDropEffects effect)
    {
        if (_entered) return true;
        if (ReferenceEquals(_rejectedData, data)) return false;
        if (target == IntPtr.Zero || data is not ComDataObject nativeData)
        {
            if (!_loggedUnsupportedData)
            {
                _loggedUnsupportedData = true;
                AppLogger.Log($"Shell drop-target helper unavailable for data object {data.GetType().FullName}.");
            }
            return false;
        }

        try
        {
            _helper ??= (IDropTargetHelper)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"))!)!;
            var nativePoint = new NativePoint(point.X, point.Y);
            _helper.DragEnter(target, nativeData, ref nativePoint, (uint)effect);
            _entered = true;
            return true;
        }
        catch (Exception ex)
        {
            _rejectedData = data;
            ReleaseHelper("Shell drop-target DragEnter failed", ex);
            return false;
        }
    }

    internal void DragOver(System.Drawing.Point point, System.Windows.DragDropEffects effect)
    {
        if (!_entered || _helper == null) return;
        try
        {
            var nativePoint = new NativePoint(point.X, point.Y);
            _helper.DragOver(ref nativePoint, (uint)effect);
        }
        catch (Exception ex) { ReleaseHelper("Shell drop-target DragOver failed", ex); }
    }

    internal void DragLeave()
    {
        if (!_entered) return;
        try
        {
            _helper?.DragLeave();
        }
        catch (Exception ex) { AppLogger.LogException("Shell drop-target DragLeave failed", ex); }
        finally { _entered = false; }
    }

    internal void Show(bool visible)
    {
        if (!_entered || _helper == null) return;
        try { _helper.Show(visible); }
        catch (Exception ex) { ReleaseHelper("Shell drop-target Show failed", ex); }
    }

    internal void Drop(System.Windows.IDataObject data, System.Drawing.Point point,
        System.Windows.DragDropEffects effect)
    {
        if (!_entered || _helper == null || data is not ComDataObject nativeData)
        {
            DragLeave();
            return;
        }
        try
        {
            var nativePoint = new NativePoint(point.X, point.Y);
            _helper.Drop(nativeData, ref nativePoint, (uint)effect);
        }
        catch (Exception ex) { AppLogger.LogException("Shell drop-target Drop failed", ex); }
        finally { _entered = false; }
    }

    public void Dispose()
    {
        DragLeave();
        if (_helper != null && Marshal.IsComObject(_helper)) Marshal.FinalReleaseComObject(_helper);
        _helper = null;
        _rejectedData = null;
    }

    private void ReleaseHelper(string context, Exception ex)
    {
        AppLogger.LogException(context, ex);
        _entered = false;
        if (_helper != null && Marshal.IsComObject(_helper)) Marshal.FinalReleaseComObject(_helper);
        _helper = null;
    }

    [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTargetHelper
    {
        void DragEnter(IntPtr target, [MarshalAs(UnmanagedType.Interface)] ComDataObject dataObject,
            ref NativePoint point, uint effect);
        void DragLeave();
        void DragOver(ref NativePoint point, uint effect);
        void Drop([MarshalAs(UnmanagedType.Interface)] ComDataObject dataObject,
            ref NativePoint point, uint effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y) { X = x; Y = y; }
        public int X;
        public int Y;
    }
}
