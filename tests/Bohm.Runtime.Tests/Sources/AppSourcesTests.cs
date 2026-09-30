using Bohm.Runtime.Sources;
using Bohm.Runtime.Tests.Storage;

namespace Bohm.Runtime.Tests.Sources;

/// <summary>
/// The pages an application reads from: each source is a rule (where, which table, which columns)
/// and the person's permission for it; what was read is kept as a list of readings, oldest first.
/// </summary>
public sealed class AppSourcesTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly SourceRule Prices = new("https://shop.example/items", "#prices", ["Item", "Price"]);

    private readonly string _folder = Directory.CreateTempSubdirectory("bohm-sources-").FullName;
    private readonly ManualClock _clock = new(Start);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AppSources Open() => AppSources.Open(_folder, _clock);

    private static string[][] Rows(params string[][] rows) => rows;

    [Fact]
    public async Task A_declared_source_is_listed_with_its_rule_and_permission_after_reopening()
    {
        await Open().DeclareAsync("prices", Prices, granted: true, TestContext.Current.CancellationToken);

        var source = Assert.Single(await Open().ListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("prices", source.Name);
        Assert.Equal(Prices.Site, source.Rule.Site);
        Assert.Equal(Prices.Selector, source.Rule.Selector);
        Assert.Equal(Prices.Columns, source.Rule.Columns);
        Assert.Equal(new SourceGrant(Prices.Site, Start), source.Grant);
        Assert.Null(source.LastReadAt);
    }

    [Fact]
    public async Task A_reading_from_the_granted_site_is_kept_as_named_cells_with_its_time_and_page()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: true, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var (outcome, reading) = await sources.RecordAsync("prices", "https://shop.example/items?page=1", ["Item", "Price"],
            Rows(["Pen", "1.20"], ["Ink", "3.00"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecordOutcome.Recorded, outcome);
        Assert.NotNull(reading);
        var kept = Assert.Single((await Open().ReadingsAsync("prices", TestContext.Current.CancellationToken))!);
        Assert.Equal(Start.AddMinutes(5), kept.ReadAt);
        Assert.Equal("https://shop.example/items?page=1", kept.Source);
        Assert.Equal(2, kept.Rows.Count);
        Assert.Equal("Pen", kept.Rows[0]["Item"]);
        Assert.Equal("3.00", kept.Rows[1]["Price"]);
        Assert.Equal(Start.AddMinutes(5), Assert.Single(await Open().ListAsync(TestContext.Current.CancellationToken)).LastReadAt);
    }

    [Fact]
    public async Task Readings_accumulate_oldest_first()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: true, TestContext.Current.CancellationToken);
        await sources.RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(1));
        await sources.RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen", "1.25"]), TestContext.Current.CancellationToken);

        var readings = (await Open().ReadingsAsync("prices", TestContext.Current.CancellationToken))!;

        Assert.Equal(["1.20", "1.25"], readings.Select(r => r.Rows[0]["Price"]));
    }

    [Theory]
    [InlineData("https://shop.example.evil/items")]      // another host that starts with the same letters
    [InlineData("https://shop.example/itemsX")]          // another path that starts with the same letters
    [InlineData("http://shop.example/items")]            // another scheme
    [InlineData("https://other.example/items")]
    public async Task A_reading_from_outside_the_granted_site_is_refused_and_nothing_is_kept(string page)
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: true, TestContext.Current.CancellationToken);

        var (outcome, reading) = await sources.RecordAsync("prices", page, ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecordOutcome.OutsideGrant, outcome);
        Assert.Null(reading);
        Assert.Empty((await sources.ReadingsAsync("prices", TestContext.Current.CancellationToken))!);
        Assert.Null(Assert.Single(await sources.ListAsync(TestContext.Current.CancellationToken)).LastReadAt);
    }

    [Fact]
    public async Task A_site_given_as_a_bare_host_does_not_cover_a_longer_host()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices with { Site = "https://shop.example" }, granted: true, TestContext.Current.CancellationToken);

        var (longer, _) = await sources.RecordAsync("prices", "https://shop.example.evil/items", ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);
        var (under, _) = await sources.RecordAsync("prices", "https://shop.example/items", ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecordOutcome.OutsideGrant, longer);
        Assert.Equal(RecordOutcome.Recorded, under);
    }

    [Theory]
    [InlineData("https://shop.example/items")]
    [InlineData("https://shop.example/items/42")]
    [InlineData("https://shop.example/items?page=2")]
    [InlineData("https://shop.example/items#top")]
    public async Task Pages_under_the_granted_site_are_inside_it(string page)
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: true, TestContext.Current.CancellationToken);

        var (outcome, _) = await sources.RecordAsync("prices", page, ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecordOutcome.Recorded, outcome);
    }

    [Fact]
    public async Task A_source_declared_without_permission_cannot_be_read()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: false, TestContext.Current.CancellationToken);

        var (outcome, _) = await sources.RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecordOutcome.NotGranted, outcome);
        Assert.Null(Assert.Single(await sources.ListAsync(TestContext.Current.CancellationToken)).Grant);
    }

    [Fact]
    public async Task Columns_other_than_the_rules_are_refused_rather_than_stored()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: true, TestContext.Current.CancellationToken);

        var (renamed, _) = await sources.RecordAsync("prices", Prices.Site, ["Item", "Cost"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);
        var (reordered, _) = await sources.RecordAsync("prices", Prices.Site, ["Price", "Item"], Rows(["1.20", "Pen"]), TestContext.Current.CancellationToken);
        var (shortRow, _) = await sources.RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecordOutcome.ShapeMismatch, renamed);
        Assert.Equal(RecordOutcome.ShapeMismatch, reordered);
        Assert.Equal(RecordOutcome.ShapeMismatch, shortRow);
        Assert.Empty((await sources.ReadingsAsync("prices", TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task An_undeclared_name_has_no_readings_and_cannot_be_read_into()
    {
        var sources = Open();

        var (outcome, _) = await sources.RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecordOutcome.Unknown, outcome);
        Assert.Null(await sources.ReadingsAsync("prices", TestContext.Current.CancellationToken));
        Assert.False(await sources.RevokeAsync("prices", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Revoking_stops_new_readings_and_keeps_the_ones_already_read()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: true, TestContext.Current.CancellationToken);
        await sources.RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);

        Assert.True(await sources.RevokeAsync("prices", TestContext.Current.CancellationToken));
        var (outcome, _) = await sources.RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen", "1.25"]), TestContext.Current.CancellationToken);

        Assert.Equal(RecordOutcome.NotGranted, outcome);
        Assert.Null(Assert.Single(await Open().ListAsync(TestContext.Current.CancellationToken)).Grant);
        Assert.Equal("1.20", Assert.Single((await Open().ReadingsAsync("prices", TestContext.Current.CancellationToken))!).Rows[0]["Price"]);
    }

    [Fact]
    public async Task An_incomplete_last_reading_left_by_a_crash_is_skipped()
    {
        var sources = Open();
        await sources.DeclareAsync("prices", Prices, granted: true, TestContext.Current.CancellationToken);
        await sources.RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen", "1.20"]), TestContext.Current.CancellationToken);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_folder, "sources"), "*.ndjson"));
        await File.AppendAllTextAsync(file, "{\"readAt\":\"2026-10-0", TestContext.Current.CancellationToken);

        var readings = (await Open().ReadingsAsync("prices", TestContext.Current.CancellationToken))!;

        Assert.Single(readings);
        // and the next reading still lands on a line of its own
        await Open().RecordAsync("prices", Prices.Site, ["Item", "Price"], Rows(["Pen", "1.30"]), TestContext.Current.CancellationToken);
        Assert.Equal(["1.20", "1.30"], (await Open().ReadingsAsync("prices", TestContext.Current.CancellationToken))!.Select(r => r.Rows[0]["Price"]));
    }

    [Theory]
    [InlineData("prices", true)]
    [InlineData("price-list-2", true)]
    [InlineData("", false)]
    [InlineData("Prices", false)]
    [InlineData("../prices", false)]
    [InlineData("-prices", false)]
    [InlineData("a.b", false)]
    [InlineData("sources", true)]
    public void Names_are_short_lowercase_words_safe_in_a_path_and_a_file_name(string name, bool valid) =>
        Assert.Equal(valid, AppSources.IsValidName(name));

    [Fact]
    public void A_rule_needs_a_web_address_a_table_and_distinct_named_columns()
    {
        Assert.Throws<ArgumentException>(() => SourceRule.Validate(new SourceRule("file:///c:/x", "#t", ["A"])));
        Assert.Throws<ArgumentException>(() => SourceRule.Validate(new SourceRule("shop.example", "#t", ["A"])));
        Assert.Throws<ArgumentException>(() => SourceRule.Validate(new SourceRule("https://shop.example/", "", ["A"])));
        Assert.Throws<ArgumentException>(() => SourceRule.Validate(new SourceRule("https://shop.example/", "#t", [])));
        Assert.Throws<ArgumentException>(() => SourceRule.Validate(new SourceRule("https://shop.example/", "#t", ["A", "A"])));
        Assert.Throws<ArgumentException>(() => SourceRule.Validate(new SourceRule("https://shop.example/", "#t", ["A", " "])));
        SourceRule.Validate(Prices);
    }
}
