using System.Text;
using System.Text.Json;
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
    /// The llama-server executable that runs the model. Nothing is looked up or downloaded: without
    /// it (or when it is not there) the model cannot be loaded, and the reason says so.
    /// </summary>
    public string? ServerPath { get; init; }

    /// <summary>Context length in tokens; the model's own default when <see langword="null"/>.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Replaces the model — for tests.</summary>
    public IChatClient? Client { get; init; }
}

/// <summary>
/// The model applications' requests go to when no key is connected. Either fixed by whoever started
/// the runtime (<see cref="RuntimeHostOptions.LocalModel"/>) or chosen by the person and remembered
/// in <c>local-model.json</c> at the data root, like a connected key is remembered in the vault.
/// </summary>
/// <remarks>
/// The model loads on first use and stays loaded: loading takes seconds, so it is neither done at
/// start (most sessions never use it) nor repeated per request. Choosing another model unloads the
/// current one; a request it is answering at that moment may fail.
/// </remarks>
internal sealed class LocalModel(RuntimeHostOptions options) : IAsyncDisposable
{
    /// <summary>Format identifier written into <c>local-model.json</c>.</summary>
    public const string Format = "bohm.local-model/0";

    private const string FileName = "local-model.json";

    /// <summary>
    /// How long one call to the model may take before it is given up as hung. Generous on purpose:
    /// on a slow processor a single legitimate call (a long prompt, or one round of a tool loop) can
    /// take several minutes, and the model library's own default (5 minutes) cut such calls off.
    /// Callers still end a call sooner by cancelling it — an application closing its request, or a
    /// person stopping a proposal.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalModelOptions? _chosen = Read(options);
    private ITextGenerator? _generator;
    private GeneratorChatClient? _client;
    private Task? _loading;

    /// <summary>Whether the model was fixed by whoever started the runtime, so the person cannot change it.</summary>
    public bool Fixed => options.LocalModel is not null;

    /// <summary>The model in use, or <see langword="null"/> when there is none.</summary>
    public LocalModelOptions? Current => options.LocalModel ?? _chosen;

    /// <summary>Whether a model is set at all — not whether it loads.</summary>
    public bool Configured => Current is not null;

    /// <summary>Whether the model is loaded now.</summary>
    public bool Loaded => _client is not null;

    /// <summary>Whether a load started ahead of use (<see cref="StartLoading"/>) is still under way.</summary>
    public bool Loading => _loading is { IsCompleted: false };

    /// <summary>Why the last attempt to load the model failed, or <see langword="null"/> — cleared by a new choice or a load that succeeds.</summary>
    public LocalModelFailure? LastFailure { get; private set; }

    /// <summary>
    /// Starts loading the model in the background, so the first request does not wait for it — a
    /// model on a cold disk has taken over a minute. Does nothing when there is no model, it is
    /// loaded, or a load is already under way.
    /// </summary>
    /// <returns>Whether a model is set (whether there was anything to load).</returns>
    public bool StartLoading()
    {
        if (!Configured) return false;
        if (Loaded || Loading) return true;
        _loading = Task.Run(async () =>
        {
            try
            {
                await GetAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (LocalModelUnavailableException)
            {
                // Recorded in LastFailure by GetAsync.
            }
        });
        return true;
    }

    /// <summary>Uses the model file at <paramref name="modelPath"/> from now on, and remembers it.</summary>
    /// <exception cref="InvalidOperationException">The model is fixed.</exception>
    /// <exception cref="ArgumentException">There is no model file at that path.</exception>
    public async Task ChooseAsync(string? modelPath, CancellationToken cancellationToken)
    {
        if (Fixed) throw new InvalidOperationException("The model was set when the runtime was started.");
        if (modelPath is not null
            && (!Path.IsPathFullyQualified(modelPath) || !File.Exists(modelPath)
                || !string.Equals(Path.GetExtension(modelPath), ".gguf", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Not a model file (.gguf) on this computer.", nameof(modelPath));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(options.DataRoot, FileName);
            if (modelPath is null)
            {
                File.Delete(path);
                _chosen = null;
            }
            else
            {
                Directory.CreateDirectory(options.DataRoot);
                var aside = path + ".tmp";
                await File.WriteAllTextAsync(aside,
                    JsonSerializer.Serialize(new Stored(Format, modelPath), LocalModelJson.Default.Stored) + "\n",
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                File.Move(aside, path, overwrite: true);
                _chosen = WithServer(modelPath);
            }

            LastFailure = null;
            await UnloadAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The model's chat client, loading it the first time.</summary>
    /// <exception cref="LocalModelUnavailableException">The model or its server could not be loaded.</exception>
    public async Task<IChatClient> GetAsync(CancellationToken cancellationToken)
    {
        if (Current is { Client: { } replaced }) return replaced;
        if (_client is { } loaded) return loaded;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { } raced) return raced;
            var settings = Current ?? throw new LocalModelUnavailableException(
                new LocalModelFailure(LocalModelFailure.NotChosen), "No model on this computer is chosen.");
            if (!File.Exists(settings.ModelPath))
            {
                var file = Path.GetFileName(settings.ModelPath);
                throw Failed(new LocalModelUnavailableException(
                    new LocalModelFailure(LocalModelFailure.ModelMissing, File: file), $"The model file {file} is not there."));
            }

            // Without a server named here the model library would fetch one from the internet on first
            // use; nothing on this path may leave the computer, so a missing server is a failure instead.
            if (settings.ServerPath is null || !File.Exists(settings.ServerPath))
                throw Failed(new LocalModelUnavailableException(
                    new LocalModelFailure(LocalModelFailure.ServerMissing),
                    "The program that runs models on this computer (llama-server) is not installed with this copy."));

            var generatorOptions = new GeneratorOptions
            {
                // Only what is on this computer: a missing model fails instead of being downloaded.
                DisableAutoDownload = true,
                MaxContextLength = settings.ContextLength,
                ServerUpdateOptions = new LlamaServerUpdateOptions { ServerBinaryPath = settings.ServerPath, AutoDownloadUpdates = false },
                LlamaOptions = new LlamaOptions { RequestTimeout = RequestTimeout },
            };
            try
            {
                _generator = await LocalGenerator.LoadFromPathAsync(settings.ModelPath, generatorOptions).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw Failed(new LocalModelUnavailableException(
                    new LocalModelFailure(LocalModelFailure.StartFailed, Detail: e.Message),
                    $"The model on this computer could not be started: {e.Message}", e));
            }

            LastFailure = null;
            _client = new GeneratorChatClient(_generator);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await UnloadAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task UnloadAsync()
    {
        _client?.Dispose();
        _client = null;
        if (_generator is { } generator)
        {
            _generator = null;
            await generator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private LocalModelUnavailableException Failed(LocalModelUnavailableException e)
    {
        LastFailure = e.Failure;
        return e;
    }

    private LocalModelOptions WithServer(string modelPath) => new() { ModelPath = modelPath, ServerPath = options.LlamaServerPath };

    /// <summary>The remembered choice, or <see langword="null"/> when there is none or it cannot be read.</summary>
    private static LocalModelOptions? Read(RuntimeHostOptions options)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(options.DataRoot, FileName)));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("format", out var format) && format.ValueEquals(Format)
                && root.TryGetProperty("modelPath", out var path) && path.GetString() is { Length: > 0 } modelPath)
                return new LocalModelOptions { ModelPath = modelPath, ServerPath = options.LlamaServerPath };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // None chosen, or a file that cannot be used: no local model until one is chosen again.
        }

        return null;
    }

    internal sealed record Stored(string Format, string ModelPath);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LocalModel.Stored))]
internal sealed partial class LocalModelJson : System.Text.Json.Serialization.JsonSerializerContext;

/// <summary>
/// Why the local model could not be loaded, as a stable reason and the values that go with it. The runtime
/// makes no sentences for the screen — the shell turns the reason into the user's language.
/// </summary>
/// <param name="Reason">One of the reason constants below.</param>
/// <param name="File">The model file's name, for <see cref="ModelMissing"/>.</param>
/// <param name="Detail">The model library's own message, for <see cref="StartFailed"/> — technical, shown as is.</param>
internal sealed record LocalModelFailure(string Reason, string? File = null, string? Detail = null)
{
    /// <summary>No model is chosen.</summary>
    public const string NotChosen = "not-chosen";

    /// <summary>The chosen model file is not there any more.</summary>
    public const string ModelMissing = "model-missing";

    /// <summary>The program that runs models (llama-server) is not installed with this copy.</summary>
    public const string ServerMissing = "server-missing";

    /// <summary>The model library could not start the model.</summary>
    public const string StartFailed = "start-failed";
}

/// <summary>The configured local model could not be loaded.</summary>
/// <remarks>
/// <see cref="Exception.Message"/> is English and goes to apps in their provider's error shape; the shell reads
/// <see cref="Failure"/>.
/// </remarks>
internal sealed class LocalModelUnavailableException : Exception
{
    public LocalModelUnavailableException() { }

    public LocalModelUnavailableException(string message) : base(message) { }

    public LocalModelUnavailableException(string message, Exception inner) : base(message, inner) { }

    public LocalModelUnavailableException(LocalModelFailure failure, string message, Exception? inner = null)
        : base(message, inner) => Failure = failure;

    /// <summary>Why, as a reason — <see langword="null"/> only for an exception made without one.</summary>
    public LocalModelFailure? Failure { get; }
}
