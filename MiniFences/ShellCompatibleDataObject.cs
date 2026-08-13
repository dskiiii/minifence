using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MiniFences;

/// <summary>
/// WPF's managed DataObject exposes COM IDataObject but returns E_NOTIMPL when
/// the Shell drag-image helper stores its private formats. WinForms DataObject
/// implements those arbitrary COM formats, so this adapter exposes it through
/// both interfaces expected by WPF's DoDragDrop pipeline.
/// </summary>
internal sealed class ShellCompatibleDataObject : System.Windows.IDataObject, ComDataObject, IDisposable
{
    private readonly System.Windows.Forms.DataObject _inner = new();
    private readonly Dictionary<NativeFormatKey, STGMEDIUM> _shellFormats = [];
    private bool _disposed;
    private ComDataObject Native => _inner;

    public object? GetData(string format) => _inner.GetData(format);
    public object? GetData(string format, bool autoConvert) => _inner.GetData(format, autoConvert);
    public object? GetData(Type format) => _inner.GetData(format);
    public bool GetDataPresent(string format) => _inner.GetDataPresent(format);
    public bool GetDataPresent(string format, bool autoConvert) => _inner.GetDataPresent(format, autoConvert);
    public bool GetDataPresent(Type format) => _inner.GetDataPresent(format);
    public string[] GetFormats() => _inner.GetFormats();
    public string[] GetFormats(bool autoConvert) => _inner.GetFormats(autoConvert);
    public void SetData(string format, object data) => _inner.SetData(format, data);
    public void SetData(string format, object data, bool autoConvert) => _inner.SetData(format, autoConvert, data);
    public void SetData(Type format, object data) => _inner.SetData(format, data);
    public void SetData(object data) => _inner.SetData(data);

    void ComDataObject.GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        foreach (var pair in _shellFormats)
        {
            if (!pair.Key.Matches(format, pair.Value.tymed)) continue;
            medium = DuplicateMedium(pair.Value, format.cfFormat);
            return;
        }
        Native.GetData(ref format, out medium);
    }
    void ComDataObject.GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => Native.GetDataHere(ref format, ref medium);
    int ComDataObject.QueryGetData(ref FORMATETC format)
    {
        foreach (var pair in _shellFormats)
            if (pair.Key.Matches(format, pair.Value.tymed)) return 0;
        return Native.QueryGetData(ref format);
    }
    int ComDataObject.GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) => Native.GetCanonicalFormatEtc(ref input, out output);
    void ComDataObject.SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
    {
        var key = new NativeFormatKey(format.cfFormat, format.dwAspect, format.lindex);
        if (_shellFormats.Remove(key, out var previous)) ReleaseStgMedium(ref previous);
        _shellFormats[key] = release ? medium : DuplicateMedium(medium, format.cfFormat);
    }
    IEnumFORMATETC ComDataObject.EnumFormatEtc(DATADIR direction) => Native.EnumFormatEtc(direction);
    int ComDataObject.DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) =>
        Native.DAdvise(ref format, flags, sink, out connection);
    void ComDataObject.DUnadvise(int connection) => Native.DUnadvise(connection);
    int ComDataObject.EnumDAdvise(out IEnumSTATDATA? enumAdvise) => Native.EnumDAdvise(out enumAdvise);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var stored in _shellFormats.Values)
        {
            var medium = stored;
            ReleaseStgMedium(ref medium);
        }
        _shellFormats.Clear();
    }

    private static STGMEDIUM DuplicateMedium(STGMEDIUM source, short clipboardFormat)
    {
        if (source.tymed is TYMED.TYMED_ISTREAM or TYMED.TYMED_ISTORAGE)
        {
            if (source.unionmember == IntPtr.Zero) throw new COMException("Invalid Shell interface medium.");
            Marshal.AddRef(source.unionmember);
            return new STGMEDIUM { tymed = source.tymed, unionmember = source.unionmember, pUnkForRelease = null };
        }
        var duplicate = OleDuplicateData(source.unionmember, unchecked((ushort)clipboardFormat), 0);
        if (duplicate == IntPtr.Zero) throw new OutOfMemoryException("Could not duplicate Shell drag data.");
        return new STGMEDIUM { tymed = source.tymed, unionmember = duplicate, pUnkForRelease = null };
    }

    private readonly record struct NativeFormatKey(short ClipboardFormat, DVASPECT Aspect, int Index)
    {
        internal bool Matches(FORMATETC format, TYMED storedMedium) =>
            ClipboardFormat == format.cfFormat && Aspect == format.dwAspect && Index == format.lindex &&
            (format.tymed & storedMedium) != 0;
    }

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern IntPtr OleDuplicateData(IntPtr source, uint clipboardFormat, uint flags);

    [System.Runtime.InteropServices.DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
