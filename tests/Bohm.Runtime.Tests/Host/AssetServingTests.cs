using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Tests.Assets;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The served document points cached code at its local copy however the document spells the
/// address — and leaves alone any address that is not cached, even one that starts like a cached one.
/// </summary>
public sealed class AssetServingTests : IAsyncLifetime
{
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        var cdn = new FakeCdn()
            .File("https://cdn.test/", "text/javascript", "window.fromRoot = 1;")
            .File("https://cdn.test/style.css", "text/css", "p { color: red; }")
            .File("https://fonts.test/css?family=A&display=swap", "text/css", "p { font-family: A; }")
            .File("https://cdn.test/lib.js", "text/javascript", "window.lib = 1;");
        _host = await RunningHost.StartAsync(configure: o => o with { AssetHttpHandler = () => cdn, FetchAssetsOnAdoption = false });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Cached_code_is_pointed_at_however_its_address_is_written_and_nothing_else_is()
    {
        var id = await _host.AdoptAsync("""
            <!doctype html><title>Spellings</title>
            <script src="https://cdn.test"></script>
            <link rel="stylesheet" href="https://CDN.TEST:443/style.css#top">
            <link rel="stylesheet" href="https://fonts.test/css?family=A&amp;display=swap">
            <script src="https://cdn.test/lib.js"></script>
            <script>const more = ["https://cdn.test/lib.js", https://cdn.test/lib.js;</script>
            <script src="https://cdn.test/lib.js.map"></script>
            <p>See https://cdn.test/about for details.</p>
            """);
        using (var fetched = await _host.ControlClient().PostAsync($"/__control/apps/{id}/assets", null))
            Assert.Equal(4, JsonDocument.Parse(await fetched.Content.ReadAsStringAsync()).RootElement.GetProperty("cached").GetArrayLength());

        using var served = await _host.ClientForApp(id).GetAsync("/");
        HttpAssert.Status(HttpStatusCode.OK, served);
        var html = Encoding.UTF8.GetString(await served.Content.ReadAsByteArrayAsync());

        Assert.Contains("""<script src="/__bohm/asset/https/cdn.test/"></script>""", html, StringComparison.Ordinal); // host only, no path
        Assert.Contains("""href="/__bohm/asset/https/cdn.test/style.css#top">""", html, StringComparison.Ordinal); // upper case, default port, fragment kept
        Assert.Contains("""href="/__bohm/asset/https/fonts.test/css?family=A&display=swap">""", html, StringComparison.Ordinal); // &amp; in an attribute
        Assert.Contains("""["/__bohm/asset/https/cdn.test/lib.js", /__bohm/asset/https/cdn.test/lib.js;""", html, StringComparison.Ordinal); // unquoted, then a semicolon
        Assert.Contains("""src="https://cdn.test/lib.js.map">""", html, StringComparison.Ordinal); // starts like a cached address, is not one
        Assert.Contains("https://cdn.test/about", html, StringComparison.Ordinal);
    }
}
