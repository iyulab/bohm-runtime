using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A proposal asked for as lines: while the model writes the application — a provider sending the call as it is
/// written — its HTML arrives piece by piece, a proposal sent back to the model is said as it happens, and the
/// last line is the proposal or why there is none. Through the organization's server, so the provider's own
/// pieces go through the same bridge a real one does.
/// </summary>
public sealed class AppProposalStreamTests : IAsyncLifetime
{
    private const string Html = """
        <!doctype html><title>Reading log</title><p id="empty"></p>
        <script>document.getElementById("empty").textContent = "Nothing yet — add the first book.";</script>
        """;

    private static readonly string[] BrokenProblems = ["Uncaught ReferenceError: documnt is not defined (line 1)"];

    private const string Instruction = """{"question":"A reading log for the books I borrow","lang":"en"}""";

    private FakeProvider _server = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync();
        using var set = await _host.ControlClient().PutAsync("/__control/llm/company-model",
            new StringContent($$"""{"endpoint":"{{new Uri(_server.Address, "v1/")}}","model":"m"}""", Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.OK, set);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _server.DisposeAsync();
    }

    private static string ProposeApp(string title, string html) =>
        JsonSerializer.Serialize(new { title, sources = Array.Empty<object>(), html, summary = "A list of borrowed books." });

    [Fact]
    public async Task Asked_for_lines_the_application_arrives_as_it_is_written_and_then_the_proposal()
    {
        _server.StreamedCalls.Enqueue(("propose_app", ProposeApp("Reading log", Html)));

        using var response = await ProposalLinesAsync(Instruction);

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var lines = await LinesAsync(response);
        var progress = lines.SkipLast(1).ToList();
        Assert.True(progress.Count > Html.Length / FakeProvider.ArgumentPiece / 2, $"{progress.Count} lines");   // pieces, not one line at the end
        Assert.True(progress[0].GetProperty("start").GetBoolean());
        Assert.Single(progress, l => l.TryGetProperty("start", out _));
        Assert.Equal(Html, string.Concat(progress.Select(l => l.GetProperty("writing").GetString())));
        var done = lines[^1];
        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Equal(Html, done.GetProperty("html").GetString());
        Assert.Equal("Reading log", done.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_proposal_sent_back_is_said_between_the_two_writings()
    {
        _server.StreamedCalls.Enqueue(("propose_app", ProposeApp("", Html)));
        _server.StreamedCalls.Enqueue(("propose_app", ProposeApp("Reading log", Html)));

        using var response = await ProposalLinesAsync(Instruction);

        var lines = await LinesAsync(response);
        var kinds = lines.SkipLast(1).Select(l => l.TryGetProperty("refused", out _) ? "refused" : l.TryGetProperty("start", out _) ? "start" : "writing")
            .Where(k => k != "writing").ToList();
        Assert.Equal(["start", "refused", "start"], kinds);
        Assert.Contains("title", lines.SkipLast(1).Single(l => l.TryGetProperty("refused", out _)).GetProperty("refused").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("done", lines[^1].GetProperty("status").GetString());
        Assert.Single(lines[^1].GetProperty("refused").EnumerateArray());
    }

    [Fact]
    public async Task A_fix_writes_its_new_text_as_it_is_written()
    {
        const string Broken = """<!doctype html><title>Reading log</title><script>documnt.title = "Log";</script>""";
        _server.StreamedCalls.Enqueue(("replace", JsonSerializer.Serialize(new { old_text = "documnt.title", new_text = "document.title" })));
        var request = JsonSerializer.Serialize(new
        {
            question = "A reading log for the books I borrow",
            lang = "en",
            broken = new { html = Broken, problems = BrokenProblems },
        });

        using var response = await ProposalLinesAsync(request);

        var lines = await LinesAsync(response);
        Assert.Equal("document.title", string.Concat(lines.SkipLast(1).Select(l => l.GetProperty("writing").GetString())));
        var done = lines[^1];
        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Contains("document.title = \"Log\"", done.GetProperty("html").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_to_a_saved_application_asked_for_lines_writes_its_new_text_as_it_is_written()
    {
        var id = await _host.AdoptAsync("""<!doctype html><title>Log</title><h1 id="t">Reading log</h1>""");
        _server.StreamedCalls.Enqueue(("replace", JsonSerializer.Serialize(new { old_text = "Reading log", new_text = "My reading log" })));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/__control/apps/{id}/proposals")
        {
            Content = new StringContent("""{"instruction":"Call it my reading log","target":{"html":"<h1 id=\"t\">Reading log</h1>","text":"Reading log"}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        using var response = await _host.ControlClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        HttpAssert.Status(HttpStatusCode.OK, response);
        var lines = await LinesAsync(response);
        Assert.Equal("My reading log", string.Concat(lines.SkipLast(1).Select(l => l.GetProperty("writing").GetString())));
        var done = lines[^1];
        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Contains("My reading log", done.GetProperty("html").GetString(), StringComparison.Ordinal);
        Assert.Single(done.GetProperty("edits").EnumerateArray());
    }

    [Fact]
    public async Task Asked_for_lines_a_server_that_refuses_ends_them_with_a_failed_line_that_says_why()
    {
        _server.Refusal = (429, """{"error":{"message":"Slow down.","type":"rate_limit"}}""");

        using var response = await ProposalLinesAsync(Instruction);

        HttpAssert.Status(HttpStatusCode.OK, response);   // sent before the model was asked
        var failed = Assert.Single(await LinesAsync(response));
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal(429, failed.GetProperty("provider").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Asked_for_lines_a_missing_model_is_still_said_by_the_status()
    {
        await using var host = await RunningHost.StartAsync();

        using var response = await ProposalLinesAsync(Instruction, host);

        HttpAssert.Status(HttpStatusCode.Conflict, response);
    }

    private async Task<HttpResponseMessage> ProposalLinesAsync(string body, RunningHost? host = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__control/apps/proposals") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/x-ndjson");
        return await (host ?? _host).ControlClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private static async Task<List<JsonElement>> LinesAsync(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
}
