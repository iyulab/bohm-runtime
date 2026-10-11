namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Reads where a provider's traffic goes instead of the provider itself, from one environment variable:
/// <c>host=address</c> pairs separated by <c>;</c> — for example
/// <c>api.anthropic.com=https://gateway.example/anthropic/</c>. The address speaks the same API as the
/// provider (a gateway in front of it); the provider is still the one the person chose and connected a key for.
/// </summary>
internal static class LlmEndpointsVariable
{
    public const string Name = "BOHM_LLM_ENDPOINTS";

    /// <summary>The pairs that can be used, and the ones that cannot (to say so), from the variable's value.</summary>
    public static (IReadOnlyDictionary<string, Uri> Endpoints, IReadOnlyList<string> Unusable) Parse(string? value)
    {
        var endpoints = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        var unusable = new List<string>();
        foreach (var pair in (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            var host = at > 0 ? pair[..at].Trim() : "";
            if (host.Length == 0 || LlmProviders.All.All(p => !string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase))
                || !Uri.TryCreate(pair[(at + 1)..].Trim(), UriKind.Absolute, out var address)
                || (address.Scheme != Uri.UriSchemeHttps && !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback)))
            {
                unusable.Add(pair);
                continue;
            }
            // A base address ends in «/», so the provider's own paths are appended to it rather than replacing its last part.
            endpoints[host] = address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/");
        }
        return (endpoints, unusable);
    }
}
