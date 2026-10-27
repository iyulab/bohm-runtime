namespace Bohm.Runtime.Host.Llm;

/// <summary>How a provider expects its API key.</summary>
internal enum KeyStyle
{
    /// <summary><c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    Bearer,

    /// <summary><c>x-api-key: &lt;key&gt;</c>.</summary>
    ApiKeyHeader,

    /// <summary><c>x-goog-api-key: &lt;key&gt;</c> header or <c>key=&lt;key&gt;</c> query parameter.</summary>
    Google,
}

/// <summary>An AI provider whose API an adopted application may call directly.</summary>
/// <param name="Id">Short identifier, used to name the key in the vault.</param>
/// <param name="Host">The provider's API host.</param>
/// <param name="DisplayName">Name shown to the person.</param>
/// <param name="Style">How the key is presented.</param>
/// <param name="OpenAICompatiblePath">
/// Where on <paramref name="Host"/> the provider answers OpenAI-shaped chat requests — the base the
/// runtime's own agent uses with this provider's key, whatever shape the applications use.
/// </param>
internal sealed record LlmProvider(string Id, string Host, string DisplayName, KeyStyle Style, string OpenAICompatiblePath)
{
    public string VaultName => ProviderDeclaration.VaultNameOf(Id);
}

internal static class LlmProviders
{
    /// <summary>The providers shipped with the runtime — derived from <see cref="ProviderDeclaration.BuiltIn"/>, which is where one is added.</summary>
    public static IReadOnlyList<LlmProvider> All => ProviderDeclaration.BuiltInForApps;

    public static LlmProvider? ByHost(string host) => All.FirstOrDefault(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase));

    public static LlmProvider? ById(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// What an application is given when it asks for a key: a value that is useless anywhere but
    /// this runtime's proxy for this application. It is never a real key, so an application that
    /// stores it stores nothing secret.
    /// </summary>
    public static string Placeholder(string appId) => $"bohm-key-{appId}";
}
