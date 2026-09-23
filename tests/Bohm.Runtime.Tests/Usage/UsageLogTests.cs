using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Tests.Usage;

public sealed class UsageLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bohm-usage-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private UsageLog Open(params string[] lines)
    {
        var path = Path.Combine(_dir, "usage.ndjson");
        File.WriteAllLines(path, lines);
        return UsageLog.Open(path);
    }

    [Fact]
    public void The_last_used_day_is_the_latest_day_with_both_opening_and_input()
    {
        var log = Open(
            """{"date":"2026-09-01","signal":"opened"}""",
            """{"date":"2026-09-01","signal":"input"}""",
            """{"date":"2026-09-10","signal":"opened"}""",
            """{"date":"2026-09-10","signal":"input"}""",
            """{"date":"2026-09-20","signal":"opened"}""");

        Assert.Equal(new DateOnly(2026, 9, 10), log.LastUsedOn);
    }

    [Fact]
    public void Opening_alone_or_nothing_at_all_is_never_used()
    {
        Assert.Null(Open("""{"date":"2026-09-20","signal":"opened"}""", """{"date":"2026-09-20","event":"load-error"}""").LastUsedOn);
        Assert.Null(UsageLog.Open(Path.Combine(_dir, "none.ndjson")).LastUsedOn);
    }
}
