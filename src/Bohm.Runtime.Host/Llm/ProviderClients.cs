using IronHive.Providers.Declarations;
using Microsoft.Extensions.AI;

using Declared = IronHive.Providers.Declarations;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Builds the client for a model of a declared provider: the adapter is picked by the format the provider speaks, not by its name.
/// The declaration is ours (it carries what the person sees — name, limits, origin); IronHive's declaration and factory are what know
/// each wire's adapter, how its base address is written and where it lets a key go, so this only translates one into the other.
/// </summary>
internal static class ProviderClients
{
    /// <summary>
    /// A client for <paramref name="modelId"/> of <paramref name="provider"/> — through the format of <paramref name="model"/>
    /// when the declaration lists it with one of its own, otherwise the provider's — asked with <paramref name="key"/>
    /// (<see langword="null"/> for a provider that takes none). The declaration is the whole credential decision: a provider that is handed no key is asked
    /// without one, never with a key the SDK finds in the machine's environment or in a profile of the person's own tools (IronHive 0.74.0 guarantees it;
    /// <c>ProviderClientsTests</c> pins it with sentinel values on every wire).
    /// </summary>
    public static IChatClient Create(ProviderDeclaration provider, DeclaredModel? model, string modelId, string? key)
    {
        var wire = WireOf(model is null ? provider.Api : provider.ApiOf(model));
        var auth = provider.Auth;
        if (auth.Style == AuthStyle.None) key = null;

        // A model that speaks another format or sits under another path is resolved here (wire and endpoint above), so the declaration handed over is already about that one model.
        var declaration = new Declared.ProviderDeclaration
        {
            Id = provider.Id,
            Wire = wire,
            BaseUrl = (model is null ? provider.BaseUrl : provider.RootOf(model)).AbsoluteUri,
            ApiKey = key,
            KeyHeader = key is null ? null : KeyHeaderOf(auth, wire),
            KeyScheme = key is not null && auth.Style == AuthStyle.Header ? string.Empty : null,
        };

        return ProviderClientFactory.Create(declaration, modelId);
    }

    private static WireFormat WireOf(ProviderApi api) => api switch
    {
        ProviderApi.GoogleGemini => WireFormat.GoogleAI,
        ProviderApi.AnthropicMessages => WireFormat.Anthropic,
        ProviderApi.OpenAIResponses => WireFormat.OpenAIResponses,
        _ => WireFormat.OpenAIChatCompletions,
    };

    /// <summary>The header the key travels in when it is not the wire's own; <see langword="null"/> for the wire's own.</summary>
    private static string? KeyHeaderOf(ProviderAuth auth, WireFormat wire) => auth.Style switch
    {
        AuthStyle.Header => auth.HeaderName,
        AuthStyle.Google => wire == WireFormat.GoogleAI ? null : "x-goog-api-key",
        _ => null,
    };
}
