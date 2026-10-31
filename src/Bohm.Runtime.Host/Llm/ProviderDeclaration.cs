using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.Host.Llm;

/// <summary>The wire format a provider speaks — a closed set, each with an adapter behind it.</summary>
internal enum ProviderApi
{
    /// <summary>OpenAI Chat Completions, as every OpenAI-compatible server speaks it.</summary>
    OpenAIChat,

    /// <summary>OpenAI Responses.</summary>
    OpenAIResponses,

    /// <summary>Anthropic Messages.</summary>
    AnthropicMessages,

    /// <summary>Gemini <c>generateContent</c>.</summary>
    GoogleGemini,
}

internal static class ProviderApis
{
    private static readonly (string Name, ProviderApi Api)[] Names =
    [
        ("openai-chat", ProviderApi.OpenAIChat),
        ("openai-responses", ProviderApi.OpenAIResponses),
        ("anthropic-messages", ProviderApi.AnthropicMessages),
        ("google-gemini", ProviderApi.GoogleGemini),
    ];

    public static string NameOf(ProviderApi api) => Names.First(n => n.Api == api).Name;

    public static bool TryParse(string? name, out ProviderApi api)
    {
        foreach (var known in Names)
        {
            if (string.Equals(known.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                api = known.Api;
                return true;
            }
        }

        api = default;
        return false;
    }
}

/// <summary>How a provider expects the key on a request.</summary>
internal enum AuthStyle
{
    /// <summary><c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    Bearer,

    /// <summary>The key in a header the declaration names.</summary>
    Header,

    /// <summary><c>x-goog-api-key</c>.</summary>
    Google,

    /// <summary>No key: the server asks for none.</summary>
    None,
}

/// <param name="Style">How the key is presented.</param>
/// <param name="HeaderName">The header's name when <paramref name="Style"/> is <see cref="AuthStyle.Header"/>.</param>
/// <param name="KeyOptional">The key is presented when one is connected but none is needed — a server an administrator listed, which may or may not ask for one. Not part of the file format: only a listed server has it.</param>
internal sealed record ProviderAuth(AuthStyle Style, string? HeaderName = null, bool KeyOptional = false)
{
    public static readonly ProviderAuth Bearer = new(AuthStyle.Bearer);

    public static readonly ProviderAuth None = new(AuthStyle.None);

    /// <summary>A bearer key that is presented when connected and not needed to use the provider.</summary>
    public static readonly ProviderAuth OptionalBearer = new(AuthStyle.Bearer, KeyOptional: true);

    /// <summary>Whether the provider has a place for a key at all — one that can be connected, and is presented when it is.</summary>
    public bool TakesKey => Style != AuthStyle.None;

    /// <summary>Whether the provider cannot be used until a key is connected.</summary>
    public bool KeyRequired => TakesKey && !KeyOptional;

    internal static string NameOf(AuthStyle style) => style switch
    {
        AuthStyle.Bearer => "bearer",
        AuthStyle.Header => "header",
        AuthStyle.Google => "google",
        _ => "none",
    };
}

/// <summary>Where a declaration came from. A later value stands over an earlier one with the same id.</summary>
internal enum ProviderOrigin
{
    /// <summary>Shipped with the runtime.</summary>
    BuiltIn,

    /// <summary>Added by the person.</summary>
    Person,

    /// <summary>Given by whoever administers this computer; the person only chooses among them.</summary>
    Policy,
}

/// <summary>One model of a provider, with what the declaration says about it.</summary>
/// <param name="Id">The model's name as the server knows it.</param>
/// <param name="Name">The name to show the person, when the declaration gives one.</param>
/// <param name="Api">A format of its own, for a gateway whose models speak different formats; <see langword="null"/> takes the provider's.</param>
/// <param name="Path">A path of its own, relative to the provider's base address and ending with a slash; <see langword="null"/> takes the base address.</param>
/// <param name="Limits">What is known of its limits.</param>
/// <param name="Input">What it takes in — <c>text</c>, and <c>image</c> when declared; <see langword="null"/> when the declaration does not say.</param>
internal sealed record DeclaredModel(string Id, string? Name, ProviderApi? Api, string? Path, ModelLimits Limits, IReadOnlyList<string>? Input);

/// <summary>
/// An AI provider as data: where it is, which format it speaks, how it wants the key, and the models
/// the person may choose. Built-in providers, ones an administrator lists and ones the person adds
/// all have this shape; only <see cref="Origin"/> differs. The key is never part of it — it is kept
/// in the vault under <see cref="VaultName"/>.
/// </summary>
/// <param name="Id">Short identifier: lowercase letters, digits and <c>-</c>.</param>
/// <param name="Name">The name to show the person.</param>
/// <param name="BaseUrl">The address requests are made under, ending with a slash.</param>
/// <param name="Api">The format the provider speaks, unless a model says its own.</param>
/// <param name="Auth">How the key is presented.</param>
/// <param name="Models">The models to choose from; empty when the server is to be asked.</param>
/// <param name="Origin">Where it came from.</param>
internal sealed partial record ProviderDeclaration(
    string Id, string Name, Uri BaseUrl, ProviderApi Api, ProviderAuth Auth, IReadOnlyList<DeclaredModel> Models, ProviderOrigin Origin)
{
    /// <summary>The longest id taken.</summary>
    public const int MaxId = 32;

    /// <summary>Ids that name something other than a provider declaration.</summary>
    private static readonly string[] Reserved = ["cloud", "company-model", "local", "key", "providers"];

    /// <summary>Header names a declaration cannot use for the key: the ones the request itself needs.</summary>
    private static readonly string[] ForbiddenHeaders = ["host", "content-length", "content-type", "transfer-encoding", "connection", "accept"];

    /// <summary>Properties that would hold a key. A key does not belong in a declaration, so one that has it is refused rather than quietly kept.</summary>
    private static readonly string[] KeyProperties = ["apikey", "api_key", "key", "token", "secret", "password"];

    /// <summary>Where the key of provider <paramref name="id"/> is kept in the vault.</summary>
    public static string VaultNameOf(string id) => $"llm/{id}";

    /// <summary>
    /// A place in the vault of its own for the key, when it is not <see cref="VaultNameOf"/> of the id — a server an administrator
    /// listed keeps its key where a listed server's key has always been kept (<see cref="CompanyModel.VaultNameOf"/>), so a key already
    /// connected stays connected. Not part of the file format.
    /// </summary>
    public string? KeyVault { get; init; }

    /// <summary>Where this provider's key is kept in the vault.</summary>
    public string VaultName => KeyVault ?? VaultNameOf(Id);

    /// <summary>The format <paramref name="model"/> is spoken in.</summary>
    public ProviderApi ApiOf(DeclaredModel model) => model.Api ?? Api;

    /// <summary>The address requests for <paramref name="model"/> are made under: the base address, plus the model's path when it has one.</summary>
    public Uri RootOf(DeclaredModel model) => model.Path is { Length: > 0 } path ? new Uri(BaseUrl, path) : BaseUrl;

    /// <summary>The declared model <paramref name="id"/>, or <see langword="null"/>.</summary>
    public DeclaredModel? Find(string? id) => Models.FirstOrDefault(m => m.Id == id);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,30}[a-z0-9])?$")]
    private static partial Regex IdPattern();

    /// <summary>Whether <paramref name="id"/> is one a declaration may carry: the pattern, and not one that names something else.</summary>
    public static bool IsUsableId(string id) => IdPattern().IsMatch(id) && !Reserved.Contains(id);

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex HeaderNamePattern();

    /// <summary>
    /// Reads one declaration. <paramref name="problem"/> says what was wrong with a refusal, naming the
    /// property — it is for the person who wrote the declaration, so it is plain words and never echoes a value.
    /// </summary>
    public static bool TryParse(string? json, ProviderOrigin origin, out ProviderDeclaration? declaration, out string? problem)
    {
        declaration = null;
        try
        {
            using var document = JsonDocument.Parse(json ?? "");
            return TryParse(document.RootElement, origin, out declaration, out problem);
        }
        catch (JsonException)
        {
            problem = "not JSON";
            return false;
        }
    }

    /// <inheritdoc cref="TryParse(string?, ProviderOrigin, out ProviderDeclaration?, out string?)"/>
    public static bool TryParse(JsonElement element, ProviderOrigin origin, out ProviderDeclaration? declaration, out string? problem)
    {
        declaration = null;
        if (element.ValueKind != JsonValueKind.Object) return Refuse("not an object", out problem);
        if (element.EnumerateObject().Any(p => KeyProperties.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
            return Refuse("a key does not belong in a declaration — connect it separately", out problem);

        var id = Text(element, "id");
        if (id is null || !IdPattern().IsMatch(id)) return Refuse("id: lowercase letters, digits and '-', up to 32", out problem);
        if (Reserved.Contains(id)) return Refuse("id: reserved", out problem);

        var name = Text(element, "name") ?? id;
        if (name.Length > 64 || name.Any(char.IsControl)) return Refuse("name: too long or has control characters", out problem);

        if (!TryBase(Text(element, "baseUrl"), out var baseUrl)) return Refuse("baseUrl: an http or https address without user name, query or fragment", out problem);

        if (!ProviderApis.TryParse(Text(element, "api"), out var api)) return Refuse("api: openai-chat, openai-responses, anthropic-messages or google-gemini", out problem);

        if (!TryAuth(element, out var auth)) return Refuse("auth: style bearer, header (with a name), google or none", out problem);
        if (auth!.Style != AuthStyle.None && baseUrl!.Scheme == Uri.UriSchemeHttp && !IsLocalAddress(baseUrl.Host))
            return Refuse("baseUrl: a key does not travel over plain http away from this computer or a private network, use https", out problem);

        var models = new List<DeclaredModel>();
        if (element.TryGetProperty("models", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) return Refuse("models: not a list", out problem);
            foreach (var entry in list.EnumerateArray())
            {
                if (!TryModel(entry, out var model, out var modelProblem)) return Refuse(modelProblem!, out problem);
                if (models.Any(m => m.Id == model!.Id)) return Refuse("models: a model listed twice", out problem);
                models.Add(model!);
            }
        }

        // Some wires cannot carry the key in some places; say so here instead of letting a request quietly go out another way.
        if (KeyPlacementProblem(auth!, new[] { api }.Concat(models.Where(m => m.Api is not null).Select(m => m.Api!.Value))) is { } placement)
            return Refuse(placement, out problem);

        problem = null;
        declaration = new ProviderDeclaration(id, name, baseUrl!, api, auth!, models, origin);
        return true;
    }

    /// <summary>
    /// What is wrong with presenting the key as <paramref name="auth"/> on one of <paramref name="apis"/>, or <see langword="null"/>.
    /// Anthropic's SDK writes the key as a bearer and nothing else (an <c>x-api-key</c> or a bare <c>Authorization</c> cannot be sent);
    /// Gemini's key is <c>x-goog-api-key</c> (an <c>Authorization</c> header is refused). A gateway's own header works on both.
    /// </summary>
    private static string? KeyPlacementProblem(ProviderAuth auth, IEnumerable<ProviderApi> apis)
    {
        if (auth.Style != AuthStyle.Header) return null;
        var header = auth.HeaderName!.ToLowerInvariant();
        foreach (var api in apis)
        {
            if (api == ProviderApi.AnthropicMessages && header is "x-api-key" or "authorization")
                return "auth: Anthropic Messages takes the key as a bearer (style bearer), or in a header of the gateway's own";
            if (api == ProviderApi.GoogleGemini && header == "authorization")
                return "auth: Gemini takes the key as style google, or in a header of the gateway's own";
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="host"/> is this computer or a private network: loopback, a private or link-local address,
    /// or a name that cannot be reached from outside (no dot, <c>.local</c>, <c>.lan</c>, <c>.internal</c>).
    /// </summary>
    public static bool IsLocalAddress(string host)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var address))
        {
            if (IPAddress.IsLoopback(address)) return true;
            var bytes = address.GetAddressBytes();
            return address.AddressFamily switch
            {
                AddressFamily.InterNetwork => bytes[0] == 10
                    || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                    || (bytes[0] == 192 && bytes[1] == 168)
                    || (bytes[0] == 169 && bytes[1] == 254)
                    || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127),   // shared address space (carrier-grade NAT, overlay networks)
                AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC,   // fc00::/7
                _ => false,
            };
        }

        return !host.Contains('.')
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="address"/> is an address a provider can be reached at, normalized to end with a slash.</summary>
    public static bool TryBase(string? address, out Uri? baseUrl)
    {
        baseUrl = null;
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        // The base is appended to (…/chat/completions), so it ends with a slash.
        baseUrl = uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
        return true;
    }

    private static bool TryAuth(JsonElement element, out ProviderAuth? auth)
    {
        auth = ProviderAuth.Bearer;
        if (!element.TryGetProperty("auth", out var value)) return true;
        if (value.ValueKind != JsonValueKind.Object) return false;
        switch (Text(value, "style")?.ToLowerInvariant())
        {
            case null or "bearer":
                auth = ProviderAuth.Bearer;
                return true;
            case "google":
                auth = new ProviderAuth(AuthStyle.Google);
                return true;
            case "none":
                auth = ProviderAuth.None;
                return true;
            case "header":
                var header = Text(value, "name");
                if (header is null || !HeaderNamePattern().IsMatch(header) || ForbiddenHeaders.Contains(header.ToLowerInvariant())) return false;
                auth = new ProviderAuth(AuthStyle.Header, header);
                return true;
            default:
                return false;
        }
    }

    private static bool TryModel(JsonElement entry, out DeclaredModel? model, out string? problem)
    {
        model = null;
        if (entry.ValueKind != JsonValueKind.Object) return Refuse("models: an entry is not an object", out problem);
        var id = Text(entry, "id");
        if (id is null || id.Length > 200 || id.Any(char.IsControl)) return Refuse("models: an entry has no usable id", out problem);

        ProviderApi? api = null;
        if (Text(entry, "api") is { } named)
        {
            if (!ProviderApis.TryParse(named, out var own)) return Refuse("models: an api that is not one of the known formats", out problem);
            api = own;
        }

        string? path = null;
        if (Text(entry, "path") is { } relative)
        {
            if (!TryPath(relative, out path)) return Refuse("models: a path must be relative, without '..', query or fragment", out problem);
        }

        model = new DeclaredModel(id, Text(entry, "name"), api, path,
            ModelLimits.TryCreate(Count(entry, "contextWindow"), Count(entry, "maxTokens"), Flag(entry, "reasoning"), out var limits) ? limits! : ModelLimits.Unknown,
            Input(entry));
        problem = null;
        return true;
    }

    private static bool TryPath(string relative, out string? path)
    {
        path = null;
        var trimmed = relative.Trim();
        if (trimmed.Length is 0 or > 128 || trimmed.StartsWith('/') || trimmed.Contains("://") || trimmed.Contains('\\')
            || trimmed.Contains('?') || trimmed.Contains('#') || trimmed.Split('/').Contains(".."))
            return false;
        path = trimmed.EndsWith('/') ? trimmed : trimmed + "/";
        return true;
    }

    private static bool Refuse(string reason, out string? problem)
    {
        problem = reason;
        return false;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

    private static int? Count(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) ? count : null;

    private static bool? Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static List<string>? Input(JsonElement entry)
    {
        if (!entry.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array) return null;
        var kinds = input.EnumerateArray().Where(k => k.ValueKind == JsonValueKind.String)
            .Select(k => k.GetString()!.Trim().ToLowerInvariant()).Where(k => k is "text" or "image").Distinct().ToList();
        return kinds.Count > 0 ? kinds : null;
    }

    /// <summary>Writes the declaration as the JSON <see cref="TryParse(JsonElement, ProviderOrigin, out ProviderDeclaration?, out string?)"/> reads. The key is not in it.</summary>
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("id", Id);
        writer.WriteString("name", Name);
        writer.WriteString("baseUrl", BaseUrl.AbsoluteUri);
        writer.WriteString("api", ProviderApis.NameOf(Api));
        writer.WriteStartObject("auth");
        writer.WriteString("style", ProviderAuth.NameOf(Auth.Style));
        if (Auth.HeaderName is { } header) writer.WriteString("name", header);
        writer.WriteEndObject();
        writer.WriteStartArray("models");
        foreach (var model in Models)
        {
            writer.WriteStartObject();
            writer.WriteString("id", model.Id);
            if (model.Name is { } display) writer.WriteString("name", display);
            if (model.Api is { } own) writer.WriteString("api", ProviderApis.NameOf(own));
            if (model.Path is { } path) writer.WriteString("path", path);
            if (model.Limits.ContextWindow is { } window) writer.WriteNumber("contextWindow", window);
            if (model.Limits.MaxOutputTokens is { } maxTokens) writer.WriteNumber("maxTokens", maxTokens);
            if (model.Limits.Reasoning is { } reasoning) writer.WriteBoolean("reasoning", reasoning);
            if (model.Input is { } input)
            {
                writer.WriteStartArray("input");
                foreach (var kind in input) writer.WriteStringValue(kind);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// The providers the runtime ships with, each as the declaration the runtime's own client is built from and as the
    /// <see cref="LlmProvider"/> an application's relayed request is matched to (by host). One list, so a provider is
    /// added in one place.
    /// </summary>
    private static readonly (ProviderDeclaration Declaration, LlmProvider ForApps)[] Shipped =
    [
        Ship("openai", "OpenAI", "https://api.openai.com/v1/", ProviderApi.OpenAIResponses, ProviderAuth.Bearer, KeyStyle.Bearer, "v1/"),
        Ship("anthropic", "Anthropic", "https://api.anthropic.com/", ProviderApi.AnthropicMessages, ProviderAuth.Bearer, KeyStyle.ApiKeyHeader, "v1/"),
        Ship("google", "Google Gemini", "https://generativelanguage.googleapis.com/", ProviderApi.GoogleGemini, new ProviderAuth(AuthStyle.Google), KeyStyle.Google, "v1beta/openai/"),
        Ship("groq", "Groq", "https://api.groq.com/openai/v1/", ProviderApi.OpenAIChat, ProviderAuth.Bearer, KeyStyle.Bearer, "openai/v1/"),
        Ship("openrouter", "OpenRouter", "https://openrouter.ai/api/v1/", ProviderApi.OpenAIChat, ProviderAuth.Bearer, KeyStyle.Bearer, "api/v1/"),
        Ship("mistral", "Mistral", "https://api.mistral.ai/v1/", ProviderApi.OpenAIChat, ProviderAuth.Bearer, KeyStyle.Bearer, "v1/"),
    ];

    /// <summary>The providers the runtime ships with, in the same shape as any other. Their models are not listed: the person names the model.</summary>
    public static IReadOnlyList<ProviderDeclaration> BuiltIn { get; } = Shipped.Select(s => s.Declaration).ToList();

    /// <summary>The same providers as an application's relayed requests see them: how the key is carried and where OpenAI-shaped chat is answered.</summary>
    internal static IReadOnlyList<LlmProvider> BuiltInForApps { get; } = Shipped.Select(s => s.ForApps).ToList();

    /// <param name="appStyle">How an application's own request carries the key — not what the runtime's client sends (Anthropic's SDK presents it as a bearer).</param>
    /// <param name="appChatPath">Where, on the provider's host, it answers OpenAI-shaped chat requests.</param>
    private static (ProviderDeclaration, LlmProvider) Ship(string id, string name, string address, ProviderApi api, ProviderAuth auth, KeyStyle appStyle, string appChatPath)
    {
        var baseUrl = new Uri(address);
        return (new ProviderDeclaration(id, name, baseUrl, api, auth, [], ProviderOrigin.BuiltIn), new LlmProvider(id, baseUrl.Host, name, appStyle, appChatPath));
    }
}
