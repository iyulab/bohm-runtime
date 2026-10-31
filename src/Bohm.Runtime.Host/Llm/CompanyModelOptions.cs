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
