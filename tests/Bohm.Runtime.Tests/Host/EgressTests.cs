using Bohm.Runtime.Host;

namespace Bohm.Runtime.Tests.Host;

public sealed class EgressTests
{
    [Fact]
    public void Kinds_are_counted_apart_by_host_and_a_refusal_once_per_application()
    {
        var egress = new Egress(TimeProvider.System);
        egress.Sent("api.openai.com");
        egress.Sent("api.openai.com");
        egress.Fetched("cdn.jsdelivr.net", 3);
        egress.Blocked("app-a", "r.jina.ai");
        egress.Blocked("app-a", "R.JINA.AI");
        egress.Blocked("app-b", "r.jina.ai");

        var snapshot = egress.Snapshot();

        Assert.Equal([new HostCount("api.openai.com", 2)], snapshot.Sent);
        Assert.Equal([new HostCount("cdn.jsdelivr.net", 3)], snapshot.Fetched);
        Assert.Equal([new HostCount("r.jina.ai", 2)], snapshot.Blocked);
    }

    [Fact]
    public void A_new_run_starts_empty()
    {
        var snapshot = new Egress(TimeProvider.System).Snapshot();

        Assert.Empty(snapshot.Sent);
        Assert.Empty(snapshot.Fetched);
        Assert.Empty(snapshot.Blocked);
    }
}
