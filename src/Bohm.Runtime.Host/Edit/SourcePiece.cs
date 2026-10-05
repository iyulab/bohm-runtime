using System.Globalization;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// Replacing one piece of an application's source with new text, as the model asked: the piece exactly as written when it
/// is there once, or else — the mistake models make most when copying a piece back — the one place whose lines differ from
/// it only in the whitespace they start with.
/// </summary>
/// <remarks>
/// Lines are compared without their leading whitespace, so a piece copied with an indent of 15 spaces where the source has
/// 14 is still found, but only when exactly one place matches. A first line may begin, and a last line end, inside a line
/// of the source, as an exact piece may. When every line was off by the same indent, the new text is shifted by it too, so
/// it lands at the source's indentation; otherwise it is put in as written.
/// </remarks>
internal static class SourcePiece
{
    /// <summary>What replacing did: the new source, the piece of source it replaced and what replaced it; or why not.</summary>
    /// <param name="Source">The source with the piece replaced, or <see langword="null"/> when nothing was.</param>
    /// <param name="Message">What to tell the model.</param>
    internal sealed record Result(string? Source, string? Old, string? New, string Message);

    public static Result Replace(string source, string oldText, string newText)
    {
        if (string.IsNullOrEmpty(oldText)) return new(null, null, null, "old_text is empty; copy an exact piece of the source.");
        var at = source.IndexOf(oldText, StringComparison.Ordinal);
        if (at >= 0)
        {
            if (source.IndexOf(oldText, at + 1, StringComparison.Ordinal) >= 0)
                return new(null, null, null, "old_text appears more than once; include more of the text around it.");
            return new(string.Concat(source.AsSpan(0, at), newText, source.AsSpan(at + oldText.Length)), oldText, newText, "replaced");
        }

        var found = Loosely(source, oldText);
        if (found.Count > 1) return new(null, null, null, "old_text appears more than once; include more of the text around it.");
        if (found.Count == 0) return new(null, null, null, NotFound(source, oldText));

        var (start, end, shift, atLineStart) = found[0];
        var old = source[start..end];
        var replacement = Shifted(newText, shift, atLineStart);
        return new(string.Concat(source.AsSpan(0, start), replacement, source.AsSpan(end)), old, replacement,
            "replaced (the source's indentation differed from old_text's; matched ignoring the whitespace lines start with)");
    }

    // Each place whose lines equal the piece's once leading whitespace is set aside: its start and end in the source, and
    // the indent every line was off by (source minus piece) when it was the same for all of them.
    private static List<(int Start, int End, int? Shift, bool FirstAtLineStart)> Loosely(string source, string piece)
    {
        var wanted = piece.Split('\n');
        if (wanted.Length < 2 && wanted[0].TrimStart().Length == wanted[0].Length) return []; // one line, no indent: exact or nothing
        var lines = source.Split('\n');
        var starts = new int[lines.Length];
        for (int i = 1; i < lines.Length; i++) starts[i] = starts[i - 1] + lines[i - 1].Length + 1;

        var found = new List<(int, int, int?, bool)>();
        for (var j = 0; j + wanted.Length <= lines.Length; j++)
        {
            int? shift = null;
            var even = true;
            var atLineStart = false;
            var start = -1;
            var end = -1;
            for (var k = 0; k < wanted.Length; k++)
            {
                var have = lines[j + k];
                var want = wanted[k].TrimStart();
                var body = have.TrimStart();
                var indent = have.Length - body.Length;
                bool first = k == 0, last = k == wanted.Length - 1;
                int from, to;
                if (first && last)
                {
                    var inLine = body.IndexOf(want, StringComparison.Ordinal);
                    if (want.Length == 0 || inLine < 0) goto next;
                    (from, to) = (indent + inLine, indent + inLine + want.Length);
                }
                else if (first)
                {
                    // The piece may begin inside the line: the line ends with it.
                    if (!body.EndsWith(want, StringComparison.Ordinal)) goto next;
                    (from, to) = (have.Length - want.Length, have.Length);
                }
                else if (last)
                {
                    // The piece may end inside the line: the line begins with it.
                    if (!body.StartsWith(want, StringComparison.Ordinal)) goto next;
                    // An empty last line is the piece ending with its line break: it ends where this line starts.
                    (from, to) = want.Length == 0 ? (0, 0) : (indent, indent + want.Length);
                }
                else
                {
                    if (body != want) goto next;
                    (from, to) = (indent, have.Length);
                }

                // A first line that is the whole line: from where the line starts when the piece brought its own indent
                // (its new text brings one too), else from where the source's indent ends.
                if (first)
                {
                    atLineStart = body == want && wanted[k].Length > want.Length;
                    start = starts[j] + (atLineStart ? 0 : from);
                }
                if (last) end = starts[j + k] + to;
                // The indent this line was off by — counted only where both the piece and the source give the whole line.
                if (want.Length > 0 && (k > 0 || body == want))
                {
                    var off = indent - (wanted[k].Length - want.Length);
                    if (shift is null) shift = off;
                    else if (shift != off) even = false;
                }
            }

            found.Add((start, end, even ? shift : null, atLineStart));
            next:;
        }

        return found;
    }

    // The new text moved by the indent the piece was off by, line by line; as written when that was not one indent. Its
    // first line moves only when the replacement starts where the line does — otherwise the source's indent stays before it.
    private static string Shifted(string text, int? shift, bool firstAtLineStart)
    {
        if (shift is not { } by || by == 0) return text;
        var lines = text.Split('\n');
        for (var i = firstAtLineStart ? 0 : 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            if (by > 0) lines[i] = new string(' ', by) + lines[i];
            else
            {
                var lead = lines[i].Length - lines[i].TrimStart().Length;
                lines[i] = lines[i][Math.Min(lead, -by)..];
            }
        }

        return string.Join('\n', lines);
    }

    // Not there even loosely: say what usually went wrong, and where the piece's first line is when that line is in the source.
    private static string NotFound(string source, string piece)
    {
        var message = "old_text was not found; copy it exactly from the source, without the line numbers read_source puts before each line.";
        var first = piece.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first is null) return message;
        var lines = source.Split('\n');
        var at = Array.FindIndex(lines, l => l.Trim() == first);
        if (at >= 0) return message + $" Its first line is line {at + 1} of the source; read the lines from there and copy them as they are.";
        // A model copying a line back often writes what it expects rather than what is there — a bracket made to pair, a
        // name spelled right. Read again, it copies the same: so the one line that is nearly its first, and where they part.
        return Closest(lines, first) is { } near
            ? message + string.Create(CultureInfo.InvariantCulture,
                $" The nearest line is line {near.Line}, which differs from old_text where the source has «{near.Source}» and old_text has «{near.Piece}». That line as it is:\n{near.Line}: {lines[near.Line - 1].TrimEnd('\r')}")
            : message;
    }

    /// <summary>Characters shown on each side of where a line and the piece part.</summary>
    private const int DifferenceContext = 8;

    /// <summary>The shortest first line looked for nearly — a shorter one is near too much of any source.</summary>
    private const int MinNearLength = 12;

    // The one place in the source — a whole line or a stretch of one — within a few characters of the piece's first line
    // (by edit distance: a twentieth of its length, and at least two), and the stretch where the two differ, with a little
    // of what they share around it. Null when no place is that near, or when two lines are equally near.
    private static (int Line, string Source, string Piece)? Closest(string[] lines, string first)
    {
        if (first.Length < MinNearLength) return null;
        var allowed = Math.Max(2, first.Length / 20);
        (int Line, int Distance, int End)? best = null;
        var tied = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length < first.Length - allowed) continue;
            var (distance, end) = NearestIn(lines[i], first, allowed);
            if (distance > allowed) continue;
            if (best is null || distance < best.Value.Distance) (best, tied) = ((i + 1, distance, end), false);
            else if (distance == best.Value.Distance) tied = true;
        }

        if (best is not { } found || tied) return null;
        var line = lines[found.Line - 1];
        var stretch = line[Math.Max(0, found.End - first.Length)..found.End];
        var prefix = 0;
        while (prefix < stretch.Length && prefix < first.Length && stretch[prefix] == first[prefix]) prefix++;
        var suffix = 0;
        while (suffix < stretch.Length - prefix && suffix < first.Length - prefix && stretch[^(suffix + 1)] == first[^(suffix + 1)]) suffix++;
        string Around(string text) => text[Math.Max(0, prefix - DifferenceContext)..Math.Min(text.Length, text.Length - suffix + DifferenceContext)];
        return (found.Line, Around(stretch), Around(first));
    }

    // The fewest edits that turn `piece` into some stretch of `text`, and where that stretch ends (approximate matching —
    // Sellers: the stretch may start anywhere). Given up (over `limit`) once every way through has gone past it.
    private static (int Distance, int End) NearestIn(string text, string piece, int limit)
    {
        var previous = new int[text.Length + 1];   // a row per character of the piece; a column per place in the text
        var current = new int[text.Length + 1];
        for (var i = 1; i <= piece.Length; i++)
        {
            current[0] = i;
            var lowest = i;
            for (var j = 1; j <= text.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (piece[i - 1] == text[j - 1] ? 0 : 1));
                lowest = Math.Min(lowest, current[j]);
            }

            if (lowest > limit) return (limit + 1, 0);
            (previous, current) = (current, previous);
        }

        var end = 0;
        for (var j = 1; j <= text.Length; j++) if (previous[j] < previous[end]) end = j;
        return (previous[end], end);
    }
}
