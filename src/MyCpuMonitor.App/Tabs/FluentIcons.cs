using System.Globalization;
using Microsoft.UI.Xaml.Media;

namespace MyCpuMonitor.Tabs;

/// <summary>
/// Fluent UI System Icons (Microsoft, MIT): the WinUI / Fluent design icon set, ~2900 icons by name ("board",
/// "data_trending", "games"…). The font is a file next to the app; the name → code point map (20 px variants, else the
/// closest size) is embedded and read on first use.
/// </summary>
internal static class FluentIcons
{
    private static Dictionary<string, string>? _glyphs;
    private static string[]? _names;

    public static FontFamily Font { get; } = new("ms-appx:///Assets/Icons/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular");

    /// <summary>All icon names, sorted.</summary>
    public static IReadOnlyList<string> Names => _names ??= Glyphs.Keys.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Icons offered first in the picker: monitoring-flavored.</summary>
    public static IReadOnlyList<string> Suggested { get; } =
    [
        "board", "data_trending", "data_area", "data_line", "gauge", "top_speed", "developer_board", "memory", "ram", "storage",
        "hard_drive", "flash", "temperature", "battery_charge", "plug_connected", "wifi_1", "globe", "cloud", "server",
        "database", "code", "bug", "window_dev_tools", "games", "video", "music_note_2", "headphones", "bot", "sparkle",
        "shield", "lock_closed", "timer", "clock", "calendar_ltr", "home", "apps", "grid", "star", "heart", "flag",
    ];

    public static bool Exists(string name) => Glyphs.ContainsKey(name);

    /// <summary>The glyph of <paramref name="name"/>, or of the default page icon if unknown.</summary>
    public static string Glyph(string? name) =>
        name is not null && Glyphs.TryGetValue(name, out var glyph) ? glyph : Glyphs.GetValueOrDefault(Core.Layout.LayoutPage.DefaultIcon, "?");

    /// <summary>"data_trending" → "Data trending".</summary>
    public static string DisplayName(string name)
    {
        var text = name.Replace('_', ' ');
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static Dictionary<string, string> Glyphs => _glyphs ??= Load();

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = typeof(FluentIcons).Assembly.GetManifestResourceStream("FluentIcons.txt")
                           ?? throw new InvalidOperationException("FluentIcons.txt resource missing.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var space = line.IndexOf(' ');
            if (space > 0 && int.TryParse(line.AsSpan(space + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                map[line[..space]] = char.ConvertFromUtf32(code);
        }
        return map;
    }
}
