using IronHive.Abstractions.Models;
using IronHive.Providers.OpenAI.Compatible;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The context window an OpenAI-compatible server reports for a model: asked once, nobody's text goes with it.
/// </summary>
/// <remarks>
/// The window comes from the server's model list as IronHive's OpenAI-compatible model finder reads it (vLLM's
/// <c>max_model_len</c>) — not from the model's name, and not from what the model was trained on. The request leaves this computer,
/// so it is counted as sent to the server's host.
/// </remarks>
internal static class ServerContextWindow
{
    /// <summary>
    /// What the server at <paramref name="address"/> (the whole address is the base — no API path is added) reports for
    /// <paramref name="model"/> — its context window, as its model list says — or <see langword="null"/> when it does not list the model, or does not answer in
    /// <see cref="AnswerModel.CheckTimeout"/>. The request it was asked for goes on either way.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<ServerModel?> AskAsync(Uri address, string? key, string model, Egress egress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnswerModel.CheckTimeout);
        egress.Sent(address.Authority);
        try
        {
            using var finder = new OpenAICompatibleModelFinder(new OpenAICompatibleConfig { BaseUrl = address.AbsoluteUri, Path = "", ApiKey = key });
            return await finder.FindModelAsync(model, timeout.Token).ConfigureAwait(false) is { } card
                ? new ServerModel((card as LanguageModelCard)?.ContextWindow)
                : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not reported: the window stays unknown until a refusal tells it.
            return null;
        }
    }
}

/// <summary>What an OpenAI-compatible server's model list says of one model: its context window.</summary>
internal sealed record ServerModel(int? ContextWindow);
