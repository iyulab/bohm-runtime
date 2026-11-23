using System.Runtime.CompilerServices;
using IronHive.Abstractions.Exceptions;
using LMSupply.Exceptions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The model on this computer's refusal of a request too long for its context, in the shape every other model's refusal
/// takes: <see cref="ContextOverflowException"/>, with the window and the length asked for.
/// </summary>
/// <remarks>
/// The model library raises its own <see cref="ContextLengthExceededException"/> — both for a request the server turns
/// away before answering and for one that fails inside the stream. What recovers from a request that is too long (a
/// smaller answer, the pages read cut down) waits for <see cref="ContextOverflowException"/>; left in the library's
/// type, the refusal skipped every recovery and was reported as a model that gave no answer.
/// </remarks>
internal sealed class LocalOverflowChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ContextLengthExceededException e)
        {
            throw Overflow(e);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ChatResponseUpdate> updates;
        try
        {
            updates = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (ContextLengthExceededException e)
        {
            throw Overflow(e);
        }

        await using (updates.ConfigureAwait(false))
        {
            while (true)
            {
                try
                {
                    if (!await updates.MoveNextAsync().ConfigureAwait(false)) yield break;
                }
                catch (ContextLengthExceededException e)
                {
                    throw Overflow(e);
                }

                yield return updates.Current;
            }
        }
    }

    internal static ContextOverflowException Overflow(ContextLengthExceededException e) =>
        new(e.Message, e) { ContextWindow = e.MaxContextLength > 0 ? e.MaxContextLength : null, RequestTokens = e.TokenCount };
}
