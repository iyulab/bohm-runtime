using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// An application registry: a folder (a share, a drive) or a static web location holding
/// <see cref="IndexFile"/> and the packages it lists under <c>apps/&lt;id&gt;/&lt;version&gt;.bohm</c>. There is no
/// server logic — whoever may write the folder publishes, everyone who may read it installs. The index
/// names each package's hash, so the chain of trust runs index → package → every file in it. A reader
/// ignores members it does not know.
/// </summary>
public static class AppRegistry
{
    /// <summary>Format identifier of a registry index.</summary>
    public const string Format = "bohm.registry/0";

    /// <summary>Version of the index's own schema.</summary>
    public const string ManifestVersion = "0";

    /// <summary>Name of the index at the registry's root.</summary>
    public const string IndexFile = "index.json";

    /// <summary>The channel a version is on when it names none.</summary>
    public const string DefaultChannel = "default";

    /// <summary>How long reading a registry may take before it counts as not reachable now — a share that is away can hold a read for half a minute.</summary>
    public static readonly TimeSpan ReachLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads the index of the registry at <paramref name="root"/>, a folder path. A registry that cannot be
    /// reached within <paramref name="reach"/> is <see cref="RegistryState.Unreachable"/> — being offline is
    /// a state, not an error. An index past its <see cref="RegistryIndex.ValidUntil"/> is refused as
    /// <see cref="RegistryState.Expired"/>: an old index handed out again must not bring back old versions.
    /// </summary>
    public static async Task<RegistryRead> ReadAsync(string root, TimeProvider clock, TimeSpan reach, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(clock);
        byte[]? bytes;
        try
        {
            bytes = await Task.Run(() => ReadIndexBytes(root), cancellationToken).WaitAsync(reach, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new RegistryRead(RegistryState.Unreachable, null);
        }

        if (bytes is null) return new RegistryRead(RegistryState.UnknownFormat, null);
        RegistryIndex? index;
        try
        {
            index = JsonSerializer.Deserialize(bytes, RegistryJson.Default.RegistryIndex);
        }
        catch (JsonException)
        {
            return new RegistryRead(RegistryState.UnknownFormat, null);
        }

        if (index?.Format != Format || index.Apps is null) return new RegistryRead(RegistryState.UnknownFormat, null);
        if (index.ValidUntil <= clock.GetUtcNow()) return new RegistryRead(RegistryState.Expired, null);
        return new RegistryRead(RegistryState.Ok, index);
    }

    /// <summary>The index's bytes, or <see langword="null"/> for a folder that is there but holds no index; throws when the folder is not there.</summary>
    private static byte[]? ReadIndexBytes(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The registry folder is not there.");
        var path = Path.Combine(root, IndexFile);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// The version of <paramref name="app"/> to install: <paramref name="version"/> when given, otherwise the
    /// highest on <see cref="DefaultChannel"/>. Versions are published counts, compared as numbers.
    /// </summary>
    public static RegistryVersion? Choose(RegistryApp app, string? version)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (version is not null) return app.Versions.FirstOrDefault(v => v.Version == version);
        return app.Versions.Where(v => v.Channels is null || v.Channels.Count == 0 || v.Channels.Contains(DefaultChannel))
            .OrderByDescending(v => long.TryParse(v.Version, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .FirstOrDefault();
    }

    /// <summary>The channel <paramref name="version"/> is installed from: <see cref="DefaultChannel"/> when it is on it (or names none), else the first it names.</summary>
    public static string ChannelOf(RegistryVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Channels is null || version.Channels.Count == 0 || version.Channels.Contains(DefaultChannel) ? DefaultChannel : version.Channels[0];
    }
}

/// <summary>What reading a registry found.</summary>
public enum RegistryState
{
    /// <summary>The index was read and is current.</summary>
    Ok,

    /// <summary>The registry could not be reached now — offline, away, or not allowed.</summary>
    Unreachable,

    /// <summary>The index is past its <see cref="RegistryIndex.ValidUntil"/>.</summary>
    Expired,

    /// <summary>There is no index, or it is not in <see cref="AppRegistry.Format"/>.</summary>
    UnknownFormat,
}

/// <summary>The outcome of reading a registry: its state, and the index when it is <see cref="RegistryState.Ok"/>.</summary>
public sealed record RegistryRead(RegistryState State, RegistryIndex? Index);

/// <summary>A registry's index — one file, its signature (when there is one) inside it, so the index and its signature cannot drift apart while it is rewritten.</summary>
/// <param name="ManifestVersion">See <see cref="AppRegistry.ManifestVersion"/>.</param>
/// <param name="Format">See <see cref="AppRegistry.Format"/>.</param>
/// <param name="Name">The registry's name, as people see it.</param>
/// <param name="Date">When the index was written.</param>
/// <param name="ValidUntil">After this the index is refused (see <see cref="AppRegistry.ReadAsync"/>).</param>
/// <param name="Channels">The channels versions may be on, by id, with their names.</param>
/// <param name="Apps">The applications it holds.</param>
/// <param name="Signature">A signature over the rest, when the registry signs it — kept, not checked here.</param>
public sealed record RegistryIndex(string ManifestVersion, string Format, string Name, DateTimeOffset Date, DateTimeOffset ValidUntil,
    IReadOnlyDictionary<string, RegistryChannel>? Channels, IReadOnlyList<RegistryApp> Apps, JsonElement? Signature = null);

/// <summary>A channel of a registry.</summary>
/// <param name="Name">Its name, as people see it.</param>
public sealed record RegistryChannel(string Name);

/// <summary>An application a registry holds.</summary>
/// <param name="Id">The application's id, as in its packages.</param>
/// <param name="Name">The name a person sees.</param>
/// <param name="Description">What it is for, when the publisher said.</param>
/// <param name="Permissions">What it asks to do — the latest package manifest's, so it is seen before anything is fetched.</param>
/// <param name="Versions">Its published versions.</param>
public sealed record RegistryApp(string Id, string Name, string? Description, PackagePermissions Permissions, IReadOnlyList<RegistryVersion> Versions);

/// <summary>A published version of an application in a registry.</summary>
/// <param name="Version">The published version (see <see cref="PackageManifest.Version"/>).</param>
/// <param name="Src">The package's path under the registry's root, <c>/</c>-separated.</param>
/// <param name="Sha256">The package file's hash, <c>sha256:</c> and lowercase hexadecimal — as a manifest writes a file's.</param>
/// <param name="Contract">The contract the application runs under.</param>
/// <param name="Channels">The channels it is on; none means <see cref="AppRegistry.DefaultChannel"/>.</param>
public sealed record RegistryVersion(string Version, string Src, string Sha256, int Contract, IReadOnlyList<string>? Channels);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RegistryIndex))]
internal sealed partial class RegistryJson : JsonSerializerContext;
