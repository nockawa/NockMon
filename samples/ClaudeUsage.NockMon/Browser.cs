using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeUsage.NockMon;

/// <summary>
/// Opens links in the user's browser as the user: NockMon is elevated, and a browser it starts itself (ShellExecute, or
/// a Hyperlink's NavigateUri) would run as administrator. Explorer hands the link to the running, non-elevated shell, as
/// NockMon does for folders.
/// </summary>
internal static class Browser
{
    public static bool Open(Uri url)
    {
        // An absolute URI escapes quotes and spaces: the argument can't break out of its quotes.
        if (!url.IsAbsoluteUri || url.Scheme is not ("https" or "http") || url.AbsoluteUri.Contains('"'))
            return false;
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var process = Process.Start(new ProcessStartInfo(explorer, $"\"{url.AbsoluteUri}\"") { UseShellExecute = false });
            return process is not null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
