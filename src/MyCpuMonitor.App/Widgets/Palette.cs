using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace MyCpuMonitor.Widgets;

/// <summary>Series colors (readable on light and dark) and theme-dependent chart chrome.</summary>
public static class Palette
{
    public static readonly Color[] Series =
    [
        Rgb(0x0078D4), Rgb(0xE74856), Rgb(0x00B294), Rgb(0xFFB900),
        Rgb(0x8764B8), Rgb(0xFF8C00), Rgb(0x498205), Rgb(0xC239B3),
    ];

    public static Color SeriesColor(string? hex, int index) =>
        TryParse(hex, out var color) ? color : Series[index % Series.Length];

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, null, out var v))
            return false;
        color = Rgb(v);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color Text(ElementTheme theme) => theme == ElementTheme.Dark ? ColorHelper.FromArgb(0xC0, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0xB0, 0, 0, 0);
    public static Color SubtleText(ElementTheme theme) => theme == ElementTheme.Dark ? ColorHelper.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x80, 0, 0, 0);
    public static Color Grid(ElementTheme theme) => theme == ElementTheme.Dark ? ColorHelper.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x1C, 0, 0, 0);

    public static Color WithAlpha(Color c, byte alpha) => ColorHelper.FromArgb(alpha, c.R, c.G, c.B);

    private static Color Rgb(uint v) => ColorHelper.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
}
