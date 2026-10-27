using IronHive.Abstractions.Http;
using IronHive.Extensions.AI;
using IronHive.Providers.Anthropic;
using IronHive.Providers.GoogleAI;
using IronHive.Providers.OpenAI;
using IronHive.Providers.OpenAI.Compatible;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>Builds the client for a model of a declared provider: the adapter is picked by the format the provider speaks, not by its name.</summary>
internal static class ProviderClients
{
    /// <summary>
    /// A client for <paramref name="modelId"/> of <paramref name="provider"/> — through the format of <paramref name="model"/>
    /// when the declaration lists it with one of its own, otherwise the provider's — asked with <paramref name="key"/>
    /// (<see langword="null"/> for a provider that takes none).
    /// </summary>
    public static IChatClient Create(ProviderDeclaration provider, DeclaredModel? model, string modelId, string? key)
    {
        var api = model is null ? provider.Api : provider.ApiOf(model);
        var root = model is null ? provider.BaseUrl : provider.RootOf(model);
        var auth = provider.Auth;
        if (auth.Style == AuthStyle.None) key = null;

        switch (api)
        {
            case ProviderApi.GoogleGemini:
                // Gemini through its own API: its models sign each tool call and refuse the next turn without
                // the signature, which the OpenAI-compatible base drops on the way back. IronHive's Gemini
                // provider carries the signatures through the tool loop.
                var google = new GoogleAIConfig { HttpOptions = new Google.GenAI.Types.HttpOptions { BaseUrl = root.ToString().TrimEnd('/') } };
                if (key is not null)
                {
                    if (auth.Style == AuthStyle.Google) google.ApiKey = key;
                    else google.Headers = new Dictionary<string, string> { [HeaderOf(auth)] = ValueOf(auth, key) };
                }

                return new GoogleAIMessageGenerator(google).AsChatClient(modelId, "googleai");

            case ProviderApi.AnthropicMessages:
                // Anthropic through its own Messages API: its OpenAI-compatible base is meant for trying
                // models out, not for real use, and leaves out what the Messages API carries.
                var anthropic = new AnthropicConfig { BaseUrl = root.ToString().TrimEnd('/') };
                if (key is not null)
                {
                    // The SDK owns the credential's slot and presents the key the way the API version it speaks expects (a bearer, at the
                    // version in use) — it refuses that header in Headers. Any other header the key is declared to travel in goes there.
                    if (auth.Style == AuthStyle.Bearer || (auth.Style == AuthStyle.Header && string.Equals(auth.HeaderName, "x-api-key", StringComparison.OrdinalIgnoreCase))) anthropic.ApiKey = key;
                    else anthropic.Headers = new Dictionary<string, string> { [HeaderOf(auth)] = ValueOf(auth, key) };
                }

                return new AnthropicMessageGenerator(anthropic).AsChatClient(modelId, "anthropic");

            case ProviderApi.OpenAIResponses:
                // OpenAI through its Responses API. Every request says store:false, so what is sent is
                // answered, not kept on the provider's side.
                var responses = new OpenAIConfig { BaseUrl = root.ToString().TrimEnd('/'), ApiKey = key ?? string.Empty };
                if (key is not null && PlacementOf(auth) is { } placed) responses.ApiKeyPlacement = placed;
                return new OpenAIMessageGenerator(responses).AsChatClient(modelId, "openai");

            default:
                // Chat Completions at an OpenAI-compatible base — a reasoning model's reasoning_content and a refusal's status are kept.
                var chat = new OpenAICompatibleConfig { BaseUrl = root.AbsoluteUri, Path = "", ApiKey = key };
                if (key is not null && PlacementOf(auth) is { } chatPlaced) chat.ApiKeyPlacement = chatPlaced;
                return new OpenAICompatibleMessageGenerator(chat).AsChatClient(modelId, "openai-compatible");
        }
    }

    /// <summary>Where the key goes on a request other than the format's own default; <see langword="null"/> for a bearer key.</summary>
    private static CredentialPlacement? PlacementOf(ProviderAuth auth) => auth.Style switch
    {
        AuthStyle.Header => CredentialPlacement.InHeader(auth.HeaderName!),
        AuthStyle.Google => CredentialPlacement.InHeader("x-goog-api-key"),
        _ => null,
    };

    private static string HeaderOf(ProviderAuth auth) => auth.Style switch
    {
        AuthStyle.Header => auth.HeaderName!,
        AuthStyle.Google => "x-goog-api-key",
        _ => "Authorization",
    };

    private static string ValueOf(ProviderAuth auth, string key) => auth.Style == AuthStyle.Bearer ? "Bearer " + key : key;
}
