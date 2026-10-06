using System.Text;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using IronHive.Abstractions.Models;
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

    /// <summary>The listed server it is on (<see cref="CompanyModelList"/>); <see langword="null"/> for one the person set by its address.</summary>
    public string? Server { get; init; }

    /// <summary>The name to show the person, when the list gives one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>What the model takes in, when the list says: <c>text</c>, and <c>image</c>.</summary>
    public IReadOnlyList<string>? Input { get; init; }

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
/// The organization's model server (<see cref="CompanyModelOptions"/>). Either listed by whoever started
/// the runtime — an administrator's policy (<see cref="CompanyModelList"/>): the person chooses among the
/// listed models and cannot set another — or set by the person by its address. The choice is remembered
/// in <c>company-model.json</c> at the data root. A key, when a server wants one, is kept in the vault
/// like a provider's — one per listed server.
/// </summary>
/// <remarks>
/// When it is set, it answers an application's AI chat requests for a provider with no key connected,
/// and makes proposals unless the person chose a provider for them — ahead of a model on this computer,
/// being the one the organization provides. Requests leave this computer, so each is counted as sent to
/// the server's host.
/// </remarks>
internal sealed partial class CompanyModel : IDisposable
{
    /// <summary>Format identifier written into <c>company-model.json</c>.</summary>
    public const string Format = "bohm.company-model/0";

    /// <summary>Where the key of a server set by its address (or given the older way) is kept in the vault.</summary>
    public const string VaultName = "llm/company-model";

    private const string FileName = "company-model.json";

    /// <summary>Format identifier written into <c>company-model-learned.json</c>.</summary>
    public const string LearnedFormat = "bohm.company-model-learned/0";

    /// <summary>What was learned of servers' models by asking them — kept apart from the choice, which a listed server takes from the list.</summary>
    private const string LearnedFileName = "company-model-learned.json";

    /// <summary>The one short question that tells whether a model thinks — nothing of an application's goes with it.</summary>
    public const string ThinkingQuestion = "Reply with one word: ok";

    /// <summary>How long the one short question that tells whether a model thinks may take.</summary>
    public static readonly TimeSpan ThinkingQuestionTimeout = TimeSpan.FromSeconds(30);

    private readonly RuntimeHostOptions _options;
    private readonly ICredentialVault _vault;
    private readonly Egress _egress;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _clientLock = new();
    private CompanyModelOptions? _current;
    private (CompanyModelOptions Server, string? Key, ModelFitChatClient Client)? _client;
    private ModelFitChatClient? _askedFor;
    private ModelFitChatClient? _thinkingAskedFor;
    private readonly HashSet<string> _thinks;

    public CompanyModel(RuntimeHostOptions options, ICredentialVault vault, Egress egress)
    {
        (_options, _vault, _egress) = (options, vault, egress);
        var remembered = Read(options);
        _thinks = ReadLearned(options);
        // A listed choice is taken from the list as it is now: a model no longer listed falls back to the first.
        _current = options.CompanyModels is { } list
            ? list.Find(remembered?.Server ?? "", remembered?.Model) ?? list.First
            : remembered;
    }

    /// <summary>Whether an administrator listed the servers, so the person only chooses among them.</summary>
    public bool Fixed => _options.CompanyModels is not null;

    /// <summary>What the person may choose from, when the servers are listed.</summary>
    public CompanyModelList? List => _options.CompanyModels;

    /// <summary>The server in use, or <see langword="null"/> when there is none.</summary>
    public CompanyModelOptions? Current => _current;

    /// <summary>Whether a server is set at all — not whether it answers.</summary>
    public bool Configured => Current is not null;

    /// <summary>Where the key of <paramref name="server"/> (a listed server's name; empty or <see langword="null"/> otherwise) is kept.</summary>
    public static string VaultNameOf(string? server) => string.IsNullOrEmpty(server) ? VaultName : $"{VaultName}/{server}";

    /// <summary>Where the key of the server in use is kept.</summary>
    public string KeyVaultName => VaultNameOf(Current?.Server);

    /// <summary>The key of the server in use, or <see langword="null"/>.</summary>
    public string? Key => _vault.Read(KeyVaultName) is { Length: > 0 } key ? key : null;

    /// <summary>Whether a key for the server in use is connected.</summary>
    public bool KeyConnected => Key is not null;

    /// <summary>Whether a key for <paramref name="server"/> is connected.</summary>
    public bool KeyConnectedFor(string? server) => !string.IsNullOrEmpty(_vault.Read(VaultNameOf(server)));

    /// <summary>The host requests go to — what they are counted as sent to.</summary>
    public string? Host => Current?.Endpoint.Authority;

    /// <summary>
    /// Uses <paramref name="choice"/> from now on and remembers it; <see langword="null"/> uses none.
    /// When the servers are listed, the choice must be one of their models — taken as listed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The servers are listed and <paramref name="choice"/> is not one of their models.</exception>
    public async Task ChooseAsync(CompanyModelOptions? choice, CancellationToken cancellationToken)
    {
        if (List is { } list)
        {
            choice = choice is null ? null : list.Find(choice.Server ?? "", choice.Model);
            if (choice is null)
                throw new InvalidOperationException("The organization's model servers were listed when the runtime was started.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_options.DataRoot, FileName);
            if (choice is null)
            {
                File.Delete(path);
            }
            else
            {
                Directory.CreateDirectory(_options.DataRoot);
                var aside = path + ".tmp";
                await File.WriteAllTextAsync(aside,
                    JsonSerializer.Serialize(new Stored(Format, choice.Endpoint.AbsoluteUri, choice.Model,
                        choice.Limits.ContextWindow, choice.Limits.MaxOutputTokens, choice.Limits.Reasoning,
                        string.IsNullOrEmpty(choice.Server) ? null : choice.Server), CompanyModelJson.Default.Stored) + "\n",
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                File.Move(aside, path, overwrite: true);
            }

            _current = choice;
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

    /// <summary>The context window the server reported for the model in use (<see cref="LimitsAsync"/>), or <see langword="null"/> when it has not been asked or said none.</summary>
    public int? ReportedContextWindow
    {
        get
        {
            lock (_clientLock)
                return _client is { } kept && kept.Server == Current ? kept.Client.ReportedWindow : null;
        }
    }

    /// <summary>The server model's limits as known now — as set, with a context window learned from a refusal when none was set.</summary>
    public ModelLimits Limits
    {
        get
        {
            lock (_clientLock)
                return _client is { } kept && kept.Server == Current ? kept.Client.Limits : Current?.Limits ?? ModelLimits.Unknown;
        }
    }

    /// <summary>
    /// The server model's limits as known now, after asking the server once — per server, model and
    /// key — for the context it accepts when nobody set one. A server that does not say, or does not
    /// answer, leaves the window unknown; the request it was asked for goes on either way.
    /// </summary>
    /// <remarks>
    /// The window comes from the server's model list as IronHive's OpenAI-compatible model finder reads
    /// it (vLLM's <c>max_model_len</c>) — not from the model's name, and not from what the model was
    /// trained on. The request leaves this computer, so it is counted as sent to the server's host.
    /// </remarks>
    /// <param name="again">Asks even when the server was asked before and said nothing — the person checking it again.</param>
    /// <param name="thinking">Also asks whether the model thinks (<see cref="AskThinkingAsync"/>) — before a long task, not in a check, which has its own short wait.</param>
    public async Task<ModelLimits> LimitsAsync(CancellationToken cancellationToken, bool again = false, bool thinking = true)
    {
        if (FittedClient() is not { } client) return ModelLimits.Unknown;
        await AskWindowAsync(client, again, cancellationToken).ConfigureAwait(false);
        if (thinking) await AskThinkingAsync(client, cancellationToken).ConfigureAwait(false);
        return client.Limits;
    }

    private async Task AskWindowAsync(ModelFitChatClient client, bool again, CancellationToken cancellationToken)
    {
        CompanyModelOptions server;
        string? key;
        lock (_clientLock)
        {
            if (client.Limits.ContextWindow is not null || (!again && ReferenceEquals(_askedFor, client)) || _client is not { } kept || kept.Client != client)
                return;
            (_askedFor, server, key) = (client, kept.Server, kept.Key);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        _egress.Sent(server.Endpoint.Authority);
        try
        {
            using var finder = new OpenAICompatibleModelFinder(ConfigOf(server, key));
            if (await finder.FindModelAsync(server.Model, timeout.Token).ConfigureAwait(false) is LanguageModelCard { ContextWindow: { } window })
                client.Reported(window);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not reported: the window stays unknown until a refusal tells it.
        }
        catch (OperationCanceledException)
        {
            lock (_clientLock)
                if (ReferenceEquals(_askedFor, client)) _askedFor = null; // asked again next time
            throw;
        }
    }

    /// <summary>
    /// Whether the model thinks, when nobody said and it was not learned before: one short question, once
    /// per server, model and key. A model that thinks unasked spends a long task's whole answer thinking the
    /// first time — minutes on a server's model — before it is known; a few words of answer tell it first.
    /// What it shows is remembered (<see cref="RememberThinks"/>). A server that does not answer in
    /// <see cref="ThinkingQuestionTimeout"/> leaves it unknown; the task goes on either way.
    /// </summary>
    private async Task AskThinkingAsync(ModelFitChatClient client, CancellationToken cancellationToken)
    {
        lock (_clientLock)
        {
            if (client.Limits.Reasoning is not null || ReferenceEquals(_thinkingAskedFor, client) || _client is not { } kept || kept.Client != client)
                return;
            _thinkingAskedFor = client;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ThinkingQuestionTimeout);
        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, ThinkingQuestion)], new ChatOptions { MaxOutputTokens = 64 }, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No answer, or a refusal: whether it thinks stays unknown until an answer shows it.
        }
        catch (OperationCanceledException)
        {
            lock (_clientLock)
                if (ReferenceEquals(_thinkingAskedFor, client)) _thinkingAskedFor = null; // asked again next time
            throw;
        }
    }

    /// <summary>The whole address the person gave is the base — no API path is added to it.</summary>
    private static OpenAICompatibleConfig ConfigOf(CompanyModelOptions server, string? key) =>
        new() { BaseUrl = server.Endpoint.AbsoluteUri, Path = "", ApiKey = key };

    private ModelFitChatClient? FittedClient()
    {
        if (Current is not { } current) return null;
        var key = Key;
        lock (_clientLock)
        {
            if (_client is { } kept && kept.Server == current && kept.Key == key) return kept.Client;
            var generator = new OpenAICompatibleMessageGenerator(ConfigOf(current, key));
            var limits = current.Limits.Reasoning is null && _thinks.Contains(LearnedName(current)) ? current.Limits with { Reasoning = true } : current.Limits;
            var client = new ModelFitChatClient(
                new CountedAsSent(generator.AsChatClient(current.Model, "openai-compatible"), _egress, current.Endpoint.Authority), limits,
                () => RememberThinks(current));
            _client = (current, key, client);
            return client;
        }
    }

    /// <summary>How a server's model is named among what was learned: its address and model name.</summary>
    private static string LearnedName(CompanyModelOptions server) => server.Endpoint.AbsoluteUri + " " + server.Model;

    /// <summary>
    /// Remembers that <paramref name="server"/>'s model thinks, so the next start knows it before its first
    /// long task. Written whole each time; a file that cannot be written leaves it learned for this run only.
    /// </summary>
    private void RememberThinks(CompanyModelOptions server)
    {
        string[] thinks;
        lock (_clientLock)
        {
            if (!_thinks.Add(LearnedName(server))) return;
            thinks = [.. _thinks.Order(StringComparer.Ordinal)];
        }

        try
        {
            Directory.CreateDirectory(_options.DataRoot);
            var path = Path.Combine(_options.DataRoot, LearnedFileName);
            var aside = path + ".tmp";
            File.WriteAllText(aside, JsonSerializer.Serialize(new Learned(LearnedFormat, thinks), CompanyModelJson.Default.Learned) + "\n", new UTF8Encoding(false));
            File.Move(aside, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Known for this run; asked again after the next start.
        }
    }

    /// <summary>The servers' models learned to think, or none when there is no file or it cannot be read.</summary>
    private static HashSet<string> ReadLearned(RuntimeHostOptions options)
    {
        try
        {
            var learned = JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(options.DataRoot, LearnedFileName)), CompanyModelJson.Default.Learned);
            if (learned is { Format: LearnedFormat, Thinks: { } thinks }) return [.. thinks.Where(t => !string.IsNullOrEmpty(t))];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing learned yet, or a file that cannot be used: learned again by asking.
        }

        return [];
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
                if (root.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.String)
                    stored = stored! with { Server = server.GetString() };
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
        if (Key is { } key) request.Headers.Authorization = new("Bearer", key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        _egress.Sent(server.Endpoint.Authority);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                return new(status is 401 or 403 ? CheckResult.KeyRefused : status == 404 ? CheckResult.NotFound : CheckResult.Refused, status, null);
            var listed = await ListsAsync(response, server.Model, timeout.Token).ConfigureAwait(false);
            if (listed == true) await LimitsAsync(timeout.Token, again: true, thinking: false).ConfigureAwait(false);
            return new(CheckResult.Answers, status, listed, ReportedContextWindow);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(CheckResult.Unreachable, null, null, Unreached: UnreachedOf(e));
        }
    }

    /// <summary>
    /// The models a server serves — at <paramref name="endpoint"/>, or the server in use when that is
    /// <see langword="null"/> — so the person picks a model's name instead of typing it, with the context
    /// window the server reports for each (vLLM's <c>max_model_len</c>) when it does. Uses <paramref name="key"/>,
    /// or, asking the server in use, the connected key. Nothing of an application's goes with it; the
    /// request still leaves this computer, so it is counted as sent to the server's host.
    /// </summary>
    /// <returns><see langword="null"/> when no address was given and no server is set.</returns>
    public async Task<ModelsResult?> ModelsAsync(Uri? endpoint, string? key, CancellationToken cancellationToken)
    {
        var server = endpoint ?? Current?.Endpoint;
        if (server is null) return null;
        if (key is null && Current is { } current && current.Endpoint == server) key = Key;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        _egress.Sent(server.Authority);
        try
        {
            using var finder = new OpenAICompatibleModelFinder(new OpenAICompatibleConfig { BaseUrl = server.AbsoluteUri, Path = "", ApiKey = key });
            var cards = await finder.ListModelsAsync(timeout.Token).ConfigureAwait(false);
            var models = cards
                .Select(card => new ListedModel(card.ModelId, (card as LanguageModelCard)?.ContextWindow))
                .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                .DistinctBy(model => model.Id, StringComparer.Ordinal)
                .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new(CheckResult.Answers, null, models);
        }
        catch (System.ClientModel.ClientResultException e) when (e.Status > 0 && !cancellationToken.IsCancellationRequested)
        {
            // The server answered with a refusal — the OpenAI wire client says so with the status it gave.
            var status = e.Status;
            return new(status is 401 or 403 ? CheckResult.KeyRefused : status == 404 ? CheckResult.NotFound : CheckResult.Refused, status, []);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(CheckResult.Unreachable, null, [], UnreachedOf(e));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException && !cancellationToken.IsCancellationRequested)
        {
            // Answered, but not with a model list it can read: nothing to pick from; the name is typed.
            return new(CheckResult.Answers, null, []);
        }
    }

    /// <summary>
    /// The model the server in use turns speech into text with, picked from its list by name (Whisper and the
    /// other speech-to-text families a server lists under their own names — the list says nothing else of a
    /// model's kind), or <see langword="null"/> when it lists none or no server is set. What an answered list
    /// showed is kept while the server and key stay the same; a list that could not be read is asked again.
    /// </summary>
    public async Task<string?> TranscriptionModelAsync(CancellationToken cancellationToken)
    {
        if (Current is not { } server) return null;
        var asked = (server.Endpoint.AbsoluteUri, Key);
        lock (_clientLock)
            if (_transcription is { } known && known.For == asked) return known.Model;
        var listed = await ModelsAsync(null, null, cancellationToken).ConfigureAwait(false);
        if (listed?.Result != CheckResult.Answers) return null;
        var model = listed.Models.Select(m => m.Id).FirstOrDefault(IsTranscriptionModel);
        lock (_clientLock)
            _transcription = (asked, model);
        return model;
    }

    /// <summary>Whether a listed model's name is one of a speech-to-text family.</summary>
    internal static bool IsTranscriptionModel(string id) => TranscriptionNames().IsMatch(id);

    [System.Text.RegularExpressions.GeneratedRegex(@"whisper|transcri|speech-to-text|sensevoice|paraformer|parakeet|canary|(^|[-_/.])(stt|asr)([-_/.]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TranscriptionNames();

    private ((string Endpoint, string? Key) For, string? Model)? _transcription;

    /// <summary>One model a server lists.</summary>
    /// <param name="Id">The name requests use.</param>
    /// <param name="ContextWindow">The context window the server reports for it, or <see langword="null"/> when it says none.</param>
    internal sealed record ListedModel(string Id, int? ContextWindow);

    /// <summary>What <see cref="ModelsAsync"/> found — <see cref="CheckResult"/>'s results, with the models when it <see cref="CheckResult.Answers"/>.</summary>
    internal sealed record ModelsResult(string Result, int? Status, IReadOnlyList<ListedModel> Models, string? Unreached = null);

    /// <summary>
    /// Why a server was not reached, as far as the failure says — the name does not resolve, nothing
    /// accepts the connection, or no answer came in time — so the person is told what to look at; <see langword="null"/>
    /// when it says none of these. Read from the failure's kind, not its message, which the system words in its own language.
    /// </summary>
    internal static string? UnreachedOf(Exception failure)
    {
        if (failure is OperationCanceledException) return CheckResult.NoAnswer;
        if (failure is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }) return CheckResult.HostNotFound;
        for (var e = failure.InnerException; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException socket)
                return socket.SocketErrorCode switch
                {
                    System.Net.Sockets.SocketError.ConnectionRefused => CheckResult.ConnectionRefused,
                    System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData => CheckResult.HostNotFound,
                    System.Net.Sockets.SocketError.TimedOut => CheckResult.NoAnswer,
                    _ => null,
                };
        }

        return null;
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
    /// <param name="ReportedContextWindow">The context window the server reports for the model, when it lists the model and says it and nobody set one.</param>
    /// <param name="Unreached">For <see cref="Unreachable"/>, why when the failure says: <see cref="HostNotFound"/>, <see cref="ConnectionRefused"/> or <see cref="NoAnswer"/>.</param>
    internal sealed record CheckResult(string Result, int? Status, bool? ModelListed, int? ReportedContextWindow = null, string? Unreached = null)
    {
        public const string Answers = "answers";
        public const string KeyRefused = "key-refused";
        public const string NotFound = "not-found";
        public const string Refused = "refused";
        public const string Unreachable = "unreachable";

        /// <summary>The server's name does not resolve.</summary>
        public const string HostNotFound = "host-not-found";

        /// <summary>Nothing accepts connections at the address.</summary>
        public const string ConnectionRefused = "connection-refused";

        /// <summary>No answer came within <see cref="CheckTimeout"/>.</summary>
        public const string NoAnswer = "no-answer";
    }

    public void Dispose()
    {
        _gate.Dispose();
        _client?.Client.Dispose();
    }

    internal sealed record Stored(string Format, string Endpoint, string Model,
        [property: System.Text.Json.Serialization.JsonPropertyName("contextWindow")] int? ContextWindow = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("maxTokens")] int? MaxTokens = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("reasoning")] bool? Reasoning = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("server")] string? Server = null);

    /// <summary><c>company-model-learned.json</c>: the servers' models (address and model name) learned to think.</summary>
    internal sealed record Learned(string Format, string[]? Thinks);

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
[System.Text.Json.Serialization.JsonSerializable(typeof(CompanyModel.Learned))]
internal sealed partial class CompanyModelJson : System.Text.Json.Serialization.JsonSerializerContext;
