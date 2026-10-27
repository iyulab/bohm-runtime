using System.Net;
using System.Net.Sockets;
using IronHive.Abstractions.Exceptions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// What a provider answered to one short request in its declared format. The words are for the person who
/// set the provider up: they say where it went wrong, never what the provider's reply held.
/// </summary>
/// <param name="Result"><c>ok</c> (it answered), <c>key-refused</c> (401 or 403), <c>not-found</c> (404 — often an address whose path is not where the declared
/// format is served, or a format the server does not speak), <c>bad-request</c> (400 or 422 — the server did not take the request's shape or the model's name),
/// <c>refused</c> (another status) or <c>unreachable</c> (no answer within the time allowed).</param>
/// <param name="Status">The HTTP status of the refusal, when there was one.</param>
/// <param name="Unreached">Why nothing answered, when that is the result: <c>host-not-found</c>, <c>connection-refused</c> or <c>no-answer</c>.</param>
internal sealed record ProviderCheck(string Result, int? Status = null, string? Unreached = null)
{
    /// <summary>How long a check waits before it calls the provider unreachable.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The one short question: any provider answers it, in any format, and nothing of anyone's goes with it.</summary>
    public const string Question = CompanyModel.ThinkingQuestion;

    /// <summary>
    /// Asks <paramref name="modelId"/> of <paramref name="provider"/> the one short question through
    /// <paramref name="client"/> — the client built from the provider's declaration, so the format, the path and the way the
    /// key travels are the ones the person's chat would use.
    /// </summary>
    public static async Task<ProviderCheck> RunAsync(IChatClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            // A small output bound: a model that thinks first may end without any text, and it still answered.
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, Question)], new ChatOptions { MaxOutputTokens = 32 }, timeout.Token).ConfigureAwait(false);
            return new("ok");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("unreachable", Unreached: "no-answer");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Classify(exception);
        }
    }

    /// <summary>What the provider's refusal (or silence) says.</summary>
    public static ProviderCheck Classify(Exception exception)
    {
        int? status = exception switch
        {
            HttpRequestException { StatusCode: { } code } => (int)code,
            ProviderResponseException { EquivalentStatusCode: { } code } => (int)code,
            BillingException { StatusCode: { } code } => (int)code,
            RateLimitException => 429,
            ContextOverflowException => 400,
            _ => null,
        };

        if (status is { } refused)
        {
            return refused switch
            {
                401 or 403 => new("key-refused", refused),
                404 => new("not-found", refused),
                400 or 422 => new("bad-request", refused),
                _ => new("refused", refused),
            };
        }

        // No status: nothing answered, or what answered was not the format that was declared.
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket)
                return new("unreachable", Unreached: socket.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData => "host-not-found",
                    SocketError.ConnectionRefused => "connection-refused",
                    _ => "no-answer",
                });
            if (inner is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
                return new("unreachable", Unreached: "host-not-found");
            if (inner is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError })
                return new("unreachable", Unreached: "connection-refused");
        }

        return exception is HttpRequestException or TimeoutException
            ? new("unreachable", Unreached: "no-answer")
            : new("refused");   // it answered, but not in the declared format
    }
}
