using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Edit;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A change made elsewhere: the person gave the application's code to an AI service they use in their
/// own browser and pasted the answer back. It becomes a proposal like a model's — the same changes to
/// look at, the same preview, applied or discarded the same way — with no model on this computer.
/// </summary>
public sealed class PastedProposalTests : IAsyncLifetime
{
    private const string App = "<!doctype html><title>Tasks</title>\n<ul id=\"list\"></ul>\n<button onclick=\"addTask()\">Add Task</button>\n<script>function addTask() { localStorage.setItem('n', '1'); }</script>\n";

    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await RunningHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public void The_document_is_taken_from_a_fenced_block_or_from_its_first_tag_and_a_fragment_is_not_one()
    {
        var answer = "Here is the updated app:\n\n```html\n<!DOCTYPE html>\n<html><body><p>new</p></body></html>\n```\n\nIt now says «new».";
        Assert.Equal("<!DOCTYPE html>\n<html><body><p>new</p></body></html>\n", PastedProposals.Extract(answer));
        Assert.Equal("<html lang=\"ko\"><p>x</p></html>\n", PastedProposals.Extract("Sure! <html lang=\"ko\"><p>x</p></html> Done."));
        Assert.Equal("<!doctype html><p>no end\n", PastedProposals.Extract("<!doctype html><p>no end\n\n"));
        // The largest fenced block that is a document — not a snippet shown before it.
        Assert.StartsWith("<!doctype html><title>B", PastedProposals.Extract("```js\nlet a = 1;\n```\n```html\n<!doctype html><title>B</title>\n```"));
        Assert.Null(PastedProposals.Extract("Change the button to <button style=\"color:green\">Add</button>."));
        Assert.Null(PastedProposals.Extract("I can't do that."));
    }

    [Fact]
    public void Changes_are_runs_of_lines_and_an_added_run_carries_the_line_before_it()
    {
        var before = "a\nb\nc\nd\n";
        Assert.Empty(PastedProposals.Diff(before, before));
        Assert.Equal([new SourceEdit("b\n", "B\n")], PastedProposals.Diff(before, "a\nB\nc\nd\n"));
        Assert.Equal([new SourceEdit("b\n", "b\nx\n")], PastedProposals.Diff(before, "a\nb\nx\nc\nd\n"));
        Assert.Equal([new SourceEdit("a\n", "x\na\n")], PastedProposals.Diff(before, "x\na\nb\nc\nd\n"));
        Assert.Equal([new SourceEdit("b\n", "B\n"), new SourceEdit("d\n", "")], PastedProposals.Diff(before, "a\nB\nc\n"));
        // Each change is an exact piece of the source: applied in order, they make the new source.
        var after = "a\nB\nc\nx\nd\ne\n";
        var rebuilt = before;
        foreach (var edit in PastedProposals.Diff(before, after)) rebuilt = rebuilt.Replace(edit.Old, edit.New, StringComparison.Ordinal);
        Assert.Equal(after, rebuilt);
    }

    [Fact]
    public async Task A_pasted_answer_becomes_a_proposal_without_a_model_and_nothing_is_applied()
    {
        var id = await _host.AdoptAsync(App);
        var answer = "Done:\n```html\n" + App.Replace("Add Task", "Add a task", StringComparison.Ordinal) + "```";

        using var response = await _host.ControlClient().PostAsync($"/__control/apps/{id}/proposals/from-html", new StringContent(answer, Encoding.UTF8));

        HttpAssert.Status(HttpStatusCode.OK, response);
        var proposal = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(App.Replace("Add Task", "Add a task", StringComparison.Ordinal), proposal.GetProperty("html").GetString());
        var edit = Assert.Single(proposal.GetProperty("edits").EnumerateArray());
        Assert.Equal("<button onclick=\"addTask()\">Add Task</button>\n", edit.GetProperty("old").GetString());
        Assert.Equal("<button onclick=\"addTask()\">Add a task</button>\n", edit.GetProperty("new").GetString());
        Assert.Equal("pasted", proposal.GetProperty("model").GetString());
        Assert.Equal(Encoding.UTF8.GetBytes(App), await _host.Catalog.ReadHtmlAsync(id));   // not applied
    }

    [Fact]
    public async Task An_answer_without_a_whole_document_is_refused_with_a_reason_and_an_unknown_app_is_not_found()
    {
        var id = await _host.AdoptAsync(App);

        using var fragment = await _host.ControlClient().PostAsync($"/__control/apps/{id}/proposals/from-html", new StringContent("Use <b>bold</b> there.", Encoding.UTF8));
        using var empty = await _host.ControlClient().PostAsync($"/__control/apps/{id}/proposals/from-html", new StringContent("", Encoding.UTF8));
        using var unknown = await _host.ControlClient().PostAsync("/__control/apps/0123456789abcdef0123456789abcdef/proposals/from-html", new StringContent(App, Encoding.UTF8));

        HttpAssert.Status(HttpStatusCode.BadRequest, fragment);
        Assert.Equal("not-a-document", JsonDocument.Parse(await fragment.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        HttpAssert.Status(HttpStatusCode.BadRequest, empty);
        HttpAssert.Status(HttpStatusCode.NotFound, unknown);
    }

    [Fact]
    public async Task The_code_in_use_can_be_read_to_hand_it_elsewhere_and_no_data_comes_with_it()
    {
        var id = await _host.AdoptAsync(App);
        var page = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"n","value":"secret-entry"}]}""")).Dispose();

        using var source = await _host.ControlClient().GetAsync($"/__control/apps/{id}/source");
        using var unknown = await _host.ControlClient().GetAsync("/__control/apps/0123456789abcdef0123456789abcdef/source");

        HttpAssert.Status(HttpStatusCode.OK, source);
        Assert.Equal("text/html", source.Content.Headers.ContentType?.MediaType);
        var text = await source.Content.ReadAsStringAsync();
        Assert.Equal(App, text);
        Assert.DoesNotContain("secret-entry", text, StringComparison.Ordinal);
        HttpAssert.Status(HttpStatusCode.NotFound, unknown);
    }

    [Fact]
    public async Task Line_ends_follow_the_application_not_the_answer()
    {
        var id = await _host.AdoptAsync(App.Replace("\n", "\r\n", StringComparison.Ordinal));

        using var response = await _host.ControlClient().PostAsync($"/__control/apps/{id}/proposals/from-html", new StringContent(App.Replace("Tasks", "Jobs", StringComparison.Ordinal), Encoding.UTF8));

        var proposal = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var edit = Assert.Single(proposal.GetProperty("edits").EnumerateArray());   // one line changed, not every line
        Assert.Equal("<!doctype html><title>Jobs</title>\r\n", edit.GetProperty("new").GetString());
    }
}
