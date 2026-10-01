using System.Text.Json;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The model servers an administrator lists for the organization, and the models on each — what the
/// person may choose from. The first model of the first server is used until the person chooses
/// another.
/// </summary>
/// <remarks>
/// Read from the shape model-list files commonly take:
/// <code>
/// { "providers": { "gpu": { "baseUrl": "http://models.example:8000/v1", "api": "openai-completions",
///     "models": [ { "id": "qwen3", "name": "Qwen 3", "contextWindow": 32768, "maxTokens": 8192,
///                   "reasoning": true, "input": ["text", "image"] } ] } } }
/// </code>
/// Only OpenAI-compatible servers are taken (<c>api</c> left out, <c>openai-completions</c>,
/// <c>openai-compatible</c> or <c>openai</c>). A key is never taken from the list — a list an
/// administrator distributes is readable by everyone on the computer — so a server that wants one has
/// it connected by the person, kept in the vault under the server's name. What cannot be used — a
/// server with an unusable address or another API, a model without an id, a repeated name — is left
/// out, and the rest is kept; limits that do not make sense leave that model's limits unknown.
/// </remarks>
public sealed record CompanyModelList(IReadOnlyList<CompanyServer> Servers)
{
    /// <summary>The longest server name taken — it becomes part of the key's name in the vault.</summary>
    public const int MaxServerName = 64;

    private static readonly string[] OpenAICompatible = ["openai-completions", "openai-compatible", "openai"];

    /// <summary>The model used until the person chooses another.</summary>
    public CompanyModelOptions First => Servers[0].Choice(Servers[0].Models[0]);

    /// <summary>Every model as a choice, servers in the list's order.</summary>
    public IEnumerable<CompanyModelOptions> Choices => Servers.SelectMany(s => s.Models.Select(s.Choice));

    /// <summary>The listed model <paramref name="model"/> on server <paramref name="server"/>, or <see langword="null"/>.</summary>
    public CompanyModelOptions? Find(string? server, string? model) =>
        Servers.FirstOrDefault(s => string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase)) is { } found
        && found.Models.FirstOrDefault(m => m.Id == model) is { } listed
            ? found.Choice(listed)
            : null;

    /// <summary>A list of the one server and model given the older way — an address and a model's name.</summary>
    /// <remarks>Its server has no name, so its key stays where a server's key was kept before lists.</remarks>
    public static CompanyModelList Of(CompanyModelOptions server) =>
        new([new CompanyServer("", server.Endpoint, [new CompanyListedModel(server.Model, null, server.Limits, ["text"])])]);

    /// <summary>The usable part of <paramref name="json"/>; <see langword="false"/> when it is not such a list or nothing in it is usable.</summary>
    public static bool TryParse(string? json, out CompanyModelList? list)
    {
        list = null;
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("providers", out var providers) || providers.ValueKind != JsonValueKind.Object)
                return false;

            var servers = new List<CompanyServer>();
            foreach (var provider in providers.EnumerateObject())
            {
                var name = provider.Name.Trim();
                if (name.Length is 0 or > MaxServerName || name.Any(c => char.IsControl(c) || c == '/')
                    || servers.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    || provider.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var server = provider.Value;
                if (server.TryGetProperty("api", out var api)
                    && !(api.ValueKind == JsonValueKind.String && OpenAICompatible.Contains(api.GetString()!.Trim(), StringComparer.OrdinalIgnoreCase)))
                    continue;
                if (!server.TryGetProperty("baseUrl", out var baseUrl) || baseUrl.ValueKind != JsonValueKind.String
                    || !CompanyModelOptions.TryCreate(baseUrl.GetString(), "-", out var address)
                    || !server.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                    continue;

                var listed = new List<CompanyListedModel>();
                foreach (var model in models.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object
                        || !model.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                        || id.GetString()!.Trim() is not { Length: > 0 } modelId
                        || listed.Any(m => m.Id == modelId))
                        continue;
                    listed.Add(new CompanyListedModel(modelId,
                        Text(model, "name"),
                        ModelLimits.TryCreate(Count(model, "contextWindow"), Count(model, "maxTokens"), Flag(model, "reasoning"), out var limits) ? limits! : ModelLimits.Unknown,
                        Input(model)));
                }

                if (listed.Count > 0) servers.Add(new CompanyServer(name, address!.Endpoint, listed));
            }

            if (servers.Count == 0) return false;
            list = new CompanyModelList(servers);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement model, string name) =>
        model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

    private static int? Count(JsonElement model, string name) =>
        model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) ? count : null;

    private static bool? Flag(JsonElement model, string name) =>
        model.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>What the model takes in — <c>text</c>, and <c>image</c> when listed; text alone when the list does not say.</summary>
    private static List<string> Input(JsonElement model)
    {
        if (!model.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array) return ["text"];
        var kinds = input.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String)
            .Select(k => k.GetString()!.Trim().ToLowerInvariant()).Where(k => k is "text" or "image").Distinct().ToList();
        return kinds.Count > 0 ? kinds : ["text"];
    }
}

/// <summary>One listed model server.</summary>
/// <param name="Name">Its name in the list — the key's name in the vault, and how the person's choice remembers it. Empty for a server given the older way.</param>
/// <param name="Endpoint">Its OpenAI-compatible base address.</param>
/// <param name="Models">Its listed models, in the list's order — never empty.</param>
public sealed record CompanyServer(string Name, Uri Endpoint, IReadOnlyList<CompanyListedModel> Models)
{
    /// <summary><paramref name="model"/> on this server, as the server in use.</summary>
    public CompanyModelOptions Choice(CompanyListedModel model) =>
        new(Endpoint, model.Id) { Limits = model.Limits, Server = Name, DisplayName = model.Name, Input = model.Input };
}

/// <summary>One listed model.</summary>
/// <param name="Id">The model's name as the server knows it.</param>
/// <param name="Name">The name to show the person, when the list gives one.</param>
/// <param name="Limits">What the list says about its limits.</param>
/// <param name="Input">What it takes in: <c>text</c>, and <c>image</c> when listed.</param>
public sealed record CompanyListedModel(string Id, string? Name, ModelLimits Limits, IReadOnlyList<string> Input);
