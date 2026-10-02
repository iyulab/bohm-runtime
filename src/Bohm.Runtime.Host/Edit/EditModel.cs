using System.Text;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Llm;
using IronHive.Extensions.AI;
using IronHive.Providers.Anthropic;
using IronHive.Providers.GoogleAI;
using IronHive.Providers.OpenAI;
using IronHive.Providers.OpenAI.Compatible;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Edit;

/// <summary>A connected provider and the name of its model that proposes changes.</summary>
/// <param name="Provider">The provider's id (<see cref="LlmProvider.Id"/>).</param>
/// <param name="Model">The model's name as the provider knows it.</param>
internal sealed record EditModelChoice(string Provider, string Model);

/// <summary>What is missing for a proposal to be made: <c>localModel</c> (neither a model on this computer nor the organization's model server is set) or <c>key</c> (the chosen provider's key is not connected).</summary>
internal sealed record EditModelMissing(string Needs, string? Provider);

/// <summary>The model a proposal is made with, the name reported with the proposal, and what is known of the model's limits.</summary>
internal sealed record ChosenEditModel(IChatClient Client, string Name, bool OnThisComputer, ModelLimits Limits);

/// <summary>
/// A model the person may choose for one of the runtime's agents: by default the organization's model
/// server when one is set (<see cref="CompanyModel"/>), otherwise the model on this computer — unless the
/// person chose a connected provider and one of its models, remembered in a file at the data root.
/// </summary>
/// <remarks>
/// OpenAI, Anthropic and Gemini are reached through their own APIs, the rest at their OpenAI-compatible
/// base (<see cref="LlmProvider.OpenAICompatiblePath"/>). Choosing a provider sends what the agent works on
/// there, which is why it is the person's choice and never the default; every request is counted as sent.
/// A provider's client owns its connections, so one is kept while the provider, model and key stay the same.
/// </remarks>
internal abstract class ProviderChoice(RuntimeHostOptions options, ICredentialVault vault, LocalModel local, CompanyModel company, Egress egress) : IDisposable
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
            return _read;
        }
        set => (_read, _wasRead) = (value, true);
    }

    /// <summary>The chosen provider and model, or <see langword="null"/> for the model on this computer.</summary>
    public EditModelChoice? Chosen => Remembered;

    /// <summary>What is missing before a proposal can be made, or <see langword="null"/> when nothing is.</summary>
    public EditModelMissing? Missing => Remembered is { } chosen
        ? string.IsNullOrEmpty(vault.Read(LlmProviders.ById(chosen.Provider)!.VaultName)) ? new("key", chosen.Provider) : null
        : company.Configured || local.Configured ? null : new("localModel", null);

    /// <summary>Uses <paramref name="choice"/> from now on and remembers it; <see langword="null"/> goes back to the model on this computer.</summary>
    /// <exception cref="ArgumentException">No such provider, or no model name.</exception>
    public async Task ChooseAsync(EditModelChoice? choice, CancellationToken cancellationToken)
    {
        if (choice is not null && (LlmProviders.ById(choice.Provider) is null || string.IsNullOrWhiteSpace(choice.Model)))
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
                var aside = path + ".tmp";
                await File.WriteAllTextAsync(aside,
                    JsonSerializer.Serialize(new Stored(Format, choice.Provider, choice.Model), EditModelJson.Default.Stored) + "\n",
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                File.Move(aside, path, overwrite: true);
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
                    await company.LimitsAsync(cancellationToken).ConfigureAwait(false));
            if (!local.Configured) return null;
            var onThisComputer = await local.GetAsync(cancellationToken).ConfigureAwait(false);
            return new(onThisComputer, LocalName, OnThisComputer: true, local.Limits);
        }

        var provider = LlmProviders.ById(chosen.Provider)!;
        var key = vault.Read(provider.VaultName);
        if (string.IsNullOrEmpty(key)) return null;

        lock (_clientLock)
        {
            if (_client is { } kept && kept.Choice == chosen && kept.Key == key)
                return new(kept.Client, $"{provider.Id}/{chosen.Model}", OnThisComputer: false, ModelLimits.Unknown);
        }

        var root = options.LlmEndpoints?.GetValueOrDefault(provider.Host) ?? new Uri($"https://{provider.Host}/");
        var client = provider.Id switch
        {
            // Gemini through its own API: its models sign each tool call and refuse the next turn without
            // the signature, which the OpenAI-compatible base drops on the way back. IronHive's Gemini
            // provider carries the signatures through the tool loop.
            "google" => new GoogleAIMessageGenerator(new GoogleAIConfig { ApiKey = key, HttpOptions = new Google.GenAI.Types.HttpOptions { BaseUrl = root.ToString().TrimEnd('/') } })
                .AsChatClient(chosen.Model, "googleai"),
            // Anthropic through its own Messages API: its OpenAI-compatible base is meant for trying
            // models out, not for real use, and leaves out what the Messages API carries.
            "anthropic" => new AnthropicMessageGenerator(new AnthropicConfig { ApiKey = key, BaseUrl = root.ToString().TrimEnd('/') })
                .AsChatClient(chosen.Model, "anthropic"),
            // OpenAI through its own Responses API. Every request says store:false, so the application's
            // source is sent to be answered, not kept on OpenAI's side.
            "openai" => new OpenAIMessageGenerator(new OpenAIConfig { ApiKey = key, BaseUrl = new Uri(root, provider.OpenAICompatiblePath).ToString().TrimEnd('/') })
                .AsChatClient(chosen.Model, "openai"),
            // The rest speak Chat Completions at an OpenAI-compatible base — the same provider as the
            // organization's server (a reasoning model's reasoning_content, a refusal's status).
            _ => new OpenAICompatibleMessageGenerator(new OpenAICompatibleConfig { BaseUrl = new Uri(root, provider.OpenAICompatiblePath).AbsoluteUri, Path = "", ApiKey = key })
                .AsChatClient(chosen.Model, "openai-compatible"),
        };
        var counted = new CountedAsSent(client, egress, provider.Host);
        lock (_clientLock)
            _client = (chosen, key, counted);
        return new(counted, $"{provider.Id}/{chosen.Model}", OnThisComputer: false, ModelLimits.Unknown);
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
                && root.TryGetProperty("provider", out var provider) && LlmProviders.ById(provider.GetString() ?? "") is { } known
                && root.TryGetProperty("model", out var model) && model.GetString() is { Length: > 0 } name)
                return new EditModelChoice(known.Id, name);
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
    }

    internal sealed record Stored(string Format, string Provider, string Model);
}

/// <summary>The model the runtime's own agent proposes changes to an application with (<c>edit-model.json</c>).</summary>
internal sealed class EditModel(RuntimeHostOptions options, ICredentialVault vault, LocalModel local, CompanyModel company, Egress egress)
    : ProviderChoice(options, vault, local, company, egress)
{
    protected override string FileName => "edit-model.json";

    protected override string Format => "bohm.edit-model/0";
}

/// <summary>
/// The model questions about the open web pages go to (<c>agent-model.json</c>). The pages may be the
/// organization's own, so a provider is only ever the person's explicit choice.
/// </summary>
internal sealed class AgentModel(RuntimeHostOptions options, ICredentialVault vault, LocalModel local, CompanyModel company, Egress egress)
    : ProviderChoice(options, vault, local, company, egress)
{
    protected override string FileName => "agent-model.json";

    protected override string Format => "bohm.agent-model/0";
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ProviderChoice.Stored))]
internal sealed partial class EditModelJson : System.Text.Json.Serialization.JsonSerializerContext;
