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

    [Fact]
    public async Task Files_larger_than_the_limit_are_refused()
    {
        var cdn = new FakeCdn().File("https://cdn.example/big.js", "text/javascript", new byte[2048]);
        var cache = AssetCache.Open(_directory);

        await cache.FetchAsync("""<script src="https://cdn.example/big.js"></script>""", new HttpClient(cdn), _clock, new AssetCacheLimits { MaxFileBytes = 1024 });

        Assert.Equal("too large", Assert.Single(cache.Failures).Reason);
    }
}
