using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// A provider's refusal of a proposal request, as the provider put it: its status and its own
/// message — a fact the shell shows the person, not a sentence of ours. Without it the person sees
/// only that the proposal did not finish, never why (a wrong model name, a missing thought signature).
/// </summary>
/// <param name="Status">The provider's HTTP status.</param>
/// <param name="Message">The provider's own message, when its answer carried one; bounded.</param>
/// <param name="RetryAfter">
/// The seconds a busy or rate-limited provider asked the caller to wait before asking again, when it said
/// (<c>Retry-After</c>) — so the person hears «in a few seconds» instead of guessing; rounded up.
/// </param>
/// <param name="Billing">
/// The provider refused because the account's balance, credit or quota ran out (a 402, an exhausted quota sent as
/// a 429, a «credit balance is too low» 400) — waiting will not help and a different model name will not either;
/// the person tops up that service or picks another. Written only when true.
/// </param>
internal sealed record ProviderRefusal(int Status, string? Message, int? RetryAfter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Billing = false)
{
    private const int MaxMessage = 500;

    public static ProviderRefusal? Of(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is ClientResultException { Status: > 0 } refused)
                return new ProviderRefusal(refused.Status, MessageOf(refused.GetRawResponse()?.Content?.ToString()), RetryAfterOf(refused.GetRawResponse()));
            // Anthropic through its own API: one exception type per status, all carrying the raw body.
            if (e is Anthropic.Exceptions.AnthropicApiException { StatusCode: > 0 } anthropic)
                return new ProviderRefusal((int)anthropic.StatusCode, MessageOf(anthropic.ResponseBody));
            // Gemini through its own API: the SDK has already read the provider's message out of the body.
            if (e is Google.GenAI.ClientError { StatusCode: > 0 } client)
                return new ProviderRefusal(client.StatusCode, Bounded(client.Message));
            if (e is Google.GenAI.ServerError { StatusCode: > 0 } server)
                return new ProviderRefusal(server.StatusCode, Bounded(server.Message));
            // A billing refusal, whichever provider sent it: the outermost word, before the SDK exception it may wrap
            // (whose status alone would read as a rate limit or a bad request). Inside a stream it carries no status.
            if (e is IronHive.Abstractions.Exceptions.BillingException billing)
                return new ProviderRefusal((int?)billing.StatusCode ?? 402, Bounded(e.Message), Billing: true);
            // An OpenAI-compatible server through IronHive: a rate limit comes as its own type, which has no status to carry — it is a 429.
            if (e is IronHive.Abstractions.Exceptions.RateLimitException limited)
                return new ProviderRefusal(429, Bounded(e.Message), Seconds(limited.RetryAfter));
            // Any other refusal: its status, and its own message already read out of the body.
            if (e is HttpRequestException { StatusCode: { } status })
                return new ProviderRefusal((int)status, Bounded(e.Message),
                    Seconds((e as IronHive.Abstractions.Exceptions.ProviderHttpException)?.RetryAfter));
        }

        return null;
    }

    /// <summary><c>retry-after-ms</c> or <c>Retry-After</c> (seconds or an HTTP date) of a raw response.</summary>
    internal static int? RetryAfterOf(PipelineResponse? response)
    {
        if (response is null) return null;
        if (response.Headers.TryGetValue("retry-after-ms", out var ms) && double.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
            return Seconds(TimeSpan.FromMilliseconds(milliseconds));
        if (!response.Headers.TryGetValue("Retry-After", out var value) || string.IsNullOrWhiteSpace(value)) return null;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return Seconds(TimeSpan.FromSeconds(seconds));
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? Seconds(at - DateTimeOffset.UtcNow) : null;
    }

    private static int? Seconds(TimeSpan? wait) =>
        wait is { } w && w >= TimeSpan.Zero ? (int)Math.Ceiling(Math.Min(w.TotalSeconds, int.MaxValue)) : null;

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
                return Bounded(text);
        }
        catch (JsonException)
        {
            // Not the shape providers refuse in: the status alone is the fact.
        }

        return null;
    }

    private static string? Bounded(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= MaxMessage ? text : text[..MaxMessage] + "…";
}
