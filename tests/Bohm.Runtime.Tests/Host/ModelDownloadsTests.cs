using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.DependencyInjection;

namespace Bohm.Runtime.Tests.Host;

/// <summary>Getting a model onto this computer — with a stand-in model source, so nothing leaves the computer.</summary>
public sealed class ModelDownloadsTests : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("bohm-models-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_catalog_lists_each_model_with_its_licence_size_and_whether_it_is_here_without_the_network()
    {
        var source = new StandIn(_cache);
        File.WriteAllText(Path.Combine(_cache, "small.gguf"), "x");
        await using var host = await StartAsync(source);

        var catalog = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/local-model/catalog")).RootElement;

        var entries = catalog.EnumerateArray().Select(e => (e.GetProperty("id").GetString(), e.GetProperty("license").GetString(), e.GetProperty("sizeBytes").GetInt64(), e.GetProperty("downloaded").GetBoolean())).ToList();
        Assert.Equal([("small", "MIT", 1_000L, true), ("large", "Gemma", 9_000L, false)], entries);
        Assert.Equal(0, source.Asked);
        Assert.Empty(Sent(await EgressAsync(host)));
    }

    [Fact]
    public async Task Describing_a_model_asks_its_size_once_and_counts_it_as_sent_to_the_model_host()
    {
        var source = new StandIn(_cache);
        await using var host = await StartAsync(source);

        var known = await DescribeAsync(host, "large");
        Assert.Equal(("ok", "Large", "Gemma", 9_000L, false), (known.GetProperty("result").GetString(), known.GetProperty("name").GetString(), known.GetProperty("license").GetString(), known.GetProperty("sizeBytes").GetInt64(), known.GetProperty("downloaded").GetBoolean()));
        var other = await DescribeAsync(host, "someone/model-GGUF");
        Assert.Equal(("ok", JsonValueKind.Null), (other.GetProperty("result").GetString(), other.GetProperty("license").ValueKind)); // a repository the catalog does not know: no licence to tell
        Assert.Equal("not-found", (await DescribeAsync(host, "missing")).GetProperty("result").GetString());
        Assert.Equal(3, source.Asked);
        Assert.Equal(["huggingface.example"], Sent(await EgressAsync(host)));
    }

    [Fact]
    public async Task A_downloaded_model_becomes_the_one_in_use()
    {
        var source = new StandIn(_cache);
        await using var host = await StartAsync(source);

        using (var started = await DownloadAsync(host, "large"))
        {
            HttpAssert.Status(HttpStatusCode.Accepted, started);
            var download = JsonDocument.Parse(await started.Content.ReadAsStringAsync()).RootElement.GetProperty("download");
            Assert.Equal(("large", "Large"), (download.GetProperty("model").GetString(), download.GetProperty("name").GetString())); // progress reads as the catalog's name
        }

        await host.Services.GetRequiredService<ModelDownloads>().Settled;
        var state = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/local-model")).RootElement;
        Assert.Equal(Path.Combine(_cache, "large.gguf"), state.GetProperty("modelPath").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("download").ValueKind);
        Assert.Equal(["huggingface.example"], Sent(await EgressAsync(host)));
    }

    [Fact]
    public async Task A_download_can_be_watched_and_stopped_and_one_runs_at_a_time()
    {
        var source = new StandIn(_cache) { Hold = new TaskCompletionSource() };
        await using var host = await StartAsync(source);

        using (var started = await DownloadAsync(host, "large")) HttpAssert.Status(HttpStatusCode.Accepted, started);
        await source.Reported.Task;
        var running = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/local-model")).RootElement.GetProperty("download");
        Assert.Equal((4_500L, 9_000L, JsonValueKind.Null), (running.GetProperty("bytes").GetInt64(), running.GetProperty("total").GetInt64(), running.GetProperty("failure").ValueKind));
        using (var second = await DownloadAsync(host, "small")) HttpAssert.Status(HttpStatusCode.Conflict, second);

        using (var stop = await host.ControlClient().DeleteAsync("/__control/llm/local-model/download")) HttpAssert.Status(HttpStatusCode.OK, stop);
        await host.Services.GetRequiredService<ModelDownloads>().Settled;
        var state = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/local-model")).RootElement;
        Assert.Equal("stopped", state.GetProperty("download").GetProperty("failure").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("modelPath").ValueKind); // the model in use stays as it was
    }

    [Fact]
    public async Task A_model_the_host_does_not_have_ends_the_download_saying_so_and_a_fixed_model_cannot_be_replaced()
    {
        var source = new StandIn(_cache);
        await using (var host = await StartAsync(source))
        {
            using (var started = await DownloadAsync(host, "missing")) HttpAssert.Status(HttpStatusCode.Accepted, started);
            await host.Services.GetRequiredService<ModelDownloads>().Settled;
            var state = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/local-model")).RootElement;
            Assert.Equal("not-found", state.GetProperty("download").GetProperty("failure").GetString());
            using var empty = await host.ControlClient().PostAsync("/__control/llm/local-model/download", new StringContent(" ", Encoding.UTF8, "text/plain"));
            HttpAssert.Status(HttpStatusCode.BadRequest, empty);
        }

        await using var fixedHost = await RunningHost.StartAsync(configure: o => o with { ModelSource = source, LocalModel = new LocalModelOptions { ModelPath = "fixed.gguf" } });
        using var refused = await DownloadAsync(fixedHost, "large");
        HttpAssert.Status(HttpStatusCode.Conflict, refused);
    }

    [Theory]
    [InlineData(4_000_000_000L, "4B")]
    [InlineData(1_500_000_000L, "1.5B")]
    [InlineData(600_000_000L, "600M")]
    [InlineData(0L, null)]
    public void Parameter_counts_read_the_way_model_names_write_them(long count, string? written) =>
        Assert.Equal(written, LMSupplyModelSource.FormatParameters(count));

    private static Task<RunningHost> StartAsync(IModelSource source) => RunningHost.StartAsync(configure: o => o with { ModelSource = source });

    private static Task<HttpResponseMessage> DownloadAsync(RunningHost host, string model) =>
        host.ControlClient().PostAsync("/__control/llm/local-model/download", new StringContent(model, Encoding.UTF8, "text/plain"));

    private static async Task<JsonElement> DescribeAsync(RunningHost host, string model)
    {
        using var described = await host.ControlClient().PostAsync("/__control/llm/local-model/describe", new StringContent(model, Encoding.UTF8, "text/plain"));
        HttpAssert.Status(HttpStatusCode.OK, described);
        return JsonDocument.Parse(await described.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> EgressAsync(RunningHost host) =>
        JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.Clone();

    private static List<string?> Sent(JsonElement egress) => egress.GetProperty("sent").EnumerateArray().Select(s => s.GetProperty("host").GetString()).ToList();

    /// <summary>Two catalog models; «missing» is not on the host; downloads write a file into the cache.</summary>
    private sealed class StandIn(string cache) : IModelSource
    {
        public int Asked;

        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Host => "huggingface.example";

        public IReadOnlyList<ModelCatalogEntry> Catalog() =>
        [
            new("small", "Small", "A small model.", "1B", "MIT", "MIT", 1_000),
            new("large", "Large", "A larger model.", "9B", "Gemma", "Conditional", 9_000),
        ];

        public bool IsDownloaded(string model) => File.Exists(Path.Combine(cache, model + ".gguf"));

        public Task<long> DownloadSizeAsync(string model, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Asked);
            return model == "missing" ? throw new KeyNotFoundException(model) : Task.FromResult(model == "small" ? 1_000L : 9_000L);
        }

        public async Task<string> DownloadAsync(string model, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken)
        {
            if (model == "missing") throw new KeyNotFoundException(model);
            progress.Report(new(4_500, 9_000));
            Reported.TrySetResult();
            if (Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
            var path = Path.Combine(cache, model + ".gguf");
            await File.WriteAllTextAsync(path, "gguf", cancellationToken);
            return path;
        }
    }
}
