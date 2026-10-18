using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Authway.Native;
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
internal sealed class CloudAccount(RuntimeHostOptions options, ICredentialVault vault, IHttpClientFactory clients) : IDisposable
{
    /// <summary>The provider id a choice of the account is remembered with.</summary>
    public const string ProviderId = "bohm";

    /// <summary>Where the signed-in session is kept — its refresh token among it, so in the vault.</summary>
    public const string VaultName = "cloud/session";

    /// <summary>The environment variable the cloud's address is read from.</summary>
    public const string Variable = "BOHM_CLOUD";

    /// <summary>The environment variable a client id other than <see cref="DefaultClientId"/> is read from (a test identity service).</summary>
    public const string ClientIdVariable = "BOHM_CLOUD_CLIENT_ID";

    /// <summary>Bohm's own client at the identity service: a public client signing in through the loopback (RFC 8252).</summary>
    public const string DefaultClientId = "bohm-desktop";

    /// <summary>How long a sign-in waits for the browser to come back.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NativeClient? _client;
    private PendingSignIn? _pending;

    /// <summary>The cloud's base address, ending in «/».</summary>
    public Uri? Endpoint => options.CloudEndpoint;

    /// <summary>Whether the account can be chosen at all — not whether it is signed in.</summary>
    public bool Configured => Endpoint is not null;

    /// <summary>Whether someone is signed in on this computer — told from the kept session, without asking anyone.</summary>
    public bool SignedIn => vault.Read(VaultName) is { Length: > 0 };

    /// <summary>The host requests go to — what they are counted as sent to.</summary>
    public string? Host => Endpoint?.Authority;

    /// <summary>
    /// The account's access token, refreshed first when it is about to end — or why there is none: <c>signedOut</c> (no
    /// session, or the identity service no longer takes it: sign in again) or <c>unreachable</c>. This process alone
    /// refreshes the session: the identity service gives a new refresh token on each use and ends the session when an old
    /// one is used again, so two refreshing at once — two requests here, or the shell beside — would sign the person out.
    /// </summary>
    public async Task<(string? Token, string? Problem)> TokenAsync(CancellationToken cancellationToken)
    {
        if (!SignedIn) return (null, "signedOut");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await ClientAsync(cancellationToken).ConfigureAwait(false) is not { } client) return (null, "unreachable");
            return await client.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false) is { } token ? (token, null) : (null, "signedOut");
        }
        catch (Exception exception) when (exception is NativeSignInException or HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, "unreachable");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts signing in: the address the person opens to sign in at the identity service, which comes back to a port on
    /// this computer (<see cref="FinishSignInAsync"/>) — or why not: <c>unreachable</c> (the cloud does not name its
    /// identity service, or it cannot be reached). A sign-in already under way is given up.
    /// </summary>
    public async Task<(Uri? Url, string? Problem)> BeginSignInAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _pending, null)?.Dispose();
            if (await ClientAsync(cancellationToken).ConfigureAwait(false) is not { } client) return (null, "unreachable");
            var pending = await client.BeginSignInAsync(cancellationToken).ConfigureAwait(false);
            _pending = pending;
            return (pending.Url, null);
        }
        catch (Exception exception) when (exception is NativeSignInException or HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, "unreachable");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Waits for the sign-in begun with <see cref="BeginSignInAsync"/> to come back, and keeps the session: <see langword="null"/>
    /// when signed in, or why not — <c>noSignIn</c> (none under way), <c>denied</c> (the person declined, or the identity
    /// service refused), <c>timedOut</c>, <c>cancelled</c> (a new sign-in began, or signing out, meanwhile) or <c>unreachable</c>.
    /// </summary>
    public async Task<string?> FinishSignInAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _pending) is not { } pending) return "noSignIn";
        try
        {
            await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (NativeSignInException exception)
        {
            return exception.Failure switch
            {
                NativeSignInFailure.Denied => "denied",
                NativeSignInFailure.TimedOut => "timedOut",
                _ => "unreachable",
            };
        }
        catch (HttpRequestException)
        {
            return "unreachable";
        }
        catch (Exception exception) when (exception is ObjectDisposedException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return "cancelled";   // given up here: a new sign-in began, or the person signed out meanwhile
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _pending, null, pending) == pending) pending.Dispose();
        }
    }

    /// <summary>
    /// Signs out: the session's tokens are revoked at the identity service and the session is forgotten here — forgotten
    /// even when the service cannot be reached (then the tokens end on their own).
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _pending, null)?.Dispose();
            if (SignedIn && await ClientAsync(cancellationToken).ConfigureAwait(false) is { } client)
                await client.SignOutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is NativeSignInException or HttpRequestException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
        }
        finally
        {
            vault.Delete(VaultName);
            _gate.Release();
        }
    }

    /// <summary>
    /// The client signing in at the identity service the cloud names — its OAuth 2.0 Protected Resource Metadata (RFC 9728,
    /// <c>authorization_servers</c>) — learned once; <see langword="null"/> when the cloud does not name one. Called under <see cref="_gate"/>.
    /// </summary>
    private async Task<NativeClient?> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { } known) return known;
        if (Endpoint is not { } endpoint) return null;
        var http = clients.CreateClient(nameof(CloudAccount));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(BalanceTimeout);
        var metadata = new Uri(endpoint, "/.well-known/oauth-protected-resource" + endpoint.AbsolutePath.TrimEnd('/'));
        using var response = await http.GetAsync(metadata, limit.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false));
        if (answer.RootElement.ValueKind != JsonValueKind.Object
            || !answer.RootElement.TryGetProperty("authorization_servers", out var servers) || servers.ValueKind != JsonValueKind.Array
            || servers.EnumerateArray().FirstOrDefault() is not { ValueKind: JsonValueKind.String } first
            || EndpointOf(first.GetString()) is null)
            return null;

        return _client = new NativeClient(
            new NativeClientOptions(first.GetString()!, options.CloudClientId ?? DefaultClientId) { StoreKey = VaultName },
            new VaultTokenStore(vault), http);
    }

    public void Dispose()
    {
        _pending?.Dispose();
        _client?.Dispose();
        _gate.Dispose();
    }

    /// <summary>The session kept in the vault — where the person's other secrets are.</summary>
    private sealed class VaultTokenStore(ICredentialVault vault) : ITokenStore
    {
        public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult(vault.Read(key));

        public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            vault.Write(key, value);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(string key, CancellationToken cancellationToken)
        {
            vault.Delete(key);
            return ValueTask.CompletedTask;
        }
    }

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
        var (token, notSignedIn) = await TokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return (null, notSignedIn);
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
        var (token, notSignedIn) = await TokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return (null, null, notSignedIn);
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
        var (token, notSignedIn) = await TokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return (null, notSignedIn);
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
        var (token, notSignedIn) = await TokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return notSignedIn;
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
        var (token, notSignedIn) = await TokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is null) return (null, notSignedIn);
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
