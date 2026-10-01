using System.Text;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using IronHive.Extensions.AI;
using IronHive.Providers.OpenAI.Compatible;
using Microsoft.Extensions.AI;

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
    /// <summary>What whoever set the server said about the model's limits.</summary>
    public ModelLimits Limits { get; init; } = ModelLimits.Unknown;

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
    private readonly Lock _clientLock = new();
    private CompanyModelOptions? _chosen = Read(options);
    private (CompanyModelOptions Server, string? Key, ModelFitChatClient Client)? _client;

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
                    JsonSerializer.Serialize(new Stored(Format, choice.Endpoint.AbsoluteUri, choice.Model,
                        choice.Limits.ContextWindow, choice.Limits.MaxOutputTokens, choice.Limits.Reasoning), CompanyModelJson.Default.Stored) + "\n",
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

    /// <summary>
    /// A client for the server, or <see langword="null"/> when none is set. Every request is counted as sent.
    /// </summary>
    /// <remarks>
    /// Through IronHive's OpenAI-compatible provider: it speaks Chat Completions the way self-hosted
    /// servers do (a reasoning model's <c>reasoning_content</c>, no key for a server that asks for none)
    /// and keeps a refusal's HTTP status. The client owns its connections, so one is kept while the
    /// server, model and key stay the same, instead of a new one per request.
    /// </remarks>
    public IChatClient? Client() => FittedClient();

    /// <summary>The server model's limits as known now — as set, with a context window learned from a refusal when none was set.</summary>
    public ModelLimits Limits
    {
        get
        {
            lock (_clientLock)
                return _client is { } kept && kept.Server == Current ? kept.Client.Limits : Current?.Limits ?? ModelLimits.Unknown;
        }
    }

    private ModelFitChatClient? FittedClient()
    {
        if (Current is not { } current) return null;
        var key = vault.Read(VaultName);
        if (string.IsNullOrEmpty(key)) key = null;
        lock (_clientLock)
        {
            if (_client is { } kept && kept.Server == current && kept.Key == key) return kept.Client;
            // The whole address the person gave is the base — no API path is added to it.
            var generator = new OpenAICompatibleMessageGenerator(new OpenAICompatibleConfig { BaseUrl = current.Endpoint.AbsoluteUri, Path = "", ApiKey = key });
            var client = new ModelFitChatClient(
                new CountedAsSent(generator.AsChatClient(current.Model, "openai-compatible"), egress, current.Endpoint.Authority), current.Limits);
            _client = (current, key, client);
            return client;
        }
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
            {
                // Limits were added later: a file without them, or with ones that do not make sense, keeps the server.
                return ModelLimits.TryCreate(Number(root, "contextWindow"), Number(root, "maxTokens"),
                    root.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind is JsonValueKind.True or JsonValueKind.False ? reasoning.GetBoolean() : null,
                    out var limits)
                    ? stored! with { Limits = limits! }
                    : stored;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // None set, or a file that cannot be used: no server until one is set again.
        }

        return null;
    }

    /// <summary>How long <see cref="CheckAsync"/> waits for the server before calling it unreachable.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Asks the server once for its models (<c>GET {base}models</c>, with the key when one is connected) —
    /// whether it answers, and whether it knows the model it was set with — so a wrong address, key or
    /// model name shows where it was set, not at the first question. Nothing of an application's goes
    /// with it; the request still leaves this computer, so it is counted as sent to the server's host.
    /// </summary>
    /// <returns><see langword="null"/> when no server is set.</returns>
    public async Task<CheckResult?> CheckAsync(HttpClient http, CancellationToken cancellationToken)
    {
        if (Current is not { } server) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server.Endpoint, "models"));
        if (vault.Read(VaultName) is { Length: > 0 } key) request.Headers.Authorization = new("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        egress.Sent(server.Endpoint.Authority);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return new(status is 401 or 403 ? CheckResult.KeyRefused : status == 404 ? CheckResult.NotFound : CheckResult.Refused, status, null);
            return new(CheckResult.Answers, status, await ListsAsync(response, server.Model, timeout.Token).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(CheckResult.Unreachable, null, null);
        }
    }

    /// <summary>Whether an OpenAI-shaped model list (<c>{ data: [{ id }] }</c>) names <paramref name="model"/>; <see langword="null"/> when the answer is not such a list.</summary>
    private static async Task<bool?> ListsAsync(HttpResponseMessage response, string model, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var list = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (list.RootElement.ValueKind != JsonValueKind.Object
                || !list.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;
            return data.EnumerateArray().Any(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String && string.Equals(id.GetString(), model, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What <see cref="CheckAsync"/> found.</summary>
    /// <param name="Result">One of <see cref="Answers"/>, <see cref="KeyRefused"/>, <see cref="NotFound"/>, <see cref="Refused"/>, <see cref="Unreachable"/>.</param>
    /// <param name="Status">The server's HTTP status, when it answered.</param>
    /// <param name="ModelListed">Whether its model list names the model it was set with; <see langword="null"/> when it gave no such list.</param>
    internal sealed record CheckResult(string Result, int? Status, bool? ModelListed)
    {
        public const string Answers = "answers";
        public const string KeyRefused = "key-refused";
        public const string NotFound = "not-found";
        public const string Refused = "refused";
        public const string Unreachable = "unreachable";
    }

    public void Dispose()
    {
        _gate.Dispose();
        _client?.Client.Dispose();
    }

    internal sealed record Stored(string Format, string Endpoint, string Model,
        [property: System.Text.Json.Serialization.JsonPropertyName("contextWindow")] int? ContextWindow = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("maxTokens")] int? MaxTokens = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("reasoning")] bool? Reasoning = null);

    /// <summary>A whole number at <paramref name="name"/> that fits an <see cref="int"/>, or <see langword="null"/>.</summary>
    private static int? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
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

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(CompanyModel.Stored))]
internal sealed partial class CompanyModelJson : System.Text.Json.Serialization.JsonSerializerContext;
