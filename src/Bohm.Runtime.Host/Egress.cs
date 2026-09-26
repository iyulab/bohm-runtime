namespace Bohm.Runtime.Host;

/// <summary>
/// What left this computer since the runtime started, by host — so the person can see it, not take
/// it on trust. Three kinds, because they mean different things: <b>sent</b> carries an application's
/// data out (AI requests relayed to a provider, and an application's source sent with a proposal to a chosen provider); <b>fetched</b> asks for code and brings it in (an
/// application's libraries, cached at adoption) — the request leaves, the application's data does
/// not; <b>blocked</b> is a connection an application tried and the runtime refused — nothing left.
/// </summary>
/// <remarks>Kept in memory only: it describes this run. Hosts only — never paths, bodies or keys.</remarks>
internal sealed class Egress(TimeProvider clock)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _sent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _fetched = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string App, string Host)> _blockedSeen = [];

    public DateTimeOffset Since { get; } = clock.GetUtcNow();

    /// <summary>One request carrying an application's data went to <paramref name="host"/>.</summary>
    public void Sent(string host) => Add(_sent, host, 1);

    /// <summary><paramref name="files"/> files of code were fetched from <paramref name="host"/>.</summary>
    public void Fetched(string host, int files) => Add(_fetched, host, files);

    /// <summary>Application <paramref name="appId"/> tried to reach <paramref name="host"/> and was refused. Counted once per application.</summary>
    public void Blocked(string appId, string host)
    {
        lock (_lock)
        {
            if (_blockedSeen.Add((appId, host.ToLowerInvariant()))) _blocked[host] = _blocked.GetValueOrDefault(host) + 1;
        }
    }

    public EgressSnapshot Snapshot()
    {
        lock (_lock) return new(Since, List(_sent), List(_fetched), List(_blocked));
    }

    private void Add(Dictionary<string, int> counts, string host, int n)
    {
        lock (_lock) counts[host] = counts.GetValueOrDefault(host) + n;
    }

    private static List<HostCount> List(Dictionary<string, int> counts) =>
        counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => new HostCount(c.Key, c.Value)).ToList();
}

/// <summary>A host and how many times.</summary>
internal sealed record HostCount(string Host, int Count);

/// <summary>What left this computer since <see cref="Since"/>, by kind (see <see cref="Egress"/>).</summary>
internal sealed record EgressSnapshot(DateTimeOffset Since, IReadOnlyList<HostCount> Sent, IReadOnlyList<HostCount> Fetched, IReadOnlyList<HostCount> Blocked);
