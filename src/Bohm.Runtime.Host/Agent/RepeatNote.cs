using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Agent;

/// <summary>
/// Notices a turn that has gone round the same few calls again and again. A page that undoes what was just done (a list that
/// drops its choice, a form that clears itself) gives the model the same answer to the same two steps, and a model that
/// repeats a step that changed nothing twice is told not to — but not that a pair of steps does the same. The whole
/// conversation comes back each turn, so the calls are read from it; the note is for the latest result only and is not
/// kept in the conversation. Only a run of calls that puts text in a field is noticed: going round scroll-and-read, or
/// clicking "next" and reading, is how a long page or a list is read, and the note would be wrong there.
/// </summary>
internal static class RepeatNote
{
    /// <summary>How many times in a row a run of two or three calls must come round before it is said (a call alone never is).</summary>
    private static readonly int[] RoundsFor = [0, 0, 3, 3];

    private const string TypeCall = "type(";

    /// <summary>What a call is, for telling two of them apart: the tool and what it was given.</summary>
    public static string Signature(FunctionCallContent call) =>
        $"{call.Name}({string.Join(",", (call.Arguments ?? new Dictionary<string, object?>()).OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={a.Value}"))})";

    /// <summary>The note for the calls made so far, oldest first — or <c>null</c> when the latest ones are not going round.</summary>
    public static string? Of(IReadOnlyList<string> calls)
    {
        for (var length = 2; length < RoundsFor.Length; length++)
        {
            var needed = length * RoundsFor[length];
            if (calls.Count < needed) continue;
            var going = true;
            for (var i = calls.Count - needed + length; i < calls.Count && going; i++) going = calls[i] == calls[i - length];
            if (going && calls.Skip(calls.Count - length).Any(c => c.StartsWith(TypeCall, StringComparison.Ordinal))) return Note(length, RoundsFor[length]);
        }

        return null;
    }

    private static string Note(int length, int rounds) =>
        $"You have gone through the same {length} calls {rounds} times in a row"
        + " and the page has not kept the result. Do not run them again: take a new snapshot and read what the page shows now,"
        + " try another control, or stop and tell the person what is in the way.";
}
