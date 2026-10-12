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
/// <param name="Status">The provider's HTTP status — for an answer the provider failed after it accepted the request, the status its vendor documents for that error, else 502.</param>
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

    /// <summary>
    /// The provider's own code for the refusal (<c>error.type</c> of Anthropic's shape, <c>error.code</c> of OpenAI's) — for an account
    /// a metering gateway refused, the gateway's: <c>insufficient_balance</c>, <c>hold_rejected</c>, <c>cap_reached</c> or <c>wallet_suspended</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    /// <summary>The largest output limit the account's credits can hold now, when the gateway refused a request's (<c>hold_rejected</c> — <c>max_affordable_max_tokens</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AffordableMaxTokens { get; init; }

    public static ProviderRefusal? Of(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is ClientResultException { Status: > 0 } refused)
                return FromBody(refused.Status, refused.GetRawResponse()?.Content?.ToString(), RetryAfterOf(refused.GetRawResponse()));
            // Anthropic through its own API: one exception type per status, all carrying the raw body.
            if (e is Anthropic.Exceptions.AnthropicApiException { StatusCode: > 0 } anthropic)
                return FromBody((int)anthropic.StatusCode, anthropic.ResponseBody);
            // Gemini through its own API: the SDK has already read the provider's message out of the body.
            if (e is Google.GenAI.ClientError { StatusCode: > 0 } client)
                return new ProviderRefusal(client.StatusCode, Bounded(client.Message));
            if (e is Google.GenAI.ServerError { StatusCode: > 0 } server)
                return new ProviderRefusal(server.StatusCode, Bounded(server.Message));
            // A billing refusal, whichever provider sent it: the outermost word, before the SDK exception it may wrap
            // (whose status alone would read as a rate limit or a bad request). Inside a stream it carries no status.
            // Its message is the provider's sentence; the provider's whole answer, with a gateway's own fields, is its response body.
            if (e is IronHive.Abstractions.Exceptions.BillingException billing)
            {
                var told = FromBody((int?)billing.StatusCode ?? 402, billing.ResponseBody, billing: true);
                return told with { Message = SaidBy(told, billing.ResponseBody, e), Code = told.Code ?? billing.ErrorCode };
            }
            // An OpenAI-compatible server through IronHive: a rate limit comes as its own type, which has no status to carry — it is a 429.
            if (e is IronHive.Abstractions.Exceptions.RateLimitException limited)
                return new ProviderRefusal(429, Bounded(e.Message), Seconds(limited.RetryAfter));
            // A provider that accepted the request and then failed the answer — an error inside a stream that had started, a
            // dropped connection: the status the vendor documents for the same error outside a stream (an overload 529, a server
            // error 500, a bad request 400), so it is told like that error; with none documented it reads as an upstream that
            // failed its response (502) — a passing failure, asking again usually works. With the provider's code and message.
            if (e is IronHive.Abstractions.Exceptions.ProviderResponseException failed)
                return new ProviderRefusal((int?)failed.EquivalentStatusCode ?? 502, Bounded(failed.ErrorCode is { Length: > 0 } code && !e.Message.Contains(code, StringComparison.Ordinal) ? $"{code}: {e.Message}" : e.Message));
            // Any other refusal from an IronHive provider: its status, its sentence, and the code its answer carries.
            if (e is IronHive.Abstractions.Exceptions.ProviderHttpException { StatusCode: { } providerStatus } http)
            {
                var told = FromBody((int)providerStatus, http.ResponseBody, Seconds(http.RetryAfter));
                return told with { Message = SaidBy(told, http.ResponseBody, e), Code = told.Code ?? http.ErrorCode };
            }

            // Any other refusal: its status, and its own message already read out of the body.
            if (e is HttpRequestException { StatusCode: { } status })
                return new ProviderRefusal((int)status, Bounded(e.Message));
        }

        return null;
    }

    /// <summary>
    /// The provider's message: the one its answer carried, else — when the provider keeps no answer to read — the exception's sentence.
    /// An answer with no message of its own (a gateway's plain-text or HTML error page) tells none: it is not the provider's word to show.
    /// </summary>
    private static string? SaidBy(ProviderRefusal told, string? body, Exception e) => body is null ? Bounded(e.Message) : told.Message;

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

    /// <summary>A refusal with <paramref name="status"/>, told by what the provider's answer <paramref name="body"/> says.</summary>
    private static ProviderRefusal FromBody(int status, string? body, int? retryAfter = null, bool billing = false)
    {
        var (message, code, affordable) = Read(body);
        return new ProviderRefusal(status, message, retryAfter, billing) { Code = code, AffordableMaxTokens = affordable };
    }

    /// <summary>
    /// <c>error.message</c> of an object (OpenAI, Anthropic, most compatible bases) or of the first
    /// element of an array (Gemini's OpenAI-compatible base).
    /// </summary>
    internal static string? MessageOf(string? body) => Read(body).Message;

    /// <summary>
    /// What the provider's <c>error</c> says: its message, its code — <c>type</c> in Anthropic's shape, else <c>code</c> when it
    /// is a word — and, when a metering gateway refused (a <c>tokenway</c> object beside the provider's own error), the
    /// gateway's code instead and its <c>max_affordable_max_tokens</c>.
    /// </summary>
    private static (string? Message, string? Code, long? AffordableMaxTokens) Read(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return default;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return default;

            var message = error.TryGetProperty("message", out var said) && said.ValueKind == JsonValueKind.String && said.GetString() is { Length: > 0 } text
                ? Bounded(text) : null;
            var code = (root.TryGetProperty("type", out var shape) && shape.ValueEquals("error") ? Word(error, "type") : null) ?? Word(error, "code");
            long? affordable = null;
            if (root.TryGetProperty("tokenway", out var gateway) && gateway.ValueKind == JsonValueKind.Object)
            {
                code = Word(gateway, "code") ?? code;
                affordable = gateway.TryGetProperty("max_affordable_max_tokens", out var most) && most.ValueKind == JsonValueKind.Number && most.TryGetInt64(out var tokens) && tokens >= 0 ? tokens : null;
            }

            return (message, code, affordable);
        }
        catch (JsonException)
        {
            // Not the shape providers refuse in: the status alone is the fact.
        }

        return default;

        static string? Word(JsonElement error, string name) =>
            error.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 64 } word ? word : null;
    }

    private static string? Bounded(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= MaxMessage ? text : text[..MaxMessage] + "…";
}
