using System.Text.Json.Serialization;
using IronHive.Agent.Loop;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// Something a proposal's model did while it worked, told as it happens: a piece of what it thinks before it
/// writes (a model that thinks), a piece of the text a tool call is writing — <see cref="Start"/> on the first
/// piece of a call — or a proposal the runtime refused, and why (the model is told and writes again).
/// </summary>
/// <remarks>Sent as one line of an answer made as it goes, so what a line does not carry is left out of it.</remarks>
internal sealed record ProposalProgress(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Thinking = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Writing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Start = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refused = null);

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
        var calls = new Dictionary<string, ToolArgumentText?>(StringComparer.Ordinal);
        var told = refused?.Count ?? 0;
        TurnRecord? record = null;
        // A turn starts from a text prompt; one that shows more starts from the whole message and continues from it.
        if (attachments is { Count: > 0 }) loop.InitializeHistory([new ChatMessage(ChatRole.User, [new TextContent(prompt), .. attachments])]);
        var stream = attachments is { Count: > 0 } ? loop.ContinueStreamingAsync(cancellationToken) : loop.RunStreamingAsync(prompt, cancellationToken);
        await foreach (var chunk in stream.ConfigureAwait(false))
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
