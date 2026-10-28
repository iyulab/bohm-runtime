using LocalOrigin.Storage;
using Bohm.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;
using Bohm.Runtime.Host.Extensions;

namespace Bohm.Runtime.Tests.Host;

public sealed class ControlPlaneTests : IAsyncLifetime
{
    private const string Page = "<!doctype html><title>Stock</title>";

    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await RunningHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_loaded_page_reports_the_highest_write_it_has_applied()
    {
        var id = (await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page))).Id;
        var other = (await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page + "<p>other</p>"))).Id;
        var page = await _host.LoadAsync(id);
        using var client = _host.ControlClient();

        Assert.Equal(0, await AckAsync(client, id, page.Tab));
        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"a","value":"1"},{"seq":2,"op":"set","key":"b","value":"2"}]}""")).Dispose();
        Assert.Equal(2, await AckAsync(client, id, page.Tab));

        // Another application's page, and a page that never existed, are not this application's to report.
        using var foreign = await client.GetAsync($"/__control/apps/{other}/tabs/{page.Tab}");
        using var unknown = await client.GetAsync($"/__control/apps/{id}/tabs/0123456789abcdef0123456789abcdef");
        HttpAssert.Status(HttpStatusCode.NotFound, foreign);
        HttpAssert.Status(HttpStatusCode.NotFound, unknown);

        static async Task<long> AckAsync(HttpClient client, string app, string tab) =>
            JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{app}/tabs/{tab}")).RootElement.GetProperty("ack").GetInt64();
    }

    [Fact]
    public async Task A_repair_made_while_loading_is_counted_in_the_application_usage_record()
    {
        // A folder copied while a write was in progress: the last journal line is incomplete.
        var id = (await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page))).Id;
        await using (var storage = await _host.Catalog.OpenStorageAsync(id))
            await storage.ApplyAsync([LocalOrigin.Storage.KeyValueOperation.Set("kept", "yes")]);
        await File.AppendAllTextAsync(Path.Combine(_host.DataRoot, "adopted", id, "storage", "journal.ndjson"), "{\"seq\":2,\"op\":\"set\",\"key\":\"lost\",\"va");

        using (var page = await _host.ClientForApp(id).GetAsync("/"))
            HttpAssert.Status(HttpStatusCode.OK, page);

        var days = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/usage")).RootElement.GetProperty("days");
        Assert.Equal(1, days.EnumerateArray().Sum(d => d.GetProperty("repaired").GetInt32()));
    }

    [Fact]
    public async Task The_revision_history_names_files_without_their_folders()
    {
        var folder = Path.Combine(_host.DataRoot, "..", "Downloads");
        var id = (await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page), Path.Combine(folder, "loans.html"))).Id;
        await using (var storage = await _host.Catalog.OpenStorageAsync(id))
            await _host.Catalog.ReviseAsync(id, Encoding.UTF8.GetBytes("<p>changed</p>"), null, storage);
        using var client = _host.ControlClient();

        var revisions = JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{id}/revisions")).RootElement.EnumerateArray().ToList();
        using var unknown = await client.GetAsync("/__control/apps/0123456789abcdef0123456789abcdef/revisions");

        Assert.Equal(["loans.html", null], revisions.Select(r => r.GetProperty("file").GetString()));
        Assert.Equal([false, true], revisions.Select(r => r.GetProperty("inUse").GetBoolean()));
        Assert.Equal(2, revisions[1].GetProperty("revision").GetInt32());
        HttpAssert.Status(HttpStatusCode.NotFound, unknown);
    }

    [Fact]
    public async Task Kept_data_is_taken_back_in_and_given_back_through_the_control_api()
    {
        var id = (await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes("<script>localStorage.getItem('loans')</script>"))).Id;
        await using (var storage = await _host.Catalog.OpenStorageAsync(id))
        {
            await storage.ApplyAsync([LocalOrigin.Storage.KeyValueOperation.Set("loans", "1")]);
            await _host.Catalog.ReviseAsync(id, Encoding.UTF8.GetBytes("<script>/* v2 */ localStorage.getItem('loans')</script>"), null, storage);
            await storage.ApplyAsync([LocalOrigin.Storage.KeyValueOperation.Set("loans", "5")]);
            await _host.Catalog.RevertAsync(id, storage);
        }

        using var client = _host.ControlClient();
        async Task<string?> UndoneAsync() =>
            JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{id}/revisions")).RootElement[1].GetProperty("undone").GetString();

        Assert.Equal("importable", await UndoneAsync());
        using (var undoFirst = await client.PostAsync($"/__control/apps/{id}/revisions/2/undo-import", null))
            HttpAssert.Status(HttpStatusCode.Conflict, undoFirst);
        using (var taken = await client.PostAsync($"/__control/apps/{id}/revisions/2/import", null))
            HttpAssert.Status(HttpStatusCode.OK, taken);
        Assert.Equal("imported", await UndoneAsync());
        using (var again = await client.PostAsync($"/__control/apps/{id}/revisions/2/import", null))
            HttpAssert.Status(HttpStatusCode.Conflict, again);
        using (var back = await client.PostAsync($"/__control/apps/{id}/revisions/2/undo-import", null))
            HttpAssert.Status(HttpStatusCode.OK, back);
        Assert.Equal("importable", await UndoneAsync());
        using (var none = await client.PostAsync($"/__control/apps/{id}/revisions/1/import", null))
            HttpAssert.Status(HttpStatusCode.Conflict, none);
    }


    /// <summary>The stdio MCP server the tests start (<c>tests/Bohm.Runtime.McpStub</c>, built beside this assembly in the same configuration).</summary>
    private static string McpStubPath()
    {
        var here = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var stub = Path.Combine(here.Parent!.Parent!.Parent!.Parent!.FullName, "Bohm.Runtime.McpStub", "bin", here.Parent.Name, here.Name);
        return Path.Combine(stub, OperatingSystem.IsWindows() ? "Bohm.Runtime.McpStub.exe" : "Bohm.Runtime.McpStub");
    }

    [Fact]
    public async Task Mcp_servers_are_read_as_their_documentation_writes_them_connected_and_listed_without_secret_values()
    {
        using var client = _host.ControlClient();
        async Task<JsonElement[]> McpItemsAsync() =>
            [.. JsonDocument.Parse(await client.GetStringAsync("/__control/extensions")).RootElement.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "mcp")];
        async Task<JsonElement> UntilAsync(string name, Func<JsonElement, bool> settled)
        {
            for (var tries = 0; ; tries++)
            {
                var item = (await McpItemsAsync()).Single(i => i.GetProperty("name").GetString() == name);
                if (settled(item) || tries == 200) return item;
                await Task.Delay(100);
            }
        }

        var document = JsonDocument.Parse(await client.GetStringAsync("/__control/extensions/mcp")).RootElement;
        Assert.Equal(Path.Combine(_host.DataRoot, "mcp.json"), document.GetProperty("path").GetString());
        Assert.Equal("", document.GetProperty("text").GetString());
        Assert.Empty(await McpItemsAsync());

        // Not JSON, and an entry that names both a command and an address: refused, nothing written.
        using (var notJson = await client.PutAsync("/__control/extensions/mcp", new StringContent("{ mcpServers: ")))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, notJson);
            Assert.Equal("json", JsonDocument.Parse(await notJson.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        }
        using (var both = await client.PutAsync("/__control/extensions/mcp", new StringContent("""{ "mcpServers": { "both": { "command": "x", "url": "http://127.0.0.1:9/mcp" } } }""")))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, both);
            var refused = JsonDocument.Parse(await both.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("refused", refused.GetProperty("reason").GetString());
            Assert.Equal("both", refused.GetProperty("notes")[0].GetProperty("entry").GetString());
        }
        Assert.False(File.Exists(Path.Combine(_host.DataRoot, "mcp.json")));

        // The snippet as an MCP server's documentation writes it — a local command with a secret in its environment, and a
        // turned-off address with a key in its query. The reader notes the key it does not know and keeps the entry.
        var snippet = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["stub"] = new { command = McpStubPath(), env = new { STUB_TOKEN = "s3cret" }, envFile = ".env" },
                ["remote"] = new { url = "http://127.0.0.1:9/mcp?key=s3cret", disabled = true },
            },
        });
        using (var saved = await client.PutAsync("/__control/extensions/mcp", new StringContent(snippet)))
        {
            HttpAssert.Status(HttpStatusCode.OK, saved);
            var notes = JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("notes");
            Assert.Contains(notes.EnumerateArray(), n => n.GetProperty("entry").GetString() == "stub" && n.GetProperty("kind").GetString() == "ignored");
        }
        Assert.Equal(snippet + "\n", File.ReadAllText(Path.Combine(_host.DataRoot, "mcp.json")));

        var stub = await UntilAsync("stub", i => i.GetProperty("state").GetString() is "connected" or "failed");
        Assert.Equal("connected", stub.GetProperty("state").GetString());
        Assert.Equal(1, stub.GetProperty("tools").GetInt32());
        Assert.True(stub.GetProperty("enabled").GetBoolean());
        Assert.False(stub.GetProperty("builtin").GetBoolean());
        Assert.Contains("(STUB_TOKEN)", stub.GetProperty("detail").GetString(), StringComparison.Ordinal);
        var remote = (await McpItemsAsync()).Single(i => i.GetProperty("name").GetString() == "remote");
        Assert.Equal("off", remote.GetProperty("state").GetString());
        Assert.Equal("http://127.0.0.1:9/mcp", remote.GetProperty("detail").GetString());
        Assert.DoesNotContain("s3cret", await client.GetStringAsync("/__control/extensions"), StringComparison.Ordinal);

        // Off closes it; on connects again.
        var turningOff = System.Diagnostics.Stopwatch.StartNew();
        using (var off = await client.PutAsync("/__control/extensions/mcp/stub/enabled", new StringContent("false")))
            HttpAssert.Status(HttpStatusCode.NoContent, off);
        Assert.True(turningOff.Elapsed < TimeSpan.FromSeconds(2), $"turning off took {turningOff.Elapsed}");
        Assert.Equal("off", (await McpItemsAsync()).Single(i => i.GetProperty("name").GetString() == "stub").GetProperty("state").GetString());
        using (var on = await client.PutAsync("/__control/extensions/mcp/stub/enabled", new StringContent("true")))
            HttpAssert.Status(HttpStatusCode.NoContent, on);
        Assert.Equal("connected", (await UntilAsync("stub", i => i.GetProperty("state").GetString() is "connected" or "failed")).GetProperty("state").GetString());
        using (var unknown = await client.PutAsync("/__control/extensions/mcp/nobody/enabled", new StringContent("true")))
            HttpAssert.Status(HttpStatusCode.NotFound, unknown);

        // An empty document removes the file and every server — at once: the old connections close in the background
        // (a stdio server is given seconds to exit, and a save does not wait for that).
        var clearing = System.Diagnostics.Stopwatch.StartNew();
        using (var cleared = await client.PutAsync("/__control/extensions/mcp", new StringContent("")))
            HttpAssert.Status(HttpStatusCode.OK, cleared);
        Assert.True(clearing.Elapsed < TimeSpan.FromSeconds(2), $"clearing took {clearing.Elapsed}");
        Assert.False(File.Exists(Path.Combine(_host.DataRoot, "mcp.json")));
        Assert.Empty(await McpItemsAsync());
    }

    [Fact]
    public async Task An_mcp_server_that_cannot_connect_says_why_in_a_code_a_person_can_act_on()
    {
        using var client = _host.ControlClient();
        var snippet = """{ "mcpServers": { "missing": { "command": "bohm-no-such-command-6f1d" }, "nobody": { "url": "http://127.0.0.1:9/mcp" } } }""";
        using (var saved = await client.PutAsync("/__control/extensions/mcp", new StringContent(snippet)))
            HttpAssert.Status(HttpStatusCode.OK, saved);

        var problems = new Dictionary<string, string?>();
        for (var tries = 0; problems.Count < 2 && tries < 300; tries++)
        {
            foreach (var item in JsonDocument.Parse(await client.GetStringAsync("/__control/extensions")).RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("kind").GetString() == "mcp" && item.GetProperty("state").GetString() == "failed")
                    problems[item.GetProperty("name").GetString()!] = item.GetProperty("problem").GetString();
            }
            if (problems.Count < 2) await Task.Delay(100);
        }

        Assert.Equal("command-not-found", problems.GetValueOrDefault("missing"));
        Assert.Equal("unreachable", problems.GetValueOrDefault("nobody"));
    }

    private static readonly string[] QuietArgs = ["--quiet"];

    [Fact]
    public async Task An_mcp_server_the_person_approved_is_added_to_the_document_beside_what_is_there()
    {
        using var client = _host.ControlClient();
        var file = Path.Combine(_host.DataRoot, "mcp.json");
        Task<HttpResponseMessage> AddAsync(object body) => client.PostAsync("/__control/extensions/mcp", new StringContent(JsonSerializer.Serialize(body)));
        string? Reason(HttpResponseMessage r) => JsonDocument.Parse(r.Content.ReadAsStringAsync().Result).RootElement.GetProperty("reason").GetString();

        // No document yet: one is made with mcpServers; a name in Korean stays readable.
        using (var added = await AddAsync(new { name = "파일", server = new { command = McpStubPath(), args = QuietArgs } }))
            HttpAssert.Status(HttpStatusCode.Created, added);
        var first = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        Assert.Equal("--quiet", first.GetProperty("mcpServers").GetProperty("파일").GetProperty("args")[0].GetString());
        Assert.Contains("\"파일\"", File.ReadAllText(file), StringComparison.Ordinal);

        // A second server goes beside the first; the same name again is a conflict and changes nothing.
        using (var second = await AddAsync(new { name = "remote", server = new { url = "http://127.0.0.1:9/mcp", headers = new { Authorization = "Bearer k" } } }))
            HttpAssert.Status(HttpStatusCode.Created, second);
        using (var again = await AddAsync(new { name = "파일", server = new { command = "other" } }))
            HttpAssert.Status(HttpStatusCode.Conflict, again);
        var both = JsonDocument.Parse(File.ReadAllText(file)).RootElement.GetProperty("mcpServers");
        Assert.Equal(["remote", "파일"], both.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(McpStubPath(), both.GetProperty("파일").GetProperty("command").GetString());

        // An entry that would not be used, no name, or no object: refused, nothing written.
        var before = File.ReadAllText(file);
        using (var refused = await AddAsync(new { name = "both", server = new { command = "x", url = "http://127.0.0.1:9/mcp" } }))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, refused);
            Assert.Equal("refused", Reason(refused));
        }
        using (var nameless = await AddAsync(new { name = " ", server = new { command = "x" } }))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, nameless);
            Assert.Equal("name", Reason(nameless));
        }
        using (var notObject = await AddAsync(new { name = "x", server = "npx x" }))
        {
            HttpAssert.Status(HttpStatusCode.BadRequest, notObject);
            Assert.Equal("json", Reason(notObject));
        }
        Assert.Equal(before, File.ReadAllText(file));

        // A document that keeps its servers under VS Code's «servers» gets the new one there.
        using (var replaced = await client.PutAsync("/__control/extensions/mcp", new StringContent("""{ "servers": { "a": { "command": "x" } } }""")))
            HttpAssert.Status(HttpStatusCode.OK, replaced);
        using (var beside = await AddAsync(new { name = "b", server = new { command = "y" } }))
            HttpAssert.Status(HttpStatusCode.Created, beside);
        var vsCode = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
        Assert.False(vsCode.TryGetProperty("mcpServers", out _));
        Assert.Equal(["a", "b"], vsCode.GetProperty("servers").EnumerateObject().Select(p => p.Name));

        using (var cleared = await client.PutAsync("/__control/extensions/mcp", new StringContent("")))
            HttpAssert.Status(HttpStatusCode.OK, cleared);
    }

    [Fact]
    public async Task A_web_questions_call_to_an_mcp_tool_is_described_for_the_person_then_made_by_the_runtime()
    {
        File.WriteAllText(Path.Combine(_host.DataRoot, "mcp.json"), JsonSerializer.Serialize(new { mcpServers = new { stub = new { command = McpStubPath() } } }));
        using var client = _host.ControlClient();

        // The server connects on the first look; a look while it connects waits a little, so try for a while.
        HttpResponseMessage? described = null;
        for (var i = 0; i < 20; i++)
        {
            described?.Dispose();
            described = await client.GetAsync("/__control/agent/mcp?name=mcp__stub__echo");
            if (described.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(500);
        }
        HttpAssert.Status(HttpStatusCode.OK, described!);
        var info = JsonDocument.Parse(await described!.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(("stub", "echo", false, "Says back the text it is given."),
            (info.GetProperty("server").GetString(), info.GetProperty("tool").GetString(), info.GetProperty("remote").GetBoolean(), info.GetProperty("description").GetString()));

        using var called = await client.PostAsync("/__control/agent/mcp", new StringContent("""{"name":"mcp__stub__echo","arguments":{"text":"안녕"}}"""));
        HttpAssert.Status(HttpStatusCode.OK, called);
        var result = JsonDocument.Parse(await called.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("안녕", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.False(result.GetProperty("failed").GetBoolean());

        using (var unknown = await client.GetAsync("/__control/agent/mcp?name=mcp__stub__nothing")) HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        using (var notMcp = await client.PostAsync("/__control/agent/mcp", new StringContent("""{"name":"read_page","arguments":{}}"""))) HttpAssert.Status(HttpStatusCode.NotFound, notMcp);
        using (var bad = await client.PostAsync("/__control/agent/mcp", new StringContent("not json"))) HttpAssert.Status(HttpStatusCode.BadRequest, bad);
        described!.Dispose();
    }

    [Fact]
    public async Task With_mcp_turned_off_by_policy_no_server_is_listed_or_offered_and_nothing_can_change_the_document()
    {
        await using var host = await RunningHost.StartAsync(configure: o => o with { McpAllowed = false });
        File.WriteAllText(Path.Combine(host.DataRoot, "mcp.json"), JsonSerializer.Serialize(new { mcpServers = new { stub = new { command = McpStubPath() } } }));
        using var client = host.ControlClient();

        var items = JsonDocument.Parse(await client.GetStringAsync("/__control/extensions")).RootElement.GetProperty("items");
        Assert.DoesNotContain(items.EnumerateArray(), i => i.GetProperty("kind").GetString() == "mcp");
        Assert.Empty(await host.Services.GetRequiredService<AppMcp>().ToolsAsync(CancellationToken.None));

        using (var replaced = await client.PutAsync("/__control/extensions/mcp", new StringContent("")))
        {
            HttpAssert.Status(HttpStatusCode.Forbidden, replaced);
            Assert.Equal("policy", JsonDocument.Parse(await replaced.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        }
        using (var added = await client.PostAsync("/__control/extensions/mcp", new StringContent(JsonSerializer.Serialize(new { name = "x", server = new { command = "x" } }))))
            HttpAssert.Status(HttpStatusCode.Forbidden, added);
        using (var on = await client.PutAsync("/__control/extensions/mcp/stub/enabled", new StringContent("true")))
            HttpAssert.Status(HttpStatusCode.Forbidden, on);
        Assert.True(File.Exists(Path.Combine(host.DataRoot, "mcp.json")));   // the person's document is left as it is
    }

    [Fact]
    public void A_failure_without_a_known_cause_keeps_its_own_message()
    {
        Assert.Equal("timed-out", AppMcp.Why(null));
        Assert.Equal("timed-out", AppMcp.Why(new AggregateException(new TaskCanceledException())));
        Assert.Equal("unauthorized", AppMcp.Why(new InvalidOperationException("wrap", new HttpRequestException("no", null, HttpStatusCode.Forbidden))));
        Assert.Equal("The server closed the stream.", AppMcp.Why(new AggregateException(new IOException("The server closed the stream."))));
    }

    [Fact]
    public void A_command_is_found_by_its_path_by_the_working_directory_or_on_the_path()
    {
        var stub = McpStubPath();
        Assert.True(AppMcp.CommandExists(new IronHive.Agent.Mcp.McpPluginConfig { Command = stub }));
        Assert.True(AppMcp.CommandExists(new IronHive.Agent.Mcp.McpPluginConfig { Command = Path.Combine(".", Path.GetFileName(stub)), WorkingDirectory = Path.GetDirectoryName(stub) }));
        Assert.True(AppMcp.CommandExists(new IronHive.Agent.Mcp.McpPluginConfig
        {
            Command = OperatingSystem.IsWindows() ? Path.GetFileNameWithoutExtension(stub) : Path.GetFileName(stub),   // as typed on each system
            Environment = new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(stub)! },
        }));
        Assert.False(AppMcp.CommandExists(new IronHive.Agent.Mcp.McpPluginConfig { Command = "bohm-no-such-command-6f1d" }));
        Assert.False(AppMcp.CommandExists(new IronHive.Agent.Mcp.McpPluginConfig { Command = Path.Combine(Path.GetTempPath(), "bohm-no-such-command-6f1d.exe") }));
    }

    [Fact]
    public async Task An_answer_to_a_question_nobody_asked_is_not_found_and_a_non_answer_is_refused()
    {
        using var client = _host.ControlClient();
        using (var unknown = await client.PostAsync("/__control/approvals/0123456789abcdef0123456789abcdef", new StringContent("true")))
            HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        using (var bad = await client.PostAsync("/__control/approvals/0123456789abcdef0123456789abcdef", new StringContent("\"yes\"")))
            HttpAssert.Status(HttpStatusCode.BadRequest, bad);
        using (var forServer = await client.PostAsync("/__control/approvals/0123456789abcdef0123456789abcdef", new StringContent("\"server\"")))
            HttpAssert.Status(HttpStatusCode.NotFound, forServer);   // an answer — for a question nobody asked
    }


    [Fact]
    public async Task Kept_records_are_merged_with_those_written_since_and_given_back_through_the_control_api()
    {
        var id = (await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes("<script>localStorage.getItem('loans')</script>"))).Id;
        await using (var storage = await _host.Catalog.OpenStorageAsync(id))
        {
            await storage.ApplyAsync([LocalOrigin.Storage.KeyValueOperation.Set("loans", """[{"id":1}]""")]);
            await _host.Catalog.ReviseAsync(id, Encoding.UTF8.GetBytes("<script>/* v2 */ localStorage.getItem('loans')</script>"), null, storage);
            await storage.ApplyAsync([LocalOrigin.Storage.KeyValueOperation.Set("loans", """[{"id":1},{"id":2}]""")]);
            await _host.Catalog.RevertAsync(id, storage);
            await storage.ApplyAsync([LocalOrigin.Storage.KeyValueOperation.Set("loans", """[{"id":1},{"id":3}]""")]);
        }

        using var client = _host.ControlClient();
        async Task<JsonElement> KeptAsync() => JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{id}/revisions")).RootElement[1];

        var kept = await KeptAsync();
        Assert.Equal("mergeable", kept.GetProperty("undone").GetString());
        Assert.Equal(1, kept.GetProperty("incoming").GetInt32());
        using (var undoFirst = await client.PostAsync($"/__control/apps/{id}/revisions/2/undo-merge", null))
            HttpAssert.Status(HttpStatusCode.Conflict, undoFirst);
        using (var merged = await client.PostAsync($"/__control/apps/{id}/revisions/2/merge", null))
            HttpAssert.Status(HttpStatusCode.OK, merged);
        Assert.Equal("merged", (await KeptAsync()).GetProperty("undone").GetString());
        using (var back = await client.PostAsync($"/__control/apps/{id}/revisions/2/undo-merge", null))
            HttpAssert.Status(HttpStatusCode.OK, back);
        Assert.Equal("mergeable", (await KeptAsync()).GetProperty("undone").GetString());
    }

    [Fact]
    public async Task A_suspected_loss_is_counted_in_the_application_usage_record()
    {
        var id = (await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes(Page))).Id;
        using var client = _host.ControlClient();

        using (var recorded = await client.PostAsync($"/__control/apps/{id}/loss-suspected", null))
            HttpAssert.Status(HttpStatusCode.NoContent, recorded);
        using (var unknown = await client.PostAsync("/__control/apps/0123456789abcdef0123456789abcdef/loss-suspected", null))
            HttpAssert.Status(HttpStatusCode.NotFound, unknown);

        var days = JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{id}/usage")).RootElement.GetProperty("days");
        Assert.Equal(1, days.EnumerateArray().Sum(d => d.GetProperty("lossSuspected").GetInt32()));
    }

    [Fact]
    public async Task The_shell_can_ask_whether_the_runtime_still_answers()
    {
        using var answered = await _host.ControlClient().GetAsync("/__control/health");
        using var none = await _host.ControlClient(secret: null).GetAsync("/__control/health");

        HttpAssert.Status(HttpStatusCode.OK, answered);
        Assert.Equal("Healthy", await answered.Content.ReadAsStringAsync());
        HttpAssert.Status(HttpStatusCode.Unauthorized, none);
    }

    [Fact]
    public async Task Requests_without_the_secret_are_refused()
    {
        using var none = await _host.ControlClient(secret: null).GetAsync("/__control/apps");
        using var wrong = await _host.ControlClient("guess").GetAsync("/__control/apps");

        HttpAssert.Status(HttpStatusCode.Unauthorized, none);
        HttpAssert.Status(HttpStatusCode.Unauthorized, wrong);
    }

    [Fact]
    public async Task Without_a_configured_secret_there_is_no_control_api()
    {
        await using var host = await RunningHost.StartAsync(secret: null);

        using var response = await host.ControlClient("anything").GetAsync("/__control/apps");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task The_control_api_is_not_reachable_from_an_application_origin()
    {
        var id = await _host.AdoptAsync(Page);
        var client = _host.ClientForApp(id);
        client.DefaultRequestHeaders.Authorization = new("Bearer", RunningHost.Secret);

        using var response = await client.GetAsync("/__control/apps");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task An_archived_application_is_listed_as_archived_and_not_served_until_restored()
    {
        var id = await _host.AdoptAsync(Page);
        using var client = _host.ControlClient();

        using (var archived = await client.PostAsync($"/__control/apps/{id}/archive", null))
        {
            HttpAssert.Status(HttpStatusCode.OK, archived);
            Assert.Equal(JsonValueKind.String, JsonDocument.Parse(await archived.Content.ReadAsStringAsync()).RootElement.GetProperty("archivedAt").ValueKind);
        }
        var listed = JsonDocument.Parse(await client.GetStringAsync("/__control/apps")).RootElement.EnumerateArray().Single(a => a.GetProperty("id").GetString() == id);
        Assert.Equal(JsonValueKind.String, listed.GetProperty("archivedAt").ValueKind);
        using (var page = await _host.ClientForApp(id).GetAsync("/"))
            HttpAssert.Status(HttpStatusCode.NotFound, page);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/"))
        {
            // A tab that reaches it anyway is told why, in the person's language — not the browser's own error.
            request.Headers.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9,en;q=0.8");
            using var page = await _host.ClientForApp(id).SendAsync(request);
            HttpAssert.Status(HttpStatusCode.NotFound, page);
            Assert.Contains("보관돼 있습니다", await page.Content.ReadAsStringAsync());
        }

        using (var restored = await client.PostAsync($"/__control/apps/{id}/restore", null))
        {
            HttpAssert.Status(HttpStatusCode.OK, restored);
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await restored.Content.ReadAsStringAsync()).RootElement.GetProperty("archivedAt").ValueKind);
        }
        using (var page = await _host.ClientForApp(id).GetAsync("/"))
            HttpAssert.Status(HttpStatusCode.OK, page);
        using (var unknown = await client.PostAsync("/__control/apps/0123456789abcdef0123456789abcdef/archive", null))
            HttpAssert.Status(HttpStatusCode.NotFound, unknown);
    }

    [Fact]
    public async Task The_usage_report_marks_the_day_an_application_was_put_away()
    {
        var kept = await _host.AdoptAsync(Page);
        var put = await _host.AdoptAsync(Page + "<p>put away</p>");
        using var client = _host.ControlClient();
        (await client.PostAsync($"/__control/apps/{put}/archive", null)).Dispose();

        var apps = JsonDocument.Parse(await client.GetStringAsync("/__control/usage-report")).RootElement.GetProperty("apps").EnumerateArray().ToList();
        var archivedOn = apps.Single(a => a.GetProperty("id").GetString() == put).GetProperty("archivedOn");
        Assert.Equal(DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), archivedOn.GetString());
        Assert.Equal(JsonValueKind.Null, apps.Single(a => a.GetProperty("id").GetString() == kept).GetProperty("archivedOn").ValueKind);
    }

    [Fact]
    public async Task Adopting_returns_the_application_and_its_origin()
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Page));
        content.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString("다운로드/재고 관리.html"));

        using var response = await _host.ControlClient().PostAsync("/__control/apps", content);
        var app = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        HttpAssert.Status(HttpStatusCode.Created, response);
        var id = app.GetProperty("id").GetString()!;
        Assert.Equal($"http://{id}.localhost:{_host.Port}/", app.GetProperty("origin").GetString());
        Assert.Equal("다운로드/재고 관리.html", app.GetProperty("originalPath").GetString());
        Assert.Equal(Encoding.UTF8.GetBytes(Page), await _host.Catalog.ReadHtmlAsync(id));
    }

    [Fact]
    public async Task Stopping_right_after_an_adoption_leaves_nothing_open_in_the_data_root()
    {
        // Adoption starts a background fetch of the application's code. Stopping the host at once
        // must cancel and wait for it, so the data root can be removed (no file left open).
        for (var i = 0; i < 10; i++)
        {
            var host = await RunningHost.StartAsync();
            using (var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Page)))
            using (var adopted = await host.ControlClient().PostAsync("/__control/apps", content))
                HttpAssert.Status(HttpStatusCode.Created, adopted);
            await host.DisposeAsync(); // Deletes the data root; throws if a file is still open.
            Assert.False(Directory.Exists(host.DataRoot));
        }
    }

    [Fact]
    public async Task One_application_that_cannot_be_opened_does_not_fail_the_list()
    {
        var kept = await _host.AdoptAsync(Page);
        var held = await _host.AdoptAsync(Page + "<p>held</p>");
        using var client = _host.ControlClient();

        string listed, unreadable;
        using (new FileStream(Path.Combine(_host.DataRoot, "adopted", held, "app.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            listed = await client.GetStringAsync("/__control/apps");
            unreadable = await client.GetStringAsync("/__control/apps/unreadable");
        }

        Assert.Equal([kept], JsonDocument.Parse(listed).RootElement.EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        var entry = Assert.Single(JsonDocument.Parse(unreadable).RootElement.EnumerateArray().ToList());
        Assert.Equal(held, entry.GetProperty("id").GetString());
        Assert.Equal("cannotOpen", entry.GetProperty("kind").GetString());
        Assert.Equal(0, JsonDocument.Parse(await client.GetStringAsync("/__control/apps/unreadable")).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task An_application_whose_files_cannot_be_opened_says_so_and_opens_once_they_can()
    {
        // Its data cannot be opened right now (held exclusively here — a file kept only in the cloud
        // while offline fails the same way). The failure must not stick until the host restarts.
        var id = await _host.AdoptAsync(Page);
        var journal = Path.Combine(_host.DataRoot, "adopted", id, "storage", "journal.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        using (new FileStream(journal, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var page = await _host.ClientForApp(id).GetAsync("/");
            HttpAssert.Status(HttpStatusCode.ServiceUnavailable, page);
            Assert.Contains("Nothing was deleted", await page.Content.ReadAsStringAsync());
        }

        using (var page = await _host.ClientForApp(id).GetAsync("/"))
            HttpAssert.Status(HttpStatusCode.OK, page);
    }

    [Fact]
    public async Task An_empty_body_is_not_adopted()
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps", new ByteArrayContent([]));

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Empty(await _host.Catalog.ListAsync());
    }

    [Fact]
    public async Task Earlier_adoptions_of_the_same_file_are_reported_before_adopting_again()
    {
        var first = await _host.AdoptAsync(Page);

        using var same = await _host.ControlClient().PostAsync("/__control/apps/matches", new ByteArrayContent(Encoding.UTF8.GetBytes(Page)));
        using var other = await _host.ControlClient().PostAsync("/__control/apps/matches", new ByteArrayContent(Encoding.UTF8.GetBytes("<p>other</p>")));

        var match = JsonDocument.Parse(await same.Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal(first, match.GetProperty("app").GetProperty("id").GetString());
        Assert.Equal("sameBytes", match.GetProperty("match").GetString());
        Assert.Equal(0, JsonDocument.Parse(await other.Content.ReadAsStringAsync()).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_revised_file_from_the_same_path_is_reported_as_another_version()
    {
        const string path = "다운로드/도서대출.html";
        using var v1 = new ByteArrayContent(Encoding.UTF8.GetBytes(Page));
        v1.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString(path));
        using var adopted = await _host.ControlClient().PostAsync("/__control/apps", v1);
        var first = JsonDocument.Parse(await adopted.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        using var v2 = new ByteArrayContent(Encoding.UTF8.GetBytes(Page + "<button>반납</button>"));
        v2.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString(path));
        using var response = await _host.ControlClient().PostAsync("/__control/apps/matches", v2);

        var match = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal(first, match.GetProperty("app").GetProperty("id").GetString());
        Assert.Equal("sameOriginalPath", match.GetProperty("match").GetString());
    }

    [Fact]
    public async Task A_repeated_download_beside_the_file_is_reported_by_name()
    {
        using var v1 = new ByteArrayContent(Encoding.UTF8.GetBytes(Page));
        v1.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString("다운로드/도서대출.html"));
        using var adopted = await _host.ControlClient().PostAsync("/__control/apps", v1);
        var first = JsonDocument.Parse(await adopted.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();

        using var v2 = new ByteArrayContent(Encoding.UTF8.GetBytes(Page + "<button>반납</button>"));
        v2.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString("다운로드/도서대출 (1).html"));
        using var response = await _host.ControlClient().PostAsync("/__control/apps/matches", v2);

        var match = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement[0];
        Assert.Equal(first, match.GetProperty("app").GetProperty("id").GetString());
        Assert.Equal("sameName", match.GetProperty("match").GetString());
    }

    [Fact]
    public async Task A_new_revision_runs_new_code_on_the_same_data_and_the_old_page_can_no_longer_write()
    {
        var id = await _host.AdoptAsync(Page);
        var oldPage = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, oldPage, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"}]}""")).Dispose();

        using var revised = await ReviseAsync(id, Page + "<button>반납</button>");
        var app = JsonDocument.Parse(await revised.Content.ReadAsStringAsync()).RootElement;
        using var stale = await _host.PostStorageAsync(id, oldPage, """{"tab":"TAB","ops":[{"seq":2,"op":"set","key":"loan","value":"old code"}]}""");
        var newPage = await _host.LoadAsync(id);

        HttpAssert.Status(HttpStatusCode.Created, revised);
        Assert.Equal(id, app.GetProperty("id").GetString());
        Assert.Equal(2, app.GetProperty("revision").GetInt32());
        Assert.True(app.GetProperty("canRevert").GetBoolean());
        HttpAssert.Status(HttpStatusCode.Conflict, stale);   // «retired» — the page is told its code was replaced
        Assert.Equal("""{"loan":"3"}""", newPage.Items);
        Assert.Equal(Encoding.UTF8.GetBytes(Page + "<button>반납</button>"), await _host.Catalog.ReadHtmlAsync(id));
        Assert.Contains("\"event\":\"revised\"", await File.ReadAllTextAsync(Path.Combine(_host.DataRoot, "adopted", id, "usage.ndjson")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_refused_because_the_code_was_replaced_is_counted_as_a_suspected_loss()
    {
        var id = await _host.AdoptAsync(Page);
        var oldPage = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, oldPage, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"}]}""")).Dispose();
        (await ReviseAsync(id, Page + "<button>반납</button>")).Dispose();

        // A resend of what was already applied loses nothing; a write made after the replacement does.
        using var resent = await _host.PostStorageAsync(id, oldPage, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"}]}""");
        Assert.Equal(0, await LossSuspectedAsync(id));
        using var late = await _host.PostStorageAsync(id, oldPage, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"},{"seq":2,"op":"set","key":"closes","value":"1"}]}""");

        // The page is told its code was replaced (409 «retired»), so its script stops resending; a resend that still
        // comes is not handed over again.
        using var again = await _host.PostStorageAsync(id, oldPage, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"},{"seq":2,"op":"set","key":"closes","value":"1"}]}""");
        HttpAssert.Status(HttpStatusCode.Conflict, resent);
        HttpAssert.Status(HttpStatusCode.Conflict, late);
        HttpAssert.Status(HttpStatusCode.Conflict, again);
        Assert.True(JsonDocument.Parse(await late.Content.ReadAsStringAsync()).RootElement.GetProperty("retired").GetBoolean());
        Assert.Equal(1, await LossSuspectedAsync(id));

        // The refused write is kept beside the application as it was made — only the one never applied — and the
        // application's status says so without quoting it.
        var kept = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_host.DataRoot, "adopted", id, "refused-writes.ndjson")));
        var line = JsonDocument.Parse(kept).RootElement;
        Assert.Equal("set", line.GetProperty("op").GetString());
        Assert.Equal("closes", line.GetProperty("key").GetString());
        Assert.Equal("1", line.GetProperty("value").GetString());
        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/status")).RootElement;
        Assert.Contains(status.GetProperty("recentErrors").EnumerateArray(), e => e.GetString()!.Contains("refused-writes.ndjson", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_write_under_an_unknown_tab_is_refused_without_counting_a_loss()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);

        using var response = await _host.PostStorageAsync(id, page, """{"tab":"0123456789abcdef0123456789abcdef","ops":[{"seq":1,"op":"set","key":"a","value":"1"}]}""");

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
        Assert.Equal(0, await LossSuspectedAsync(id));
    }

    [Fact]
    public async Task A_page_reports_the_last_write_it_issued_even_when_that_write_has_not_arrived()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);
        using var client = _host.ControlClient();

        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"a","value":"1"}],"issued":1}""")).Dispose();
        // The page wrote a second time while leaving, and that request never arrived; the report sent after it did.
        using var report = await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[],"issued":2,"left":true}""");

        HttpAssert.Status(HttpStatusCode.OK, report);
        var view = JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{id}/tabs/{page.Tab}")).RootElement;
        Assert.Equal(1, view.GetProperty("ack").GetInt64());
        Assert.Equal(2, view.GetProperty("issued").GetInt64());
        Assert.True(view.GetProperty("left").GetBoolean());
    }

    private async Task<int> LossSuspectedAsync(string id) =>
        JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/usage")).RootElement
            .GetProperty("days").EnumerateArray().Sum(d => d.GetProperty("lossSuspected").GetInt32());

    [Fact]
    public async Task Reverting_puts_back_the_previous_code_and_data()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"}]}""")).Dispose();
        (await ReviseAsync(id, "<p>broken</p>")).Dispose();
        var broken = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, broken, """{"tab":"TAB","ops":[{"seq":1,"op":"remove","key":"loan"}]}""")).Dispose();

        using var reverted = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions/revert", null);
        var app = JsonDocument.Parse(await reverted.Content.ReadAsStringAsync()).RootElement;
        using var again = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions/revert", null);

        HttpAssert.Status(HttpStatusCode.OK, reverted);
        Assert.Equal(1, app.GetProperty("revision").GetInt32());
        Assert.False(app.GetProperty("canRevert").GetBoolean());
        Assert.Equal("""{"loan":"3"}""", (await _host.LoadAsync(id)).Items);
        Assert.Equal(Encoding.UTF8.GetBytes(Page), await _host.Catalog.ReadHtmlAsync(id));
        HttpAssert.Status(HttpStatusCode.Conflict, again);
    }

    [Fact]
    public async Task The_same_bytes_or_an_unknown_application_are_not_revised()
    {
        var id = await _host.AdoptAsync(Page);

        using var same = await ReviseAsync(id, Page);
        using var unknown = await ReviseAsync("0123456789abcdef0123456789abcdef", Page);
        using var empty = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions", new ByteArrayContent([]));

        HttpAssert.Status(HttpStatusCode.Conflict, same);
        HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        HttpAssert.Status(HttpStatusCode.BadRequest, empty);
        Assert.Equal(1, (await _host.Catalog.GetAsync(id))!.Revision);
    }

    [Fact]
    public async Task A_change_keeps_its_request_and_going_back_to_any_revision_keeps_the_data()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"loan","value":"3"}]}""")).Dispose();
        var change = new ByteArrayContent(Encoding.UTF8.GetBytes("<p>blue</p>"));
        change.Headers.Add("X-Bohm-Request", Uri.EscapeDataString("  버튼을 파랗게 해 줘 \n"));
        (await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions", change)).Dispose();
        (await ReviseAsync(id, "<p>wide</p>")).Dispose();
        var wide = await _host.LoadAsync(id);
        (await _host.PostStorageAsync(id, wide, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"note","value":"kept"}]}""")).Dispose();

        using var restored = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions/1/restore", null);
        using var inUse = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions/4/restore", null);
        using var unknown = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions/99/restore", null);

        HttpAssert.Status(HttpStatusCode.Created, restored);
        Assert.Equal(4, JsonDocument.Parse(await restored.Content.ReadAsStringAsync()).RootElement.GetProperty("revision").GetInt32());
        HttpAssert.Status(HttpStatusCode.Conflict, inUse);
        HttpAssert.Status(HttpStatusCode.NotFound, unknown);
        Assert.Equal(Encoding.UTF8.GetBytes(Page), await _host.Catalog.ReadHtmlAsync(id));
        Assert.Equal("""{"loan":"3","note":"kept"}""", (await _host.LoadAsync(id)).Items); // the data is not wound back

        var history = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/revisions")).RootElement.EnumerateArray().ToList();
        Assert.Equal([null, "버튼을 파랗게 해 줘", null, null], history.Select(r => r.GetProperty("request").GetString()));
        Assert.Equal([0, 0, 0, 1], history.Select(r => r.GetProperty("restoredFrom").ValueKind == JsonValueKind.Number ? r.GetProperty("restoredFrom").GetInt32() : 0));
        Assert.Equal("도서대출.html", history[2].GetProperty("file").GetString());
        Assert.Equal(history[0].GetProperty("sha256").GetString(), history[3].GetProperty("sha256").GetString()); // the same code as revision 1

        // «Revert» undoes going back: the code before, and the data as it was at that moment — the same data.
        using (var reverted = await _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions/revert", null))
            Assert.Equal(3, JsonDocument.Parse(await reverted.Content.ReadAsStringAsync()).RootElement.GetProperty("revision").GetInt32());
        Assert.Equal("""{"loan":"3","note":"kept"}""", (await _host.LoadAsync(id)).Items);

        // What the person asked is theirs: a package of the application carries the history, not the words.
        var package = Path.Combine(Path.GetTempPath(), $"bohm-request-{Guid.NewGuid():n}.bohm");
        using (var packed = await _host.ControlClient().PostAsync($"/__control/apps/{id}/package?data=none", new StringContent(package, Encoding.UTF8)))
            HttpAssert.Status(HttpStatusCode.OK, packed);
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(package);
            var records = zip.Entries.Where(e => e.FullName.EndsWith("/revision.json", StringComparison.Ordinal)).Select(e => new StreamReader(e.Open()).ReadToEnd()).ToList();
            Assert.Equal(4, records.Count);
            Assert.All(records, r => Assert.DoesNotContain("request", r, StringComparison.Ordinal));
            Assert.Contains(records, r => JsonDocument.Parse(r).RootElement.TryGetProperty("restoredFrom", out var from) && from.GetInt32() == 1);
        }
        finally
        {
            File.Delete(package);
        }
    }

    private Task<HttpResponseMessage> ReviseAsync(string id, string html)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(html));
        content.Headers.Add("X-Bohm-Original-Path", Uri.EscapeDataString("다운로드/도서대출.html"));
        return _host.ControlClient().PostAsync($"/__control/apps/{id}/revisions", content);
    }

    [Fact]
    public async Task Listing_shows_every_adopted_application()
    {
        var a = await _host.AdoptAsync(Page);
        var b = await _host.AdoptAsync("<p>b</p>");

        var list = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/apps")).RootElement;

        Assert.Equal([a, b], list.EnumerateArray().Select(e => e.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task The_runtime_says_which_edition_of_the_application_contract_it_serves()
    {
        var facts = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/runtime")).RootElement;

        Assert.Equal(AppContract.Edition, facts.GetProperty("contract").GetInt32());
    }

    [Fact]
    public async Task Status_reports_todays_signals_without_any_content()
    {
        var id = await _host.AdoptAsync(Page);
        using var load = await _host.ClientForApp(id).GetAsync("/");

        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/status")).RootElement;

        Assert.True(status.GetProperty("opened").GetBoolean());
        Assert.False(status.GetProperty("input").GetBoolean());
        Assert.False(status.GetProperty("wrote").GetBoolean());
        Assert.Equal(0, status.GetProperty("loadErrors").GetInt32());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("onlineOnlyStorage").ValueKind);
    }

    [Fact]
    public async Task Status_names_the_online_database_an_application_keeps_its_data_only_in()
    {
        var id = await _host.AdoptAsync("""
            <form></form><script type="module">
            import { getFirestore } from "https://www.gstatic.com/firebasejs/11.6.1/firebase-firestore.js";
            </script>
            """);

        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/status")).RootElement;

        Assert.Equal("firestore", status.GetProperty("onlineOnlyStorage").GetString());
    }

    [Fact]
    public async Task Usage_reports_each_day_the_first_day_of_use_and_retention()
    {
        var id = await _host.AdoptAsync(Page);
        var today = DateOnly.FromDateTime(DateTime.Now);
        string Day(int back) => today.AddDays(-back).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        // Day 0 was 30 days ago; used again on day 29 — inside the fifth week.
        File.WriteAllLines(Path.Combine(_host.DataRoot, "adopted", id, "usage.ndjson"),
        [
            $$"""{"date":"{{Day(30)}}","signal":"opened"}""",
            $$"""{"date":"{{Day(30)}}","signal":"input"}""",
            $$"""{"date":"{{Day(30)}}","signal":"wrote"}""",
            $$"""{"date":"{{Day(1)}}","signal":"opened"}""",
            $$"""{"date":"{{Day(1)}}","signal":"input"}""",
            $$"""{"date":"{{Day(1)}}","event":"load-error"}""",
            $$"""{"date":"{{Day(1)}}","event":"revised"}""",
        ]);

        var usage = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/usage")).RootElement;

        Assert.Equal(Day(0), usage.GetProperty("today").GetString());
        Assert.Equal(Day(30), usage.GetProperty("firstUsed").GetString());
        Assert.Equal(Day(1), usage.GetProperty("lastUsed").GetString());
        Assert.Equal(30, usage.GetProperty("day").GetInt32());
        Assert.Equal("retained", usage.GetProperty("retention").GetString());
        var days = usage.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(2, days.Count);
        Assert.True(days[0].GetProperty("wrote").GetBoolean());
        Assert.Equal(1, days[1].GetProperty("loadErrors").GetInt32());
        Assert.Equal("revised", usage.GetProperty("revisions")[0].GetProperty("event").GetString());
        // Only facts — no keys, values or text of the application's data.
        Assert.DoesNotContain("Stock", usage.GetRawText());
    }

    [Fact]
    public async Task Usage_of_an_application_never_used_has_not_started()
    {
        var id = await _host.AdoptAsync(Page);

        var usage = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/usage")).RootElement;

        Assert.Equal("not-started", usage.GetProperty("retention").GetString());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("firstUsed").ValueKind);
        Assert.Empty(usage.GetProperty("days").EnumerateArray());
    }

    [Fact]
    public async Task A_page_reports_how_its_reads_matched_the_data_and_usage_shows_it_per_revision()
    {
        var id = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(id);

        async Task<HttpResponseMessage> Report(string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/usage")
            {
                Content = new StringContent(json.Replace("TAB", page.Tab, StringComparison.Ordinal), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Cookie", page.Cookie);
            request.Headers.Add("X-Bohm-Request", "1");
            return await _host.ClientForApp(id).SendAsync(request);
        }

        using (var ok = await Report("""{"tab":"TAB","kind":"keys","missing":1,"unread":2,"seeded":2}"""))
            HttpAssert.Status(HttpStatusCode.OK, ok);
        // More unread keys than it was given is not a fact a page can observe.
        using (var bad = await Report("""{"tab":"TAB","kind":"keys","missing":0,"unread":3,"seeded":2}"""))
            HttpAssert.Status(HttpStatusCode.BadRequest, bad);
        using (var missing = await Report("""{"tab":"TAB","kind":"keys","missing":1}"""))
            HttpAssert.Status(HttpStatusCode.BadRequest, missing);

        var usage = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/usage")).RootElement;
        var keys = Assert.Single(usage.GetProperty("keys").EnumerateArray());
        Assert.Equal(1, keys.GetProperty("revision").GetInt32());
        Assert.Equal(1, keys.GetProperty("missing").GetInt32());
        Assert.Equal(2, keys.GetProperty("unread").GetInt32());
        Assert.Equal(2, keys.GetProperty("seeded").GetInt32());
    }

    [Fact]
    public async Task The_usage_report_has_every_application_by_id_and_nothing_that_names_or_holds_its_data()
    {
        var a = await _host.Catalog.AdoptAsync(Encoding.UTF8.GetBytes("<!doctype html><title>Payroll</title>"), @"C:\Users\someone\Desktop\payroll-2026.html");
        var b = await _host.AdoptAsync(Page);
        var page = await _host.LoadAsync(a.Id);
        using (var write = await _host.PostStorageAsync(a.Id, page, """{"tab":"TAB","ops":[{"seq":1,"op":"set","key":"salary-kim","value":"secret-4200"}]}"""))
            HttpAssert.Status(HttpStatusCode.OK, write);

        var text = await _host.ControlClient().GetStringAsync("/__control/usage-report");
        var report = JsonDocument.Parse(text).RootElement;

        Assert.Equal("bohm.usage-report/0", report.GetProperty("format").GetString());
        Assert.Equal([a.Id, b], report.GetProperty("apps").EnumerateArray().Select(e => e.GetProperty("id").GetString()));
        var first = report.GetProperty("apps")[0];
        Assert.Equal(1, first.GetProperty("revision").GetInt32());
        Assert.True(first.GetProperty("usage").GetProperty("days")[0].GetProperty("wrote").GetBoolean());
        foreach (var leak in new[] { "payroll", "someone", "Desktop", "salary", "secret", "Payroll", "Stock" })
            Assert.DoesNotContain(leak, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Usage_of_an_unknown_application_is_not_found()
    {
        using var response = await _host.ControlClient().GetAsync("/__control/apps/0123456789abcdef0123456789abcdef/usage");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task Status_of_an_unknown_application_is_not_found()
    {
        using var response = await _host.ControlClient().GetAsync("/__control/apps/0123456789abcdef0123456789abcdef/status");

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Fact]
    public async Task Drain_reports_quiet_when_nothing_is_being_written()
    {
        using var response = await _host.ControlClient().PostAsync("/__control/drain", null);

        Assert.True(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("quiet").GetBoolean());
    }

    [Fact]
    public async Task The_usage_record_holds_dates_and_signals_only()
    {
        var id = await _host.AdoptAsync(Page);
        using var load = await _host.ClientForApp(id).GetAsync("/");

        var lines = await File.ReadAllLinesAsync(Path.Combine(_host.DataRoot, "adopted", id, "usage.ndjson"));

        var line = Assert.Single(lines);
        Assert.Matches("""^\{"date":"\d{4}-\d{2}-\d{2}","signal":"opened"\}$""", line);
    }

    [Fact]
    public async Task Listing_carries_the_last_day_each_application_was_used()
    {
        var used = await _host.AdoptAsync(Page);
        var neverUsed = await _host.AdoptAsync("<p>b</p>");
        await File.WriteAllLinesAsync(Path.Combine(_host.DataRoot, "adopted", used, "usage.ndjson"),
            ["""{"date":"2026-09-10","signal":"opened"}""", """{"date":"2026-09-10","signal":"input"}"""]);

        var list = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/apps")).RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("lastUsed"));

        Assert.Equal("2026-09-10", list[used].GetString());
        Assert.Equal(JsonValueKind.Null, list[neverUsed].ValueKind);
    }

    [Fact]
    public async Task Loading_twice_on_one_day_records_the_signal_once()
    {
        var id = await _host.AdoptAsync(Page);
        (await _host.ClientForApp(id).GetAsync("/")).Dispose();
        (await _host.ClientForApp(id).GetAsync("/")).Dispose();

        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_host.DataRoot, "adopted", id, "usage.ndjson")));
    }

    [Fact]
    public async Task Control_responses_are_json()
    {
        using var response = await _host.ControlClient().GetAsync("/__control/apps");

        Assert.Equal(new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" }, response.Content.Headers.ContentType);
    }
}
