using IronHive.Abstractions.Models;
using IronHive.Providers.Anthropic;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// What a provider speaking Anthropic's format says of one of its models in its model information (<c>GET v1/models/{id}</c>):
/// the context window and the largest answer. Asked once; nobody's text goes with it.
/// </summary>
/// <remarks>
/// The values come from the provider as IronHive's Anthropic model finder reads them (<c>max_input_tokens</c>, <c>max_tokens</c>) —
/// not from the model's name. Whether the model thinks is not read there yet (the finder does not carry the model's capabilities), so
/// it is still learned from the model's first answer. The request leaves this computer, so it is counted as sent to the provider's host.
/// </remarks>
internal static class ProviderModelInfo
{
    /// <summary>
    /// The limits <paramref name="address"/> says for <paramref name="model"/>, or <see langword="null"/> when it says none, does not
    /// know the model, or does not answer in <see cref="AnswerModel.CheckTimeout"/>. The request it was asked for goes on either way.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<ModelLimits?> AskAnthropicAsync(Uri address, string? key, string model, Egress egress, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AnswerModel.CheckTimeout);
        egress.Sent(address.Authority);
        try
        {
            using var finder = new AnthropicModelFinder(new AnthropicConfig { BaseUrl = address.AbsoluteUri, ApiKey = key ?? string.Empty });
            return await finder.FindModelAsync(model, timeout.Token).ConfigureAwait(false) is LanguageModelCard card
                && (card.ContextWindow is not null || card.MaxOutputTokens is not null)
                ? new ModelLimits(card.ContextWindow, card.MaxOutputTokens)
                : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not said: the limits stay unknown until a refusal or an answer tells them.
            return null;
        }
    }
}
