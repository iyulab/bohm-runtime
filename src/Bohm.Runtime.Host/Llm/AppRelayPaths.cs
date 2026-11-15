using System.Text.RegularExpressions;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// What an application may ask a built-in provider for through the relay, with the person's key: using AI — chat, the provider's
/// own message and generation formats, embeddings, speech to text and back, image generation, moderation, counting tokens, and
/// listing models — and nothing else. The rest of a provider's API (files, batches, fine-tuning, assistants and stores, anything
/// deleted, the account itself) acts on the person's account rather than asking the AI something, and is refused.
/// </summary>
/// <remarks>
/// Derived from what is shipped per provider (<see cref="LlmProvider.OpenAICompatiblePath"/> and its key style), so a provider
/// added to that one list is covered the same way. Paths are compared as written, without dot segments.
/// </remarks>
internal static partial class AppRelayPaths
{
    /// <summary>Whether <paramref name="method"/> <paramref name="path"/> (after the provider's host) is one an application may relay.</summary>
    public static bool Allows(LlmProvider provider, string method, string path)
    {
        var trimmed = path.Trim('/');
        if (trimmed.Split('/').Any(segment => segment is "." or ".." || segment.Contains('%')))
            return false;

        var post = HttpMethods.IsPost(method);
        var get = HttpMethods.IsGet(method);
        var compatible = provider.OpenAICompatiblePath.Trim('/');
        if (compatible.Length > 0 && trimmed.StartsWith(compatible + "/", StringComparison.Ordinal))
        {
            var under = trimmed[(compatible.Length + 1)..];
            if (post && under is "chat/completions" or "embeddings") return true;
            if (get && (under == "models" || ModelById().IsMatch(under))) return true;
        }

        return provider.Style switch
        {
            KeyStyle.ApiKeyHeader => (post && trimmed is "v1/messages" or "v1/messages/count_tokens")
                || (get && trimmed.StartsWith("v1/", StringComparison.Ordinal) && (trimmed == "v1/models" || ModelById().IsMatch(trimmed[3..]))),
            KeyStyle.Google => (post && GeminiUse().IsMatch(trimmed)) || (get && GeminiModels().IsMatch(trimmed)),
            _ when provider.Id == "openai" => post && trimmed is "v1/responses" or "v1/audio/transcriptions" or "v1/audio/translations" or "v1/audio/speech"
                or "v1/images/generations" or "v1/moderations",
            _ => false,
        };
    }

    [GeneratedRegex(@"^models/[A-Za-z0-9._:-]+$")]
    private static partial Regex ModelById();

    [GeneratedRegex(@"^v1(alpha|beta)?/models/[A-Za-z0-9._-]+:(generateContent|streamGenerateContent|countTokens|embedContent|batchEmbedContents)$")]
    private static partial Regex GeminiUse();

    [GeneratedRegex(@"^v1(alpha|beta)?/models(/[A-Za-z0-9._-]+)?$")]
    private static partial Regex GeminiModels();
}
