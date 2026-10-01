using System.Runtime.CompilerServices;
using IronHive.Abstractions.Exceptions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// What is known about one model's limits — each <see langword="null"/> when nobody said. Whoever set
/// the model knows them (an administrator, the person, or the server itself); the runtime never guesses
/// them from the model's name.
/// </summary>
/// <param name="ContextWindow">Tokens the model takes in at once, the answer included.</param>
/// <param name="MaxOutputTokens">The most tokens one answer may have.</param>
/// <param name="Reasoning">Whether the model thinks before it answers — <see langword="false"/> means it has no such step to turn off.</param>
public sealed record ModelLimits(int? ContextWindow = null, int? MaxOutputTokens = null, bool? Reasoning = null)
{
    /// <summary>Nothing known.</summary>
    public static readonly ModelLimits Unknown = new();

    /// <summary>
    /// Whether the values make sense together: counts above zero, and an answer no larger than the
    /// context window it has to fit in.
    /// </summary>
    public static bool TryCreate(int? contextWindow, int? maxOutputTokens, bool? reasoning, out ModelLimits? limits)
    {
        limits = null;
        if (contextWindow is <= 0 || maxOutputTokens is <= 0) return false;
        if (contextWindow is { } window && maxOutputTokens > window) return false;
        limits = new ModelLimits(contextWindow, maxOutputTokens, reasoning);
        return true;
    }

    /// <summary>
    /// Whether a feature should say how much the model thinks: on a model known to think, and on a
    /// model on this computer unless it is known not to (those are small, and an unbounded thinking
    /// step makes them too slow to use).
    /// </summary>
    public bool ThinksOn(bool onThisComputer) => onThisComputer ? Reasoning != false : Reasoning == true;
}

/// <summary>
/// Keeps each request to a model inside what the model takes: an answer bound above the model's own
/// is lowered to it, no thinking setting goes to a model that has none, and a request the model
/// refuses as too long for its context is tried once more with a smaller answer when that is what
/// did not fit. The window learned from a refusal is kept for later requests.
/// </summary>
/// <remarks>
/// The refusal arrives as <see cref="ContextOverflowException"/> — the provider turns each server's
/// own wording into it — so nothing here reads error text.
/// </remarks>
internal sealed class ModelFitChatClient(IChatClient inner, ModelLimits limits) : DelegatingChatClient(inner)
{
    /// <summary>An answer smaller than this is not worth a second request.</summary>
    public const int SmallestUsefulAnswer = 256;

    private int? _learnedWindow;

    /// <summary>The limits as known now: as given, with a context window learned from a refusal when none was given.</summary>
    public ModelLimits Limits => limits.ContextWindow is null && _learnedWindow is { } learned ? limits with { ContextWindow = learned } : limits;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var fitted = Fit(options);
        try
        {
            return await base.GetResponseAsync(list, fitted, cancellationToken).ConfigureAwait(false);
        }
        catch (ContextOverflowException refused) when (Smaller(refused, fitted) is { } smaller)
        {
            return await base.GetResponseAsync(list, smaller, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var fitted = Fit(options);
        ChatOptions? retry = null;
        await using (var updates = base.GetStreamingResponseAsync(list, fitted, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            // A refusal comes before the first update; once one has arrived, the answer is the model's.
            bool any;
            try
            {
                any = await updates.MoveNextAsync().ConfigureAwait(false);
            }
            catch (ContextOverflowException refused) when (Smaller(refused, fitted) is { } smaller)
            {
                retry = smaller;
                any = false;
            }

            if (retry is null)
            {
                if (!any) yield break;
                do yield return updates.Current;
                while (await updates.MoveNextAsync().ConfigureAwait(false));
                yield break;
            }
        }

        await foreach (var update in base.GetStreamingResponseAsync(list, retry, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    /// <summary>The request's options with the model's own bounds applied; the caller's are not changed.</summary>
    private ChatOptions? Fit(ChatOptions? options)
    {
        var bound = limits.MaxOutputTokens;
        var lower = bound is { } most && (options?.MaxOutputTokens is null || options.MaxOutputTokens > most);
        var noThinking = limits.Reasoning == false && options?.Reasoning is not null;
        if (!lower && !noThinking) return options;

        var fitted = options?.Clone() ?? new ChatOptions();
        if (lower) fitted.MaxOutputTokens = bound;
        if (noThinking) fitted.Reasoning = null;
        return fitted;
    }

    /// <summary>
    /// The options for a second request when the refusal says the answer's room is what did not fit —
    /// the request without its answer fits the window, and what is left of the window is a useful
    /// answer; otherwise <see langword="null"/>, and the refusal stands.
    /// </summary>
    private ChatOptions? Smaller(ContextOverflowException refused, ChatOptions? fitted)
    {
        if (refused.ContextWindow is not { } window) return null;
        _learnedWindow = window;
        if (refused.RequestTokens is not { } requested || fitted?.MaxOutputTokens is not { } answer) return null;

        var input = requested - answer;
        var room = window - input;
        if (input <= 0 || room < SmallestUsefulAnswer || room >= answer) return null;

        var smaller = fitted.Clone();
        smaller.MaxOutputTokens = room;
        return smaller;
    }
}
