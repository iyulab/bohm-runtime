using LMSupply;
using LMSupply.Runtime;
using LMSupply.Transcriber;

namespace Bohm.Runtime.Host.Llm;

/// <summary>A loaded model that turns a recording into text on this computer.</summary>
public interface ISpeechToText : IAsyncDisposable
{
    /// <summary>What was said in <paramref name="audio"/> (WAV, MP3, WebM/Opus or Ogg/Opus, recognised from its bytes).</summary>
    /// <param name="language">An ISO 639-1 code, or <see langword="null"/> to identify it from the recording.</param>
    Task<string> TranscribeAsync(byte[] audio, string? language, CancellationToken cancellationToken);
}

/// <summary>
/// Where the speech model on this computer comes from. The default is LMSupply's Whisper model from Hugging Face,
/// run on the ONNX Runtime shipped next to the runtime; tests supply a stand-in so nothing leaves the computer.
/// </summary>
public interface ISpeechModelSource
{
    /// <summary>The host downloads come from — what is counted as sent to.</summary>
    string Host { get; }

    /// <summary>How many bytes getting the model takes. Asks the host once.</summary>
    Task<long> DownloadSizeAsync(CancellationToken cancellationToken);

    /// <summary>Whether the model is on this computer, complete. Read without the network.</summary>
    Task<bool> IsDownloadedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads the model — getting it first when <paramref name="download"/> and it is not on this computer;
    /// otherwise a missing model fails instead of being got.
    /// </summary>
    Task<ISpeechToText> LoadAsync(bool download, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>LMSupply's Whisper Small, on the CPU, with the native ONNX Runtime from a directory the runtime ships.</summary>
/// <remarks>
/// Small rather than the library's default Base: on browser recordings Base dropped the second sentence of an
/// eleven-second recording, which an application keeping a log would lose without anyone noticing.
/// </remarks>
internal sealed class LMSupplySpeechModelSource : ISpeechModelSource
{
    /// <summary>The library's alias for Whisper Small (about 970 MB).</summary>
    internal const string Model = "quality";

    private static readonly Lock RuntimeLock = new();
    private static string? s_runtimeDirectory;

    public LMSupplySpeechModelSource(string runtimeDirectory)
    {
        // The native runtime is process-wide and set once, before the first model loads: from the shipped directory,
        // with nothing looked up or fetched — the model download is the only step here that reaches the network.
        lock (RuntimeLock)
        {
            if (s_runtimeDirectory is null)
            {
                try
                {
                    RuntimeManager.Configure(new RuntimeManagerOptions { RuntimeDirectory = runtimeDirectory, DisableAutoDownload = true });
                }
                catch (InvalidOperationException)
                {
                    // Something in this process loaded an ONNX model first; the runtime it set up stays.
                }

                s_runtimeDirectory = runtimeDirectory;
            }
        }
    }

    public string Host => "huggingface.co";

    // The files a load would fetch, as the host lists them — the catalog's own figure is an estimate that can be far off
    // (it said 970 MB for a download of about 250 MB).
    // The library's failures in the interface's terms, as for a generator's model (a download the network broke is an
    // HttpRequestException — LMSupply 0.110 wraps it in ModelDownloadException).
    public async Task<long> DownloadSizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await LocalTranscriber.GetDownloadSizeBytesAsync(Model, Options(download: true), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (LMSupplyModelSource.Translated(e) is { } translated)
        {
            throw translated;
        }
    }

    public Task<bool> IsDownloadedAsync(CancellationToken cancellationToken) =>
        LocalTranscriber.IsModelDownloadedAsync(Model, Options(download: false), cancellationToken);

    public async Task<ISpeechToText> LoadAsync(bool download, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var relay = progress is null ? null : new Relay(p => progress.Report(new(p.OverallBytesDownloaded ?? p.BytesDownloaded, p.OverallTotalBytes ?? p.TotalBytes)));
        try
        {
            return new Loaded(await LocalTranscriber.LoadAsync(Model, Options(download), relay, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (LMSupplyModelSource.Translated(e) is { } translated)
        {
            throw translated;
        }
    }

    private static TranscriberOptions Options(bool download) => new() { Provider = ExecutionProvider.Cpu, DisableAutoDownload = !download };

    private sealed class Loaded(ITranscriberModel model) : ISpeechToText
    {
        public async Task<string> TranscribeAsync(byte[] audio, string? language, CancellationToken cancellationToken) =>
            (await model.TranscribeAsync(audio, new TranscribeOptions { Language = language }, cancellationToken).ConfigureAwait(false)).Text;

        public ValueTask DisposeAsync() => model.DisposeAsync();
    }

    /// <summary>Reports on the thread that reports — no synchronization context to post to.</summary>
    private sealed class Relay(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}

/// <summary>
/// The speech model on this computer, which turns an application's recordings into text when neither an OpenAI key nor
/// the organization's server answers them. The person gets it once, on asking; after that it runs without the network.
/// </summary>
/// <remarks>
/// Getting it loads it, and it stays loaded; otherwise it loads on the first recording. Recordings are turned into text
/// one at a time — a second waits for the first. An application never causes a download: a recording sent before the
/// model is on this computer is refused, and the shell offers to get it.
/// </remarks>
internal sealed class LocalSpeech(RuntimeHostOptions options, Egress egress) : IAsyncDisposable
{
    private readonly ISpeechModelSource? _source = options.SpeechModelSource
        ?? (options.SpeechRuntimeDirectory is { } directory ? new LMSupplySpeechModelSource(directory) : null);

    private readonly SemaphoreSlim _load = new(1, 1);
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Lock _lock = new();
    private ISpeechToText? _model;
    private bool _downloaded;
    private Running? _running;

    /// <summary>Whether this copy can run a speech model at all (its native runtime ships with it).</summary>
    public bool Supported => _source is not null;

    /// <summary>Whether the model is loaded now.</summary>
    public bool Loaded => _model is not null;

    /// <summary>
    /// How many bytes getting the model takes — asks its host once, counted as sent there; <see langword="null"/> when the
    /// host could not be asked.
    /// </summary>
    /// <exception cref="NotSupportedException">This copy cannot run a speech model.</exception>
    public async Task<long?> DownloadSizeAsync(CancellationToken cancellationToken)
    {
        var source = _source ?? throw new NotSupportedException("This copy cannot run a speech model.");
        egress.Sent(source.Host);
        try
        {
            return await source.DownloadSizeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Why the last load of a downloaded model failed — the model library's own message — or <see langword="null"/>.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Whether the model is on this computer. Read without the network.</summary>
    public async Task<bool> DownloadedAsync(CancellationToken cancellationToken)
    {
        if (_source is null) return false;
        if (_downloaded) return true;
        return _downloaded = await _source.IsDownloadedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The download under way or the last one that ended without the model, or <see langword="null"/>.</summary>
    public ModelDownloads.DownloadView? Download
    {
        get
        {
            lock (_lock) return _running?.View();
        }
    }

    /// <summary>
    /// Gets the model onto this computer in the background and loads it — nothing to do when it is here already.
    /// </summary>
    /// <exception cref="NotSupportedException">This copy cannot run a speech model.</exception>
    public async Task<ModelDownloads.Started> StartDownloadAsync(CancellationToken cancellationToken)
    {
        var source = _source ?? throw new NotSupportedException("This copy cannot run a speech model.");
        if (await DownloadedAsync(cancellationToken).ConfigureAwait(false)) return ModelDownloads.Started.InUse;

        Running running;
        lock (_lock)
        {
            if (_running is { Ended: false }) return ModelDownloads.Started.Busy;
            running = _running = new Running();
        }

        egress.Sent(source.Host);
        running.Task = Task.Run(async () =>
        {
            try
            {
                var model = await source.LoadAsync(download: true, running, running.Stop.Token).ConfigureAwait(false);
                await _load.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (_model is { } earlier) await earlier.DisposeAsync().ConfigureAwait(false);
                    _model = model;
                    _downloaded = true;
                    LastFailure = null;
                }
                finally
                {
                    _load.Release();
                }

                lock (_lock)
                    if (ReferenceEquals(_running, running)) _running = null; // arrived and loaded: nothing left to show
            }
            catch (Exception e)
            {
                running.Fail(e switch
                {
                    OperationCanceledException when running.Stop.IsCancellationRequested => ModelDownloads.DownloadView.Stopped,
                    HttpRequestException or OperationCanceledException => ModelDownloads.DownloadView.Unreachable,
                    IOException or UnauthorizedAccessException => ModelDownloads.DownloadView.Disk,
                    _ => ModelDownloads.DownloadView.Other,
                });
            }
        }, CancellationToken.None); // it outlives the request that started it; stopping is what ends it
        return ModelDownloads.Started.Downloading;
    }

    /// <summary>Stops the download under way, if there is one.</summary>
    public void StopDownload()
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

    /// <summary>What was said in <paramref name="audio"/>, loading the model the first time.</summary>
    /// <exception cref="LocalModelUnavailableException">The model is not on this computer, or does not load.</exception>
    public async Task<string> TranscribeAsync(byte[] audio, string? language, CancellationToken cancellationToken)
    {
        var model = await GetAsync(cancellationToken).ConfigureAwait(false);
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await model.TranscribeAsync(audio, language, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turn.Release();
        }
    }

    private async Task<ISpeechToText> GetAsync(CancellationToken cancellationToken)
    {
        if (_model is { } loaded) return loaded;
        if (_source is null || !await DownloadedAsync(cancellationToken).ConfigureAwait(false))
            throw new LocalModelUnavailableException("No model on this computer turns speech into text yet.");

        await _load.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_model is { } raced) return raced;
            try
            {
                // The loaded model serves every recording after this one: one that gives up while it loads does not
                // throw away a load the next would only have to start again.
                _model = await _source.LoadAsync(download: false, progress: null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LastFailure = e.Message;
                throw new LocalModelUnavailableException($"The speech model on this computer could not be started: {e.Message}", e);
            }

            LastFailure = null;
            return _model;
        }
        finally
        {
            _load.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopDownload();
        if (_model is { } model)
        {
            _model = null;
            await model.DisposeAsync().ConfigureAwait(false);
        }

        _load.Dispose();
        _turn.Dispose();
    }

    private sealed class Running : IProgress<ModelDownloadProgress>
    {
        private ModelDownloadProgress _progress = new(0, null);
        private string? _failure;

        public CancellationTokenSource Stop { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;

        public bool Ended => _failure is not null;

        public void Report(ModelDownloadProgress value) => _progress = value;

        public void Fail(string failure) => _failure = failure;

        public ModelDownloads.DownloadView View() => new(LMSupplySpeechModelSource.Model, null, _progress.Bytes, _progress.Total, _failure);
    }
}
