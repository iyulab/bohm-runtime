using System.Text.RegularExpressions;

namespace Bohm.Runtime.Host;

/// <summary>
/// What an application's source asks to load from another computer — code (a script or style sheet, an
/// <c>@import</c>, a JavaScript <c>import</c>) or a file (an image, a font, a frame). An application runs on
/// this computer and reaches no other: a version that needs one opens broken wherever there is no network,
/// and the code it would load was never read here. Making and changing an application refuse the same things.
/// </summary>
internal static partial class OutsideReferences
{
    /// <summary>One line per other computer the source loads from, saying what to do instead.</summary>
    internal static List<string> Problems(string html) => [.. Found(html).Select(Message)];

    /// <summary>The <see cref="Problems"/> of <paramref name="after"/> that <paramref name="before"/> did not have — what a change brings in.</summary>
    internal static List<string> Added(string before, string after)
    {
        var had = Found(before).ToHashSet();
        return [.. Found(after).Where(r => !had.Contains(r)).Select(Message)];
    }

    private static string Message((string Host, bool Code) found) => found.Code
        ? $"The application loads code from {found.Host}, which cannot be reached; write it in the file."
        : $"The application loads a file (an image, a font or a frame) from {found.Host}, which cannot be reached; put it in the file or leave it out.";

    // One entry per host — code when anything from it is code (an @import url(…) is both patterns).
    private static IEnumerable<(string Host, bool Code)> Found(string html) =>
        Code().Matches(html).Select(m => (Host: m.Groups["host"].Value.ToLowerInvariant(), Code: true))
            .Concat(Files().Matches(html).Select(m => (Host: m.Groups["host"].Value.ToLowerInvariant(), Code: false)))
            .GroupBy(r => r.Host, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Any(r => r.Code)));

    // <script src>, <link href>, CSS @import (with or without url()), JavaScript import … from / import(…) / import "…".
    [GeneratedRegex(
        """<(?:script|link)\b[^>]*\b(?:src|href)\s*=\s*["']?(?:https?:)?//(?<host>[^/"'\s>]+)"""
        + """|@import\s+(?:url\(\s*)?["']?(?:https?:)?//(?<host>[^/"'\s)]+)"""
        + """|\bimport\b(?:\s*\(\s*|[^;"'()]*?\bfrom\s*|\s*)["'](?:https?:)?//(?<host>[^/"'\s]+)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Code();

    // <img|iframe|video|audio|source|embed|object|track src|data|poster>, and url(…) in CSS (fonts, backgrounds).
    [GeneratedRegex(
        """<(?:img|iframe|video|audio|source|embed|object|track)\b[^>]*\b(?:src|data|poster)\s*=\s*["']?(?:https?:)?//(?<host>[^/"'\s>]+)"""
        + """|\burl\(\s*["']?(?:https?:)?//(?<host>[^/"'\s)]+)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Files();
}
