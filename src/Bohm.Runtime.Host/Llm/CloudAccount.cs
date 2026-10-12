using System.Runtime.CompilerServices;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Edit;
using IronHive.Extensions.AI;
using IronHive.Providers.Anthropic;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The person's account with the Bohm cloud: models the account pays for with its credits, reached through the
/// cloud's relay, which speaks each provider's own API, with the account's access token in place of a provider key.
/// There is one only when the runtime was started with the cloud's address (<see cref="RuntimeHostOptions.CloudEndpoint"/>);
/// without it nothing is ever sent there and the account cannot be chosen.
/// </summary>
/// <remarks>
/// Choosing the account sends what an agent works on to the cloud, and from there to the model's provider — so,
/// like choosing a provider, it is only ever the person's choice, and every request is counted as sent to the cloud.
/// </remarks>
internal sealed class CloudAccount(RuntimeHostOptions options, ICredentialVault vault)
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

    /// <summary>
    /// A client for <paramref name="model"/> paid for by the account whose access token is <paramref name="token"/>: Anthropic's
    /// Messages API at the cloud's relay — the API that carries a model's thinking and the prompt cache.
    /// </summary>
    public IChatClient Client(string model, string token) =>
        new CreditsChatClient(new AnthropicMessageGenerator(new AnthropicConfig { ApiKey = token, BaseUrl = new Uri(Endpoint!, "anthropic").AbsoluteUri })
            .AsChatClient(model, "anthropic"));
}

/// <summary>
/// A request the account's credits cannot hold at its output limit (<c>hold_rejected</c>, which says the largest limit
/// they can hold) is asked once more with that limit — a shorter answer rather than none. The other refusals of the
/// account (its balance is spent, an application's monthly limit is reached) are not asked again: waiting does not help.
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
