using System.Text.Json;
using Bohm.Runtime.Host;
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

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken));

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

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken));

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
    public async Task A_model_that_thought_its_first_answer_away_is_asked_again_to_think_briefly()
    {
        var thinking = new ThinksAwayFirst(Propose("c1", PricesFrom(2, "Item", "Price")));
        var model = new ModelFitChatClient(thinking, ModelLimits.Unknown); // nobody said whether it thinks

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken));

        Assert.Equal("Prices", proposal.Title);
        Assert.Equal([null, ReasoningEffort.Low, ReasoningEffort.Low], thinking.Efforts); // the first as the server likes; again, briefly
        Assert.Equal(thinking.Sent[0], thinking.Sent[1]); // the same request again, not a continuation of the cut answer
    }

    [Fact]
    public async Task A_model_known_to_think_whose_answer_still_reached_the_limit_is_not_asked_again()
    {
        var thinking = new ThinksAwayFirst(Propose("c1", PricesFrom(2, "Item", "Price")));
        var model = new ModelFitChatClient(thinking, new ModelLimits(Reasoning: true));

        var failure = await Assert.ThrowsAsync<ProposalFailedException>(() => AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken));

        Assert.Equal(ProposalFailedException.OutputLimit, failure.Stopped);
        Assert.Single(thinking.Efforts);
    }

    /// <summary>
    /// A server model whose first answer is all thinking, cut at the length limit; after that it proposes, then closes —
    /// recording the thinking it was asked for and how many messages it was sent each time.
    /// </summary>
    private sealed class ThinksAwayFirst(FunctionCallContent proposal) : IChatClient
    {
        public List<ReasoningEffort?> Efforts { get; } = [];

        public List<int> Sent { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Efforts.Add(options?.Reasoning?.Effort);
            Sent.Add(list.Count);
            var (answer, finish) = Efforts.Count switch
            {
                1 => (new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("The person wants a price list. Let me think about every column")]), ChatFinishReason.Length),
                2 => (new ChatMessage(ChatRole.Assistant, [proposal]), ChatFinishReason.ToolCalls),
                _ => (new ChatMessage(ChatRole.Assistant, "A price list read from the shop."), ChatFinishReason.Stop),
            };
            return Task.FromResult(new ChatResponse(answer) { FinishReason = finish });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static readonly AppRequest Instruction = new("A reading log for the books I borrow", null, "en", null);

    private const string LogHtml = """
        <!doctype html><title>Reading log</title><ul id="books"></ul>
        <script>
        const books = JSON.parse(localStorage.getItem('books') || '[]');
        for (const b of books) { const li = document.createElement('li'); li.textContent = b.title; document.getElementById('books').append(li); }
        </script>
        """;

    [Fact]
    public async Task What_the_person_asked_alone_becomes_an_application_that_reads_no_source()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", Array.Empty<object>(), LogHtml, "Reading log"));
        model.Script.Enqueue(new TextContent("A log of borrowed books, kept on this computer."));

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Instruction, TestContext.Current.CancellationToken));

        Assert.Equal("Reading log", proposal.Title);
        Assert.Equal(LogHtml, proposal.Html);
        Assert.Empty(proposal.Sources);
        Assert.Empty(proposal.Refused);
        var system = string.Join('\n', model.Calls[0].Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        Assert.Contains(AppFacts.HowItRuns, system, StringComparison.Ordinal);   // the same facts a change is held to
        Assert.Contains("[hidden] { display: none !important; }", system, StringComparison.Ordinal);   // a dialog's display must not show a hidden layer
        Assert.Contains("fetch('/__bohm/pages/' + id)", system, StringComparison.Ordinal);   // it can receive the page the person is reading
        var prompt = string.Join('\n', model.Calls[0].Messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        Assert.Contains("A reading log for the books I borrow", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("page-tables", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_was_asked_alone_may_be_a_thing_to_do_now_and_then_no_application_is_made()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(new FunctionCallContent("c1", "one_time_task", new Dictionary<string, object?> { ["reason"] = " The open pages, summarized now. " }));
        model.Script.Enqueue(new TextContent(""));

        var task = Assert.IsType<OneTimeTask>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown,
            new AppRequest("Summarize all the pages open now", null, "en", null), TestContext.Current.CancellationToken));

        Assert.Equal("The open pages, summarized now.", task.Reason);
        var system = string.Join('\n', model.Calls[0].Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        Assert.Contains("call one_time_task", system, StringComparison.Ordinal);
        Assert.Contains("(\"an app that summarizes the", system, StringComparison.Ordinal);   // a tool that works on pages is still an application
    }

    [Fact]
    public async Task Only_what_was_asked_alone_is_offered_a_thing_to_do_now()
    {
        var made = new FakeChatModel();
        made.Script.Enqueue(Propose("c1", Array.Empty<object>(), LogHtml, "Reading log"));
        var fromPages = new FakeChatModel();
        fromPages.Script.Enqueue(Propose("c1", PricesFrom(2, "Item", "Price")));

        await AppProposals.ProposeAsync(made, ModelLimits.Unknown, Instruction, TestContext.Current.CancellationToken);
        await AppProposals.ProposeAsync(fromPages, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken);

        // Both may read a built-in skill; only what was asked alone may be a thing to do now.
        Assert.Equal(["load_skill", "one_time_task", "propose_app"], made.Calls[0].Options!.Tools!.Select(t => t.Name).Order());
        Assert.Equal(["load_skill", "propose_app"], fromPages.Calls[0].Options!.Tools!.Select(t => t.Name).Order());   // an answer's pages are asked to be read again
    }

    [Fact]
    public async Task An_application_written_after_saying_it_is_a_thing_to_do_now_is_the_proposal()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(new FunctionCallContent("c1", "one_time_task", new Dictionary<string, object?> { ["reason"] = "Now." }));
        model.Script.Enqueue(Propose("c2", Array.Empty<object>(), LogHtml, "Reading log"));

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Instruction, TestContext.Current.CancellationToken));

        Assert.Equal(LogHtml, proposal.Html);
    }

    [Fact]
    public async Task An_application_from_an_instruction_may_answer_longer_than_one_reading_pages()
    {
        var made = new FakeChatModel();
        made.Script.Enqueue(Propose("c1", Array.Empty<object>(), LogHtml, "Reading log"));
        var fromPages = new FakeChatModel();
        fromPages.Script.Enqueue(Propose("c1", PricesFrom(2, "Item", "Price")));

        await AppProposals.ProposeAsync(made, ModelLimits.Unknown, Instruction, TestContext.Current.CancellationToken);
        await AppProposals.ProposeAsync(fromPages, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken);

        // A tool the person lives with (import, rules, charts) ran out at the page-reading limit; a model known to answer less is fitted down.
        Assert.Equal(AppProposals.MaxInstructionOutputTokens, made.Calls[0].Options?.MaxOutputTokens);
        Assert.Equal(AppProposals.MaxOutputTokens, fromPages.Calls[0].Options?.MaxOutputTokens);
        Assert.True(AppProposals.MaxInstructionOutputTokens > AppProposals.MaxOutputTokens);
    }

    [Fact]
    public async Task An_application_from_an_instruction_may_put_markup_on_the_page_it_wrote_itself()
    {
        const string html = "<!doctype html><div id=\"app\"></div><script>document.getElementById('app').innerHTML = '<h1>Books</h1>';</script>";
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", Array.Empty<object>(), html, "Books"));

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Instruction, TestContext.Current.CancellationToken));

        Assert.Equal(html, proposal.Html);   // no page values reach it — the innerHTML rule is about those
    }

    [Theory]
    [InlineData("<script>fetch('/__bohm/sources/books')</script>", "books")]
    [InlineData("<script src=\"https://cdn.example.com/x.js\"></script>", "cdn.example.com")]
    [InlineData("<script>const total;</script>", "does not parse as JavaScript, at line 1")]
    public async Task An_application_from_an_instruction_that_reads_a_source_loads_code_from_elsewhere_or_does_not_parse_is_sent_back(string html, string reason)
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", Array.Empty<object>(), html, "Books"));
        model.Script.Enqueue(Propose("c2", Array.Empty<object>(), LogHtml, "Reading log"));

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Instruction, TestContext.Current.CancellationToken));

        Assert.Equal(LogHtml, proposal.Html);
        Assert.Contains(reason, Assert.Single(proposal.Refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_application_from_an_instruction_that_declares_a_source_is_sent_back()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", PricesFrom(1, "Item"), LogHtml, "Reading log"));
        model.Script.Enqueue(Propose("c2", Array.Empty<object>(), LogHtml, "Reading log"));

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Instruction, TestContext.Current.CancellationToken));

        Assert.Empty(proposal.Sources);
        Assert.Contains("no web pages", Assert.Single(proposal.Refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_never_proposes_fails_the_proposal()
    {
        var model = new FakeChatModel { Reply = "I cannot do that." };

        var failure = await Assert.ThrowsAsync<ProposalFailedException>(() => AppProposals.ProposeAsync(model, ModelLimits.Unknown, Request, TestContext.Current.CancellationToken));

        Assert.Contains("I cannot do that.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_is_the_one_given_with_the_proposal_not_the_closing_that_tells_how_it_got_there()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(Propose("c1", PricesFrom(1, "Item"), LogHtml, "Reading log"));   // refused: no pages to read
        var call = Propose("c2", Array.Empty<object>(), LogHtml, "Reading log");
        call.Arguments!["summary"] = "빌린 책을 적어 두는 기록장입니다.";
        model.Script.Enqueue(call);
        model.Script.Enqueue(new TextContent("The second proposal was accepted, so I keep it as it is."));

        var proposal = Assert.IsType<AppProposal>(await AppProposals.ProposeAsync(model, ModelLimits.Unknown, Instruction, TestContext.Current.CancellationToken));

        Assert.Equal("빌린 책을 적어 두는 기록장입니다.", proposal.Summary);
        var system = string.Join('\n', model.Calls[0].Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        Assert.Contains("Give\npropose_app a summary", system.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_share_target_the_facts_show_a_model_is_one_the_runtime_reads()
    {
        var link = System.Text.RegularExpressions.Regex.Match(AppFacts.HowItRuns, "<link rel=\"manifest\"[^>]*>").Value;

        var target = Bohm.Runtime.Pages.ShareTarget.Find("<!doctype html><head>" + link + "</head><title>Cards</title>");

        Assert.NotNull(target);   // what the model is told to write is what makes an application receive pages
        Assert.Equal("title", target.Title);
        Assert.Equal("url", target.Url);
    }

    private const string BrokenLog = """
        <!doctype html><title>Reading log</title><ul id="books"></ul>
        <script>
        const books = JSON.parse(localStorage.getItem('books') || '[]');
        books.forEch(b => { const li = document.createElement('li'); li.textContent = b.title; document.getElementById('books').append(li); });
        </script>
        """;

    [Fact]
    public async Task A_made_application_that_failed_when_opened_is_fixed_from_itself_and_kept_to_the_same_checks()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(new FunctionCallContent("c1", "replace", new Dictionary<string, object?> { ["old_text"] = "books.forEch(", ["new_text"] = "books.forEach(" }));
        model.Script.Enqueue(new TextContent("Fixed the misspelled forEach."));
        var broken = Instruction with { Broken = new BrokenVersion(BrokenLog, ["Uncaught TypeError: books.forEch is not a function (line 5)"]) };

        var proposal = await AppProposals.FixAsync(model, ModelLimits.Unknown, broken, TestContext.Current.CancellationToken);

        Assert.Equal(BrokenLog.Replace("books.forEch(", "books.forEach(", StringComparison.Ordinal), proposal.Html);
        Assert.Equal("Reading log", proposal.Title);
        Assert.Empty(proposal.Sources);
        Assert.Equal("Fixed the misspelled forEach.", proposal.Summary);
        var asked = model.Calls[0].Messages.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("books.forEch is not a function (line 5)", asked, StringComparison.Ordinal);
        Assert.Contains("A reading log for the books I borrow", asked, StringComparison.Ordinal);
        Assert.Contains("around line 5", asked, StringComparison.Ordinal);   // shown where the error is
    }

    /// <summary>A 1×1 PNG, as the shell sends a picture of the application.</summary>
    private const string Picture = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public async Task A_new_application_is_looked_over_from_a_picture_of_it_and_kept_to_the_same_checks()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(new FunctionCallContent("c1", "replace", new Dictionary<string, object?> { ["old_text"] = "<ul id=\"books\"></ul>", ["new_text"] = "<p id=\"empty\">Add your first book.</p><ul id=\"books\"></ul>" }));
        model.Script.Enqueue(new TextContent("Said what to do first."));
        var looked = Instruction with { Review = new ReviewedVersion(LogHtml, Picture) };

        var proposal = await AppProposals.ReviewAsync(model, ModelLimits.Unknown, looked, TestContext.Current.CancellationToken);

        Assert.Contains("<p id=\"empty\">Add your first book.</p>", proposal.Html, StringComparison.Ordinal);
        Assert.Equal("Said what to do first.", proposal.Summary);
        var asked = model.Calls[0].Messages.Last(m => m.Role == ChatRole.User);
        var picture = Assert.Single(asked.Contents.OfType<DataContent>());   // the picture goes with the request, in one message
        Assert.Equal("image/png", picture.MediaType);
        Assert.Contains("A reading log for the books I borrow", asked.Text, StringComparison.Ordinal);
        Assert.Contains("A good application looks finished", asked.Text, StringComparison.Ordinal);
        Assert.Contains(model.Calls[0].Messages, m => m.Role == ChatRole.System);   // the edit's own instructions still lead
    }

    [Fact]
    public async Task Nothing_to_change_after_a_look_keeps_the_version_as_it_was()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(new TextContent("Nothing falls short."));
        var looked = Instruction with { Review = new ReviewedVersion(LogHtml, Picture) };

        var proposal = await AppProposals.ReviewAsync(model, ModelLimits.Unknown, looked, TestContext.Current.CancellationToken);

        Assert.Equal(LogHtml, proposal.Html);
    }

    [Theory]
    [InlineData("https://example.com/shot.png")]
    [InlineData("data:text/html;base64,PGgxPg==")]
    public async Task A_look_needs_a_picture(string image)
    {
        var looked = Instruction with { Review = new ReviewedVersion(LogHtml, image) };

        await Assert.ThrowsAsync<ArgumentException>(() => AppProposals.ReviewAsync(new FakeChatModel(), ModelLimits.Unknown, looked, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_line_an_error_names_is_shown_as_it_is_now_beside_the_error()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(new FunctionCallContent("c1", "replace", new Dictionary<string, object?> { ["old_text"] = "books.forEch(", ["new_text"] = "books.forEach(" }));
        model.Script.Enqueue(new TextContent("Fixed the misspelled forEach."));
        var broken = Instruction with { Broken = new BrokenVersion(BrokenLog, ["Uncaught TypeError: books.forEch is not a function (line 4)"]) };

        await AppProposals.FixAsync(model, ModelLimits.Unknown, broken, TestContext.Current.CancellationToken);

        var asked = model.Calls[0].Messages.Last(m => m.Role == ChatRole.User).Text;
        Assert.Contains("Source lines 1–", asked, StringComparison.Ordinal);
        Assert.Contains("as they are now:\n4: books.forEch(", asked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fix_that_would_load_code_from_elsewhere_is_not_kept()
    {
        var model = new FakeChatModel();
        model.Script.Enqueue(new FunctionCallContent("c1", "replace", new Dictionary<string, object?> { ["old_text"] = "<script>", ["new_text"] = "<script src=\"https://cdn.example.com/fix.js\"></script><script>" }));
        model.Script.Enqueue(new TextContent("Loaded a helper."));
        var broken = Instruction with { Broken = new BrokenVersion(BrokenLog, ["Uncaught TypeError: books.forEch is not a function (line 5)"]) };

        var failure = await Assert.ThrowsAsync<ProposalFailedException>(() => AppProposals.FixAsync(model, ModelLimits.Unknown, broken, TestContext.Current.CancellationToken));

        Assert.Contains("cdn.example.com", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_built_in_skills_ship_beside_the_runtime_and_are_valid()
    {
        var skills = IronHive.Agent.Skills.SkillsLoader.Create(new IronHive.Agent.Skills.SkillsConfig { Roots = [AppProposals.SkillsRoot] });

        Assert.Empty(skills.Diagnostics);
        Assert.Equal(["ai-inside-the-app", "photos-voice-and-files", "printable-page", "records-and-tables"], skills.Skills.Select(s => s.Name).Order());
        Assert.Equal(0, skills.DroppedCount);
        // What the model is told: each skill's name and when to read it.
        Assert.Contains("printable-page", skills.Contributor.GetInstructions(), StringComparison.Ordinal);
    }
}
