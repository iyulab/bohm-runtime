using System.Globalization;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// What a model shown only the part of a source around an element is told of the rest: the lines elsewhere that use what
/// the element names (its id, its classes, the functions its handlers call), and an outline — where each script and style
/// block starts and ends and where each function is declared. Without them the model reads the rest a range at a time to
/// find where to change, and a small model on this computer spends its rounds reading and stops before any change.
/// Read from the text like the other checks here: good enough to point at a line, which is all a model needs to read it.
/// </summary>
internal static partial class SourceMap
{
    /// <summary>The most lines elsewhere shown for what the element names.</summary>
    public const int MaxRelatedLines = 24;

    /// <summary>The most outline entries shown.</summary>
    public const int MaxOutlineEntries = 60;

    /// <summary>The longest line shown in either list; the model reads the rest with read_source.</summary>
    private const int MaxLineLength = 160;

    /// <summary>Names the element uses that its handling code is likely to name too — its id, classes and handlers' functions.</summary>
    internal static IReadOnlyList<string> NamesIn(string elementHtml)
    {
        var start = StartTagOf().Match(elementHtml);
        if (!start.Success) return [];
        var tag = start.Value;
        var names = new List<string>();
        if (IdAttribute().Match(tag) is { Success: true } id) names.Add(id.Groups["v"].Value);
        if (ClassAttribute().Match(tag) is { Success: true } classes)
            names.AddRange(classes.Groups["v"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (Match handler in HandlerAttribute().Matches(tag))
            names.AddRange(Called().Matches(handler.Groups["v"].Value).Select(m => m.Groups["name"].Value));
        return [.. names.Where(n => n.Length >= 3 && Word().IsMatch(n)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>1-based lines outside <paramref name="from"/>–<paramref name="to"/> that use one of <paramref name="names"/> as a word.</summary>
    internal static IReadOnlyList<int> Related(string[] lines, IReadOnlyList<string> names, int from, int to)
    {
        if (names.Count == 0) return [];
        var used = new Regex($@"(?<![\w$-])(?:{string.Join('|', names.Select(Regex.Escape))})(?![\w$-])", RegexOptions.CultureInvariant);
        var found = new List<int>();
        for (var i = 1; i <= lines.Length && found.Count < MaxRelatedLines; i++)
            if ((i < from || i > to) && used.IsMatch(lines[i - 1])) found.Add(i);
        return found;
    }

    /// <summary>1-based lines outside <paramref name="from"/>–<paramref name="to"/> where a script or style block starts or ends, or a function is declared.</summary>
    internal static IReadOnlyList<int> Outline(string[] lines, int from, int to)
    {
        var found = new List<int>();
        for (var i = 1; i <= lines.Length && found.Count < MaxOutlineEntries; i++)
            if ((i < from || i > to) && Landmark().IsMatch(lines[i - 1])) found.Add(i);
        return found;
    }

    /// <summary>
    /// The text told after the part shown: the related lines, then the outline — empty when there is neither. Each line as it
    /// is, with its number, cut at <see cref="MaxLineLength"/>.
    /// </summary>
    public static string Describe(string[] lines, string elementHtml, int from, int to)
    {
        var related = Related(lines, NamesIn(elementHtml), from, to);
        var outline = Outline(lines, from, to).Except(related).ToList();
        if (related.Count == 0 && outline.Count == 0) return "";
        var text = new System.Text.StringBuilder();
        if (related.Count > 0)
        {
            text.Append("Lines elsewhere that use what the element names:\n");
            foreach (var n in related) text.Append(Line(lines, n)).Append('\n');
            text.Append('\n');
        }
        if (outline.Count > 0)
        {
            text.Append("Where the rest of the source has its scripts, styles and functions:\n");
            foreach (var n in outline) text.Append(Line(lines, n)).Append('\n');
            text.Append('\n');
        }
        return text.ToString();
    }

    private static string Line(string[] lines, int n)
    {
        var line = lines[n - 1].Trim();
        if (line.Length > MaxLineLength) line = string.Concat(line.AsSpan(0, MaxLineLength), " …");
        return n.ToString(CultureInfo.InvariantCulture) + ": " + line;
    }

    /// <summary>
    /// Ranges like <c>120-160, 300-340</c> (or a single line number) as 1-based inclusive pairs; what is not a range is left out.
    /// </summary>
    public static IReadOnlyList<(int From, int To)> Ranges(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var ranges = new List<(int, int)>();
        foreach (Match m in RangeText().Matches(text))
        {
            var start = int.Parse(m.Groups["from"].Value, CultureInfo.InvariantCulture);
            var end = m.Groups["to"].Success ? int.Parse(m.Groups["to"].Value, CultureInfo.InvariantCulture) : start;
            ranges.Add((Math.Min(start, end), Math.Max(start, end)));
        }
        return ranges;
    }

    [GeneratedRegex(@"^\s*<[a-zA-Z][^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex StartTagOf();

    [GeneratedRegex(@"\bid\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"\bclass\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ClassAttribute();

    [GeneratedRegex(@"\bon[a-z]+\s*=\s*[""'](?<v>[^""']+)[""']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HandlerAttribute();

    [GeneratedRegex(@"(?<name>[A-Za-z_$][\w$]*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex Called();

    [GeneratedRegex(@"^[A-Za-z_$][\w$-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    // A script or style tag; a function declaration; a function or arrow function assigned to a name; a listener added.
    [GeneratedRegex(@"</?(?:script|style)\b|\bfunction\s+[A-Za-z_$][\w$]*\s*\(|\b(?:const|let|var)\s+[A-Za-z_$][\w$]*\s*=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*=>|[A-Za-z_$][\w$]*\s*=>)|\.addEventListener\s*\(", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Landmark();

    [GeneratedRegex(@"(?<from>\d+)\s*(?:[-–]\s*(?<to>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex RangeText();
}
