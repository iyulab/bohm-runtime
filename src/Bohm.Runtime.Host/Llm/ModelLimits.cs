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

    /// <summary>
    /// What is known of <paramref name="model"/> now: what its client has learned since
    /// <paramref name="given"/> was read — thinking seen in an answer, a window from a refusal — or
    /// <paramref name="given"/> when its client learns nothing. A task of several rounds reads it for
    /// each request, so a model that thought in the first round is asked not to in the next.
    /// </summary>
    public static ModelLimits Of(IChatClient model, ModelLimits given) => model.GetService<ModelFitChatClient>()?.Limits ?? given;
}

/// <summary>
/// Keeps each request to a model inside what the model takes: an answer bound above the model's own
/// is lowered to it, no thinking setting goes to a model that has none, and a request the model
/// refuses as too long for its context is tried once more with a smaller answer when that is what
/// did not fit. The window learned from a refusal is kept for later requests; a window the server
/// reported (<see cref="Reported"/>) stands until a refusal says otherwise. Thinking in an answer marks
/// a model nobody described as one that thinks, and <paramref name="thinkingLearned"/> is told so once —
/// for the owner to remember it past this client's life.
/// </summary>
/// <remarks>
/// The refusal arrives as <see cref="ContextOverflowException"/> — the provider turns each server's
/// own wording into it — so nothing here reads error text.
/// </remarks>
internal sealed class ModelFitChatClient(IChatClient inner, ModelLimits limits, Action? thinkingLearned = null) : DelegatingChatClient(inner)
{
    /// <summary>An answer smaller than this is not worth a second request.</summary>
    public const int SmallestUsefulAnswer = 256;

    private int? _learnedWindow;
    private int? _reportedWindow;
    private volatile bool _seenThinking;

    /// <summary>
    /// The limits as known now: as given, and when no context window was given, the one learned from a
    /// refusal, or else the one the server reported; when nobody said whether the model thinks, it does
    /// once an answer of its carried thinking.
    /// </summary>
    public ModelLimits Limits
    {
        get
        {
            var known = limits;
            if (known.ContextWindow is null && (_learnedWindow ?? _reportedWindow) is { } window) known = known with { ContextWindow = window };
            if (known.Reasoning is null && _seenThinking) known = known with { Reasoning = true };
            return known;
        }
    }

    /// <summary>The context window the server reported, or <see langword="null"/> when it reported none.</summary>
    public int? ReportedWindow => _reportedWindow;

    /// <summary>Keeps the context window the server reported for the model; one that is not above zero is not kept.</summary>
    public void Reported(int contextWindow)
    {
        if (contextWindow > 0) _reportedWindow = contextWindow;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? [.. messages];
        var fitted = Fit(options);
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(list, fitted, cancellationToken).ConfigureAwait(false);
        }
        catch (ContextOverflowException refused) when (Smaller(refused, fitted) is { } smaller)
        {
            response = await base.GetResponseAsync(list, smaller, cancellationToken).ConfigureAwait(false);
        }

        foreach (var message in response.Messages) Saw(message.Contents);
        return response;
    }

    /// <summary>
    /// Notes thinking in what the model sent. A model that thinks without being asked spends its answer's
    /// room on it — a long task then ends before its result — so features that know it ask it to think
    /// briefly (<see cref="ModelLimits.ThinksOn"/>). Kept for this client's life, like the learned window.
    /// </summary>
    private void Saw(IList<AIContent> contents)
    {
        if (_seenThinking || !contents.Any(c => c is TextReasoningContent { Text.Length: > 0 })) return;
        _seenThinking = true;
        if (limits.Reasoning is null) thinkingLearned?.Invoke();
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
                do
                {
                    Saw(updates.Current.Contents);
                    yield return updates.Current;
                }
                while (await updates.MoveNextAsync().ConfigureAwait(false));
                yield break;
            }
        }

        await foreach (var update in base.GetStreamingResponseAsync(list, retry, cancellationToken).ConfigureAwait(false))
        {
            Saw(update.Contents);
            yield return update;
        }
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
