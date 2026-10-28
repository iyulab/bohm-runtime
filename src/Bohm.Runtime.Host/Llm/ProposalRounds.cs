namespace Bohm.Runtime.Host.Llm;

/// <summary>Where a proposal's rounds of tool calls went.</summary>
/// <param name="Rounds">Rounds in which the model called tools.</param>
/// <param name="Calls">Calls by tool name (<c>read_source</c>, <c>replace</c>, a skill or MCP tool's name).</param>
/// <param name="Missed">Replacements that changed nothing — the piece was not found, or found more than once.</param>
internal sealed record ProposalRounds(int Rounds, IReadOnlyDictionary<string, int> Calls, int Missed)
{
    /// <summary>Counted from the messages a turn added; <paramref name="kept"/> is how many replacements the source took.</summary>
    public static ProposalRounds Of(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, int kept)
    {
        var calls = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var rounds = 0;
        foreach (var message in messages)
        {
            var any = false;
            foreach (var call in message.Contents.OfType<Microsoft.Extensions.AI.FunctionCallContent>())
            {
                calls[call.Name] = calls.GetValueOrDefault(call.Name) + 1;
                any = true;
            }
            if (any) rounds++;
        }
        return new ProposalRounds(rounds, calls, Math.Max(0, calls.GetValueOrDefault("replace") - kept));
    }
}
