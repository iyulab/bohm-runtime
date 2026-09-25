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

    private readonly SemaphoreSlim _gate = new(1, 1);
    private LocalModelOptions? _chosen = Read(options);
    private ITextGenerator? _generator;
    private GeneratorChatClient? _client;

    /// <summary>Whether the model was fixed by whoever started the runtime, so the person cannot change it.</summary>
    public bool Fixed => options.LocalModel is not null;

    /// <summary>The model in use, or <see langword="null"/> when there is none.</summary>
    public LocalModelOptions? Current => options.LocalModel ?? _chosen;

    /// <summary>Whether a model is set at all — not whether it loads.</summary>
    public bool Configured => Current is not null;

    /// <summary>Whether the model is loaded now.</summary>
    public bool Loaded => _client is not null;

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
            var settings = Current ?? throw new LocalModelUnavailableException("No model on this computer is chosen.");
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

/// <summary>The configured local model could not be loaded.</summary>
internal sealed class LocalModelUnavailableException : Exception
{
    public LocalModelUnavailableException() { }

    public LocalModelUnavailableException(string message) : base(message) { }

    public LocalModelUnavailableException(string message, Exception inner) : base(message, inner) { }
}
