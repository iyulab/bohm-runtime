using Bohm.Runtime.Assets;
using Bohm.Runtime.Tests.Storage;

namespace Bohm.Runtime.Tests.Assets;

public sealed class AssetScannerTests
{
    [Fact]
    public void Finds_scripts_modules_styles_and_module_imports_written_in_a_document()
    {
        var found = AssetScanner.ScanHtml("""
            <script src="https://cdn.example/lib.js"></script>
            <script type="module" src="https://cdn.example/app.mjs"></script>
            <script type="module">import { a } from "https://esm.example/pkg"; import("https://esm.example/lazy");</script>
            <link rel="stylesheet" href="https://fonts.example/css?family=X&amp;display=swap">
            <link rel="modulepreload" href="https://cdn.example/pre.mjs">
            <style>@import url("https://cdn.example/extra.css"); @font-face { src: url(https://fonts.example/f.woff2) }</style>
            <script src="footer.js"></script>
            <script type="module">import React from "react";</script>
            """);

        Assert.Equal(
            [
                ("https://cdn.example/lib.js", AssetKind.Script),
                ("https://cdn.example/app.mjs", AssetKind.Module),
                ("https://esm.example/pkg", AssetKind.Module),
                ("https://esm.example/lazy", AssetKind.Module),
                ("https://fonts.example/css?family=X&display=swap", AssetKind.Style),
                ("https://cdn.example/pre.mjs", AssetKind.Module),
                ("https://cdn.example/extra.css", AssetKind.Style),
                ("https://fonts.example/f.woff2", AssetKind.Font),
            ],
            found.Select(r => (r.Url.AbsoluteUri, r.Kind)));
    }

    [Fact]
    public void Follows_minified_module_imports_relative_to_the_module()
    {
        var found = AssetScanner.ScanModule("""import{x as y}from"/npm/dep@1/+esm";export*from"./util.js";import"https://other.example/side.js";""",
            new Uri("https://cdn.example/npm/pkg@2/+esm"));

        Assert.Equal(["https://cdn.example/npm/dep@1/+esm", "https://cdn.example/npm/pkg@2/util.js", "https://other.example/side.js"],
            found.Select(r => r.Url.AbsoluteUri));
    }
}

public sealed class AssetCacheTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bohm-assets-").FullName;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Caches_code_follows_redirects_and_module_imports_and_records_where_it_came_from()
    {
        var cdn = new FakeCdn()
            .Redirect("https://esm.example/pkg", "https://cdn.example/npm/pkg/+esm")
            .File("https://cdn.example/npm/pkg/+esm", "application/javascript; charset=utf-8", """import{d}from"/npm/dep/+esm";export const shout=s=>d(s);""")
            .File("https://cdn.example/npm/dep/+esm", "application/javascript", "export const d=s=>s.toUpperCase();")
            .File("https://cdn.example/lib.js", "text/javascript", "window.lib=1;");
        var cache = AssetCache.Open(_directory);

        var (cached, failed) = await cache.FetchAsync("""<script src="https://cdn.example/lib.js"></script><script type="module">import {shout} from "https://esm.example/pkg";</script>""",
            new HttpClient(cdn), _clock);

        Assert.Equal((3, 0), (cached, failed));
        var pkg = cache.Find("https://esm.example/pkg")!;
        Assert.Equal("https://cdn.example/npm/pkg/+esm", pkg.FinalUrl);
        Assert.Same(pkg, cache.Find("https://cdn.example/npm/pkg/+esm"));
        Assert.Equal("/npm/dep/+esm", new Uri(cache.FindByPath("/npm/dep/+esm")!.Url).PathAndQuery);
        Assert.True(File.Exists(Path.Combine(_directory, pkg.Sha256)));
        Assert.Contains("\"format\": \"bohm.assets/0\"", await File.ReadAllTextAsync(Path.Combine(_directory, "index.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_and_data_are_not_kept_as_code()
    {
        var cdn = new FakeCdn()
            .File("https://cdn.example/lib.js", "text/html", "<html>login required</html>");
        var cache = AssetCache.Open(_directory);

        var (cached, failed) = await cache.FetchAsync("""<script src="https://cdn.example/lib.js"></script><script src="https://cdn.example/missing.js"></script>""",
            new HttpClient(cdn), _clock);

        Assert.Equal((0, 2), (cached, failed));
        Assert.Equal(["not code (text/html)", "the server answered 404"], cache.Failures.Select(f => f.Reason));
    }

    [Fact]
    public async Task The_cache_survives_reopening_and_a_second_fetch_reuses_what_is_there()
    {
        var cdn = new FakeCdn().File("https://cdn.example/lib.js", "text/javascript", "window.lib=1;");
        const string html = """<script src="https://cdn.example/lib.js"></script>""";
        await AssetCache.Open(_directory).FetchAsync(html, new HttpClient(cdn), _clock);

        var reopened = AssetCache.Open(_directory);
        Assert.NotNull(reopened.Find("https://cdn.example/lib.js"));

        await reopened.FetchAsync(html, new HttpClient(cdn), _clock);
        Assert.Single(cdn.Requested);
    }

    [Theory]
    [InlineData("http://cdn.example/lib.js")]
    [InlineData("https://localhost/lib.js")]
    [InlineData("https://127.0.0.1/lib.js")]
    [InlineData("https://10.0.0.5/lib.js")]
    [InlineData("https://192.168.1.10:8443/lib.js")]
    [InlineData("https://172.16.0.1/lib.js")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/lib.js")]
    [InlineData("https://[fd00::1]/lib.js")]
    [InlineData("https://intranet.local/lib.js")]
    [InlineData("https://app.internal/lib.js")]
    [InlineData("https://printer/lib.js")]
    public async Task Only_public_https_addresses_are_ever_requested(string url)
    {
        // A document is untrusted input: fetching whatever it names would let it make this computer
        // request addresses on its own network. Such references are refused without a request.
        var cdn = new FakeCdn().File(url, "text/javascript", "x");
        var cache = AssetCache.Open(_directory);

        await cache.FetchAsync($"""<script src="{url}"></script>""", new HttpClient(cdn), _clock);

        Assert.Empty(cdn.Requested);
        Assert.Equal("not a public https address", Assert.Single(cache.Failures).Reason);
    }

    [Fact]
    public async Task A_redirect_to_a_local_address_is_not_kept()
    {
        var cdn = new FakeCdn()
            .Redirect("https://cdn.example/lib.js", "https://192.168.0.1/lib.js")
            .File("https://192.168.0.1/lib.js", "text/javascript", "x");
        var cache = AssetCache.Open(_directory);

        await cache.FetchAsync("""<script src="https://cdn.example/lib.js"></script>""", new HttpClient(cdn), _clock);

        Assert.Empty(cache.Assets);
        Assert.Equal("redirected to a non-public address", Assert.Single(cache.Failures).Reason);
    }

    [Fact]
    public async Task Files_larger_than_the_limit_are_refused()
    {
        var cdn = new FakeCdn().File("https://cdn.example/big.js", "text/javascript", new byte[2048]);
        var cache = AssetCache.Open(_directory);

        await cache.FetchAsync("""<script src="https://cdn.example/big.js"></script>""", new HttpClient(cdn), _clock, new AssetCacheLimits { MaxFileBytes = 1024 });

        Assert.Equal("too large", Assert.Single(cache.Failures).Reason);
    }

    [Fact]
    public async Task Files_are_requested_together_within_the_overall_and_per_host_limits_and_kept_in_the_order_found()
    {
        // A module graph is wide: one file at a time made a single application wait minutes on a slow CDN.
        var cdn = new FakeCdn();
        var urls = new List<string>();
        foreach (var host in new[] { "a.example", "b.example", "c.example", "d.example" })
        {
            cdn.Slow(host, TimeSpan.FromMilliseconds(150));
            for (var i = 0; i < 4; i++)
            {
                var url = $"https://{host}/m{i}.js";
                cdn.File(url, "text/javascript", $"window.{host[0]}{i}=1;");
                urls.Add(url);
            }
        }

        var cache = AssetCache.Open(_directory);
        var (cached, failed) = await cache.FetchAsync(string.Concat(urls.Select(u => $"""<script src="{u}"></script>""")), new HttpClient(cdn), _clock);

        Assert.Equal((16, 0), (cached, failed));
        Assert.Equal(urls, cache.Assets.Select(a => a.Url));
        Assert.Equal(6, cdn.MostAtOnce);
        Assert.Equal(2, cdn.MostAtOncePerHost);
    }

    [Fact]
    public async Task When_the_time_budget_runs_out_what_arrived_is_kept_and_the_rest_says_why()
    {
        var cdn = new FakeCdn()
            .File("https://fast.example/lib.js", "text/javascript", "window.lib=1;")
            .File("https://slow.example/big.js", "text/javascript", "window.big=1;")
            .Slow("slow.example", TimeSpan.FromSeconds(30));
        var cache = AssetCache.Open(_directory);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var (cached, failed) = await cache.FetchAsync(
            """<script src="https://fast.example/lib.js"></script><script src="https://slow.example/big.js"></script>""",
            new HttpClient(cdn), _clock, new AssetCacheLimits { TimeBudget = TimeSpan.FromMilliseconds(300) });

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
        Assert.Equal((1, 1), (cached, failed));
        Assert.Equal("https://fast.example/lib.js", Assert.Single(cache.Assets).Url);
        Assert.Equal(("https://slow.example/big.js", "the time budget ran out"), (cache.Failures[0].Url, cache.Failures[0].Reason));
    }

    [Fact]
    public async Task A_fetch_cut_short_keeps_what_an_earlier_fetch_cached_and_follows_it_from_disk()
    {
        var cdn = new FakeCdn()
            .File("https://cdn.example/app.mjs", "text/javascript", """import"/dep.mjs";""")
            .File("https://cdn.example/dep.mjs", "text/javascript", "export const d=1;");
        const string before = """<script type="module" src="https://cdn.example/app.mjs"></script>""";
        await AssetCache.Open(_directory).FetchAsync(before, new HttpClient(cdn), _clock);
        cdn.File("https://cdn.example/new.js", "text/javascript", "window.n=1;");
        var requestedBefore = cdn.Requested.Count;

        var cache = AssetCache.Open(_directory);
        var (cached, failed) = await cache.FetchAsync(before + """<script src="https://cdn.example/new.js"></script>""",
            new HttpClient(cdn), _clock, new AssetCacheLimits { TimeBudget = TimeSpan.Zero });

        Assert.Equal((2, 1), (cached, failed));
        Assert.Equal(["https://cdn.example/app.mjs", "https://cdn.example/dep.mjs"], cache.Assets.Select(a => a.Url));
        Assert.All(cache.Assets, a => Assert.True(File.Exists(Path.Combine(_directory, a.Sha256))));
        Assert.Equal("the time budget ran out", Assert.Single(cache.Failures).Reason);
        Assert.Equal(requestedBefore, cdn.Requested.Count);
    }

    [Fact]
    public async Task Failed_requests_count_toward_the_request_limit()
    {
        var cdn = new FakeCdn();
        var cache = AssetCache.Open(_directory);

        var (cached, failed) = await cache.FetchAsync(string.Concat(Enumerable.Range(0, 10).Select(i => $"""<script src="https://cdn.example/missing{i}.js"></script>""")),
            new HttpClient(cdn), _clock, new AssetCacheLimits { MaxRequests = 3 });

        Assert.Equal((0, 10), (cached, failed));
        Assert.Equal(3, cdn.Requested.Count);
        Assert.Equal(7, cache.Failures.Count(f => f.Reason == "too many requests for one fetch"));
    }

    [Fact]
    public async Task A_chain_of_re_exporting_modules_is_followed_past_three_packages()
    {
        // Some CDNs answer a package name with a module that re-exports a pinned build, so three
        // packages importing each other are six files deep.
        var cdn = new FakeCdn();
        for (var i = 0; i < 6; i++)
            cdn.File($"https://cdn.example/m{i}.js", "text/javascript", i < 5 ? $"""export*from"/m{i + 1}.js";""" : "export const leaf=1;");
        var cache = AssetCache.Open(_directory);

        var (cached, failed) = await cache.FetchAsync("""<script type="module" src="https://cdn.example/m0.js"></script>""", new HttpClient(cdn), _clock);

        Assert.Equal((6, 0), (cached, failed));
    }
}
