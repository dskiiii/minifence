using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniFences.Services;

namespace MiniFences;

internal static class ShellDragSourceImage
{
    private const int IconSize = 48;
    private const int ImageHeight = 82;
    // Match the desktop item cell instead of squeezing the filename into the
    // 48px glyph width. The Shell image still keeps the icon centred.
    internal const int DragLabelWidth = 86;
    internal const int DragLabelMaxLines = 2;

    internal static bool TryInitialize(ShellCompatibleDataObject data, ImageSource? icon, string? label)
    {
        if (icon == null) return false;
        var bitmap = IntPtr.Zero;
        try
        {
            var text = string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim();
            var typeface = new Typeface(
                new System.Windows.Media.FontFamily("Segoe UI"),
                System.Windows.FontStyles.Normal,
                System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal);
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                12,
                System.Windows.Media.Brushes.White,
                1.0)
            {
                TextAlignment = System.Windows.TextAlignment.Center,
                Trimming = System.Windows.TextTrimming.CharacterEllipsis,
                MaxLineCount = DragLabelMaxLines,
                MaxTextWidth = DragLabelWidth,
                MaxTextHeight = 30
            };
            const int width = DragLabelWidth;
            var shadow = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                System.Windows.FlowDirection.LeftToRight,
                typeface,
                12,
                System.Windows.Media.Brushes.Black,
                1.0)
            {
                TextAlignment = System.Windows.TextAlignment.Center,
                Trimming = System.Windows.TextTrimming.CharacterEllipsis,
                MaxLineCount = DragLabelMaxLines,
                MaxTextWidth = width,
                MaxTextHeight = 30
            };
            var visual = new DrawingVisual();
            TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawImage(icon, new System.Windows.Rect((width - IconSize) / 2d, 0, IconSize, IconSize));
                if (text.Length > 0)
                {
                    drawing.DrawText(shadow, new System.Windows.Point(1, 53));
                    drawing.DrawText(formatted, new System.Windows.Point(0, 52));
                }
            }
            var rendered = new RenderTargetBitmap(width, ImageHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual);
            var straight = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * ImageHeight * 4];
            straight.CopyPixels(pixels, width * 4, 0);
            bitmap = CreateDib(pixels, width, ImageHeight);
            if (bitmap == IntPtr.Zero) return false;

            var image = new ShellDragImage
            {
                Size = new NativeSize { Width = width, Height = ImageHeight },
                CursorOffset = new NativePoint { X = width / 2, Y = IconSize / 2 },
                Bitmap = bitmap,
                ColorKey = 0xFFFFFFFF
            };
            var helperType = Type.GetTypeFromCLSID(new Guid("4657278A-411B-11D2-839A-00C04FD918D0"));
            if (helperType == null) return false;
            var helper = (IDragSourceHelper)Activator.CreateInstance(helperType)!;
            helper.InitializeFromBitmap(ref image, data);
            data.SetData(DesktopDragData.NativeShellImageFormat, true, false);
            AppLogger.Log("Native Shell drag image initialized successfully.");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Could not initialize native Shell drag image", ex);
            return false;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }

    private static IntPtr CreateDib(byte[] pixels, int width, int height)
    {
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
                Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length
            }
        };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap != IntPtr.Zero && bits != IntPtr.Zero) Marshal.Copy(pixels, 0, bits, pixels.Length);
            return bitmap;
        }
        finally { if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc); }
    }

    [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        void InitializeFromBitmap(ref ShellDragImage image, [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject dataObject);
        void InitializeFromWindow(IntPtr window, ref NativePoint point, [MarshalAs(UnmanagedType.Interface)] System.Runtime.InteropServices.ComTypes.IDataObject dataObject);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ShellDragImage { public NativeSize Size; public NativePoint CursorOffset; public IntPtr Bitmap; public uint ColorKey; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width; public int Height; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width; public int Height; public ushort Planes; public ushort BitCount;
        public uint Compression; public uint SizeImage; public int XPelsPerMeter; public int YPelsPerMeter;
        public uint ClrUsed; public uint ClrImportant;
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
