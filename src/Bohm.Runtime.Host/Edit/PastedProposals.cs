using System.Text.RegularExpressions;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// Turns a new source a person brought back from elsewhere — an AI service they asked in their own
/// browser, given the application's code — into a proposal of the same shape a model's proposal has.
/// Nothing is asked of a model here: the source is compared with the one in use, and the person sees
/// the same changes, opens the same preview and applies or discards it the same way.
/// </summary>
/// <remarks>
/// What comes back is an answer, not a file: it may wrap the page in a fenced block and words around
/// it. Only a whole document is taken — a fragment cannot be applied as a revision, and guessing where
/// it belongs is what a model is for.
/// </remarks>
internal static partial class PastedProposals
{
    /// <summary>What a proposal names as its model when no model on this computer or behind a key made it.</summary>
    public const string Model = "pasted";

    /// <summary>
    /// The largest number of changed lines compared line by line on each side. Past it, the changed
    /// middle is shown as one change — the comparison is quadratic, and so many changes are not read one by one.
    /// </summary>
    public const int MaxComparedLines = 2000;

    /// <summary>
    /// The proposal for replacing <paramref name="source"/> with the document in <paramref name="pasted"/>,
    /// or <see langword="null"/> when the answer holds no whole HTML document.
    /// </summary>
    public static EditProposal? From(string source, string pasted)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (Extract(pasted) is not { } document) return null;
        // An answer copied from a chat comes with its own line ends; the application keeps the ones it has.
        var html = source.Contains("\r\n", StringComparison.Ordinal) ? document.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) : document.Replace("\r\n", "\n", StringComparison.Ordinal);
        return new EditProposal(html, "", Diff(source, html));
    }

    /// <summary>
    /// The whole HTML document in an answer: from the first <c>&lt;!doctype html</c> or <c>&lt;html</c> to the last
    /// <c>&lt;/html&gt;</c> — or, without one, to the code fence that closes the block it is in, or to the end.
    /// <see langword="null"/> when the answer holds no document.
    /// </summary>
    public static string? Extract(string pasted)
    {
        ArgumentNullException.ThrowIfNull(pasted);
        var start = DocumentStart().Match(pasted);
        if (!start.Success) return null;
        var end = pasted.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
        string document;
        if (end > start.Index) document = pasted[start.Index..(end + "</html>".Length)];
        else
        {
            var fence = pasted.IndexOf("\n```", start.Index, StringComparison.Ordinal);
            document = (fence >= 0 ? pasted[start.Index..fence] : pasted[start.Index..]).TrimEnd();
        }

        return document + "\n";
    }

    /// <summary>
    /// The changes from <paramref name="before"/> to <paramref name="after"/>, line by line: each run of lines
    /// that differs becomes one exact piece of <paramref name="before"/> and what replaces it. A run that only
    /// adds lines carries the line before it, so the piece still says where the new lines go.
    /// </summary>
    public static IReadOnlyList<SourceEdit> Diff(string before, string after)
    {
        var a = Lines(before);
        var b = Lines(after);
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;
        var edits = new List<SourceEdit>();
        if (prefix == a.Length && prefix == b.Length) return edits;

        var oldMiddle = a[prefix..(a.Length - suffix)];
        var newMiddle = b[prefix..(b.Length - suffix)];
        if (oldMiddle.Length > MaxComparedLines || newMiddle.Length > MaxComparedLines)
        {
            edits.Add(Edit(a, prefix, oldMiddle, newMiddle));
            return edits;
        }

        // Longest common subsequence of lines over the changed middle; each gap between matched lines is a change.
        var n = oldMiddle.Length;
        var m = newMiddle.Length;
        var common = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                common[i, j] = oldMiddle[i] == newMiddle[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);

        int x = 0, y = 0, fromX = 0, fromY = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && oldMiddle[x] == newMiddle[y])
            {
                if (fromX < x || fromY < y) edits.Add(Edit(a, prefix + fromX, oldMiddle[fromX..x], newMiddle[fromY..y]));
                x++;
                y++;
                fromX = x;
                fromY = y;
            }
            else if (y < m && (x == n || common[x, y + 1] >= common[x + 1, y])) y++;
            else x++;
        }

        if (fromX < n || fromY < m) edits.Add(Edit(a, prefix + fromX, oldMiddle[fromX..], newMiddle[fromY..]));
        return edits;
    }

    private static SourceEdit Edit(string[] source, int at, string[] removed, string[] added)
    {
        if (removed.Length > 0) return new SourceEdit(string.Concat(removed), string.Concat(added));
        // Only added: anchor on the line before (or, at the very top, the line after) so the piece is a real place.
        return at > 0
            ? new SourceEdit(source[at - 1], source[at - 1] + string.Concat(added))
            : at < source.Length ? new SourceEdit(source[at], string.Concat(added) + source[at]) : new SourceEdit("", string.Concat(added));
    }

    /// <summary>The lines of <paramref name="text"/>, each with its own line end, so joined they are the text again.</summary>
    private static string[] Lines(string text)
    {
        var lines = new List<string>();
        var from = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[from..(i + 1)]);
            from = i + 1;
        }

        if (from < text.Length) lines.Add(text[from..]);
        return [.. lines];
    }

    [GeneratedRegex(@"```[ \t]*(?:html|htm)?[ \t]*\r?\n(?<body>.*?)\r?\n[ \t]*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Fence();

    [GeneratedRegex(@"<!doctype\s+html|<html[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentStart();
}
