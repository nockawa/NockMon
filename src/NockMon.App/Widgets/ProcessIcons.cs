using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NockMon.Core.Sampling;

namespace NockMon.Widgets;

/// <summary>
/// Small icons of process executables, for the UI thread. Paths are cached per process instance (PID + create time),
/// icons per executable and pixel size: extraction (~1 ms) only happens the first time an executable is shown.
/// Executables without an icon get the stock application icon; processes without an image (System, Idle…) get none.
/// </summary>
internal static unsafe partial class ProcessIcons
{
    private const int MaxInstances = 4096;

    private static readonly Dictionary<(int Pid, long CreateTime), string?> Paths = [];
    private static readonly Dictionary<(string Path, int Size), ImageSource?> Icons = [];
    private static readonly Dictionary<int, ImageSource?> DefaultIcons = [];

    public static ImageSource? Get(int pid, long createTime, int pixelSize)
    {
        if (!Paths.TryGetValue((pid, createTime), out var path))
        {
            if (Paths.Count >= MaxInstances)
                Paths.Clear();
            Paths[(pid, createTime)] = path = ProcessImage.TryGetPath(pid, createTime);
        }
        if (path is null)
            return null;

        var key = (path.ToLowerInvariant(), pixelSize);
        if (!Icons.TryGetValue(key, out var icon))
            Icons[key] = icon = Extract(path, 0, pixelSize) ?? Default(pixelSize);
        return icon;
    }

    private static ImageSource? Default(int pixelSize)
    {
        if (DefaultIcons.TryGetValue(pixelSize, out var icon))
            return icon;
        var info = new StockIconInfo { cbSize = (uint)sizeof(StockIconInfo) };
        icon = SHGetStockIconInfo(SIID_APPLICATION, SHGSI_ICONLOCATION, &info) == 0
            ? Extract(new string(info.szPath), info.iIcon, pixelSize)
            : null;
        return DefaultIcons[pixelSize] = icon;
    }

    private static WriteableBitmap? Extract(string path, int index, int pixelSize)
    {
        // Small icon only (HIWORD of the size): S_FALSE when the file has no icon.
        nint small = 0;
        if (SHDefExtractIcon(path, index, 0, null, &small, (uint)(pixelSize << 16)) != 0 || small == 0)
            return null;
        try
        {
            return ToBitmap(small, pixelSize);
        }
        finally
        {
            DestroyIcon(small);
        }
    }

    /// <summary>HICON → premultiplied BGRA bitmap. Icons without alpha (old 24-bit) take their transparency from the mask.</summary>
    private static WriteableBitmap? ToBitmap(nint icon, int size)
    {
        IconInfo info;
        if (!GetIconInfo(icon, &info))
            return null;
        var dc = CreateCompatibleDC(0);
        try
        {
            if (info.hbmColor == 0)
                return null; // Monochrome icon: not worth supporting.
            var header = new BitmapInfoHeader { biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[size * size * 4];
            fixed (byte* p = pixels)
            {
                if (GetDIBits(dc, info.hbmColor, 0, (uint)size, p, &header, 0) == 0)
                    return null;
            }

            var hasAlpha = false;
            for (var i = 3; i < pixels.Length && !hasAlpha; i += 4)
                hasAlpha = pixels[i] != 0;
            if (!hasAlpha && info.hbmMask != 0)
            {
                var mask = new byte[size * size * 4];
                var maskHeader = header;
                fixed (byte* m = mask)
                    GetDIBits(dc, info.hbmMask, 0, (uint)size, m, &maskHeader, 0);
                for (var i = 0; i < pixels.Length; i += 4)
                    pixels[i + 3] = mask[i] == 0 ? (byte)255 : (byte)0; // Mask: white = transparent.
            }

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var a = pixels[i + 3];
                if (a == 255)
                    continue;
                pixels[i] = (byte)(pixels[i] * a / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * a / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * a / 255);
            }

            var bitmap = new WriteableBitmap(size, size);
            using (var stream = bitmap.PixelBuffer.AsStream())
                stream.Write(pixels);
            bitmap.Invalidate();
            return bitmap;
        }
        finally
        {
            DeleteDC(dc);
            if (info.hbmColor != 0)
                DeleteObject(info.hbmColor);
            if (info.hbmMask != 0)
                DeleteObject(info.hbmMask);
        }
    }

    private const uint SIID_APPLICATION = 2;
    private const uint SHGSI_ICONLOCATION = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct StockIconInfo
    {
        public uint cbSize;
        public nint hIcon;
        public int iSysImageIndex;
        public int iIcon;
        public fixed char szPath[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
        public uint bmiColors; // BITMAPINFO's first RGBQUAD (unused for BI_RGB 32 bpp).
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHDefExtractIcon(string iconFile, int index, uint flags, nint* large, nint* small, uint iconSize);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetStockIconInfo(uint siid, uint flags, StockIconInfo* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(nint icon, IconInfo* info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(nint dc, nint bitmap, uint start, uint lines, void* bits, BitmapInfoHeader* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);
}
