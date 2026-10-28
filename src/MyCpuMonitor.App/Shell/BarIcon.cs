using System.Runtime.InteropServices;

namespace MyCpuMonitor.Shell;

/// <summary>
/// Builds a small-icon HICON showing a history as bars (one pixel column per value, newest on the right), drawn
/// directly into a 32-bit DIB: no imaging dependency. The caller owns (and must destroy) the returned icon.
/// </summary>
internal static unsafe partial class BarIcon
{
    private const uint Border = 0xFF6E6E6E;
    private const uint Background = 0xFF141414;
    private const uint Bar = 0xFF4CC2FF;
    private const int SM_CXSMICON = 49;

    public static int SmallIconSize(nint hwnd) => GetSystemMetricsForDpi(SM_CXSMICON, Win32.GetDpiForWindow(hwnd));

    /// <param name="fractions">Values in 0..1, oldest first; the last (size - 2) are drawn.</param>
    public static nint Create(int size, ReadOnlySpan<float> fractions)
    {
        var header = new BitmapInfoHeader
        {
            biSize = (uint)sizeof(BitmapInfoHeader),
            biWidth = size,
            biHeight = -size, // Top-down.
            biPlanes = 1,
            biBitCount = 32,
        };
        void* bits;
        var color = CreateDIBSection(0, &header, 0, &bits, 0, 0);
        var mask = CreateBitmap(size, size, 1, 1, null);
        try
        {
            var pixels = new Span<uint>(bits, size * size);
            var inner = size - 2;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                    pixels[y * size + x] = x == 0 || y == 0 || x == size - 1 || y == size - 1 ? Border : Background;
            }

            var values = fractions.Length > inner ? fractions[^inner..] : fractions;
            var offset = inner - values.Length; // Right-aligned: the newest value is at the right edge.
            for (var i = 0; i < values.Length; i++)
            {
                var height = (int)MathF.Round(Math.Clamp(values[i], 0, 1) * inner);
                var x = 1 + offset + i;
                for (var y = 0; y < height; y++)
                    pixels[(size - 2 - y) * size + x] = Bar;
            }

            var info = new IconInfo { fIcon = 1, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(&info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint hdc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitCount, void* bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(IconInfo* info);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
