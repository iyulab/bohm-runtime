using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;
using LMSupply;
using LMSupply.Lifecycle;
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
    public void The_librarys_failures_read_in_the_interfaces_terms()
    {
        // LMSupply 0.110 wraps a download the network broke in ModelDownloadException — a generator's model and the
        // speech model both read it as HttpRequestException, which the download view shows as unreachable.
        var broke = new HttpRequestException("no route", null, HttpStatusCode.BadGateway);
        var network = Assert.IsType<HttpRequestException>(LMSupplyModelSource.Translated(new LMSupply.Exceptions.ModelDownloadException("download failed", "m", broke)));
        Assert.Equal(HttpStatusCode.BadGateway, network.StatusCode);
        Assert.IsType<KeyNotFoundException>(LMSupplyModelSource.Translated(new LMSupply.Exceptions.ModelNotFoundException("no such model", "m")));
        Assert.Null(LMSupplyModelSource.Translated(new LMSupply.Exceptions.ModelDownloadException("disk full", "m", new IOException("full"))));
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
        // Whether it runs here, as the library judges it — and the one it would choose; a model it does not judge says nothing.
        var fits = catalog.EnumerateArray().Select(e => (e.GetProperty("fit").ValueKind == JsonValueKind.Null ? null : e.GetProperty("fit").GetString(), e.GetProperty("recommended").GetBoolean())).ToList();
        Assert.Equal([("gpu", true), (null, false)], fits);
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
    public async Task A_model_already_here_is_in_use_at_once_with_no_download_and_nothing_sent()
    {
        var source = new StandIn(_cache);
        File.WriteAllText(Path.Combine(_cache, "small.gguf"), "gguf");
        await using var host = await StartAsync(source);

        using var used = await DownloadAsync(host, "small");

        HttpAssert.Status(HttpStatusCode.OK, used); // done when it answers — not accepted to run in the background
        var state = JsonDocument.Parse(await used.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(Path.Combine(_cache, "small.gguf"), state.GetProperty("modelPath").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("download").ValueKind); // no progress line to show, not even for a moment
        Assert.Equal(0, source.Fetched);
        Assert.Empty(Sent(await EgressAsync(host)));
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
    public async Task A_download_stopped_before_its_first_progress_ends_stopped()
    {
        var source = new StandIn(_cache) { Hold = new TaskCompletionSource() };
        await using var host = await StartAsync(source);

        using (var started = await DownloadAsync(host, "large")) HttpAssert.Status(HttpStatusCode.Accepted, started);
        using (var stop = await host.ControlClient().DeleteAsync("/__control/llm/local-model/download")) HttpAssert.Status(HttpStatusCode.OK, stop);
        await host.Services.GetRequiredService<ModelDownloads>().Settled;

        var state = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/local-model")).RootElement;
        Assert.Equal("stopped", state.GetProperty("download").GetProperty("failure").GetString());

        // The library's download is stopped too, not only the wait for it: let it go on, and nothing arrives.
        source.Hold!.SetResult();
        await Task.Delay(300);
        Assert.False(source.IsDownloaded("large"));
    }

    [Fact]
    public async Task Without_a_llama_server_a_model_not_here_is_not_downloaded_and_nothing_is_sent()
    {
        // The model library's own source with no server shipped: a download would fetch a model this copy cannot run
        // (and, left to itself, the library would fetch a server too) — so it is refused before anything is asked.
        await using var host = await RunningHost.StartAsync();

        using var refused = await DownloadAsync(host, "someone/not-on-this-computer-GGUF");

        HttpAssert.Status(HttpStatusCode.Conflict, refused);
        Assert.Empty(Sent(await EgressAsync(host)));
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
        public string Cache => cache;

        public int Asked;

        public int Fetched;

        public TaskCompletionSource? Hold { get; init; }

        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Host => "huggingface.example";

        public IReadOnlyList<ModelCatalogEntry> Catalog() =>
        [
            new("small", "Small", "A small model.", "1B", "MIT", "MIT", 1_000, Fit: "gpu", Recommended: true),
            new("large", "Large", "A larger model.", "9B", "Gemma", "Conditional", 9_000),
        ];

        public bool IsDownloaded(string model) => File.Exists(Path.Combine(cache, model + ".gguf"));
        public Task<bool> IsDownloadedAsync(string model, CancellationToken cancellationToken) => Task.FromResult(IsDownloaded(model));

        public Task<long> DownloadSizeAsync(string model, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Asked);
            return model == "missing" ? throw new KeyNotFoundException(model) : Task.FromResult(model == "small" ? 1_000L : 9_000L);
        }

        public Task<string> LocalPathAsync(string model, CancellationToken cancellationToken) =>
            Task.FromResult(IsDownloaded(model) ? Path.Combine(cache, model + ".gguf") : throw new KeyNotFoundException(model));

        public IModelArtifacts Artifacts => new Files(this);

        /// <summary>What a download fetches: one file into the cache, after reporting half of it.</summary>
        private sealed class Files(StandIn source) : IModelArtifacts
        {
            public string Kind => "generator";

            public Task<bool> IsAvailableOfflineAsync(string modelId, CancellationToken cancellationToken = default) => Task.FromResult(source.IsDownloaded(modelId));

            public Task<long> GetRemainingDownloadBytesAsync(string modelId, CancellationToken cancellationToken = default) => source.DownloadSizeAsync(modelId, cancellationToken);

            public async Task<RemainingDownload> GetRemainingDownloadAsync(string modelId, CancellationToken cancellationToken = default) =>
                new(await source.DownloadSizeAsync(modelId, cancellationToken), 0L);

            public async Task DownloadAsync(string modelId, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref source.Fetched);
                if (modelId == "missing") throw new KeyNotFoundException(modelId);
                progress?.Report(new DownloadProgress { FileName = modelId + ".gguf", BytesDownloaded = 4_500, TotalBytes = 9_000 });
                source.Reported.TrySetResult();
                if (source.Hold is { } hold) await hold.Task.WaitAsync(cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(source.Cache, modelId + ".gguf"), "gguf", cancellationToken);
            }

            public bool Delete(string modelId) => throw new NotSupportedException();
        }
    }
}
