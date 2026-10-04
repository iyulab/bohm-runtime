using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// An application that asked for a JSON answer (<c>response_format</c> <c>json_object</c> or a schema)
/// gets JSON, whichever model answers it: an answer that is one Markdown code fence around valid JSON
/// comes back as that JSON.
/// </summary>
/// <remarks>
/// A server can drop the JSON constraint without saying so — a llama.cpp server with thinking turned
/// off (which the relay does for a model that thinks) answers <c>json_object</c> with the JSON in a
/// <c>```json</c> fence, and an application's <c>JSON.parse</c> of it fails. Only that wrapping is
/// undone: an answer already JSON, an answer with words outside the fence, more than one fence, or a
/// fence around something that is not JSON is passed on as it came — the application decides what to do
/// with an answer that is not JSON, the relay does not guess at one. A streamed answer to a JSON request
/// is gathered and sent once, since whether it is fenced is known only at its end.
/// </remarks>
internal sealed partial class JsonModeChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        if (AsksForJson(options)) Unfence(response);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!AsksForJson(options))
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                yield return update;
            yield break;
        }

        var response = await base.GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);
        Unfence(response);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    private static bool AsksForJson(ChatOptions? options) => options?.ResponseFormat is ChatResponseFormatJson;

    /// <summary>Takes the fence off the last message's text when the text is one fence around valid JSON.</summary>
    private static void Unfence(ChatResponse response)
    {
        if (response.Messages.Count == 0) return;
        var message = response.Messages[^1];
        var text = message.Text;
        if (IsJson(text)) return;
        var fenced = OneFence().Match(text);
        if (!fenced.Success) return;
        var json = fenced.Groups["json"].Value.Trim();
        if (!IsJson(json)) return;

        var at = message.Contents.ToList().FindIndex(c => c is TextContent);
        var kept = message.Contents.Where(c => c is not TextContent).ToList();
        kept.Insert(Math.Min(at, kept.Count), new TextContent(json));
        message.Contents = kept;
    }

    private static bool IsJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The whole answer: an opening fence with an optional language word, the body, a closing fence — nothing else.
    [GeneratedRegex(@"\A\s*```[A-Za-z]*[ \t]*\r?\n(?<json>(?:(?!```).)*?)```\s*\z", RegexOptions.Singleline)]
    private static partial Regex OneFence();
}
