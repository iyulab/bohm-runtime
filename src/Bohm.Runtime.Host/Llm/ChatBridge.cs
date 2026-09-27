using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>A provider's chat request, read into Microsoft.Extensions.AI terms.</summary>
/// <param name="Messages">The conversation, system instructions first.</param>
/// <param name="Options">What the request asked of the model.</param>
/// <param name="Stream">Whether the application asked for the answer as it is written.</param>
/// <param name="Model">The model name the application asked for, echoed back where the provider echoes it.</param>
internal sealed record BridgedChat(IReadOnlyList<ChatMessage> Messages, ChatOptions Options, bool Stream, string? Model);

/// <summary>
/// Answers a request an application wrote for one provider's chat API with a model that does not
/// speak that API — the application keeps its code and gets an answer in the shape it expects.
/// </summary>
internal interface IChatBridge
{
    /// <summary>Whether, for <paramref name="provider"/>, this bridge answers chat requests at all.</summary>
    bool AnswersChat(LlmProvider provider);

    /// <summary>Whether this bridge answers the request (<paramref name="path"/> is the provider path, without the query).</summary>
    bool Handles(LlmProvider provider, string method, string path);

    /// <summary>
    /// A request of the same provider API that the model answering in the provider's place (on this computer or the organization's server) cannot answer, and why —
    /// or <see langword="null"/>. It gets an error in the provider's shape rather than a request for
    /// a key, since a key is not what is missing.
    /// </summary>
    string? Unsupported(LlmProvider provider, string method, string path);

    /// <exception cref="FormatException">The body is not a request this bridge can read.</exception>
    /// <exception cref="NotSupportedException">The request asks for something the model answering in the provider's place cannot give.</exception>
    BridgedChat Parse(ReadOnlySpan<byte> body, string path);

    /// <summary>Answers the request with <paramref name="model"/>, in the provider's shape.</summary>
    Task AnswerAsync(HttpContext context, IChatClient model, BridgedChat request);
}

internal static class ChatBridges
{
    public static readonly IReadOnlyList<IChatBridge> All = [OpenAIChatBridge.Instance, AnthropicMessagesBridge.Instance, GeminiBridge.Instance];

    public static IChatBridge? For(LlmProvider provider, string method, string path) => All.FirstOrDefault(b => b.Handles(provider, method, path));

    public static string? Unsupported(LlmProvider provider, string method, string path) =>
        All.Select(b => b.Unsupported(provider, method, path)).FirstOrDefault(m => m is not null);

    public static bool AnswersChat(LlmProvider provider) => All.Any(b => b.AnswersChat(provider));

    /// <summary>Reads <paramref name="body"/> as one JSON object.</summary>
    /// <exception cref="FormatException">It is not.</exception>
    public static JsonDocument ParseObject(ReadOnlySpan<byte> body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body.ToArray());
        }
        catch (JsonException e)
        {
            throw new FormatException("The request body is not JSON.", e);
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
        document.Dispose();
        throw new FormatException("The request body is not a JSON object.");
    }

    /// <summary>The first of <paramref name="names"/> that <paramref name="element"/> has — REST bodies accept camelCase and snake_case alike.</summary>
    public static bool TryGet(JsonElement element, out JsonElement value, params ReadOnlySpan<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
                if (element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null) return true;
        }

        value = default;
        return false;
    }

    public static double? Number(JsonElement element, params ReadOnlySpan<string> names) =>
        TryGet(element, out var value, names) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    public static string? Text(JsonElement element, params ReadOnlySpan<string> names) =>
        TryGet(element, out var value, names) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static IList<string>? Strings(JsonElement element, params ReadOnlySpan<string> names) =>
        !TryGet(element, out var value, names) ? null : value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => [.. value.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!)],
            _ => null,
        };

    /// <summary>How hard to think, from a provider's thinking budget in tokens.</summary>
    public static ReasoningEffort EffortForBudget(double? budget) => budget switch
    {
        null or <= 0 => ReasoningEffort.None,
        < 2048 => ReasoningEffort.Low,
        < 8192 => ReasoningEffort.Medium,
        _ => ReasoningEffort.High,
    };

    /// <summary>A tool call's arguments, as the model's abstractions carry them.</summary>
    public static Dictionary<string, object?> Arguments(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
            : [];

    /// <summary>A tool call's arguments as a JSON object.</summary>
    public static System.Text.Json.Nodes.JsonObject ArgumentsNode(IDictionary<string, object?>? arguments) =>
        arguments is null
            ? []
            : System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(arguments, AIJsonUtilities.DefaultOptions.GetTypeInfo(typeof(IDictionary<string, object?>))))!.AsObject();

    /// <summary>A tool the application declares: the model only proposes a call, which goes back to the application.</summary>
    public static AITool Declaration(string name, string? description, JsonElement? schema) =>
        AIFunctionFactory.CreateDeclaration(name, description, schema?.Clone() ?? JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

    /// <summary>The text of a response or update, reasoning left out — providers give that apart, if at all.</summary>
    public static string TextOf(IEnumerable<AIContent> contents) =>
        string.Concat(contents.OfType<TextContent>().Select(t => t.Text));

    public static async Task WriteAsync(HttpResponse response, string text, CancellationToken cancel)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancel).ConfigureAwait(false);
        await response.Body.FlushAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Starts a streamed answer: each write reaches the application as it is made.</summary>
    public static void StartStream(HttpContext context, string contentType)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = contentType;
        response.Headers.CacheControl = "no-cache";
        context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }
}
