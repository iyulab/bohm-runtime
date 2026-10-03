using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bohm.Runtime.Sources;
using LocalOrigin.Storage;

namespace Bohm.Runtime.Adoption;

public sealed partial class AdoptionCatalog
{
    /// <summary>Format identifier of <c>published.json</c>, the record of the last package made of an application.</summary>
    public const string PublishedFormat = "bohm.published/0";

    private const string PublishedFile = "published.json";
    private const string PackingPrefix = ".packing-";
    private const string UnpackingPrefix = ".unpacking-";

    /// <summary>
    /// Writes application <paramref name="id"/> into a package at <paramref name="target"/> — a new
    /// file the caller names (see <see cref="AppPackage"/>). The package holds the folder an export
    /// with the same <paramref name="data"/> makes, with one change: where the person's files were is
    /// theirs, so each recorded original path keeps only its file name. The manifest is written from
    /// the folder: its parts, what of the data is inside, the hash of every file. The published
    /// version goes up by one when the current code differs from the last package's.
    /// </summary>
    /// <param name="aiServices">
    /// Each AI service an application may call, by id, with the text that names it in a page (its host,
    /// or the path the runtime relays it under). A service named in the current page is listed in
    /// <see cref="PackagePermissions.Ai"/>.
    /// </param>
    /// <returns>The package, or <see langword="null"/> for an unknown id.</returns>
    /// <exception cref="IOException"><paramref name="target"/> already exists, or its folder does not.</exception>
    /// <remarks>The package is written under a temporary name beside the target and renamed at the end.</remarks>
    public async Task<PackedApp?> PackAsync(string id, string target, PackageData data, IReadOnlyDictionary<string, string> aiServices,
        KeyValueStore? openStorage = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(aiServices);
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } app) return null;
        target = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent)) throw new IOException("The folder to write the package into does not exist.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Something with that name is already there.");

        var html = await ReadHtmlAsync(id, cancellationToken).ConfigureAwait(false);
        var contentSha256 = Convert.ToHexStringLower(SHA256.HashData(html));
        var version = await PublishAsync(id, contentSha256, cancellationToken).ConfigureAwait(false);

        var folder = Path.Combine(_root, PackingPrefix + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            await ExportAsync(id, folder, openStorage, data == PackageData.All, cancellationToken).ConfigureAwait(false);
            await KeepFileNamesOnlyAsync(folder, cancellationToken).ConfigureAwait(false);
            var files = PackageFiles(folder);
            var manifest = new PackageManifest(
                AppPackage.ManifestVersion, AppPackage.Format, app.Id, version.ToString(CultureInfo.InvariantCulture), DisplayName(app), AppPackage.PlainContract,
                await ContentsAsync(files, cancellationToken).ConfigureAwait(false), AppPackage.DataName(data), Includes(folder),
                new PackagePermissions(true, AiCalled(html, aiServices), await SitesReadAsync(folder, cancellationToken).ConfigureAwait(false), [], []),
                new PackageProvenance(contentSha256, await HashesAsync(files, cancellationToken).ConfigureAwait(false)));

            var partial = target + ".partial-" + Guid.NewGuid().ToString("n")[..8];
            try
            {
                await WriteZipAsync(partial, manifest, files, cancellationToken).ConfigureAwait(false);
                File.Move(partial, target);
            }
            catch
            {
                if (File.Exists(partial)) File.Delete(partial);
                throw;
            }

            return new PackedApp(app, manifest, target);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The published version for code with hash <paramref name="contentSha256"/>: the last one again for the same code, one more otherwise.</summary>
    private async Task<int> PublishAsync(string id, string contentSha256, CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppDirectory(id), PublishedFile);
        var last = 0;
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false));
                var root = document.RootElement;
                if (root.GetProperty("format").GetString() == PublishedFormat)
                {
                    last = root.GetProperty("version").GetInt32();
                    if (root.GetProperty("contentSha256").GetString() == contentSha256) return last;
                }
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // An unreadable record counts as none: the next version is 1 again, never a guess.
                last = 0;
            }
        }

        var next = last + 1;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", PublishedFormat);
            writer.WriteNumber("version", next);
            writer.WriteString("contentSha256", contentSha256);
            writer.WriteEndObject();
        }

        await DurableFile.WriteAtomicallyAsync(path, stream.ToArray(), cancellationToken).ConfigureAwait(false);
        return next;
    }

    /// <summary>Shortens every recorded original path in an exported folder to its file name.</summary>
    private static async Task KeepFileNamesOnlyAsync(string folder, CancellationToken cancellationToken)
    {
        var records = new List<string> { Path.Combine(folder, RecordFile) };
        var revisions = Path.Combine(folder, RevisionsDirectory);
        if (Directory.Exists(revisions)) records.AddRange(Directory.EnumerateFiles(revisions, RevisionFile, SearchOption.AllDirectories));
        foreach (var record in records)
        {
            if (!File.Exists(record)) continue;
            var node = JsonNode.Parse(await File.ReadAllBytesAsync(record, cancellationToken).ConfigureAwait(false));
            if (node?["source"]?["originalPath"] is not JsonValue value || !value.TryGetValue(out string? path) || path is null) continue;
            var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
            if (name == path) continue;
            node["source"]!["originalPath"] = name;
            await File.WriteAllBytesAsync(record, JsonSerializer.SerializeToUtf8Bytes(node), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Every file in the folder, by its path in the package (<c>/</c> separators), in ordinal order.</summary>
    private static SortedDictionary<string, string> PackageFiles(string folder)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(folder, file).Replace('\\', '/')] = file;
        return files;
    }

    private static async Task<IReadOnlyDictionary<string, string>> HashesAsync(SortedDictionary<string, string> files, CancellationToken cancellationToken)
    {
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, file) in files)
        {
            await using var stream = File.OpenRead(file);
            hashes[name] = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        return hashes;
    }

    /// <summary>Each part's format, from the <c>format</c> member of the JSON files present — the highest version when a part appears more than once.</summary>
    private static async Task<IReadOnlyDictionary<string, int>> ContentsAsync(SortedDictionary<string, string> files, CancellationToken cancellationToken)
    {
        var contents = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, file) in files)
        {
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                await using var stream = File.OpenRead(file);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("format", out var format)
                    || format.ValueKind != JsonValueKind.String || FormatName().Match(format.GetString()!) is not { Success: true } match) continue;
                var part = match.Groups["part"].Value;
                var version = int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture);
                contents[part] = contents.TryGetValue(part, out var seen) ? Math.Max(seen, version) : version;
            }
            catch (JsonException)
            {
                // Not a document of ours.
            }
        }

        return contents;
    }

    /// <summary>What of the application's data the exported folder holds, by the manifest's names.</summary>
    private static List<string> Includes(string folder)
    {
        static bool HasFiles(string path) => Directory.Exists(path) && Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
        var includes = new List<string>();
        if (HasFiles(Path.Combine(folder, StorageDirectory))) includes.Add("storage");
        if (File.Exists(Path.Combine(folder, UsageFile))) includes.Add("usage");
        var revisions = Path.Combine(folder, RevisionsDirectory);
        if (Directory.Exists(revisions) && Directory.EnumerateFiles(revisions, "*", SearchOption.AllDirectories)
                .Any(f => Path.GetFileName(f) is DataBeforeFile or DataUndoneFile)) includes.Add("revision-data");
        if (HasFiles(Path.Combine(folder, AppSources.ReadingsDirectory))) includes.Add("read-rows");
        if (HasFiles(Path.Combine(folder, ImportsDirectory))) includes.Add("import-data");
        return includes;
    }

    private static List<string> AiCalled(byte[] html, IReadOnlyDictionary<string, string> aiServices)
    {
        var page = System.Text.Encoding.UTF8.GetString(html);
        return aiServices.Where(s => page.Contains(s.Value, StringComparison.OrdinalIgnoreCase)).Select(s => s.Key).Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<IReadOnlyList<string>> SitesReadAsync(string folder, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(folder, AppSources.RulesFile))) return [];
        using var sources = AppSources.Open(folder);
        return (await sources.ListAsync(cancellationToken).ConfigureAwait(false)).Select(s => s.Rule.Site).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string DisplayName(AdoptedApp app)
    {
        if (!string.IsNullOrWhiteSpace(app.Title)) return app.Title;
        var path = app.Source.OriginalPath;
        if (string.IsNullOrEmpty(path)) return app.Id;
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        return Path.GetFileNameWithoutExtension(name) is { Length: > 0 } stem ? stem : app.Id;
    }

    private static async Task WriteZipAsync(string path, PackageManifest manifest, SortedDictionary<string, string> files, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(AppPackage.ManifestFile, CompressionLevel.Optimal);
            await using (var manifestStream = entry.Open())
                await JsonSerializer.SerializeAsync(manifestStream, manifest, PackageJson.Default.PackageManifest, cancellationToken).ConfigureAwait(false);
            foreach (var (name, file) in files)
                await zip.CreateEntryFromFileAsync(file, name, CompressionLevel.Optimal, cancellationToken).ConfigureAwait(false);
        }

        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Takes in the application in package <paramref name="file"/> (see <see cref="PackAsync"/>) — from
    /// this computer or another — as <see cref="ImportAsync"/> takes in a folder: same application, same
    /// identity, with whatever data the package carries. The package is checked before anything lands:
    /// its manifest must be of a known format, every entry a plain relative path listed in the manifest
    /// with the hash it has there, and every listed file present. The file is only read.
    /// </summary>
    /// <exception cref="InvalidPackageException">The file is not a package, its format is unknown, or it did not arrive whole.</exception>
    /// <exception cref="AppAlreadyHereException">This application is already here — nothing is replaced.</exception>
    public async Task<AdoptedApp> ImportPackageAsync(string file, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        file = Path.GetFullPath(file);
        if (!File.Exists(file)) throw new InvalidPackageException(PackageProblem.NotAPackage, "There is no such file.");

        var staging = Path.Combine(_root, UnpackingPrefix + Guid.NewGuid().ToString("n")[..8]);
        try
        {
            var manifest = await UnpackAsync(file, staging, cancellationToken).ConfigureAwait(false);
            AdoptedApp? record;
            try
            {
                record = ReadRecord(await DurableFile.ReadAsync(Path.Combine(staging, RecordFile), cancellationToken).ConfigureAwait(false));
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                throw new InvalidPackageException(PackageProblem.Damaged, "The application record cannot be read.", e);
            }

            if (record is null || record.Id != manifest.Id || !File.Exists(Path.Combine(staging, HtmlFile)))
                throw new InvalidPackageException(PackageProblem.Damaged, "The package does not hold the application its manifest names.");
            if (Directory.Exists(AppDirectory(record.Id))) throw await AlreadyHereAsync(record.Id, manifest, cancellationToken).ConfigureAwait(false);

            try
            {
                return await ImportAsync(staging, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException e)
            {
                throw new InvalidPackageException(PackageProblem.Damaged, e.Message, e);
            }
            catch (InvalidOperationException)
            {
                // Taken in by another request since the check above.
                throw await AlreadyHereAsync(record.Id, manifest, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private async Task<AppAlreadyHereException> AlreadyHereAsync(string id, PackageManifest manifest, CancellationToken cancellationToken)
    {
        var revisions = await ListRevisionsAsync(id, cancellationToken).ConfigureAwait(false);
        var code = manifest.Provenance.ContentSha256;
        return new AppAlreadyHereException(id, revisions.Any(r => r.Source.Sha256 == code), revisions.Any(r => r.InUse && r.Source.Sha256 == code));
    }

    /// <summary>Checks package <paramref name="file"/> and writes its files into <paramref name="folder"/>, hashing each as it is written.</summary>
    private static async Task<PackageManifest> UnpackAsync(string file, string folder, CancellationToken cancellationToken)
    {
        ZipArchive zip;
        try
        {
            zip = ZipFile.OpenRead(file);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidPackageException(PackageProblem.NotAPackage, "The file is not a zip archive.", e);
        }

        using (zip)
        {
            if (zip.GetEntry(AppPackage.ManifestFile) is not { } manifestEntry)
                throw new InvalidPackageException(PackageProblem.NotAPackage, "The file has no manifest.");
            PackageManifest? manifest;
            try
            {
                await using var stream = manifestEntry.Open();
                manifest = await JsonSerializer.DeserializeAsync(stream, PackageJson.Default.PackageManifest, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is JsonException or InvalidDataException)
            {
                throw new InvalidPackageException(PackageProblem.NotAPackage, "The manifest cannot be read.", e);
            }

            if (manifest?.Format != AppPackage.Format || manifest.Provenance?.Files is null || string.IsNullOrEmpty(manifest.Id))
                throw new InvalidPackageException(PackageProblem.UnknownFormat, $"The package is not in the format '{AppPackage.Format}'.");

            var expected = manifest.Provenance.Files;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(folder);
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName == AppPackage.ManifestFile || entry.FullName.EndsWith('/')) continue;
                if (!IsPlainRelativePath(entry.FullName) || !seen.Add(entry.FullName))
                    throw new InvalidPackageException(PackageProblem.Damaged, $"The entry '{entry.FullName}' is not a plain relative path, or appears twice.");
                if (!expected.TryGetValue(entry.FullName, out var hash))
                    throw new InvalidPackageException(PackageProblem.Damaged, $"The entry '{entry.FullName}' is not listed in the manifest.");
                var target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidPackageException(PackageProblem.Damaged, $"The entry '{entry.FullName}' points outside the package.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!string.Equals(await WriteHashedAsync(entry, target, cancellationToken).ConfigureAwait(false), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidPackageException(PackageProblem.Damaged, $"The entry '{entry.FullName}' is not what the manifest says it is.");
            }

            if (expected.Keys.FirstOrDefault(k => !seen.Contains(k)) is { } missing)
                throw new InvalidPackageException(PackageProblem.Damaged, $"The listed file '{missing}' is missing.");
            return manifest;
        }
    }

    /// <summary>Writes the entry to <paramref name="target"/> and returns its hash as the manifest writes it.</summary>
    private static async Task<string> WriteHashedAsync(ZipArchiveEntry entry, string target, CancellationToken cancellationToken)
    {
        await using var input = entry.Open();
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>A path inside the package: <c>/</c>-separated names, none empty, <c>.</c> or <c>..</c>, with no drive, root or backslash.</summary>
    private static bool IsPlainRelativePath(string path) =>
        path.Length > 0 && !path.Contains('\\') && !path.Contains(':') && !path.StartsWith('/')
        && path.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    [GeneratedRegex(@"^(?<part>bohm\.[a-z][a-z-]*)/(?<version>\d{1,6})$")]
    private static partial Regex FormatName();
}
