using Bohm.Runtime.Host.Edit;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>Where a proposal's rounds went is counted from the messages the turn added — what a stop at the round limit is read against.</summary>
public sealed class ProposalRoundsTests
{
    private static ChatMessage Calls(params string[] names) =>
        new(ChatRole.Assistant, [.. names.Select((name, i) => (AIContent)new FunctionCallContent($"c{i}", name))]);

    [Fact]
    public void Rounds_calls_by_tool_and_missed_replacements_are_counted()
    {
        ChatMessage[] messages =
        [
            new(ChatRole.User, "make the button blue"),
            Calls("read_source"),
            new(ChatRole.Tool, [new FunctionResultContent("c0", "…")]),
            Calls("read_source", "replace"),
            new(ChatRole.Tool, [new FunctionResultContent("c0", "…"), new FunctionResultContent("c1", "old_text was not found")]),
            Calls("replace"),
            new(ChatRole.Assistant, "Made the button blue."),
        ];

        var rounds = ProposalRounds.Of(messages, kept: 1);

        Assert.Equal(3, rounds.Rounds);
        Assert.Equal(2, rounds.Calls["read_source"]);
        Assert.Equal(2, rounds.Calls["replace"]);
        Assert.Equal(1, rounds.Missed);
    }
}
