using System.Security.Cryptography;
using System.Text.Json;

namespace Bohm.Runtime.Adoption;

public sealed partial class AdoptionCatalog
{
    private const string FetchingPrefix = ".fetching-";

    /// <summary>Reads the registry at <paramref name="root"/> by this catalog's clock (see <see cref="AppRegistry.ReadAsync"/>).</summary>
    public Task<RegistryRead> ReadRegistryAsync(string root, TimeSpan reach, CancellationToken cancellationToken = default) =>
        AppRegistry.ReadAsync(root, _clock, reach, cancellationToken);

    /// <summary>
    /// Fetches application <paramref name="id"/> from the registry at <paramref name="root"/> into a local copy
    /// beside the catalog, checked against the hash the index lists before anything reads it as a package —
    /// the link between the index and the package's own manifest. The copy is the caller's to take in
    /// (<see cref="ImportPackageAsync"/>, or as a new revision) and to delete.
    /// </summary>
    /// <param name="version">The version to fetch; <see langword="null"/> for the highest on the default channel (<see cref="AppRegistry.Choose"/>).</param>
    /// <exception cref="RegistryRefusedException">The registry is not reachable now, its index is expired or unknown, or it does not list that application or version properly.</exception>
    /// <exception cref="InvalidPackageException">The package is not the one the index lists (<see cref="PackageProblem.Damaged"/>).</exception>
    public async Task<FetchedPackage> FetchFromRegistryAsync(string root, string id, string? version, TimeSpan reach, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(id);
        var read = await AppRegistry.ReadAsync(root, _clock, reach, cancellationToken).ConfigureAwait(false);
        if (read.Index is not { } index) throw new RegistryRefusedException(read.State switch
        {
            RegistryState.Unreachable => RegistryRefusedException.Unreachable,
            RegistryState.Expired => RegistryRefusedException.Expired,
            _ => RegistryRefusedException.UnknownFormat,
        });
        if (index.Apps.FirstOrDefault(a => a.Id == id) is not { } app || AppRegistry.Choose(app, version) is not { } chosen)
            throw new RegistryRefusedException(RegistryRefusedException.NotListed);

        // The index is the registry's word, not a path to trust: a listed package must sit under the root.
        var rootPath = Path.GetFullPath(root);
        var source = Path.GetFullPath(Path.Combine(rootPath, chosen.Src));
        if (!IsPlainRelativePath(chosen.Src) || !chosen.Src.EndsWith(AppPackage.Extension, StringComparison.OrdinalIgnoreCase)
            || !source.StartsWith(rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new RegistryRefusedException(RegistryRefusedException.BadEntry);

        var copy = Path.Combine(_root, FetchingPrefix + Guid.NewGuid().ToString("n")[..8] + AppPackage.Extension);
        try
        {
            string hash;
            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    sha.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }

                hash = "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new RegistryRefusedException(RegistryRefusedException.NotListed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new RegistryRefusedException(RegistryRefusedException.Unreachable);
            }

            if (!string.Equals(hash, chosen.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidPackageException(PackageProblem.Damaged, "The package is not the one the registry lists.");
            return new FetchedPackage(copy, app, chosen, new AppInstall(rootPath, AppRegistry.ChannelOf(chosen), chosen.Version));
        }
        catch
        {
            if (File.Exists(copy)) File.Delete(copy);
            throw;
        }
    }

    /// <summary>
    /// Publishes application <paramref name="id"/> to the folder registry at <paramref name="root"/>: its code (no
    /// data — a registry hands out applications, not anyone's entries) as <c>apps/&lt;id&gt;/&lt;version&gt;.bohm</c>,
    /// then the index rewritten to list it. The index is rewritten under a lock file only one publisher can
    /// create, through a temporary file, and read back — so two publishers at once both end up listed. A folder
    /// with no index becomes a registry named <paramref name="name"/>. The same code published again is the
    /// same version and changes nothing but the index's dates.
    /// </summary>
    /// <param name="aiServices">As for <see cref="PackAsync"/>.</param>
    /// <param name="validFor">How long the rewritten index stays valid (<see cref="RegistryIndex.ValidUntil"/>).</param>
    /// <returns>The version as listed, or <see langword="null"/> for an unknown id.</returns>
    /// <exception cref="RegistryRefusedException">
    /// The folder is not there (<see cref="RegistryRefusedException.Unreachable"/>), its index is not one this
    /// writes (<see cref="RegistryRefusedException.UnknownFormat"/> — it is left as it is), the version is already
    /// there with other code (<see cref="RegistryRefusedException.VersionTaken"/>), or the lock stayed held.
    /// </exception>
    public async Task<RegistryPublished?> PublishToRegistryAsync(string root, string id, string name, IReadOnlyDictionary<string, string> aiServices,
        TimeSpan validFor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        var rootPath = Path.GetFullPath(root);
        if (!Directory.Exists(rootPath)) throw new RegistryRefusedException(RegistryRefusedException.Unreachable);

        var folder = Path.Combine(rootPath, "apps", id);
        Directory.CreateDirectory(folder);
        var packing = Path.Combine(folder, PublishingPrefix + Guid.NewGuid().ToString("n")[..8] + AppPackage.Extension);
        PackedApp packed;
        try
        {
            packed = (await PackAsync(id, packing, PackageData.None, aiServices, cancellationToken: cancellationToken).ConfigureAwait(false))!;
            var src = $"apps/{id}/{packed.Manifest.Version}{AppPackage.Extension}";
            var target = Path.Combine(rootPath, src);
            if (File.Exists(target))
            {
                // Published before: the same code is the same version; other code under that version is someone else's.
                if (ReadManifest(target)?.Provenance.ContentSha256 != packed.Manifest.Provenance.ContentSha256)
                    throw new RegistryRefusedException(RegistryRefusedException.VersionTaken);
            }
            else
            {
                File.Move(packing, target);
            }

            var version = new RegistryVersion(packed.Manifest.Version, src, "sha256:" + Convert.ToHexStringLower(await HashFileAsync(target, cancellationToken).ConfigureAwait(false)),
                packed.Manifest.Contract, null);
            var entry = new RegistryApp(id, packed.Manifest.Name, null, packed.Manifest.Permissions, [version]);
            await UpdateIndexAsync(rootPath, name, entry, validFor, cancellationToken).ConfigureAwait(false);
            return new RegistryPublished(id, version);
        }
        finally
        {
            if (File.Exists(packing)) File.Delete(packing);
        }
    }

    private const string PublishingPrefix = ".publishing-";
    private const string IndexLock = "index.lock";

    /// <summary>A lock older than this was left by a publisher that stopped; it is taken over.</summary>
    private static readonly TimeSpan StaleLock = TimeSpan.FromMinutes(2);

    /// <summary>Lists <paramref name="entry"/>'s version in the index — the application's other versions kept — under the lock, and checks it is there after.</summary>
    private async Task UpdateIndexAsync(string root, string name, RegistryApp entry, TimeSpan validFor, CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(root, IndexLock);
        await using var held = await TakeLockAsync(lockPath, cancellationToken).ConfigureAwait(false);
        var indexPath = Path.Combine(root, AppRegistry.IndexFile);
        RegistryIndex? current = null;
        if (File.Exists(indexPath))
        {
            try
            {
                current = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(indexPath, cancellationToken).ConfigureAwait(false), RegistryJson.Default.RegistryIndex);
            }
            catch (JsonException)
            {
                current = null;
            }

            // An index this does not write is not overwritten.
            if (current?.Format != AppRegistry.Format || current.Apps is null) throw new RegistryRefusedException(RegistryRefusedException.UnknownFormat);
        }

        var now = _clock.GetUtcNow();
        var apps = (current?.Apps ?? []).Where(a => a.Id != entry.Id).ToList();
        var before = current?.Apps.FirstOrDefault(a => a.Id == entry.Id);
        var version = entry.Versions[0];
        var versions = (before?.Versions ?? []).Where(v => v.Version != version.Version).Append(version).ToList();
        apps.Add(entry with { Description = before?.Description, Versions = versions });
        var index = new RegistryIndex(AppRegistry.ManifestVersion, AppRegistry.Format, current?.Name ?? name, now, now + validFor, current?.Channels,
            apps.OrderBy(a => a.Id, StringComparer.Ordinal).ToList());

        var temporary = Path.Combine(root, ".index-" + Guid.NewGuid().ToString("n")[..8] + ".json");
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(index, RegistryJson.Default.RegistryIndex), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, indexPath, overwrite: true);

        // Read back: what readers will see must list it.
        var written = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(indexPath, cancellationToken).ConfigureAwait(false), RegistryJson.Default.RegistryIndex);
        if (written?.Apps.FirstOrDefault(a => a.Id == entry.Id)?.Versions.Any(v => v.Version == version.Version && v.Sha256 == version.Sha256) != true)
            throw new IOException("The registry index did not keep what was written.");
    }

    /// <summary>Creates the lock file — only one creator wins, on a local drive or a share — waiting for a holder, and taking over a stale one.</summary>
    private async Task<IAsyncDisposable> TakeLockAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                return stream;
            }
            catch (IOException)
            {
                if (attempt >= 200) throw new RegistryRefusedException(RegistryRefusedException.Busy);
                if (!File.Exists(path)) continue;   // released between the two looks
                if (_clock.GetUtcNow() - File.GetLastWriteTimeUtc(path) > StaleLock)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                        // Still held open: not stale after all.
                    }

                    continue;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static PackageManifest? ReadManifest(string package)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(package);
            using var stream = zip.GetEntry(AppPackage.ManifestFile)?.Open();
            return stream is null ? null : JsonSerializer.Deserialize(stream, PackageJson.Default.PackageManifest);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or IOException)
        {
            return null;
        }
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records that application <paramref name="id"/> was installed from a registry (<see cref="AdoptedApp.InstalledFrom"/>) and returns the record.</summary>
    /// <exception cref="KeyNotFoundException">There is no such application.</exception>
    public async Task<AdoptedApp> SetInstalledFromAsync(string id, AppInstall install, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        var app = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No adopted application '{id}'.");
        var installed = app with { InstalledFrom = install };
        await LocalOrigin.Storage.DurableFile.WriteAtomicallyAsync(Path.Combine(AppDirectory(id), RecordFile), WriteRecord(installed), cancellationToken).ConfigureAwait(false);
        return installed;
    }
}

/// <summary>A package fetched from a registry and checked against its index: the local copy, and what the index says about it.</summary>
/// <param name="File">The local copy — the caller takes it in and deletes it.</param>
/// <param name="App">The application as the index lists it.</param>
/// <param name="Version">The version fetched.</param>
/// <param name="Install">What to record on the application once it is taken in.</param>
public sealed record FetchedPackage(string File, RegistryApp App, RegistryVersion Version, AppInstall Install);

/// <summary>An application published to a registry: its id and the version as listed.</summary>
public sealed record RegistryPublished(string Id, RegistryVersion Version);

/// <summary>Why an application could not be fetched from, or published to, a registry.</summary>
public sealed class RegistryRefusedException(string reason) : Exception($"The registry refused: {reason}.")
{
    /// <summary>The registry could not be reached now.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The index is past its <c>validUntil</c>.</summary>
    public const string Expired = "expired";

    /// <summary>There is no index in a known format.</summary>
    public const string UnknownFormat = "unknown-format";

    /// <summary>The index does not list that application or version, or its package is not where the index says.</summary>
    public const string NotListed = "not-listed";

    /// <summary>The index lists the package at a path outside the registry, or not as a package.</summary>
    public const string BadEntry = "bad-entry";

    /// <summary>The version is already in the registry with other code — nothing is overwritten.</summary>
    public const string VersionTaken = "version-taken";

    /// <summary>Another publisher held the registry's lock for the whole wait.</summary>
    public const string Busy = "busy";

    /// <summary>One of the reasons above.</summary>
    public string Reason { get; } = reason;
}
