using System.Text.Json;
using Bohm.Runtime.Host.Edit;
using Bohm.Runtime.Host.Llm;
using Bohm.Runtime.Host.Promotion;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A new application proposed from an answer and the tables on the pages it came from: the model picks,
/// by number, which table of which page to read again and which of its columns, and writes the
/// application; the runtime turns the numbers into rules and refuses anything it cannot keep.
/// </summary>
public sealed class AppProposalTests
{
    private const string Html = """
        <!doctype html><title>Prices</title><table id="t"></table>
        <script>
        fetch('/__bohm/sources/prices').then(r => r.json()).then(d => {
          for (const row of d.rows) { const tr = document.createElement('tr'); tr.textContent = row.Item; document.getElementById('t').append(tr); }
        });
        </script>
        """;

    private static readonly AppRequest Request = new(
        "Compare the prices",
        "Pen costs 1.20 and ink 3.00.",
        "en",
        [
            new PageTables("https://shop.example/items?page=1#top", "Items", [
                new TableCandidate("#nav", ["Menu"], 4, [["Home"]]),
                new TableCandidate("#prices", ["Item", "Price", "Stock"], 2, [["Pen", "1.20", "5"], ["Ink", "3.00", "0"]]),
            ]),
        ]);

    private static FunctionCallContent Propose(string callId, object sources, string html = Html, string title = "Prices") =>
        new(callId, "propose_app", new Dictionary<string, object?>
        {
            ["title"] = title,
            ["sources"] = JsonSerializer.SerializeToElement(sources),
            ["html"] = html,
        });

    private static object[] PricesFrom(int table, params string[] columns) => [new { name = "prices", page = 1, table, columns }];

    [Fact]
    public async Task Table_numbers_become_rules_that_read_the_pages_site_again()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", PricesFrom(2, "Item", "Price")));
        model.Script.Enqueue(new TextContent("A price list read from the shop."));

        var proposal = await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken);

        Assert.Equal("Prices", proposal.Title);
        Assert.Equal(Html, proposal.Html);
        Assert.Equal("A price list read from the shop.", proposal.Summary);
        Assert.Empty(proposal.Refused);
        var source = Assert.Single(proposal.Sources);
        Assert.Equal("prices", source.Name);
        Assert.Equal(1, source.Page);
        Assert.Equal("https://shop.example/items", source.Rule.Site);   // the page without its query and fragment
        Assert.Equal("#prices", source.Rule.Selector);
        Assert.Equal(["Item", "Price"], source.Rule.Columns);
    }

    [Fact]
    public async Task The_model_is_shown_every_tables_headers_and_first_rows_marked_as_page_material()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", PricesFrom(2, "Item", "Price")));

        await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken);

        var prompt = string.Join('\n', model.Calls[0].Messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        Assert.Contains("<page-tables>", prompt, StringComparison.Ordinal);
        Assert.Contains("Compare the prices", prompt, StringComparison.Ordinal);
        Assert.Contains("Item | Price | Stock", prompt, StringComparison.Ordinal);
        Assert.Contains("Ink | 3.00 | 0", prompt, StringComparison.Ordinal);
        Assert.Contains("Table 2", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("#prices", prompt, StringComparison.Ordinal);   // numbers, not selectors, are what the model picks
    }

    [Theory]
    [InlineData(3, new[] { "Item" }, "table 3")]                  // no such table
    [InlineData(2, new[] { "Item", "Cost" }, "Cost")]             // no such column
    [InlineData(2, new[] { "Item", "Item" }, "twice")]            // the same column twice
    public async Task A_choice_the_page_does_not_have_is_sent_back_and_a_corrected_one_is_kept(int table, string[] columns, string reason)
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", PricesFrom(table, columns)));
        model.Script.Enqueue(Propose("c2", PricesFrom(2, "Item", "Price")));
        model.Script.Enqueue(new TextContent("Fixed."));

        var proposal = await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken);

        Assert.Equal(["Item", "Price"], Assert.Single(proposal.Sources).Rule.Columns);
        Assert.Contains(reason, Assert.Single(proposal.Refused), StringComparison.Ordinal);   // what was sent back is told with the proposal
        var refusal = model.Calls[1].Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "c1");
        Assert.Contains(reason, refusal.Result?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script>fetch('/__bohm/sources/prices').then(r=>r.json()).then(d=>{document.body.innerHTML=d.rows[0].Item})</script>", "innerHTML")]
    [InlineData("<script>fetch('/__bohm/sources/prices');fetch('/__bohm/sources/stock')</script>", "stock")]
    [InlineData("<p>no reading at all</p>", "prices")]
    [InlineData("<script src=\"https://cdn.example.com/x.js\"></script><script>fetch('/__bohm/sources/prices')</script>", "cdn.example.com")]
    public async Task An_application_that_would_trust_page_text_or_read_what_it_did_not_declare_is_refused(string html, string reason)
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", PricesFrom(2, "Item", "Price"), html));
        model.Script.Enqueue(new TextContent("Done."));

        var failure = await Assert.ThrowsAsync<ProposalFailedException>(() => AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken));

        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_never_proposes_fails_the_proposal()
    {
        var model = new FakeChatModel { Reply = "I cannot do that." };

        var failure = await Assert.ThrowsAsync<ProposalFailedException>(() => AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken));

        Assert.Contains("I cannot do that.", failure.Message, StringComparison.Ordinal);
    }
}
