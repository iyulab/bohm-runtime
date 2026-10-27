using System.Text.Json;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Llm;
using DesktopHost.Files;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Edit;

/// <summary>A connected provider and the name of its model that proposes changes.</summary>
/// <param name="Provider">The provider's id (<see cref="ProviderDeclaration.Id"/>).</param>
/// <param name="Model">The model's name as the provider knows it.</param>
internal sealed record EditModelChoice(string Provider, string Model);

/// <summary>What is missing for a proposal to be made: <c>localModel</c> (neither a model on this computer nor the organization's model server is set) or <c>key</c> (the chosen provider's key is not connected).</summary>
internal sealed record EditModelMissing(string Needs, string? Provider);

/// <summary>The model a proposal is made with, the name reported with the proposal, and what is known of the model's limits.</summary>
/// <param name="Sees">Whether the model takes pictures in: what its server's model list says, <see langword="false"/> for the model on
/// this computer, <see langword="null"/> when nobody said (<see cref="Llm.ImageInput"/> learns it once).</param>
internal sealed record ChosenEditModel(IChatClient Client, string Name, bool OnThisComputer, ModelLimits Limits, bool? Sees = null);

/// <summary>
/// A model the person may choose for one of the runtime's agents: by default the organization's model
/// server when one is set (<see cref="CompanyModel"/>), otherwise the model on this computer — unless the
/// person chose a connected provider and one of its models, remembered in a file at the data root.
/// </summary>
/// <remarks>
/// A provider is reached in the format its declaration says (<see cref="ProviderClients"/>): the built-in ones as
/// before (OpenAI, Anthropic and Gemini through their own APIs, the rest at an OpenAI-compatible base) and any the
/// person added in the format they chose. Choosing a provider sends what the agent works on
/// there, which is why it is the person's choice and never the default; every request is counted as sent.
/// A provider's client owns its connections, so one is kept while the provider, model and key stay the same.
/// </remarks>
internal abstract class ProviderChoice(RuntimeHostOptions options, ICredentialVault vault, LocalModel local, CompanyModel company, CloudAccount account, Egress egress, ProviderCatalog catalog) : IDisposable
{
    /// <summary>The file at the data root the choice is remembered in.</summary>
    protected abstract string FileName { get; }

    /// <summary>Format identifier written into <see cref="FileName"/>.</summary>
    protected abstract string Format { get; }

    /// <summary>The name a proposal made by the model on this computer is reported with.</summary>
    public const string LocalName = "local";

    /// <summary>The prefix a proposal made by the organization's model server is reported with, before the model's name.</summary>
    public const string CompanyName = "company";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _clientLock = new();
    private (EditModelChoice Choice, string Key, IChatClient Client)? _client;
    private EditModelChoice? _read;
    private bool _wasRead;

    /// <summary>The remembered choice, read from its file the first time it is needed (the file name belongs to the derived class).</summary>
    private EditModelChoice? Remembered
    {
        get
        {
            if (!_wasRead) (_read, _wasRead) = (Read(), true);
            // A provider taken away since it was chosen is no longer chosen.
            return _read is { } read && !Choosable(read.Provider) ? null : _read;
        }
        set => (_read, _wasRead) = (value, true);
    }

    /// <summary>The chosen provider and model, or <see langword="null"/> for the model on this computer.</summary>
    public EditModelChoice? Chosen => Remembered;

    /// <summary>What is missing before a proposal can be made, or <see langword="null"/> when nothing is.</summary>
    /// <remarks>The Bohm account (<see cref="CloudAccount"/>) needs its access token — <c>account</c>.</remarks>
    public EditModelMissing? Missing => Remembered switch
    {
        { Provider: CloudAccount.ProviderId } => !account.SignedIn ? new("account", CloudAccount.ProviderId) : null,
        { } chosen => catalog.ById(chosen.Provider) is { } provider && (provider.Auth.Style == AuthStyle.None || !string.IsNullOrEmpty(vault.Read(provider.VaultName))) ? null : new("key", chosen.Provider),
        null => company.Configured || local.Configured ? null : new("localModel", null),
    };

    /// <summary>Whether <paramref name="provider"/> can be chosen: a provider on the list, or the Bohm account when there is one.</summary>
    private bool Choosable(string provider) =>
        catalog.ById(provider) is not null || (provider == CloudAccount.ProviderId && account.Configured);

    /// <summary>Uses <paramref name="choice"/> from now on and remembers it; <see langword="null"/> goes back to the model on this computer.</summary>
    /// <exception cref="ArgumentException">No such provider, or no model name.</exception>
    public async Task ChooseAsync(EditModelChoice? choice, CancellationToken cancellationToken)
    {
        if (choice is not null && (!Choosable(choice.Provider) || string.IsNullOrWhiteSpace(choice.Model)))
            throw new ArgumentException("An AI provider and the name of one of its models.", nameof(choice));

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
                choice = choice with { Model = choice.Model.Trim() };
                Directory.CreateDirectory(options.DataRoot);
                await AtomicFile.WriteAllTextAsync(path,
                    JsonSerializer.Serialize(new Stored(Format, choice.Provider, choice.Model), EditModelJson.Default.Stored) + "\n", cancellationToken).ConfigureAwait(false);
            }

            Remembered = choice;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The chosen model, ready to be asked; <see langword="null"/> when something is <see cref="Missing"/>.</summary>
    /// <exception cref="LocalModelUnavailableException">The model on this computer cannot be loaded.</exception>
    public async Task<ChosenEditModel?> GetAsync(CancellationToken cancellationToken)
    {
        if (Remembered is not { } chosen)
        {
            if (company.Client() is { } organizations)
                return new(organizations, $"{CompanyName}/{company.Current!.Model}", OnThisComputer: false,
                    await company.LimitsAsync(cancellationToken).ConfigureAwait(false), company.Current.Input is { } input ? input.Contains("image") : null);
            if (!local.Configured) return null;
            var onThisComputer = await local.GetAsync(cancellationToken).ConfigureAwait(false);
            return new(onThisComputer, LocalName, OnThisComputer: true, local.Limits, Sees: false);
        }

        if (chosen.Provider == CloudAccount.ProviderId)
        {
            var (token, tokenProblem) = await account.TokenAsync(cancellationToken).ConfigureAwait(false);
            if (tokenProblem == "signedOut") return null;
            // The cloud names the gateway the account's calls go to; when it — or a fresh token — cannot be had, the request
            // fails as an unreachable provider does: the same words, the same «try again».
            var (anthropic, problem) = token is null ? (null, tokenProblem) : await account.AiAsync(cancellationToken).ConfigureAwait(false);
            var client = anthropic is null || token is null
                ? new UnreachableChatClient($"The Bohm account's AI address could not be told ({problem}).")
                : Kept(chosen, token + "\n" + anthropic.AbsoluteUri, () => new CountedAsSent(CloudAccount.Client(chosen.Model, token, anthropic), egress, anthropic.Authority));
            return new(client, $"{CloudAccount.ProviderId}/{chosen.Model}", OnThisComputer: false, ModelLimits.Unknown, Sees: true);
        }

        var provider = catalog.ById(chosen.Provider)!;
        var key = provider.Auth.Style == AuthStyle.None ? null : vault.Read(provider.VaultName);
        if (provider.Auth.Style != AuthStyle.None && string.IsNullOrEmpty(key)) return null;

        // Counted as sent where it goes: the provider, or the gateway its traffic is sent to instead.
        var declared = provider.Find(chosen.Model);
        return new(Kept(chosen, provider.BaseUrl + "\n" + key, () => new CountedAsSent(ProviderClients.Create(provider, declared, chosen.Model, key), egress, provider.BaseUrl.Authority)),
            $"{provider.Id}/{chosen.Model}", OnThisComputer: false, declared?.Limits ?? ModelLimits.Unknown, Sees: Sees(provider, declared));
    }

    /// <summary>
    /// Whether the model takes pictures in: what the declaration says of the model; otherwise yes for a built-in provider reached
    /// through its own API, whose models take pictures in as their documentation says (the chat models they offer now all do) — not
    /// asked. A provider at an OpenAI-compatible base serves models of many kinds: <see langword="null"/>, asked.
    /// </summary>
    private static bool? Sees(ProviderDeclaration provider, DeclaredModel? declared) =>
        declared?.Input is { } input ? input.Contains("image")
        : provider.Origin == ProviderOrigin.BuiltIn && provider.Api != ProviderApi.OpenAIChat ? true : null;

    /// <summary>
    /// The model that reviews a newly written application once before it is polished (<see cref="RuntimeHostOptions.ReviewModel"/>),
    /// ready to be asked with its name — or <see langword="null"/> when none is set or its provider's key is not connected.
    /// </summary>
    public (IChatClient Client, string Name)? Reviewer()
    {
        if (options.ReviewModel is not { } review || catalog.ById(review.Provider) is not { } provider) return null;
        var key = provider.Auth.Style == AuthStyle.None ? null : vault.Read(provider.VaultName);
        if (provider.Auth.Style != AuthStyle.None && string.IsNullOrEmpty(key)) return null;
        var identity = provider.BaseUrl + "\n" + key;
        lock (_clientLock)
        {
            if (_reviewer is { } kept && kept.Key == identity) return (kept.Client, $"{provider.Id}/{review.Model}");
        }

        var client = new CountedAsSent(ProviderClients.Create(provider, provider.Find(review.Model), review.Model, key), egress, provider.BaseUrl.Authority);
        lock (_clientLock)
        {
            _reviewer?.Client.Dispose();
            _reviewer = (identity, client);
        }

        return (client, $"{provider.Id}/{review.Model}");
    }

    private (string Key, IChatClient Client)? _reviewer;

    /// <summary>The client kept for <paramref name="choice"/> and <paramref name="key"/>, made by <paramref name="create"/> when they changed.</summary>
    private IChatClient Kept(EditModelChoice choice, string key, Func<IChatClient> create)
    {
        lock (_clientLock)
        {
            if (_client is { } kept && kept.Choice == choice && kept.Key == key) return kept.Client;
        }

        var client = create();
        lock (_clientLock)
            _client = (choice, key, client);
        return client;
    }

    /// <summary>The remembered choice, or <see langword="null"/> when there is none or it cannot be read.</summary>
    private EditModelChoice? Read()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(options.DataRoot, FileName)));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("format", out var format) && format.ValueEquals(Format)
                && root.TryGetProperty("provider", out var provider) && provider.GetString() is { } known && Choosable(known)
                && root.TryGetProperty("model", out var model) && model.GetString() is { Length: > 0 } name)
                return new EditModelChoice(known, name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // None chosen, or a file that cannot be used: the model on this computer until one is chosen again.
        }

        return null;
    }

    public void Dispose()
    {
        _gate.Dispose();
        _client?.Client.Dispose();
        _reviewer?.Client.Dispose();
    }

    internal sealed record Stored(string Format, string Provider, string Model);
}

/// <summary>The model the runtime's own agent proposes changes to an application with (<c>edit-model.json</c>).</summary>
internal sealed class EditModel(RuntimeHostOptions options, ICredentialVault vault, LocalModel local, CompanyModel company, CloudAccount account, Egress egress, ProviderCatalog catalog)
    : ProviderChoice(options, vault, local, company, account, egress, catalog)
{
    protected override string FileName => "edit-model.json";

    protected override string Format => "bohm.edit-model/0";
}

/// <summary>
/// The model questions about the open web pages go to (<c>agent-model.json</c>). The pages may be the
/// organization's own, so a provider is only ever the person's explicit choice.
/// </summary>
internal sealed class AgentModel(RuntimeHostOptions options, ICredentialVault vault, LocalModel local, CompanyModel company, CloudAccount account, Egress egress, ProviderCatalog catalog)
    : ProviderChoice(options, vault, local, company, account, egress, catalog)
{
    protected override string FileName => "agent-model.json";

    protected override string Format => "bohm.agent-model/0";
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ProviderChoice.Stored))]
internal sealed partial class EditModelJson : System.Text.Json.Serialization.JsonSerializerContext;
