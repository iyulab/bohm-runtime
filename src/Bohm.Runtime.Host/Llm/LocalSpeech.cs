using LMSupply;
using LMSupply.Lifecycle;
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

    /// <summary>The model's id, as <see cref="Artifacts"/> knows it.</summary>
    string Model { get; }

    /// <summary>How many bytes getting the model takes. Asks the host once.</summary>
    Task<long> DownloadSizeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The files a load opens: whether they are all on this computer (read without the network) and the download that puts
    /// them there, as the model library's lifecycle drives it (its progress, stopping, failure).
    /// </summary>
    IModelArtifacts Artifacts { get; }

    /// <summary>Loads the model, which is on this computer — a missing one fails instead of being got.</summary>
    Task<ISpeechToText> LoadAsync(CancellationToken cancellationToken);
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

    string ISpeechModelSource.Model => Model;

    // The options the load uses, so the files counted and fetched are the ones it opens.
    public IModelArtifacts Artifacts { get; } = new TranscriberArtifacts(Options(download: true));

    public async Task<ISpeechToText> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new Loaded(await LocalTranscriber.LoadAsync(Model, Options(download: false), null, cancellationToken).ConfigureAwait(false));
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

    private ModelLifecycle? _lifecycle;
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
        return _downloaded = await _source.Artifacts.IsAvailableOfflineAsync(_source.Model, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The download under way or the last one that ended without the model, or <see langword="null"/>.</summary>
    /// <remarks>Reads the lifecycle's status only while the download runs — after it, there is nothing new to read.</remarks>
    public async Task<ModelDownloads.DownloadView?> CurrentAsync(CancellationToken cancellationToken)
    {
        Running? running;
        lock (_lock) running = _running;
        if (running is null) return null;
        if (!running.Task.IsCompleted && _lifecycle is { } lifecycle
            && await lifecycle.GetStatusAsync(running.Kind, running.Model, cancellationToken).ConfigureAwait(false) is { State: ModelState.Downloading, Progress: { } progress })
            running.Progress = new(progress.OverallBytesDownloaded ?? progress.BytesDownloaded, progress.OverallTotalBytes ?? progress.TotalBytes);
        return running.View();
    }

    // The library's lifecycle over this source's files — made on first use, so a copy that never gets the model never builds it.
    private ModelLifecycle Lifecycle(ISpeechModelSource source)
    {
        lock (_lock) return _lifecycle ??= new ModelLifecycle([source.Artifacts]);
    }

    /// <summary>
    /// Gets the model onto this computer in the background and loads it — nothing to do when it is here already.
    /// </summary>
    /// <exception cref="NotSupportedException">This copy cannot run a speech model.</exception>
    public async Task<ModelDownloads.Started> StartDownloadAsync(CancellationToken cancellationToken)
    {
        var source = _source ?? throw new NotSupportedException("This copy cannot run a speech model.");
        if (await DownloadedAsync(cancellationToken).ConfigureAwait(false)) return ModelDownloads.Started.InUse;

        var lifecycle = Lifecycle(source);
        Running running;
        lock (_lock)
        {
            if (_running is { Ended: false }) return ModelDownloads.Started.Busy;
            running = _running = new Running(source.Artifacts.Kind, source.Model);
        }

        egress.Sent(source.Host);
        running.Task = Task.Run(async () =>
        {
            try
            {
                try
                {
                    await lifecycle.DownloadAsync(running.Kind, running.Model, running.Stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (running.Stop.IsCancellationRequested)
                {
                    // Stopped before the lifecycle's download was there to cancel: the wait ends here, the download too.
                    lifecycle.CancelDownload(running.Kind, running.Model);
                    throw;
                }

                var model = await source.LoadAsync(CancellationToken.None).ConfigureAwait(false);
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
                running.Fail((LMSupplyModelSource.Translated(e) ?? e) switch
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
        lock (_lock)
        {
            if (_running is not { Ended: false } running) return;
            running.Stop.Cancel();
            _lifecycle?.CancelDownload(running.Kind, running.Model);
        }
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
                _model = await _source.LoadAsync(CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>The person's side of one download: which files, and how it ended for them.</summary>
    private sealed class Running(string kind, string model)
    {
        private string? _failure;

        public string Kind => kind;

        public string Model => model;

        /// <summary>Ends this caller's wait — the lifecycle's download is cancelled with it (<see cref="StopDownload"/>).</summary>
        public CancellationTokenSource Stop { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;

        /// <summary>The last progress read while it ran.</summary>
        public ModelDownloadProgress Progress { get; set; } = new(0, null);

        public bool Ended => _failure is not null;

        public void Fail(string failure) => _failure = failure;

        public ModelDownloads.DownloadView View() => new(model, null, Progress.Bytes, Progress.Total, _failure);
    }
}
