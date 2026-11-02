using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EveryDownload.Services;

/// <summary>
/// Turns a pattern like <c>http://site/photo[001-120].jpg</c> into the 120 addresses it stands for.
/// Supports number ranges (zero padding follows the first number), letter ranges <c>[a-e]</c>, a step <c>[1-99:2]</c>,
/// and several ranges in one address.
/// </summary>
public static class UrlPattern
{
    public const int Limit = 5000;

    private static readonly Regex Group = new(@"\[(?<a>[0-9]+|[A-Za-z])-(?<b>[0-9]+|[A-Za-z])(?::(?<s>[0-9]+))?\]", RegexOptions.Compiled);

    public static bool HasPattern(string text) => Group.IsMatch(text);

    /// <summary>The addresses <paramref name="pattern"/> stands for, at most <see cref="Limit"/> of them. A text with no range yields itself.</summary>
    public static List<string> Expand(string pattern)
    {
        var results = new List<string> { "" };
        int last = 0;
        foreach (Match m in Group.Matches(pattern))
        {
            string literal = pattern[last..m.Index];
            last = m.Index + m.Length;
            var values = Values(m);
            var next = new List<string>(Math.Min(Limit, results.Count * Math.Max(1, values.Count)));
            foreach (var prefix in results)
                foreach (var v in values)
                {
                    next.Add(prefix + literal + v);
                    if (next.Count >= Limit) break;
                }
            results = next;
        }
        string tail = pattern[last..];
        return results.Select(r => r + tail).ToList();
    }

    private static List<string> Values(Match m)
    {
        string a = m.Groups["a"].Value, b = m.Groups["b"].Value;
        int step = m.Groups["s"].Success ? Math.Max(1, int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture)) : 1;
        var list = new List<string>();
        if (char.IsDigit(a[0]) && char.IsDigit(b[0]))
        {
            long from = long.Parse(a, CultureInfo.InvariantCulture), to = long.Parse(b, CultureInfo.InvariantCulture);
            int width = a.Length > 1 && a[0] == '0' ? a.Length : 0; // "001" keeps its zeros, "1" does not
            if (from > to) (from, to) = (to, from);
            for (long i = from; i <= to && list.Count < Limit; i += step) list.Add(width > 0 ? i.ToString().PadLeft(width, '0') : i.ToString(CultureInfo.InvariantCulture));
        }
        else if (a.Length == 1 && b.Length == 1 && char.IsLetter(a[0]) && char.IsLetter(b[0]))
        {
            char from = a[0], to = b[0];
            if (from > to) (from, to) = (to, from);
            for (int c = from; c <= to; c += step) list.Add(((char)c).ToString());
        }
        else list.Add(m.Value); // mixed digits and letters: leave it as typed
        return list;
    }

    /// <summary>One entry per non-empty line of <paramref name="text"/>, with every pattern expanded.</summary>
    public static List<string> ExpandLines(string text)
    {
        var all = new List<string>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (all.Count >= Limit) break;
            all.AddRange(HasPattern(line) ? Expand(line) : new List<string> { line });
        }
        return all.Take(Limit).ToList();
    }
}
