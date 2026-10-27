using System.Runtime.Versioning;
using Bohm.Runtime.Credentials;
using TauriKit.Credentials;

namespace Bohm.Runtime.Host.Credentials;

/// <summary>
/// The macOS login keychain, scoped to the signed-in user, through TauriKit.Credentials: one generic password per
/// secret — service <paramref name="service"/>, account the secret's name — the items a Tauri shell built on
/// tauri-kit-credentials reads and writes too. A verification run passes its own <paramref name="service"/> so it
/// never touches the person's keys.
/// </summary>
/// <remarks>
/// The keychain answers the program that made an item without asking; a differently signed program is asked about,
/// so the installed app keeps its keys across updates only while it is signed as the same developer.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class KeychainCredentialVault(string service = "Bohm") : ICredentialVault
{
    private readonly TauriKit.Credentials.Credentials _entries = new(service, BuildKind.Release);

    /// <inheritdoc />
    public string? Read(string name) => _entries.Get(name);

    /// <inheritdoc />
    public void Write(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _entries.Set(name, secret);
    }

    /// <inheritdoc />
    public void Delete(string name) => _entries.Delete(name);
}
