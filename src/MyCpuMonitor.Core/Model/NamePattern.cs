namespace MyCpuMonitor.Core.Model;

/// <summary>
/// Case-insensitive image name pattern: '*' and '?' wildcards, several alternatives separated by ';'.
/// An alternative without wildcard nor '.' also matches the name without its ".exe" extension ("devenv" matches "devenv.exe").
/// </summary>
public sealed class NamePattern
{
    private readonly string[] _alternatives;

    public NamePattern(string pattern)
    {
        Pattern = pattern;
        _alternatives = pattern.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public string Pattern { get; }

    public bool IsMatch(ReadOnlySpan<char> name)
    {
        foreach (var alt in _alternatives)
        {
            if (Glob(alt, name))
                return true;
            if (alt.AsSpan().IndexOfAny('*', '?') < 0 && !alt.Contains('.')
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && name[..^4].Equals(alt, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Iterative wildcard match with single backtrack point (linear for practical patterns).</summary>
    private static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, starP = -1, starT = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
            p++;
        return p == pattern.Length;
    }

    public override string ToString() => Pattern;
}
