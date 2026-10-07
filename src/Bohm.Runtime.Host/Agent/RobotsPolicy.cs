using System.Collections.Concurrent;
using System.Text;

namespace Bohm.Runtime.Host.Agent;

/// <summary>
/// Whether the browser's agent may open an address on its own: the site's robots.txt, fetched the way
/// RFC 9309 says and kept for a day per origin. A file that is not there (any 4xx) allows everything; a
/// server error or no answer allows nothing — the site may be saying no, and nobody can tell. What a
/// person opens is not asked here: this is for loads the agent chose.
/// </summary>
/// <remarks>
/// The request carries no cookies and nothing of the person's: the file is public. Each fetch is counted
/// as fetched from the site's host.
/// </remarks>
internal sealed class RobotsPolicy(IHttpClientFactory http, Egress egress, TimeProvider clock)
{
    /// <summary>The product token the agent's rules are looked up by; a site's <c>*</c> rules apply when no group names it.</summary>
    public const string ProductToken = "Bohm-Agent";

    /// <summary>The name of the HTTP client robots.txt is fetched with.</summary>
    public const string ClientName = "robots";

    /// <summary>How long a fetched file is kept (RFC 9309 asks crawlers not to keep one for longer than a day).</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);

    /// <summary>How long a server error or no answer is kept: a short outage should not keep a site closed for a day.</summary>
    public static readonly TimeSpan KeepFailure = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (Fetched File, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the site of <paramref name="address"/> says about the agent opening it.</summary>
    public async Task<SiteVerdict> CheckAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || address.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only web addresses have a robots.txt.", nameof(address));

        var robots = new Uri(address.GetLeftPart(UriPartial.Authority) + "/robots.txt");
        var key = robots.AbsoluteUri;
        var now = clock.GetUtcNow();
        if (!_cache.TryGetValue(key, out var kept) || now - kept.At >= (kept.File.Failure is null ? Keep : KeepFailure))
        {
            kept = (await FetchAsync(robots, cancellationToken).ConfigureAwait(false), now);
            _cache[key] = kept;
        }

        if (kept.File.Failure is { } failure) return new SiteVerdict(false, failure, null, robots.AbsoluteUri, null);
        var verdict = kept.File.Rules.Check(ProductToken, address.PathAndQuery);
        return new SiteVerdict(verdict.Allowed, verdict.Allowed ? null : "disallowed", verdict.Rule, robots.AbsoluteUri, verdict.CrawlDelay);
    }

    private async Task<Fetched> FetchAsync(Uri robots, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.CreateClient(ClientName).GetAsync(robots, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            egress.Fetched(robots.Host, 1);
            var status = (int)response.StatusCode;
            // 4xx: no file. A redirect still standing after the client followed five is the same — RFC 9309 lets it count as unavailable.
            if (status is (>= 300 and < 500)) return new Fetched(RobotsTxt.Empty, null);
            if (status is < 200 or >= 500) return new Fetched(RobotsTxt.Empty, "server-error");
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[RobotsTxt.MaxBytes];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false)) > 0) length += read;
            return new Fetched(RobotsTxt.Parse(Encoding.UTF8.GetString(buffer, 0, length)), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new Fetched(RobotsTxt.Empty, "unreachable");
        }
    }

    private sealed record Fetched(RobotsTxt Rules, string? Failure);
}

/// <summary>
/// The answer for one address: whether the agent may open it, why not (<c>disallowed</c>, <c>server-error</c>
/// or <c>unreachable</c>), the rule that decided it, where the rules were read and the crawl delay the site asks for.
/// </summary>
internal sealed record SiteVerdict(bool Allowed, string? Reason, string? Rule, string RobotsUrl, double? CrawlDelay);
