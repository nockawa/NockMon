using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeUsage.NockMon;

/// <summary>What a news item is about. Stored by name in widget settings: never rename a member.</summary>
public enum NewsCategory
{
    Models,
    ClaudeCode,
    ClaudeCodeReleases,
    Api,
    Sdk,
    ManagedAgents,
    Enterprise,
    CloudProviders,
}

/// <summary>One announcement. <see cref="Key"/> is its stable identity in the read set.</summary>
public sealed record NewsItem(string Key, NewsCategory Category, DateTimeOffset Date, string Text, string Url);

/// <summary>The news sources: where they are, how they are parsed, and how platform notes are sorted into categories.</summary>
public static partial class NewsFeeds
{
    public const string ReleaseNotesUrl = "https://platform.claude.com/docs/en/release-notes/overview";
    public const string WhatsNewUrl = "https://code.claude.com/docs/en/whats-new";
    public const string ChangelogUrl = "https://code.claude.com/docs/en/changelog";

    /// <summary>Settings order, with the label, a hint, and whether a new widget shows it.</summary>
    public static readonly (NewsCategory Category, string Label, string Hint, bool Default)[] Categories =
    [
        (NewsCategory.Models, "Models, pricing and plans", "Model launches and retirements, price changes, Pro and Max plan changes.", true),
        (NewsCategory.ClaudeCode, "Claude Code: weekly highlights", "The weekly digest of notable Claude Code features.", true),
        (NewsCategory.ClaudeCodeReleases, "Claude Code: every release", "One entry per version, almost daily.", false),
        (NewsCategory.Api, "API features", "New Messages API features and tools.", false),
        (NewsCategory.Sdk, "SDKs and CLI", "Client SDK and API versions, the ant CLI.", false),
        (NewsCategory.ManagedAgents, "Managed Agents", "Claude Managed Agents.", false),
        (NewsCategory.Enterprise, "Enterprise and admin", "Admin, Compliance and organization APIs, Console settings.", false),
        (NewsCategory.CloudProviders, "Cloud providers", "Claude on Amazon Bedrock, Google Cloud, Microsoft Foundry.", false),
    ];

    public static string Label(NewsCategory category) => Categories.First(c => c.Category == category).Label;

    // ---- Platform release notes: `### <Month d, yyyy>`, then one bullet per change -------------------------------

    /// <summary>One item per bullet of the most recent dates, categorized.</summary>
    public static List<NewsItem> ParseReleaseNotes(string md, int maxDates = 8)
    {
        var items = new List<NewsItem>();
        DateTimeOffset? date = null;
        var dates = 0;
        foreach (var raw in md.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                if (++dates > maxDates)
                    break;
                date = TryDate(line[4..].Trim());
                continue;
            }
            if (date is not { } when || !(line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal)))
                continue;
            var bullet = line[2..];
            var text = CleanMarkdown(bullet);
            // The date plus the head of the text: stable across a later wording tweak at the tail.
            var key = $"platform:{when:yyyy-MM-dd}:{(text.Length > 60 ? text[..60] : text)}";
            var url = MarkdownLink().Match(bullet) is { Success: true } link ? link.Groups[2].Value : ReleaseNotesUrl;
            items.Add(new NewsItem(key, Classify(bullet), when, FirstSentence(text), url));
        }
        return items;
    }

    /// <summary>
    /// The category of a release-notes bullet, from the documentation it links to and a few phrases. Model launches,
    /// prices and plans first (a launch also lists the clouds it is on); then the audience-specific ones, which would
    /// otherwise land in the generic API features.
    /// </summary>
    public static NewsCategory Classify(string bullet)
    {
        var links = MarkdownLink().Matches(bullet).Select(m => m.Groups[2].Value).ToList();
        bool Links(string pattern) => links.Any(u => Regex.IsMatch(u, pattern, RegexOptions.IgnoreCase));
        bool Says(string pattern) => Regex.IsMatch(bullet, pattern);

        if (Says(@"launched \*\*Claude"))
            return NewsCategory.Models;
        if (Says("Models API")) // Fields of GET /v1/models: an API feature, though it links the models overview.
            return NewsCategory.Api;
        if (Links(@"/about-claude/(pricing|model-deprecations|api-credits)|/docs/en/models/")
            || Says(@"Claude (Opus|Sonnet|Haiku|Fable) [\d.]+[^.]{0,60}\b(deprecat|retire)") || Says(@"\b(Pro|Max)\b[^.]{0,30}\bplans?\b"))
            return NewsCategory.Models;
        if (Links("/manage-claude/") || Says(@"Claude Enterprise|Enterprise organizations|Admin API|Compliance API|[Oo]rganization admins"))
            return NewsCategory.Enterprise;
        if (Links("claude-in-microsoft-foundry|claude-models-on-google-cloud|claude-platform-on-aws|bedrock|vertex")
            || Says("Microsoft Foundry|Google Cloud|Amazon Bedrock|Vertex AI|Claude Platform on AWS"))
            return NewsCategory.CloudProviders;
        if (Links("/managed-agents/") || Says("Managed Agents"))
            return NewsCategory.ManagedAgents;
        if (Links("/cli-sdks-libraries/|/api/migration-") || Says(@"\bSDKs?\b|API version"))
            return NewsCategory.Sdk;
        return NewsCategory.Api;
    }

    // ---- Claude Code "What's new": `**Week N**`, the dates, the headline paragraph, a digest link -------------------

    /// <summary>One item per week: its headline feature (the "Also this week" paragraph is left out).</summary>
    public static List<NewsItem> ParseWhatsNew(string md, int maxWeeks = 8)
    {
        var items = new List<NewsItem>();
        foreach (Match m in WhatsNewWeek().Matches(md).Take(maxWeeks))
        {
            var (week, range, headline) = (m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Value);
            var digest = DigestLink().Match(md, m.Index + m.Length);
            var url = digest.Success && digest.Groups[1].Value == week ? digest.Groups[2].Value.Replace(".md", "") : WhatsNewUrl;
            items.Add(new NewsItem($"whats-new:{week}|{range}", NewsCategory.ClaudeCode, WeekStart(range), FirstSentence(CleanMarkdown(headline)), url));
        }
        return items;
    }

    /// <summary>"September 7–11, 2026" or "August 31 – September 4, 2026": the start day, in the year the range ends.</summary>
    private static DateTimeOffset WeekStart(string range)
    {
        var m = Regex.Match(range, @"^([A-Za-z]+) (\d{1,2}).*(\d{4})\s*$");
        if (!m.Success || TryDate($"{m.Groups[1].Value} {m.Groups[2].Value}, {m.Groups[3].Value}") is not { } d)
            return DateTimeOffset.MinValue;
        return d > DateTimeOffset.UtcNow.AddDays(1) ? d.AddYears(-1) : d; // A range spanning New Year.
    }

    // ---- Claude Code changelog: `**x.y.z**`, the date, sections of bullets -------------------------------------

    /// <summary>One item per version: its first change and how many there are.</summary>
    public static List<NewsItem> ParseChangelog(string md, int maxVersions = 10)
    {
        var items = new List<NewsItem>();
        var versions = ChangelogVersion().Matches(md).Take(maxVersions + 1).ToList();
        for (var i = 0; i < Math.Min(versions.Count, maxVersions); i++)
        {
            var (version, date) = (versions[i].Groups[1].Value, TryDate(versions[i].Groups[2].Value));
            if (date is not { } when)
                continue;
            var end = i + 1 < versions.Count ? versions[i + 1].Index : md.Length;
            var bullets = md[(versions[i].Index + versions[i].Length)..end].Split('\n')
                .Where(l => l.StartsWith("* ", StringComparison.Ordinal)).Select(l => CleanMarkdown(l[2..])).ToList();
            var first = bullets.Count > 0 ? FirstSentence(bullets[0]) : "";
            var more = bullets.Count > 1 ? $" (+{bullets.Count - 1} more)" : "";
            items.Add(new NewsItem($"changelog:{version}", NewsCategory.ClaudeCodeReleases, when, $"v{version}: {first}{more}", ChangelogUrl));
        }
        return items;
    }

    // ---- Text -----------------------------------------------------------------------------------------------------

    private static DateTimeOffset? TryDate(string text) =>
        DateTimeOffset.TryParseExact(text, "MMMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>Links → their text, emphasis and code ticks dropped, MDX escapes undone.</summary>
    private static string CleanMarkdown(string s)
    {
        s = MarkdownLink().Replace(s, "$1");
        s = s.Replace("**", "").Replace("`", "");
        s = Regex.Replace(s, @"\\([\\`*_{}\[\]()#+\-.!$<>|])", "$1");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    private static string FirstSentence(string s)
    {
        const int max = 200;
        var dot = s.IndexOf(". ", 20, StringComparison.Ordinal);
        if (dot > 0 && dot < max)
            return s[..(dot + 1)];
        return s.Length > max ? s[..(max - 1)].TrimEnd() + "…" : s;
    }

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^\*\*Week (\d+)\*\*\s*\n\s*\n([^\n]+)\n\s*\n([^\n]+)", RegexOptions.Multiline)]
    private static partial Regex WhatsNewWeek();

    [GeneratedRegex(@"\[Read the Week (\d+) digest[^\]]*\]\(([^)\s]+)\)")]
    private static partial Regex DigestLink();

    [GeneratedRegex(@"^\*\*(\d+\.\d+\.\d+)\*\*\s*\n\s*\n([A-Z][a-z]+ \d{1,2}, \d{4})", RegexOptions.Multiline)]
    private static partial Regex ChangelogVersion();
}
