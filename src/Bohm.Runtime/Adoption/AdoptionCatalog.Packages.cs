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

    [GeneratedRegex(@"^(?<part>bohm\.[a-z][a-z-]*)/(?<version>\d{1,6})$")]
    private static partial Regex FormatName();
}
