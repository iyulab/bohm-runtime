namespace Bohm.Runtime.Credentials;

/// <summary>
/// Where the runtime keeps secrets such as AI provider keys. Secrets live here and nowhere else —
/// never in an application's folder — so handing someone an application's folder never hands
/// them a key. Applications only ever see a placeholder.
/// </summary>
public interface ICredentialVault
{
    /// <summary>The secret stored under <paramref name="name"/>, or <see langword="null"/>.</summary>
    string? Read(string name);

    /// <summary>Stores <paramref name="secret"/> under <paramref name="name"/>, replacing any previous one.</summary>
    void Write(string name, string secret);

    /// <summary>Removes the secret stored under <paramref name="name"/>, if any.</summary>
    void Delete(string name);
}

/// <summary>A vault held in memory only — for tests and for deployments that inject secrets at start.</summary>
public sealed class MemoryCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public string? Read(string name)
    {
        lock (_lock) return _secrets.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public void Write(string name, string secret)
    {
        lock (_lock) _secrets[name] = secret;
    }

    /// <inheritdoc />
    public void Delete(string name)
    {
        lock (_lock) _secrets.Remove(name);
    }
}
