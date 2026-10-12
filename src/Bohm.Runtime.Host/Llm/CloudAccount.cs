using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Edit;
using IronHive.Extensions.AI;
using IronHive.Providers.Anthropic;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The person's account with the Bohm cloud: models the account pays for with its credits. The cloud tells where the
/// account's AI calls go — a metering gateway that speaks each provider's own API and takes the account's access token
/// in place of a provider key — and the calls go there directly. There is one only when the runtime was started with
/// the cloud's address (<see cref="RuntimeHostOptions.CloudEndpoint"/>); without it nothing is ever sent there and the
/// account cannot be chosen.
/// </summary>
/// <remarks>
/// Choosing the account sends what an agent works on to that gateway, and from there to the model's provider — so,
/// like choosing a provider, it is only ever the person's choice, and every request is counted as sent to the gateway.
/// </remarks>
internal sealed class CloudAccount(RuntimeHostOptions options, ICredentialVault vault, IHttpClientFactory clients)
{
    /// <summary>The provider id a choice of the account is remembered with.</summary>
    public const string ProviderId = "bohm";

    /// <summary>Where the account's access token is kept.</summary>
    public const string VaultName = "cloud/account";

    /// <summary>The environment variable the cloud's address is read from.</summary>
    public const string Variable = "BOHM_CLOUD";

    /// <summary>The cloud's base address, ending in «/».</summary>
    public Uri? Endpoint => options.CloudEndpoint;

    /// <summary>Whether the account can be chosen at all — not whether it is signed in.</summary>
    public bool Configured => Endpoint is not null;

    /// <summary>The account's access token, or <see langword="null"/> when none is kept.</summary>
    public string? Token => vault.Read(VaultName) is { Length: > 0 } token ? token : null;

    /// <summary>The host requests go to — what they are counted as sent to.</summary>
    public string? Host => Endpoint?.Authority;

    /// <summary>
    /// The cloud's address from <paramref name="value"/>: an absolute https address, or http on this computer;
    /// <see langword="null"/> when there is none or it cannot be used.
    /// </summary>
    public static Uri? EndpointOf(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var address)
        && (address.Scheme == Uri.UriSchemeHttps || (address.Scheme == Uri.UriSchemeHttp && address.IsLoopback))
        && address.UserInfo.Length == 0 && string.IsNullOrEmpty(address.Query) && string.IsNullOrEmpty(address.Fragment)
            ? address.AbsoluteUri.EndsWith('/') ? address : new Uri(address.AbsoluteUri + "/")
            : null;

    /// <summary>How long asking the cloud for the account's balance may take.</summary>
    public static readonly TimeSpan BalanceTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The account's credits as the cloud keeps them, asked with its access token — or why they could not be told:
    /// <c>signedOut</c> (no token kept), <c>unauthorized</c> (the cloud no longer takes the token: sign in again) or
    /// <c>unreachable</c> (no answer, or not one that says it). Asked only when the person looks at the account;
    /// it carries the token and nothing of an application's, so it is not counted as an application's data sent.
    /// </summary>
    public async Task<(long? Balance, string? Problem)> BalanceAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (Token is not { } token) return (null, "signedOut");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(BalanceTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint!, "me/balance"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (null, "unauthorized");
            if (!response.IsSuccessStatusCode) return (null, "unreachable");
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false));
            return answer.RootElement.ValueKind == JsonValueKind.Object
                && answer.RootElement.TryGetProperty("balance", out var balance) && balance.ValueKind == JsonValueKind.Number
                && balance.TryGetInt64(out var credits)
                    ? (credits, null)
                    : (null, "unreachable");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, "unreachable");
        }
    }

    /// <summary>
    /// Keeps <paramref name="page"/> — a saved screen, already sealed by the shell — as a read-only link with the cloud, under the
    /// account: its address and when it ends, or why not: <c>signedOut</c>, <c>unauthorized</c>, <c>tooLarge</c>,
    /// <c>quotaReached</c> (the account's live links) or <c>unreachable</c>. The page leaves this computer, so it is counted as sent.
    /// </summary>
    public async Task<(Uri? Link, DateTimeOffset? ExpiresAt, string? Problem)> ShareAsync(HttpClient http, byte[] page, Egress egress, CancellationToken cancellationToken)
    {
        if (Token is not { } token) return (null, null, "signedOut");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ShareTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint!, "me/links"))
            {
                Content = new ByteArrayContent(page) { Headers = { ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" } } },
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            egress.Sent(Host!);
            using var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden: return (null, null, "unauthorized");
                case HttpStatusCode.RequestEntityTooLarge: return (null, null, "tooLarge");
                case HttpStatusCode.Conflict: return (null, null, "quotaReached");
                case HttpStatusCode.Created or HttpStatusCode.OK: break;
                default: return (null, null, "unreachable");
            }
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false));
            var root = answer.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } path && Uri.TryCreate(Endpoint, path, out var link)
                && link.Authority == Endpoint!.Authority
                    ? (link, root.TryGetProperty("expiresAt", out var ends) && ends.TryGetDateTimeOffset(out var at) ? at : null, null)
                    : (null, null, "unreachable");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, null, "unreachable");
        }
    }

    /// <summary>How long keeping a page with the cloud may take.</summary>
    public static readonly TimeSpan ShareTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A link the account keeps with the cloud: its id, its address (on the cloud's host), when it was made and when it ends, and the page's size.</summary>
    public sealed record Link(string Id, Uri Url, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt, long? Bytes);

    /// <summary>
    /// The account's live links, newest first, as the cloud keeps them — or why they could not be told: <c>signedOut</c>,
    /// <c>unauthorized</c> or <c>unreachable</c>. An entry the cloud describes incompletely, or whose address is not on the
    /// cloud's host, is left out. Like the balance it carries the token and nothing of an application's, so it is not counted as sent.
    /// </summary>
    public async Task<(IReadOnlyList<Link>? Links, string? Problem)> LinksAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (Token is not { } token) return (null, "signedOut");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(BalanceTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint!, "me/links"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (null, "unauthorized");
            if (!response.IsSuccessStatusCode) return (null, "unreachable");
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false));
            if (answer.RootElement.ValueKind != JsonValueKind.Array) return (null, "unreachable");
            var links = new List<Link>();
            foreach (var item in answer.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } name
                    && item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && url.GetString() is { Length: > 0 } path
                    && Uri.TryCreate(Endpoint, path, out var address) && address.Authority == Endpoint!.Authority)
                {
                    links.Add(new Link(name, address, DateOf(item, "createdAt"), DateOf(item, "expiresAt"),
                        item.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var size) ? size : null));
                }
            }

            return (links, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, "unreachable");
        }

        static DateTimeOffset? DateOf(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var at) ? at : null;
    }

    /// <summary>
    /// Takes the link <paramref name="id"/> back: it stops opening for everyone at once. <see langword="null"/> when it is gone —
    /// taken back now, or already not live (the cloud does not tell an ended link from one that never was, and either way it no
    /// longer opens) — or why not: <c>signedOut</c>, <c>unauthorized</c> or <c>unreachable</c>. Not counted as sent.
    /// </summary>
    public async Task<string?> TakeBackAsync(HttpClient http, string id, CancellationToken cancellationToken)
    {
        if (Token is not { } token) return "signedOut";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(BalanceTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(Endpoint!, "me/links/" + Uri.EscapeDataString(id)));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, limit.Token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "unauthorized",
                HttpStatusCode.NotFound => null,
                _ when response.IsSuccessStatusCode => null,
                _ => "unreachable",
            };
        }
        catch (Exception exception) when (exception is HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return "unreachable";
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> can be a link's id as the cloud gives them: letters, digits, «-» and «_», at most 64 —
    /// so what the shell passes on cannot reach another of the cloud's paths.
    /// </summary>
    public static bool IsLinkId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Where the account's AI calls go — the Anthropic Messages path of the gateway the cloud names (<c>GET me</c> →
    /// <c>ai.anthropic</c>) — or why it could not be told: <c>signedOut</c>, <c>unauthorized</c> or <c>unreachable</c>.
    /// Only an https address, or http on this computer, is taken. Carries the token and nothing of an application's.
    /// </summary>
    public async Task<(Uri? Anthropic, string? Problem)> AiAsync(CancellationToken cancellationToken)
    {
        if (Token is not { } token) return (null, "signedOut");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(BalanceTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint!, "me"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient(nameof(CloudAccount)).SendAsync(request, limit.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return (null, "unauthorized");
            if (!response.IsSuccessStatusCode) return (null, "unreachable");
            using var answer = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false));
            return answer.RootElement.ValueKind == JsonValueKind.Object
                && answer.RootElement.TryGetProperty("ai", out var ai) && ai.ValueKind == JsonValueKind.Object
                && ai.TryGetProperty("anthropic", out var path) && path.ValueKind == JsonValueKind.String
                && EndpointOf(path.GetString()) is { } anthropic
                    ? (anthropic, null)
                    : (null, "unreachable");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, "unreachable");
        }
    }

    /// <summary>
    /// A client for <paramref name="model"/> paid for by the account whose access token is <paramref name="token"/>: Anthropic's
    /// Messages API at <paramref name="anthropic"/> (<see cref="AiAsync"/>) — the API that carries a model's thinking and the prompt cache.
    /// </summary>
    public static IChatClient Client(string model, string token, Uri anthropic) =>
        new CreditsChatClient(new AnthropicMessageGenerator(new AnthropicConfig { ApiKey = token, BaseUrl = anthropic.AbsoluteUri.TrimEnd('/') })
            .AsChatClient(model, "anthropic"));
}

/// <summary>
/// A request the account's credits cannot hold at its output limit (<c>hold_rejected</c>, which says the largest limit
/// they can hold — <c>max_affordable_max_tokens</c>) is asked once more with that limit — a shorter answer rather than
/// none. The account's other refusals (<c>insufficient_balance</c>, <c>cap_reached</c>, <c>wallet_suspended</c>) are not
/// asked again: waiting does not help.
/// </summary>
internal sealed class CreditsChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <summary>The smallest output limit asked once more with: below it a model that thinks has no room left to answer.</summary>
    public const int MinOutputTokens = 2048;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatOptions? smaller;
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when ((smaller = Smaller(exception, options)) is not null)
        {
            return await base.GetResponseAsync(messages, smaller, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatOptions? smaller = null;
        await using (var first = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken))
        {
            // A refusal comes before anything is streamed, so asking again repeats nothing the caller has seen.
            var any = false;
            try
            {
                any = await first.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when ((smaller = Smaller(exception, options)) is not null)
            {
            }

            if (smaller is null)
            {
                if (!any) yield break;
                yield return first.Current;
                while (await first.MoveNextAsync().ConfigureAwait(false)) yield return first.Current;
                yield break;
            }
        }

        await foreach (var update in base.GetStreamingResponseAsync(messages, smaller, cancellationToken).ConfigureAwait(false))
            yield return update;
    }

    /// <summary>The options with the output limit the credits can hold, when <paramref name="exception"/> says so and it is worth asking.</summary>
    private static ChatOptions? Smaller(Exception exception, ChatOptions? options)
    {
        if (ProviderRefusal.Of(exception) is not { Code: "hold_rejected", AffordableMaxTokens: { } affordable }
            || affordable < MinOutputTokens || affordable >= (options?.MaxOutputTokens ?? int.MaxValue))
            return null;

        var smaller = options?.Clone() ?? new ChatOptions();
        smaller.MaxOutputTokens = (int)affordable;
        return smaller;
    }
}

/// <summary>
/// A client whose every request fails as an unreachable provider's does (no status) — for the account when the cloud
/// could not tell where its AI calls go, so the person sees «could not reach» and tries again, as with any provider.
/// </summary>
internal sealed class UnreachableChatClient(string why) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new HttpRequestException(why);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new HttpRequestException(why);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
