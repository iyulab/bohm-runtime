using System.ClientModel;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Bohm.Runtime.Host.Edit;

/// <summary>A connected provider and the name of its model that proposes changes.</summary>
/// <param name="Provider">The provider's id (<see cref="LlmProvider.Id"/>).</param>
/// <param name="Model">The model's name as the provider knows it.</param>
internal sealed record EditModelChoice(string Provider, string Model);

/// <summary>What is missing for a proposal to be made: <c>localModel</c> (none is chosen) or <c>key</c> (the chosen provider's key is not connected).</summary>
internal sealed record EditModelMissing(string Needs, string? Provider);

/// <summary>The model a proposal is made with, and the name reported with the proposal.</summary>
internal sealed record ChosenEditModel(IChatClient Client, string Name, bool OnThisComputer);

/// <summary>
/// The model the runtime's own agent proposes changes with. The model on this computer unless the
/// person chose a connected provider and one of its models, remembered in <c>edit-model.json</c> at
/// the data root. Which model an application's own requests use is a separate matter.
/// </summary>
/// <remarks>
/// A provider is reached at its OpenAI-compatible base (<see cref="LlmProvider.OpenAICompatiblePath"/>),
/// so one client serves them all. Choosing a provider sends the application's source there, which
/// is why it is the person's choice and never the default; every request is counted as sent.
/// </remarks>
internal sealed class EditModel(RuntimeHostOptions options, ICredentialVault vault, LocalModel local, Egress egress) : IDisposable
{
    /// <summary>Format identifier written into <c>edit-model.json</c>.</summary>
    public const string Format = "bohm.edit-model/0";

    /// <summary>The name a proposal made by the model on this computer is reported with.</summary>
    public const string LocalName = "local";

    private const string FileName = "edit-model.json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private EditModelChoice? _chosen = Read(options);

    /// <summary>The chosen provider and model, or <see langword="null"/> for the model on this computer.</summary>
    public EditModelChoice? Chosen => _chosen;

    /// <summary>What is missing before a proposal can be made, or <see langword="null"/> when nothing is.</summary>
    public EditModelMissing? Missing => _chosen is { } chosen
        ? string.IsNullOrEmpty(vault.Read(LlmProviders.ById(chosen.Provider)!.VaultName)) ? new("key", chosen.Provider) : null
        : local.Configured ? null : new("localModel", null);

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

            _chosen = choice;
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
        if (_chosen is not { } chosen)
            return local.Configured ? new(await local.GetAsync(cancellationToken).ConfigureAwait(false), LocalName, OnThisComputer: true) : null;

        var provider = LlmProviders.ById(chosen.Provider)!;
        var key = vault.Read(provider.VaultName);
        if (string.IsNullOrEmpty(key)) return null;

        var root = options.LlmEndpoints?.GetValueOrDefault(provider.Host) ?? new Uri($"https://{provider.Host}/");
        var client = new OpenAI.Chat.ChatClient(chosen.Model, new ApiKeyCredential(key),
            new OpenAIClientOptions { Endpoint = new Uri(root, provider.OpenAICompatiblePath) }).AsIChatClient();
        return new(new CountedAsSent(client, egress, provider.Host), $"{provider.Id}/{chosen.Model}", OnThisComputer: false);
    }

    /// <summary>The remembered choice, or <see langword="null"/> when there is none or it cannot be read.</summary>
    private static EditModelChoice? Read(RuntimeHostOptions options)
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

    public void Dispose() => _gate.Dispose();

    internal sealed record Stored(string Format, string Provider, string Model);

    /// <summary>Counts each request to the provider as the application's data sent to its host.</summary>
    private sealed class CountedAsSent(IChatClient inner, Egress egress, string host) : DelegatingChatClient(inner)
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
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(EditModel.Stored))]
internal sealed partial class EditModelJson : System.Text.Json.Serialization.JsonSerializerContext;
