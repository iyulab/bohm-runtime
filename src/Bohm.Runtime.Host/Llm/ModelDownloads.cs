using LMSupply;
using LMSupply.Exceptions;
using LMSupply.Generator;
using LMSupply.Generator.Internal.Llama;

namespace Bohm.Runtime.Host.Llm;

/// <summary>A model the model library knows by a short name — what the person is shown before getting it.</summary>
/// <param name="Id">What to ask for it by (the library's alias, or a Hugging Face repository).</param>
/// <param name="Name">The name to show.</param>
/// <param name="Description">One line about it, when the library has one.</param>
/// <param name="Parameters">Its size in parameters as the library writes it (<c>4B</c>), when known.</param>
/// <param name="License">The licence's name, when known.</param>
/// <param name="LicenseTier">How freely the licence lets it be used, as the library sorts them (<c>MIT</c>, <c>Conditional</c>, <c>ResearchOnly</c>).</param>
/// <param name="SizeBytes">About how much a download takes, when the library estimates it.</param>
public sealed record ModelCatalogEntry(string Id, string Name, string? Description, string? Parameters, string? License, string? LicenseTier, long? SizeBytes);

/// <summary>How far a download has come.</summary>
/// <param name="Bytes">Bytes received so far.</param>
/// <param name="Total">Bytes in all, when known.</param>
public sealed record ModelDownloadProgress(long Bytes, long? Total);

/// <summary>
/// Where models on this computer come from: the model library's catalog and the model host it downloads
/// from. The default is LMSupply and Hugging Face; tests supply a stand-in so nothing leaves the computer.
/// </summary>
public interface IModelSource
{
    /// <summary>The host downloads come from — what is counted as sent to.</summary>
    string Host { get; }

    /// <summary>The models the library knows by a short name. Read without the network.</summary>
    IReadOnlyList<ModelCatalogEntry> Catalog();

    /// <summary>Whether <paramref name="model"/> is on this computer already. Read without the network.</summary>
    bool IsDownloaded(string model);

    /// <summary>How many bytes getting <paramref name="model"/> would take. Asks the host once.</summary>
    /// <exception cref="KeyNotFoundException">The host has no such model.</exception>
    Task<long> DownloadSizeAsync(string model, CancellationToken cancellationToken);

    /// <summary>The path of the model file (<c>.gguf</c>) of <paramref name="model"/>, which is on this computer already. Read without the network.</summary>
    /// <exception cref="KeyNotFoundException">It is not on this computer.</exception>
    Task<string> LocalPathAsync(string model, CancellationToken cancellationToken);

    /// <summary>Gets <paramref name="model"/> and returns the path of its model file (<c>.gguf</c>) on this computer.</summary>
    /// <exception cref="KeyNotFoundException">The host has no such model.</exception>
    Task<string> DownloadAsync(string model, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken);
}

/// <summary>LMSupply's GGUF catalog and downloads from Hugging Face, into the library's cache.</summary>
internal sealed class LMSupplyModelSource : IModelSource
{
    public string Host => "huggingface.co";

    /// <remarks>
    /// The catalog is the library's GGUF aliases, each asked for as <c>gguf:&lt;alias&gt;</c> — a plain alias names an ONNX model,
    /// and a repository alone downloads its default file, not the variant the alias picks. <c>gguf:auto</c> is left out: what it
    /// picks depends on the computer, so there is nothing to show before it is asked.
    /// </remarks>
    public IReadOnlyList<ModelCatalogEntry> Catalog() =>
        GgufModelRegistry.GetAliases()
            .Select(alias => alias.StartsWith("gguf:", StringComparison.OrdinalIgnoreCase) ? alias : "gguf:" + alias)
            .Where(id => !string.Equals(id, "gguf:auto", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => (Id: id, Info: GgufModelRegistry.Resolve(id)))
            .Where(m => m.Info is not null)
            .Select(m => new ModelCatalogEntry(
                m.Id,
                string.IsNullOrEmpty(m.Info!.DisplayName) ? m.Info.RepoId : m.Info.DisplayName,
                m.Info.Description,
                FormatParameters(m.Info.ParameterCount),
                m.Info.LicenseName,
                m.Info.License.ToString(),
                m.Info.EstimatedSizeBytes))
            .ToList();

    /// <summary>«4B» for four billion parameters, «600M» below a billion; <see langword="null"/> when the catalog says none.</summary>
    internal static string? FormatParameters(long count) =>
        count <= 0 ? null
        : count >= 1_000_000_000 ? (count / 1e9).ToString(count % 1_000_000_000 == 0 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture) + "B"
        : (count / 1e6).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "M";

    public bool IsDownloaded(string model) => LocalGenerator.IsModelDownloaded(model, null);

    public async Task<long> DownloadSizeAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            return await LocalGenerator.GetDownloadSizeBytesAsync(model, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Translated(e) is { } translated)
        {
            throw translated;
        }
    }

    // The same resolution as a download, held to the cache: the file a download would have put there, with no request.
    public async Task<string> LocalPathAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            return await LocalGenerator.DownloadModelAsync(model, new GeneratorOptions { DisableAutoDownload = true }, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Translated(e) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<string> DownloadAsync(string model, IProgress<ModelDownloadProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            return await LocalGenerator.DownloadModelAsync(model, null,
                new Relay(p => progress.Report(new(p.OverallBytesDownloaded ?? p.BytesDownloaded, p.OverallTotalBytes ?? p.TotalBytes))),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (Translated(e) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// The library's failures in the interface's terms: no such model — including a repository the Hub refuses, which it
    /// answers the same whether it does not exist or is private or gated — as <see cref="KeyNotFoundException"/>, and a
    /// download the network broke as <see cref="HttpRequestException"/>. <see langword="null"/>: leave it as it is.
    /// </summary>
    internal static Exception? Translated(Exception e) => e switch
    {
        ModelNotFoundException => new KeyNotFoundException(e.Message, e),
        UnauthorizedAccessException { InnerException: HttpRequestException } => new KeyNotFoundException(e.Message, e),
        ModelDownloadException { InnerException: HttpRequestException inner } => new HttpRequestException(e.Message, e, inner.StatusCode),
        _ => null,
    };

    /// <summary>Reports on the thread that reports — no synchronization context to post to.</summary>
    private sealed class Relay(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}

/// <summary>
/// Getting a model onto this computer, one at a time, for the person: the catalog to choose from, what
/// a download would take before it starts (its size, and its licence when the catalog knows it), and the
/// download itself in the background — after which the model is the one in use (<see cref="LocalModel"/>).
/// </summary>
/// <remarks>
/// Getting a model is the one step of using a model on this computer that needs the network, and it
/// happens only when the person asks; once on the computer the model runs without it. Asking a size and
/// downloading are counted as sent to the model host. Whether the person may get models at all is the
/// shell's to decide (an administrator's policy), like the other ways out.
/// </remarks>
internal sealed class ModelDownloads(RuntimeHostOptions options, LocalModel local, Egress egress)
{
    private readonly IModelSource _source = options.ModelSource ?? new LMSupplyModelSource();
    private readonly Lock _lock = new();
    private Running? _running;

    /// <summary>The catalog, each with whether it is on this computer already.</summary>
    public IReadOnlyList<CatalogView> Catalog() =>
        _source.Catalog().Select(e => new CatalogView(e.Id, e.Name, e.Description, e.Parameters, e.License, e.LicenseTier, e.SizeBytes, _source.IsDownloaded(e.Id))).ToList();

    /// <summary>What getting <paramref name="model"/> would take — asks the host once for its size.</summary>
    public async Task<Description> DescribeAsync(string model, CancellationToken cancellationToken)
    {
        var entry = _source.Catalog().FirstOrDefault(e => string.Equals(e.Id, model, StringComparison.OrdinalIgnoreCase));
        var downloaded = _source.IsDownloaded(model);
        egress.Sent(_source.Host);
        try
        {
            var size = await _source.DownloadSizeAsync(model, cancellationToken).ConfigureAwait(false);
            return new(Description.Ok, model, entry?.Name, entry?.License, entry?.LicenseTier, size, downloaded);
        }
        catch (KeyNotFoundException)
        {
            return new(Description.NotFound, model, null, null, null, null, downloaded);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or LMSupplyException && !cancellationToken.IsCancellationRequested)
        {
            return new(Description.Unreachable, model, entry?.Name, entry?.License, entry?.LicenseTier, null, downloaded);
        }
    }

    /// <summary>The download under way or the last one that ended without the model, or <see langword="null"/>.</summary>
    public DownloadView? Current
    {
        get
        {
            lock (_lock) return _running?.View();
        }
    }

    /// <summary>
    /// Makes <paramref name="model"/> the model in use: at once when it is on this computer already — nothing to get, so
    /// nothing is sent and there is no download to show — otherwise by getting it in the background.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model was set when the runtime was started.</exception>
    public async Task<Started> StartAsync(string model, CancellationToken cancellationToken)
    {
        if (local.Fixed) throw new InvalidOperationException("The model was set when the runtime was started.");
        lock (_lock)
            if (_running is { Ended: false }) return Started.Busy;

        if (_source.IsDownloaded(model) && await LocalPathAsync(model, cancellationToken).ConfigureAwait(false) is { } here)
        {
            await local.ChooseAsync(here, cancellationToken).ConfigureAwait(false);
            lock (_lock)
                if (_running is { Ended: true }) _running = null; // a download that ended without its model is no longer the last word
            return Started.InUse;
        }

        // The catalog's name, so progress reads as the model the person chose, not its id.
        var name = _source.Catalog().FirstOrDefault(e => string.Equals(e.Id, model, StringComparison.OrdinalIgnoreCase))?.Name;
        Running running;
        lock (_lock)
        {
            if (_running is { Ended: false }) return Started.Busy;
            running = _running = new Running(model, name);
        }

        egress.Sent(_source.Host);
        running.Task = Task.Run(async () =>
        {
            try
            {
                var path = await _source.DownloadAsync(model, running, running.Stop.Token).ConfigureAwait(false);
                await local.ChooseAsync(path, CancellationToken.None).ConfigureAwait(false);
                lock (_lock)
                    if (ReferenceEquals(_running, running)) _running = null; // arrived and in use: nothing left to show
            }
            catch (Exception e)
            {
                running.Fail(e switch
                {
                    OperationCanceledException when running.Stop.IsCancellationRequested => DownloadView.Stopped,
                    KeyNotFoundException => DownloadView.NotFound,
                    HttpRequestException or OperationCanceledException => DownloadView.Unreachable,
                    IOException or UnauthorizedAccessException => DownloadView.Disk,
                    _ => DownloadView.Other,
                });
            }
        }, CancellationToken.None); // it outlives the request that started it; stopping is what ends it
        return Started.Downloading;
    }

    /// <summary>What <see cref="StartAsync"/> did.</summary>
    internal enum Started
    {
        /// <summary>Getting it in the background — <see cref="Current"/> follows it.</summary>
        Downloading,

        /// <summary>It was on this computer already and is the model in use now.</summary>
        InUse,

        /// <summary>Nothing: another download is under way.</summary>
        Busy,
    }

    // What is on this computer, or null when the cache does not hold the file a download would pick after all — then it is
    // got like any other.
    private async Task<string?> LocalPathAsync(string model, CancellationToken cancellationToken)
    {
        try
        {
            return await _source.LocalPathAsync(model, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Stops the download under way, if there is one; the model in use stays as it was.</summary>
    public void Stop()
    {
        lock (_lock) _running?.Stop.Cancel();
    }

    /// <summary>Waits for the download under way to end — for tests.</summary>
    internal Task Settled
    {
        get
        {
            lock (_lock) return _running?.Task ?? Task.CompletedTask;
        }
    }

    private sealed class Running(string model, string? name) : IProgress<ModelDownloadProgress>
    {
        private ModelDownloadProgress _progress = new(0, null);
        private string? _failure;

        public CancellationTokenSource Stop { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;

        public bool Ended => _failure is not null;

        public void Report(ModelDownloadProgress value) => _progress = value;

        public void Fail(string failure) => _failure = failure;

        public DownloadView View() => new(model, name, _progress.Bytes, _progress.Total, _failure);
    }

    /// <param name="Downloaded">Whether it is on this computer already — getting it again takes no download.</param>
    internal sealed record CatalogView(string Id, string Name, string? Description, string? Parameters, string? License, string? LicenseTier, long? SizeBytes, bool Downloaded);

    /// <param name="Result">One of <see cref="Ok"/>, <see cref="NotFound"/> (the host has no such model) or <see cref="Unreachable"/>.</param>
    /// <param name="SizeBytes">What the download takes, when the host said.</param>
    internal sealed record Description(string Result, string Model, string? Name, string? License, string? LicenseTier, long? SizeBytes, bool Downloaded)
    {
        public const string Ok = "ok";
        public const string NotFound = "not-found";
        public const string Unreachable = "unreachable";
    }

    /// <param name="Name">The catalog's name for it, or <see langword="null"/> for a repository the catalog does not know.</param>
    /// <param name="Failure">Why it ended without the model — <see cref="Stopped"/>, <see cref="NotFound"/>, <see cref="Unreachable"/>,
    /// <see cref="Disk"/> or <see cref="Other"/> — or <see langword="null"/> while it is under way.</param>
    internal sealed record DownloadView(string Model, string? Name, long Bytes, long? Total, string? Failure)
    {
        public const string Stopped = "stopped";
        public const string NotFound = "not-found";
        public const string Unreachable = "unreachable";
        public const string Disk = "disk";
        public const string Other = "other";
    }
}
