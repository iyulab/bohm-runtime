using System.ClientModel;
using System.Text.Json;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// A provider's refusal of a proposal request, as the provider put it: its status and its own
/// message — a fact the shell shows the person, not a sentence of ours. Without it the person sees
/// only that the proposal did not finish, never why (a wrong model name, a missing thought signature).
/// </summary>
/// <param name="Status">The provider's HTTP status.</param>
/// <param name="Message">The provider's own message, when its answer carried one; bounded.</param>
internal sealed record ProviderRefusal(int Status, string? Message)
{
    private const int MaxMessage = 500;

    public static ProviderRefusal? Of(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is ClientResultException { Status: > 0 } refused)
                return new ProviderRefusal(refused.Status, MessageOf(refused.GetRawResponse()?.Content?.ToString()));
        }

        return null;
    }

    /// <summary>
    /// <c>error.message</c> of an object (OpenAI, Anthropic, most compatible bases) or of the first
    /// element of an array (Gemini's OpenAI-compatible base).
    /// </summary>
    internal static string? MessageOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String && message.GetString() is { Length: > 0 } text)
                return text.Length <= MaxMessage ? text : text[..MaxMessage] + "…";
        }
        catch (JsonException)
        {
            // Not the shape providers refuse in: the status alone is the fact.
        }

        return null;
    }
}
