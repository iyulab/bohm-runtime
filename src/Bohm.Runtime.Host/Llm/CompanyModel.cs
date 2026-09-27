using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// A model server run by the person's organization on its own network, reached at an
/// OpenAI-compatible base address — the address any OpenAI client is given, such as
/// <c>http://models.example:8000/v1</c>.
/// </summary>
/// <param name="Endpoint">The OpenAI-compatible base address (<c>http</c> or <c>https</c>, no user name or password in it).</param>
/// <param name="Model">The model's name as the server knows it.</param>
public sealed record CompanyModelOptions(Uri Endpoint, string Model)
{
    /// <summary>Whether <paramref name="endpoint"/> and <paramref name="model"/> name a usable model server, normalized.</summary>
    public static bool TryCreate(string? endpoint, string? model, out CompanyModelOptions? options)
    {
        options = null;
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(model))
            return false;

        // The base is appended to (…/chat/completions), so it ends with a slash.
        var normalized = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
        options = new CompanyModelOptions(normalized, model.Trim());
        return true;
    }
}

/// <summary>
/// The organization's model server (<see cref="CompanyModelOptions"/>). Either fixed by whoever started
/// the runtime — an administrator's policy, which the person cannot change — or set by the person and
/// remembered in <c>company-model.json</c> at the data root. A key, when the server wants one, is kept in
/// the vault like a provider's.
/// </summary>
/// <remarks>
/// When it is set, it answers an application's AI chat requests for a provider with no key connected,
/// and makes proposals unless the person chose a provider for them — ahead of a model on this computer,
/// being the one the organization provides. Requests leave this computer, so each is counted as sent to
/// the server's host.
/// </remarks>
internal sealed class CompanyModel(RuntimeHostOptions options, ICredentialVault vault, Egress egress) : IDisposable
{
    /// <summary>Format identifier written into <c>company-model.json</c>.</summary>
    public const string Format = "bohm.company-model/0";

    /// <summary>Where the server's key, if any, is kept in the vault.</summary>
    public const string VaultName = "llm/company-model";

    private const string FileName = "company-model.json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CompanyModelOptions? _chosen = Read(options);

    /// <summary>Whether the server was fixed by whoever started the runtime, so the person cannot change it.</summary>
    public bool Fixed => options.CompanyModel is not null;

    /// <summary>The server in use, or <see langword="null"/> when there is none.</summary>
    public CompanyModelOptions? Current => options.CompanyModel ?? _chosen;

    /// <summary>Whether a server is set at all — not whether it answers.</summary>
    public bool Configured => Current is not null;

    /// <summary>Whether a key for the server is connected.</summary>
    public bool KeyConnected => !string.IsNullOrEmpty(vault.Read(VaultName));

    /// <summary>The host requests go to — what they are counted as sent to.</summary>
    public string? Host => Current?.Endpoint.Authority;

    /// <summary>Uses <paramref name="choice"/> from now on and remembers it; <see langword="null"/> uses none.</summary>
    /// <exception cref="InvalidOperationException">The server is fixed.</exception>
    public async Task ChooseAsync(CompanyModelOptions? choice, CancellationToken cancellationToken)
    {
        if (Fixed) throw new InvalidOperationException("The organization's model server was set when the runtime was started.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(options.DataRoot, FileName);
            if (choice is null)
            {
                File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(options.DataRoot);
                var aside = path + ".tmp";
                await File.WriteAllTextAsync(aside,
                    JsonSerializer.Serialize(new Stored(Format, choice.Endpoint.AbsoluteUri, choice.Model), CompanyModelJson.Default.Stored) + "\n",
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                File.Move(aside, path, overwrite: true);
            }

            _chosen = choice;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A client for the server, or <see langword="null"/> when none is set. Every request is counted as sent.</summary>
    public IChatClient? Client()
    {
        if (Current is not { } current) return null;
        var key = vault.Read(VaultName);
        var clientOptions = new OpenAIClientOptions { Endpoint = current.Endpoint };
        // Many servers on an organization's network ask for no key; the client library insists on one,
        // so without a key the header it would carry is taken off instead of sending a made-up value.
        if (string.IsNullOrEmpty(key)) clientOptions.AddPolicy(new WithoutAuthorization(), PipelinePosition.BeforeTransport);
        var client = new OpenAI.Chat.ChatClient(current.Model, new ApiKeyCredential(string.IsNullOrEmpty(key) ? "-" : key), clientOptions).AsIChatClient();
        return new CountedAsSent(client, egress, current.Endpoint.Authority);
    }

    /// <summary>The remembered choice, or <see langword="null"/> when there is none or it cannot be read.</summary>
    private static CompanyModelOptions? Read(RuntimeHostOptions options)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(options.DataRoot, FileName)));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("format", out var format) && format.ValueEquals(Format)
                && root.TryGetProperty("endpoint", out var endpoint) && root.TryGetProperty("model", out var model)
                && CompanyModelOptions.TryCreate(endpoint.GetString(), model.GetString(), out var stored))
                return stored;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // None set, or a file that cannot be used: no server until one is set again.
        }

        return null;
    }

    public void Dispose() => _gate.Dispose();

    internal sealed record Stored(string Format, string Endpoint, string Model);

    private sealed class WithoutAuthorization : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Remove("Authorization");
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Remove("Authorization");
            return ProcessNextAsync(message, pipeline, currentIndex);
        }
    }
}

/// <summary>Counts each request to a model as the application's data sent to its host.</summary>
internal sealed class CountedAsSent(IChatClient inner, Egress egress, string host) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        egress.Sent(host);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        egress.Sent(host);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(CompanyModel.Stored))]
internal sealed partial class CompanyModelJson : System.Text.Json.Serialization.JsonSerializerContext;
