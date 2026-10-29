namespace MyCpuMonitor.Core.Help;

/// <summary>
/// In-app documentation of a widget or metric. <see cref="Summary"/> is plain text, one or two sentences (tooltips);
/// <see cref="Details"/> is <see cref="DocMarkup"/> (editors, pinned help).
/// </summary>
public sealed record Doc(string Title, string Summary, string Details);

public enum DocStyle
{
    Normal,
    Bold,
    Code,
}

public readonly record struct DocRun(string Text, DocStyle Style);

/// <param name="Bullet">A "- " list item.</param>
public sealed record DocBlock(bool Bullet, IReadOnlyList<DocRun> Runs);

/// <summary>
/// Minimal markup for <see cref="Doc.Details"/>: blank line = new paragraph, a line starting with "- " = bullet item,
/// other lines continue the current block (joined with a space), **bold**, `code`. No nesting, no escaping.
/// </summary>
public static class DocMarkup
{
    public static IReadOnlyList<DocBlock> Parse(string markup)
    {
        var blocks = new List<DocBlock>();
        var text = new List<string>();
        var bullet = false;

        void Flush()
        {
            if (text.Count > 0)
                blocks.Add(new DocBlock(bullet, ParseInline(string.Join(' ', text))));
            text.Clear();
            bullet = false;
        }

        foreach (var raw in markup.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush();
                bullet = true;
                line = line[2..].TrimStart();
            }
            text.Add(line);
        }
        Flush();
        return blocks;
    }

    /// <summary>Splits on ** and ` delimiters. An unclosed delimiter is kept as literal text.</summary>
    public static IReadOnlyList<DocRun> ParseInline(string text)
    {
        var runs = new List<DocRun>();
        var start = 0;
        var i = 0;
        while (i < text.Length)
        {
            var (open, style) = text[i] == '`' ? ("`", DocStyle.Code)
                : text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*' ? ("**", DocStyle.Bold)
                : (null, DocStyle.Normal);
            var close = open is null ? -1 : text.IndexOf(open, i + open.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                i++;
                continue;
            }
            if (i > start)
                runs.Add(new DocRun(text[start..i], DocStyle.Normal));
            runs.Add(new DocRun(text[(i + open!.Length)..close], style));
            i = start = close + open.Length;
        }
        if (start < text.Length)
            runs.Add(new DocRun(text[start..], DocStyle.Normal));
        return runs;
    }
}
