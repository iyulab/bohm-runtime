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

    [Fact]
    public void Used_days_first_use_and_every_recorded_day_come_back_in_order()
    {
        var log = Open(
            """{"date":"2026-09-10","signal":"opened"}""",
            """{"date":"2026-09-10","signal":"input"}""",
            """{"date":"2026-09-03","signal":"opened"}""",
            """{"date":"2026-09-03","signal":"input"}""",
            """{"date":"2026-09-03","signal":"wrote"}""",
            """{"date":"2026-09-05","signal":"opened"}""",
            """{"date":"2026-09-06","event":"load-error"}""",
            """{"date":"2026-09-06","event":"load-error"}""");

        Assert.Equal([new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 10)], log.UsedDays);
        Assert.Equal(new DateOnly(2026, 9, 3), log.FirstUsedOn);
        Assert.Equal(
            [
                new UsageDay(new DateOnly(2026, 9, 3), true, true, true, 0),
                new UsageDay(new DateOnly(2026, 9, 5), true, false, false, 0),
                new UsageDay(new DateOnly(2026, 9, 6), false, false, false, 2),
                new UsageDay(new DateOnly(2026, 9, 10), true, true, false, 0),
            ],
            log.Days);
    }

    [Fact]
    public void Revisions_are_read_back_and_new_ones_are_kept()
    {
        var log = Open("""{"date":"2026-09-10","event":"revised"}""", """{"date":"2026-09-11","event":"reverted"}""");
        log.RecordRevision(reverted: false);

        Assert.Equal(3, log.Revisions.Count);
        Assert.Equal(new RevisionEvent(new DateOnly(2026, 9, 10), false), log.Revisions[0]);
        Assert.True(log.Revisions[1].Reverted);
        Assert.Equal(3, UsageLog.Open(Path.Combine(_dir, "usage.ndjson")).Revisions.Count);
    }
}
