using Bohm.Runtime.Sources;
using Bohm.Runtime.Tests.Storage;

namespace Bohm.Runtime.Tests.Sources;

/// <summary>
/// A source read on a schedule the person set: how often (no shorter than a minute), when it is due, what a reading
/// that finds the same rows keeps (nothing new), and what each kind of failure does to the schedule — waits longer,
/// tries once more, or stops until the person reads it once.
/// </summary>
public sealed class AppSourcesScheduleTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
    private static readonly SourceRule Prices = new("https://shop.example/items", "#prices", ["Item", "Price"]);
    private static readonly TimeSpan Five = TimeSpan.FromMinutes(5);

    private readonly string _folder = Directory.CreateTempSubdirectory("bohm-sources-schedule-").FullName;
    private readonly ManualClock _clock = new(Start);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AppSources Open() => AppSources.Open(_folder, _clock);

    private static string[][] Rows(params string[][] rows) => rows;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private async Task<AppSources> GrantedAsync()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: true, Cancel);
        return sources;
    }

    private static void AssertAbout(DateTimeOffset from, TimeSpan every, DateTimeOffset next) =>
        Assert.InRange(next, from + every * 0.9, from + every * 1.1);

    [Fact]
    public async Task A_schedule_is_part_of_the_permission_kept_after_reopening_and_no_shorter_than_a_minute()
    {
        var sources = await GrantedAsync();

        var (tooOften, _) = await sources.ScheduleAsync("prices", TimeSpan.FromSeconds(59), Cancel);
        var (set, source) = await sources.ScheduleAsync("prices", AppSources.MinimumEvery, Cancel);

        Assert.Equal(ScheduleOutcome.TooOften, tooOften);
        Assert.Equal(ScheduleOutcome.Scheduled, set);
        Assert.Equal(AppSources.MinimumEvery, source!.Grant!.Every);
        Assert.Equal(AppSources.MinimumEvery, Assert.Single(await Open().ListAsync(Cancel)).Grant!.Every);
        Assert.Equal(TimeSpan.FromMinutes(5), AppSources.DefaultEvery);
    }

    [Fact]
    public async Task Only_a_permitted_source_can_have_a_schedule()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: false, Cancel);

        Assert.Equal(ScheduleOutcome.NotGranted, (await sources.ScheduleAsync("prices", Five, Cancel)).Outcome);
        Assert.Equal(ScheduleOutcome.Unknown, (await sources.ScheduleAsync("stock", Five, Cancel)).Outcome);
    }

    [Fact]
    public async Task Turned_on_it_is_due_at_once_when_the_last_reading_is_older_than_the_schedule()
    {
        var sources = await GrantedAsync();
        await sources.RecordAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);
        _clock.Advance(TimeSpan.FromHours(2));

        await sources.ScheduleAsync("prices", Five, Cancel);

        Assert.Equal("prices", Assert.Single(await sources.DueAsync(Cancel)).Name);
    }

    [Fact]
    public async Task Turned_on_after_a_recent_reading_it_is_due_about_a_schedule_after_that_reading()
    {
        var sources = await GrantedAsync();
        await sources.RecordAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var (_, source) = await sources.ScheduleAsync("prices", Five, Cancel);

        AssertAbout(Start, Five, source!.Watch!.NextAt);
        Assert.Empty(await sources.DueAsync(Cancel));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(await sources.DueAsync(Cancel));
    }

    [Fact]
    public async Task A_scheduled_reading_that_finds_the_same_rows_keeps_nothing_new_but_counts_as_read()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);
        var (first, _) = await sources.RecordScheduledAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);
        _clock.Advance(Five);

        var (same, _) = await sources.RecordScheduledAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);
        _clock.Advance(Five);
        var (changed, _) = await sources.RecordScheduledAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.25"]), Cancel);

        Assert.Equal(RecordOutcome.Recorded, first);
        Assert.Equal(RecordOutcome.Unchanged, same);
        Assert.Equal(RecordOutcome.Recorded, changed);
        var readings = await sources.ReadingsAsync("prices", Cancel);
        Assert.Equal(["1.20", "1.25"], readings!.Select(r => r.Rows[0]["Price"]));
        var source = Assert.Single(await sources.ListAsync(Cancel));
        Assert.Equal(Start + Five * 2, source.LastReadAt);
        Assert.Equal(Start + Five * 2, source.Watch!.CheckedAt);
        AssertAbout(Start + Five * 2, Five, source.Watch.NextAt);
    }

    [Fact]
    public async Task A_reading_the_person_asked_for_is_kept_even_when_the_rows_are_the_same()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);
        await sources.RecordScheduledAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);

        var (outcome, _) = await sources.RecordAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);

        Assert.Equal(RecordOutcome.Recorded, outcome);
        Assert.Equal(2, (await sources.ReadingsAsync("prices", Cancel))!.Count);
    }

    [Fact]
    public async Task A_failure_that_passes_with_time_waits_longer_each_time_up_to_an_hour_and_a_reading_resets_it()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);

        var waits = new List<TimeSpan>();
        var began = _clock.GetUtcNow();
        for (var i = 0; i < 6; i++)
        {
            var failed = await sources.ReportFailureAsync("prices", SourceFailure.Transient, "timeout", cancellationToken: Cancel);
            waits.Add(failed!.Watch!.NextAt - _clock.GetUtcNow());
            Assert.False(failed.Watch.Halted);
            Assert.Equal(i + 1, failed.Watch.Failures);
            Assert.Equal(began, failed.Watch.FailingSince);   // when the failures in a row began, not the latest
            _clock.Advance(waits[^1]);
        }

        Assert.Equal([5, 10, 20, 40, 60, 60], waits.Select(w => w.TotalMinutes));
        await sources.RecordScheduledAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);
        var source = Assert.Single(await sources.ListAsync(Cancel));
        Assert.Equal(0, source.Watch!.Failures);
        Assert.Null(source.Watch.LastFailure);
        Assert.Null(source.Watch.FailingSince);
    }

    [Fact]
    public async Task A_busy_site_is_given_the_wait_it_asked_for()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);

        var failed = await sources.ReportFailureAsync("prices", SourceFailure.Transient, "429", TimeSpan.FromMinutes(30), Cancel);

        Assert.Equal(TimeSpan.FromMinutes(30), failed!.Watch!.NextAt - _clock.GetUtcNow());
    }

    [Fact]
    public async Task A_page_that_wants_the_person_stops_the_schedule_until_the_person_reads_it_once()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);

        var stopped = await sources.ReportFailureAsync("prices", SourceFailure.NeedsPerson, "sign-in", cancellationToken: Cancel);
        _clock.Advance(TimeSpan.FromHours(3));

        Assert.True(stopped!.Watch!.Halted);
        Assert.Equal("sign-in", stopped.Watch.Reason);
        Assert.Empty(await sources.DueAsync(Cancel));

        await sources.RecordAsync("prices", Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);   // the person signed in and read it
        var source = Assert.Single(await sources.ListAsync(Cancel));
        Assert.False(source.Watch!.Halted);
        Assert.Equal(Five, source.Grant!.Every);
        _clock.Advance(Five * 1.1);
        Assert.Single(await sources.DueAsync(Cancel));
    }

    [Fact]
    public async Task A_changed_table_is_tried_once_more_and_stops_on_a_second_in_a_row()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);

        var once = await sources.ReportFailureAsync("prices", SourceFailure.ShapeChanged, cancellationToken: Cancel);
        var twice = await sources.ReportFailureAsync("prices", SourceFailure.ShapeChanged, cancellationToken: Cancel);

        Assert.False(once!.Watch!.Halted);
        Assert.Equal(Five, once.Watch.NextAt - _clock.GetUtcNow());
        Assert.True(twice!.Watch!.Halted);
    }

    [Fact]
    public async Task A_page_outside_what_was_allowed_stops_at_once()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);

        var stopped = await sources.ReportFailureAsync("prices", SourceFailure.Refused, "too-many-rows", cancellationToken: Cancel);

        Assert.True(stopped!.Watch!.Halted);
        Assert.Equal(SourceFailure.Refused, stopped.Watch.LastFailure);
    }

    [Fact]
    public async Task Taking_back_the_permission_or_turning_it_off_ends_the_schedule()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);
        var (_, off) = await sources.ScheduleAsync("prices", null, Cancel);
        Assert.Null(off!.Grant!.Every);
        Assert.Null(off.Watch);

        await sources.ScheduleAsync("prices", Five, Cancel);
        await sources.RevokeAsync("prices", Cancel);
        _clock.Advance(TimeSpan.FromHours(1));

        var source = Assert.Single(await sources.ListAsync(Cancel));
        Assert.Null(source.Grant);
        Assert.Null(source.Watch);
        Assert.Empty(await sources.DueAsync(Cancel));
        Assert.Null(await sources.ReportFailureAsync("prices", SourceFailure.Transient, cancellationToken: Cancel));
    }

    [Fact]
    public async Task A_copy_without_data_leaves_the_schedule_behind_with_the_permission()
    {
        var sources = await GrantedAsync();
        await sources.ScheduleAsync("prices", Five, Cancel);
        var copyFolder = Directory.CreateTempSubdirectory("bohm-sources-copy-").FullName;
        try
        {
            await sources.CopyRulesAsync(copyFolder, Cancel);

            var copied = Assert.Single(await AppSources.Open(copyFolder, _clock).ListAsync(Cancel));
            Assert.Null(copied.Grant);
            Assert.Null(copied.Watch);
        }
        finally
        {
            Directory.Delete(copyFolder, recursive: true);
        }
    }

    [Fact]
    public async Task Sources_on_the_same_schedule_do_not_all_come_due_at_the_same_moment()
    {
        var sources = Open();
        var nexts = new List<DateTimeOffset>();
        foreach (var name in new[] { "prices", "stock", "orders", "news" })
        {
            await sources.DeclareAsync(name, Prices, granted: true, Cancel);
            await sources.RecordAsync(name, Prices.Site, Prices.Columns, Rows(["Pen", "1.20"]), Cancel);
            nexts.Add((await sources.ScheduleAsync(name, Five, Cancel)).Source!.Watch!.NextAt);
        }

        Assert.All(nexts, n => AssertAbout(Start, Five, n));
        Assert.True(nexts.Distinct().Count() > 1);
    }
}
