using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Assets;
using Bohm.Runtime.Host.Adoption;
using Bohm.Runtime.Tests.Assets;
using Microsoft.Extensions.DependencyInjection;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An application added with no internet gets its code cached once the network is back — when it is
/// opened again, or when the network comes back — and only the files the network kept out are tried again.
/// </summary>
public sealed class AssetRetryTests : IAsyncLifetime
{
    private const string Page = """
        <!doctype html><title>Offline</title>
        <script src="https://cdn.test/lib.js"></script>
        """;

    private readonly FakeCdn _cdn = new FakeCdn().File("https://cdn.test/lib.js", "text/javascript", "window.lib = 1;");
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await RunningHost.StartAsync(configure: o => o with { AssetHttpHandler = () => _cdn });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [InlineData(AssetFailureReasons.CouldNotBeReached, true)]
    [InlineData(AssetFailureReasons.TimedOut, true)]
    [InlineData(AssetFailureReasons.BudgetRanOut, true)]
    [InlineData("the server answered 503", true)]
    [InlineData("the server answered 429", true)]
    [InlineData("the server answered 408", true)]
    [InlineData("the server answered 404", false)]
    [InlineData("the server answered 403", false)]
    [InlineData("not a public https address", false)]
    [InlineData("redirected to a non-public address", false)]
    [InlineData("too large", false)]
    [InlineData("not code (text/html)", false)]
    [InlineData("the application's cache is full", false)]
    public void Only_what_the_network_or_the_server_kept_out_is_tried_again(string reason, bool transient) =>
        Assert.Equal(transient, new AssetFailure("https://cdn.test/x.js", reason).IsTransient);

    [Fact]
    public void Whether_to_try_again_is_not_written_into_the_cache_index() =>
        Assert.DoesNotContain("isTransient", JsonSerializer.Serialize(new AssetFailure("https://cdn.test/x.js", AssetFailureReasons.TimedOut)), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Opening_the_application_once_the_internet_is_back_caches_what_it_could_not_and_tries_at_most_once_in_a_while()
    {
        _cdn.Offline = true;
        var id = await _host.AdoptAsync(Page);
        var failed = await FetchAsync(id);
        Assert.Equal(AssetFailureReasons.CouldNotBeReached, failed.GetProperty("notCached")[0].GetProperty("reason").GetString());

        _cdn.Offline = false;
        await OpenAsync(id);
        var app = await _host.Services.GetRequiredService<OpenApps>().GetAsync(id);
        Assert.True(await Until(() => app.Assets.Assets.Count == 1), "the code is cached once the application is opened again");
        Assert.Empty(app.Assets.Failures);
        Assert.Contains("/__bohm/asset/https/cdn.test/lib.js", await OpenAsync(id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opening_again_and_again_while_still_offline_does_not_ask_the_network_each_time()
    {
        _cdn.Offline = true;
        var id = await _host.AdoptAsync(Page);
        await FetchAsync(id);
        await OpenAsync(id);
        Assert.True(await Until(() => Asked() == 2), "the first opening tries again");
        await OpenAsync(id);
        await OpenAsync(id);
        await Task.Delay(300);
        Assert.Equal(2, Asked()); // not again within the retry interval
    }

    [Fact]
    public async Task When_the_network_comes_back_every_application_with_code_it_could_not_reach_is_tried_again()
    {
        _cdn.Offline = true;
        var offline = await _host.AdoptAsync(Page);
        await FetchAsync(offline);
        var cached = await _host.AdoptAsync("<!doctype html><title>Plain</title><p>No code from elsewhere.</p>");
        await FetchAsync(cached);

        _cdn.Offline = false;
        var started = await _host.Services.GetRequiredService<AssetFetcher>().RetryAllAsync(CancellationToken.None);
        Assert.Equal(1, started); // only the application that has something to try again
        var app = await _host.Services.GetRequiredService<OpenApps>().GetAsync(offline);
        Assert.True(await Until(() => app.Assets.Assets.Count == 1));
    }

    private int Asked()
    {
        lock (_cdn.Requested) return _cdn.Requested.Count(u => u == "https://cdn.test/lib.js");
    }

    private async Task<JsonElement> FetchAsync(string id)
    {
        using var fetched = await _host.ControlClient().PostAsync($"/__control/apps/{id}/assets", null);
        HttpAssert.Status(HttpStatusCode.OK, fetched);
        return JsonDocument.Parse(await fetched.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<string> OpenAsync(string id)
    {
        using var served = await _host.ClientForApp(id).GetAsync("/");
        HttpAssert.Status(HttpStatusCode.OK, served);
        return Encoding.UTF8.GetString(await served.Content.ReadAsByteArrayAsync());
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }
}
