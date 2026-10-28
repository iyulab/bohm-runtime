using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Whether a model takes pictures in, for what shows it one (the web agent's <c>look</c>, the look over a new application).
/// What is known stands (<see cref="ChosenEditModel.Sees"/> — the server's model list, the providers reached through their own
/// APIs, the model on this computer). A model nobody described is asked once, and only when a picture is about to go to it:
/// a small picture of one colour — a model that names the colour sees. Asking is the only way to tell a server that drops a
/// picture without a word from one that reads it; a refusal is no guide there. The answer is kept by the model's name for the
/// runtime's life. A refusal of the request (the server does not take pictures) is kept as «does not see»; a failure that
/// says nothing about pictures (unreachable, busy, the wait ran out) is not kept, and the model is asked again next time.
/// </summary>
internal sealed class ImageInput
{
    /// <summary>A 16 × 16 PNG of one red.</summary>
    internal static readonly byte[] RedSquare = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR42mO4IyJCEmIY1TCqYfhqAAACcQQQFd0BdQAAAABJRU5ErkJggg==");

    /// <summary>The question shown with <see cref="RedSquare"/>.</summary>
    internal const string Question = "What colour fills this picture? Answer with one English word.";

    /// <summary>How long the one question may take — a model that thinks first is slow on its first request.</summary>
    internal static readonly TimeSpan AskLimit = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<string, bool> _learned = new(StringComparer.Ordinal);

    /// <summary>What is known of <paramref name="model"/> without asking: as said, as learned, or <see langword="null"/>.</summary>
    public bool? Known(ChosenEditModel model) => model.Sees ?? (_learned.TryGetValue(model.Name, out var learned) ? learned : null);

    /// <summary>Whether <paramref name="model"/> takes pictures in — known, or asked now; <see langword="null"/> when asking told nothing.</summary>
    public async Task<bool?> SeesAsync(ChosenEditModel model, CancellationToken cancellationToken)
    {
        if (Known(model) is { } known) return known;
        var asked = await AskAsync(model.Client, model.Limits, cancellationToken).ConfigureAwait(false);
        if (asked is { } answer) _learned[model.Name] = answer;
        return asked;
    }

    /// <summary>
    /// Shows <paramref name="client"/> the red square and asks its colour: <see langword="true"/> when the answer says red,
    /// <see langword="false"/> when it says something else or the request is refused as one the model does not take,
    /// <see langword="null"/> when nothing was learned.
    /// </summary>
    internal static async Task<bool?> AskAsync(IChatClient client, ModelLimits limits, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(AskLimit);
        var options = new ChatOptions { MaxOutputTokens = 256 };
        if (ModelLimits.Of(client, limits).ThinksOn(onThisComputer: false)) options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        ChatMessage question = new(ChatRole.User, [new TextContent(Question), new DataContent(RedSquare, "image/png")]);
        try
        {
            var answer = await client.GetResponseAsync([question], options, wait.Token).ConfigureAwait(false);
            return answer.Text.Contains("red", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;   // the wait ran out
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A refusal of the request itself — 400 · 415 · 422 — is the server not taking pictures; anything else tells nothing.
            return ProviderRefusal.Of(e) is { Status: 400 or 415 or 422 } ? false : null;
        }
    }
}
