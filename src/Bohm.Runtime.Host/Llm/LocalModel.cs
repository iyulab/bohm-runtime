using IronProw.LMSupply;
using LMSupply.Generator;
using LMSupply.Generator.Abstractions;
using LMSupply.Llama.Server;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// A language model on this computer that answers an application's AI requests when no key is
/// connected for the provider the application calls. Nothing leaves the computer.
/// </summary>
public sealed record LocalModelOptions
{
    /// <summary>The model file (GGUF).</summary>
    public required string ModelPath { get; init; }

    /// <summary>
    /// The llama-server executable that runs the model. When it is set, nothing is looked up or
    /// downloaded; without it the server is fetched on first use, which needs the internet.
    /// </summary>
    public string? ServerPath { get; init; }

    /// <summary>Context length in tokens; the model's own default when <see langword="null"/>.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Replaces the model — for tests.</summary>
    public IChatClient? Client { get; init; }
}

/// <summary>
/// Loads the configured model on first use and keeps it loaded; loading takes seconds, so it is
/// neither done at start (most sessions never use it) nor repeated per request.
/// </summary>
internal sealed class LocalModel(RuntimeHostOptions options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private ITextGenerator? _generator;
    private GeneratorChatClient? _client;

    /// <summary>Whether a model is configured at all — not whether it loads.</summary>
    public bool Configured => options.LocalModel is not null;

    /// <summary>The model's chat client, loading it the first time.</summary>
    /// <exception cref="LocalModelUnavailableException">The model or its server could not be loaded.</exception>
    public async Task<IChatClient> GetAsync(CancellationToken cancellationToken)
    {
        var settings = options.LocalModel ?? throw new InvalidOperationException("No local model is configured.");
        if (settings.Client is { } replaced) return replaced;
        if (_client is { } loaded) return loaded;

        await _loading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { } raced) return raced;
            if (!File.Exists(settings.ModelPath))
                throw new LocalModelUnavailableException($"The model file {Path.GetFileName(settings.ModelPath)} is not there.");

            var generatorOptions = new GeneratorOptions
            {
                // Only what is on this computer: a missing model fails instead of being downloaded.
                DisableAutoDownload = true,
                MaxContextLength = settings.ContextLength,
                ServerUpdateOptions = settings.ServerPath is { } server
                    ? new LlamaServerUpdateOptions { ServerBinaryPath = server, AutoDownloadUpdates = false }
                    : null,
            };
            try
            {
                _generator = await LocalGenerator.LoadFromPathAsync(settings.ModelPath, generatorOptions).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new LocalModelUnavailableException($"The model on this computer could not be started: {e.Message}", e);
            }

            _client = new GeneratorChatClient(_generator);
            return _client;
        }
        finally
        {
            _loading.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_generator is not null) await _generator.DisposeAsync().ConfigureAwait(false);
        _loading.Dispose();
    }
}

/// <summary>The configured local model could not be loaded.</summary>
internal sealed class LocalModelUnavailableException : Exception
{
    public LocalModelUnavailableException() { }

    public LocalModelUnavailableException(string message) : base(message) { }

    public LocalModelUnavailableException(string message, Exception inner) : base(message, inner) { }
}
