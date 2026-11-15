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
    /// <summary>Properties whose string value is an address the provider would fetch wherever they stand (OpenAI, Anthropic, Gemini; both spellings).</summary>
    private static readonly HashSet<string> AddressProperties = new(StringComparer.Ordinal)
    {
        "image_url", "file_url", "file_uri", "fileUri", "server_url", "serverUrl",
    };

    /// <summary>
    /// Where a plain <c>url</c> is one the provider fetches: an OpenAI image part (<c>image_url.url</c>), an Anthropic image or
    /// document source (<c>source.url</c>), an Anthropic remote MCP server (<c>mcp_servers[].url</c>).
    /// </summary>
    private static readonly HashSet<string> UrlParents = new(StringComparer.Ordinal)
    {
        "image_url", "source", "mcp_servers",
    };

    /// <summary>
    /// Objects that carry the application's own data rather than anything the provider acts on — a tool call's arguments and
    /// result in the conversation (Anthropic <c>tool_use.input</c>, Gemini <c>functionCall.args</c> and
    /// <c>functionResponse.response</c>) and JSON schemas (tool parameters, structured output). An address there is a value the
    /// application's own function was given or returned; it is not looked at.
    /// </summary>
    private static readonly HashSet<string> OwnDataObjects = new(StringComparer.Ordinal)
    {
        "input", "args", "response", "parameters", "parametersJsonSchema", "input_schema", "schema", "responseSchema", "response_schema",
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
        // For each open object or array, the property it is the value of (null for an array's element or the root).
        var containers = new Stack<string?>();
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
                    case JsonTokenType.StartObject when property is not null && OwnDataObjects.Contains(property):
                        reader.Skip();
                        break;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        containers.Push(property);
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        containers.TryPop(out _);
                        break;
                    case JsonTokenType.String when property is not null:
                        var value = reader.GetString() ?? "";
                        var fetched = AddressProperties.Contains(property) || (property == "url" && UrlParents.Contains(Nearest(containers) ?? ""));
                        if (fetched && Remote().IsMatch(value)) return $"an address in {property}";
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

    /// <summary>The property the innermost named container is the value of — through an array's elements to the array.</summary>
    private static string? Nearest(Stack<string?> containers)
    {
        foreach (var name in containers)
            if (name is not null) return name;
        return null;
    }

    [GeneratedRegex(@"^\s*(https?|ftp)://", RegexOptions.IgnoreCase)]
    private static partial Regex Remote();

    [GeneratedRegex(@"^(web_search|web_fetch|mcp|url_context|google_search|computer_use|code_interpreter|file_search)", RegexOptions.IgnoreCase)]
    private static partial Regex WebToolType();
}
