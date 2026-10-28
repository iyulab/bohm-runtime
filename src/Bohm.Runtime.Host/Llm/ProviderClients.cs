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
    /// (<see langword="null"/> for a provider that takes none).
    /// </summary>
    public static IChatClient Create(ProviderDeclaration provider, DeclaredModel? model, string modelId, string? key)
    {
        IsolateFromAmbientCredentials();
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

    /// <summary>
    /// The variables the Anthropic SDK reads on its own when it is not handed a credential. A declared provider's key travels in the header
    /// its declaration names, so the SDK's own slot stays empty — and the SDK then fills it from the machine: a key in the environment or a profile
    /// of the person's own Anthropic tools would be sent to whatever address they declared (measured: <c>ANTHROPIC_API_KEY</c> arrived as
    /// <c>Authorization: Bearer</c> at a third-party gateway). The person declared one provider; only that provider's key may go to it.
    /// </summary>
    private static readonly string[] AmbientAnthropicVariables =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_CUSTOM_HEADERS", "ANTHROPIC_PROFILE",
        "ANTHROPIC_IDENTITY_TOKEN", "ANTHROPIC_IDENTITY_TOKEN_FILE", "ANTHROPIC_FEDERATION_RULE_ID", "ANTHROPIC_ORGANIZATION_ID",
        "ANTHROPIC_SERVICE_ACCOUNT_ID", "ANTHROPIC_WORKSPACE_ID",
    ];

    /// <summary>
    /// TODO(upstream: IronHive — build the Anthropic client with an explicit, empty credential so the SDK never resolves one from the machine):
    /// until then this process stops offering the SDK a machine credential — the variables above are cleared and its profile directory points at an empty one.
    /// Idempotent; the runtime is the person's own process and takes its keys from its vault, never from the environment.
    /// </summary>
    internal static void IsolateFromAmbientCredentials()
    {
        foreach (var name in AmbientAnthropicVariables) Environment.SetEnvironmentVariable(name, null);
        var empty = Path.Combine(Path.GetTempPath(), "bohm-no-ambient-credentials");
        Directory.CreateDirectory(empty);
        Environment.SetEnvironmentVariable("ANTHROPIC_CONFIG_DIR", empty);
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
