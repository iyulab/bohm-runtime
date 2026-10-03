using System.Text;
using Bohm.Runtime.Pages;
using Bohm.Runtime.Tests.Storage;

namespace Bohm.Runtime.Tests.Pages;

/// <summary>
/// The pages a person sent to an application: each is kept whole in a file of its own and listed,
/// oldest first, by a summary line — the HTML gives way first when a page is too large.
/// </summary>
public sealed class AppPagesTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 4, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Directory.CreateTempSubdirectory("bohm-pages-").FullName;
    private readonly ManualClock _clock = new(Start);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private AppPages Open() => AppPages.Open(_folder, _clock);

    [Fact]
    public async Task A_sent_page_is_kept_whole_and_listed_after_reopening()
    {
        var (outcome, page) = await Open().ReceiveAsync("https://news.example/a?id=1", " A story ", "First line.\n\nSecond   line.", "<p>First line.</p>", "en", "Kim",
            TestContext.Current.CancellationToken);

        Assert.Equal(ReceiveOutcome.Received, outcome);
        Assert.Matches("^[0-9a-f]{16}$", page!.Id);
        var kept = await Open().GetAsync(page.Id, TestContext.Current.CancellationToken);
        Assert.Equal(new ReceivedPage(page.Id, Start, "https://news.example/a?id=1", "A story", "First line.\n\nSecond   line.", "<p>First line.</p>", "en", "Kim"), kept);
        var listed = Assert.Single(await Open().ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new ReceivedPageSummary(page.Id, Start, "https://news.example/a?id=1", "A story", "en", "Kim", "First line. Second line."), listed);
    }

    [Fact]
    public async Task Pages_are_listed_in_the_order_they_arrived_with_ids_that_sort_the_same_way()
    {
        var pages = Open();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await pages.ReceiveAsync($"https://news.example/{i}", $"Story {i}", "text", null, null, null, TestContext.Current.CancellationToken)).Page!.Id);
            if (i == 2) _clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(ids.Count, ids.Distinct().Count());   // the same millisecond still gives different ids
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.Equal(ids, (await Open().ListAsync(TestContext.Current.CancellationToken)).Select(p => p.Id));
    }

    [Fact]
    public async Task A_page_without_a_title_is_named_by_its_address_and_blank_details_are_left_out()
    {
        var (_, page) = await Open().ReceiveAsync("https://news.example/untitled", "  ", "text", "  ", " ", "", TestContext.Current.CancellationToken);

        Assert.Equal("https://news.example/untitled", page!.Title);
        Assert.Null(page.Html);
        Assert.Null(page.Lang);
        Assert.Null(page.Byline);
    }

    [Fact]
    public async Task The_html_is_left_out_when_text_and_html_together_pass_the_limit()
    {
        var text = new string('a', AppPages.MaxPageBytes - 10);

        var (outcome, page) = await Open().ReceiveAsync("https://news.example/long", "Long", text, "<p>" + new string('b', 20) + "</p>", null, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReceiveOutcome.ReceivedWithoutHtml, outcome);
        var kept = await Open().GetAsync(page!.Id, TestContext.Current.CancellationToken);
        Assert.Null(kept!.Html);
        Assert.Equal(text, kept.Text);
        Assert.EndsWith("…", Assert.Single(await Open().ListAsync(TestContext.Current.CancellationToken)).Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_alone_past_the_limit_is_refused_and_nothing_is_kept()
    {
        var text = new string('가', AppPages.MaxPageBytes / 3 + 1);   // three bytes each in UTF-8
        Assert.True(Encoding.UTF8.GetByteCount(text) > AppPages.MaxPageBytes);

        var (outcome, page) = await Open().ReceiveAsync("https://news.example/huge", "Huge", text, null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(ReceiveOutcome.TooLarge, outcome);
        Assert.Null(page);
        Assert.Empty(await Open().ListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("file:///C:/secret.txt", "text")]
    [InlineData("javascript:alert(1)", "text")]
    [InlineData("not an address", "text")]
    [InlineData("https://news.example/empty", "   ")]
    public async Task A_page_that_is_not_a_web_page_with_text_is_refused(string url, string text)
    {
        var (outcome, page) = await Open().ReceiveAsync(url, "x", text, null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(ReceiveOutcome.Invalid, outcome);
        Assert.Null(page);
        Assert.False(Directory.Exists(Path.Combine(_folder, AppPages.Directory)));
    }

    [Fact]
    public async Task An_unknown_or_malformed_id_finds_no_page()
    {
        await Open().ReceiveAsync("https://news.example/a", "A", "text", null, null, null, TestContext.Current.CancellationToken);

        Assert.Null(await Open().GetAsync("0000000000000000", TestContext.Current.CancellationToken));
        Assert.Null(await Open().GetAsync("../app", TestContext.Current.CancellationToken));
        Assert.Null(await Open().GetAsync("index", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_summary_line_a_crash_cut_short_is_skipped_and_the_next_page_starts_its_own_line()
    {
        await Open().ReceiveAsync("https://news.example/a", "A", "text", null, null, null, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(Path.Combine(_folder, AppPages.Directory, "index.ndjson"), """{"id":"00000000""", TestContext.Current.CancellationToken);

        await Open().ReceiveAsync("https://news.example/b", "B", "text", null, null, null, TestContext.Current.CancellationToken);

        Assert.Equal(["A", "B"], (await Open().ListAsync(TestContext.Current.CancellationToken)).Select(p => p.Title));
    }
}
