using System.Text.Json.Serialization;
using IronHive.Agent.Loop;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// Something a proposal's model did while it worked, told as it happens: a piece of the text a tool call is
/// writing — <see cref="Start"/> on the first piece of a call — or a proposal the runtime refused, and why
/// (the model is told and writes again).
/// </summary>
/// <remarks>Sent as one line of an answer made as it goes, so what a line does not carry is left out of it.</remarks>
internal sealed record ProposalProgress(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Writing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Start = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refused = null);

/// <summary>
/// A proposal's turn, streamed so what it writes can be shown while it is written. Only the text a tool call
/// writes into its long field is passed on (an application's HTML, a replacement's new text), as it arrives from
/// a provider that sends calls while they are written; the tools still run on the complete call.
/// </summary>
internal static class ProposalStream
{
    /// <summary>
    /// Runs <paramref name="loop"/> on <paramref name="prompt"/> and returns the turn's record — what the plain
    /// run answers. <paramref name="written"/> names, for each tool, the field whose text reaches
    /// <paramref name="onProgress"/>; <paramref name="refused"/> is read after each piece of the turn, and a
    /// reason it gained since is passed on as <see cref="ProposalProgress.Refused"/>.
    /// </summary>
    public static async Task<TurnRecord> RunAsync(AgentLoop loop, string prompt, IReadOnlyDictionary<string, string> written,
        IReadOnlyList<string>? refused, Func<ProposalProgress, CancellationToken, Task>? onProgress, CancellationToken cancellationToken)
    {
        var calls = new Dictionary<string, ToolArgumentText?>(StringComparer.Ordinal);
        var told = refused?.Count ?? 0;
        TurnRecord? record = null;
        await foreach (var chunk in loop.RunStreamingAsync(prompt, cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Turn is { } turn) record = turn;
            if (onProgress is null) continue;

            if (chunk.ToolCallDelta is { IsComplete: false } piece)
            {
                var start = !calls.TryGetValue(piece.Id, out var text);
                if (start)
                    calls[piece.Id] = text = piece.NameDelta is { } name && written.TryGetValue(name, out var field) ? new ToolArgumentText(field) : null;
                if (text is not null && text.Add(piece.ArgumentsDelta ?? "") is var added && (added.Length > 0 || start))
                    await onProgress(new ProposalProgress(added, start), cancellationToken).ConfigureAwait(false);
            }

            while (refused is not null && told < refused.Count)
                await onProgress(new ProposalProgress(Refused: refused[told++]), cancellationToken).ConfigureAwait(false);
        }

        return record ?? throw new InvalidOperationException("The model's turn ended without its record.");
    }
}
