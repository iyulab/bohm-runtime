using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// What in a relayed request's body would have the provider fetch an address itself — a remote image or file, a web search or
/// fetch, a remote MCP server. The application's page cannot reach other hosts (its content security policy), and a request the
/// provider fetches for would carry the application's data there without passing it; such a request is refused.
/// An image or file given inline (a <c>data:</c> address, base64) is the application's own and passes.
/// </summary>
internal static partial class AppRelayFetches
{
    /// <summary>Properties whose string value is an address the provider would fetch (OpenAI, Anthropic, Gemini; both spellings).</summary>
    private static readonly HashSet<string> AddressProperties = new(StringComparer.Ordinal)
    {
        "image_url", "url", "file_url", "file_uri", "fileUri", "server_url", "serverUrl",
    };

    /// <summary>Properties that turn on a provider's own web tools (Gemini names its tools by property).</summary>
    private static readonly HashSet<string> WebToolProperties = new(StringComparer.Ordinal)
    {
        "google_search", "googleSearch", "google_search_retrieval", "googleSearchRetrieval", "url_context", "urlContext",
    };

    /// <summary>Why the request would have the provider fetch something, or <see langword="null"/> when it would not (or is not JSON).</summary>
    public static string? Find(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
        string? property = null;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        property = reader.GetString();
                        if (property is not null && WebToolProperties.Contains(property)) return $"the {property} tool";
                        continue;
                    case JsonTokenType.String when property is not null:
                        var value = reader.GetString() ?? "";
                        if (AddressProperties.Contains(property) && Remote().IsMatch(value)) return $"an address in {property}";
                        if (property == "type" && WebToolType().IsMatch(value)) return $"the {value} tool";
                        break;
                }

                property = null;
            }
        }
        catch (JsonException)
        {
            return null;   // not JSON (a recording, a form): nothing named here to fetch — sent as the application wrote it
        }

        return null;
    }

    [GeneratedRegex(@"^\s*(https?|ftp)://", RegexOptions.IgnoreCase)]
    private static partial Regex Remote();

    [GeneratedRegex(@"^(web_search|web_fetch|mcp|url_context|google_search|computer_use|code_interpreter|file_search)", RegexOptions.IgnoreCase)]
    private static partial Regex WebToolType();
}
