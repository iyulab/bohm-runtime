using System.Text.Json.Serialization;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// Something a proposal's model did while it worked, told as it happens: a piece of what it thinks before it
/// writes (a model that thinks), a piece of the text a tool call is writing — <see cref="Start"/> on the first
/// piece of a call — a proposal the runtime refused, and why (the model is told and writes again) — or a question for the
/// person: may the AI use an MCP server's tool (<see cref="Approval"/> — answered through <c>POST /__control/approvals/{id}</c>) — or a
/// tool of a server the person allowed for this request, used without asking (<see cref="Used"/>) — or a skill the model read
/// (<see cref="Skill"/>, its name).
/// </summary>
/// <remarks>Sent as one line of an answer made as it goes, so what a line does not carry is left out of it.</remarks>
internal sealed record ProposalProgress(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Thinking = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Writing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Start = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refused = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Promotion.ApprovalAsk? Approval = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Promotion.McpUsed? Used = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Skill = null);

/// <summary>
/// A proposal's turn, streamed so what it writes can be shown while it is written. Passed on: the model's
/// thinking as it thinks (so a long wait before the writing reads as thinking, not as a stalled server), and the
/// text a tool call writes into its long field (an application's HTML, a replacement's new text), as it arrives
/// from a provider that sends calls while they are written; the tools still run on the complete call.
/// </summary>
internal static class ProposalStream
{
    /// <summary>
    /// Runs <paramref name="loop"/> on <paramref name="prompt"/> and returns the turn's record — what the plain
    /// run answers. <paramref name="written"/> names, for each tool, the field whose text reaches
    /// <paramref name="onProgress"/>; <paramref name="refused"/> is read after each piece of the turn, and a
    /// reason it gained since is passed on as <see cref="ProposalProgress.Refused"/>.
    /// </summary>
    /// <param name="attachments">What the prompt shows besides its text — a picture of the application, say. Sent with the prompt as one message.</param>
    public static async Task<TurnRecord> RunAsync(AgentLoop loop, string prompt, IReadOnlyDictionary<string, string> written,
        IReadOnlyList<string>? refused, Func<ProposalProgress, CancellationToken, Task>? onProgress, CancellationToken cancellationToken,
        IReadOnlyList<AIContent>? attachments = null)
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent(prompt), .. attachments ?? []]);
        return await FollowAsync(loop.RunStreamingAsync(message, cancellationToken), written, refused, onProgress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Continues <paramref name="loop"/> from its history — which must end with an answer to every call of its last
    /// round — with <paramref name="options"/> for this turn, passed on as <see cref="RunAsync"/> passes a turn on.
    /// </summary>
    public static Task<TurnRecord> ContinueAsync(AgentLoop loop, ChatOptions options, IReadOnlyDictionary<string, string> written,
        IReadOnlyList<string>? refused, Func<ProposalProgress, CancellationToken, Task>? onProgress, CancellationToken cancellationToken) =>
        FollowAsync(loop.ContinueStreamingAsync(options, cancellationToken), written, refused, onProgress, cancellationToken);

    /// <summary>
    /// When <paramref name="turn"/> ended at the round limit: answers the calls the limit left unrun — running each call of
    /// <paramref name="write"/> (often the work itself, written in the last round), telling the rest <paramref name="notice"/> —
    /// and continues the same conversation for one more turn offered only <paramref name="write"/>. What the model read stays
    /// in the conversation instead of being thrown away with a failure. <see langword="null"/> when the turn did not end there.
    /// </summary>
    // TODO(upstream: IronHive.Agent — a closing round at the iteration cap that the loop serves): move onto it and drop this.
    public static async Task<TurnRecord?> CloseAtRoundLimitAsync(AgentLoop loop, TurnRecord turn, AIFunction write, string notice,
        IReadOnlyDictionary<string, string> written, IReadOnlyList<string>? refused, Func<ProposalProgress, CancellationToken, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        if (turn.StopReason != TurnStopReason.StepLimit) return null;
        var history = loop.History;
        if (history.Count == 0 || history[^1].Role != ChatRole.Assistant) return null;
        var unrun = history[^1].Contents.OfType<FunctionCallContent>().ToList();
        if (unrun.Count == 0) return null;
        var answers = new List<AIContent>();
        foreach (var call in unrun)
        {
            var result = call.Name == write.Name
                ? await write.InvokeAsync(new AIFunctionArguments(call.Arguments), cancellationToken).ConfigureAwait(false)
                : notice;
            answers.Add(new FunctionResultContent(call.CallId, result));
        }
        loop.InitializeHistory([.. history, new ChatMessage(ChatRole.Tool, answers)]);
        return await ContinueAsync(loop, new ChatOptions { Tools = [write] }, written, refused, onProgress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TurnRecord> FollowAsync(IAsyncEnumerable<AgentResponseChunk> turnChunks, IReadOnlyDictionary<string, string> written,
        IReadOnlyList<string>? refused, Func<ProposalProgress, CancellationToken, Task>? onProgress, CancellationToken cancellationToken)
    {
        var calls = new Dictionary<string, ToolArgumentText?>(StringComparer.Ordinal);
        var told = refused?.Count ?? 0;
        TurnRecord? record = null;
        await foreach (var chunk in turnChunks.ConfigureAwait(false))
        {
            if (chunk.Turn is { } turn) record = turn;
            if (onProgress is null) continue;

            if (chunk.ThinkingDelta is { Length: > 0 } thought)
                await onProgress(new ProposalProgress(Thinking: thought), cancellationToken).ConfigureAwait(false);

            if (chunk.ToolCallDelta is { IsComplete: false } piece)
            {
                var start = !calls.TryGetValue(piece.Id, out var text);
                if (start)
                    calls[piece.Id] = text = piece.NameDelta is { } name && written.TryGetValue(name, out var field) ? new ToolArgumentText(field) : null;
                if (text is not null && text.Add(piece.ArgumentsDelta ?? "") is var added && (added.Length > 0 || start))
                    await onProgress(new ProposalProgress(Writing: added, Start: start), cancellationToken).ConfigureAwait(false);
            }

            while (refused is not null && told < refused.Count)
                await onProgress(new ProposalProgress(Refused: refused[told++]), cancellationToken).ConfigureAwait(false);
        }

        return record ?? throw new InvalidOperationException("The model's turn ended without its record.");
    }
}
