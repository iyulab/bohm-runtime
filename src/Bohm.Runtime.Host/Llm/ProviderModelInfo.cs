using IronHive.Abstractions.Models;
using IronHive.Providers.Anthropic;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// What a provider speaking Anthropic's format says of one of its models in its model information (<c>GET v1/models/{id}</c>):
/// the context window, the largest answer, whether it thinks and whether it takes pictures in. Asked once; nobody's text goes with it.
/// </summary>
/// <remarks>
/// The values come from the provider as IronHive's Anthropic model finder reads them (<c>max_input_tokens</c>, <c>max_tokens</c> and the
/// model's <c>capabilities</c>) — not from the model's name. A provider that reports capabilities says whether the model thinks
/// (<c>thinking</c> among the card's features) and whether it takes pictures in (<c>image</c> among its input modalities); one that
/// reports none leaves both unknown, and they are learned as before — thinking from the first answer, pictures by asking once. The
/// request leaves this computer, so it is counted as sent to the provider's host.
/// </remarks>
internal static class ProviderModelInfo
{
    /// <summary>
    /// What <paramref name="address"/> says of <paramref name="model"/>, or <see langword="null"/> when it says nothing, does not
    /// know the model, or does not answer in <see cref="AnswerModel.CheckTimeout"/>. The request it was asked for goes on either way.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<ProviderModelFacts?> AskAnthropicAsync(Uri address, string? key, string model, Egress egress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnswerModel.CheckTimeout);
        egress.Sent(address.Authority);
        try
        {
            using var finder = new AnthropicModelFinder(new AnthropicConfig { BaseUrl = address.AbsoluteUri, ApiKey = key ?? string.Empty });
            return await finder.FindModelAsync(model, timeout.Token).ConfigureAwait(false) is LanguageModelCard card ? FactsOf(card) : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not said: the limits stay unknown until a refusal or an answer tells them.
            return null;
        }
    }

    /// <summary>
    /// What a card says, or <see langword="null"/> when it says nothing. A list the provider gave counts as its answer — a feature or
    /// modality missing from it is reported as not supported; a list it did not give leaves that fact unknown.
    /// </summary>
    internal static ProviderModelFacts? FactsOf(LanguageModelCard card)
    {
        bool? thinks = card.Features is { } features ? features.Contains("thinking") : null;
        bool? sees = card.InputModalities is { } modalities ? modalities.Contains("image") : null;
        return card.ContextWindow is null && card.MaxOutputTokens is null && thinks is null && sees is null
            ? null
            : new ProviderModelFacts(new ModelLimits(card.ContextWindow, card.MaxOutputTokens, thinks), sees);
    }
}

/// <summary>What a provider said of one model: its limits (whether it thinks among them) and whether it takes pictures in.</summary>
internal sealed record ProviderModelFacts(ModelLimits Limits, bool? Sees);
