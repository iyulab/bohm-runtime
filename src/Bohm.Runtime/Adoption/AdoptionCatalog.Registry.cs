using System.Security.Cryptography;

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

/// <summary>Why an application could not be fetched from a registry.</summary>
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

    /// <summary>One of the reasons above.</summary>
    public string Reason { get; } = reason;
}
