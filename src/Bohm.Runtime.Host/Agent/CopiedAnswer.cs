using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Agent;

/// <summary>
/// Notices an answer that is the text of a page it read rather than an answer: a model asked to summarize a long article
/// now and then writes the article back out, line for line. An answer counts as copied when most of it is lines of some
/// length found word for word in what the tools returned — a quote or two in a summary is far below that. The turn is then
/// asked once more with <see cref="Note"/>, which leaves room for a person who asked for the text itself.
/// </summary>
internal static partial class CopiedAnswer
{
    /// <summary>An answer shorter than this is not looked at — a short copied line is a quote, not the page.</summary>
    internal const int ShortestCopy = 400;

    /// <summary>A line shorter than this is not counted either way: headings and labels repeat a page's words on their own.</summary>
    internal const int ShortestLine = 30;

    /// <summary>The share of the answer's characters, in lines found in a tool result, at which it is a copy.</summary>
    internal const double CopiedShare = 0.5;

    internal const string Note =
        "Your answer above repeats the text the tools returned, word for word, instead of answering. " +
        "Unless the person asked for that text itself, answer their request now in your own words and in the language they wrote in, " +
        "without copying the page. If they did ask for the text itself, write it out again as before. " +
        "Either way, answer only the person: say nothing about this message or about answering again.";

    /// <summary>Whether <paramref name="answer"/> is mostly lines copied from the tool results in <paramref name="conversation"/>.</summary>
    public static bool Copies(string answer, IReadOnlyList<ChatMessage> conversation)
    {
        if (answer.Length < ShortestCopy) return false;
        var material = conversation.SelectMany(m => m.Contents).OfType<FunctionResultContent>()
            .Select(r => r.Result as string ?? r.Result?.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => Squeeze(t!))
            .ToList();
        if (material.Count == 0) return false;

        var lines = answer.Split('\n').Select(Squeeze).Where(l => l.Length > 0).ToList();
        var total = lines.Sum(l => l.Length);
        var copied = lines.Where(l => l.Length >= ShortestLine && material.Any(m => m.Contains(l, StringComparison.Ordinal))).Sum(l => l.Length);
        return total > 0 && copied >= total * CopiedShare;
    }

    /// <summary>Whitespace runs as one space, trimmed — a page's text and an answer break lines differently.</summary>
    private static string Squeeze(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
