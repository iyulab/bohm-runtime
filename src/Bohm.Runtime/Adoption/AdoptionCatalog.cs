using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bohm.Runtime.Assets;
using Bohm.Runtime.Storage;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// The adopted applications under one data root. Each lives in its own folder, which is
/// everything needed to move it elsewhere:
/// <c>app.json</c> (the record), <c>app.html</c> (the original bytes, never modified),
/// <c>storage/</c> (its data, see <see cref="AppStorage"/>) and <c>usage.ndjson</c> (its local
/// usage record, see <see cref="UsageLog"/>).
/// </summary>
/// <remarks>
/// The catalog does not decide what to do when the same file — or another version of it — is
/// adopted again. It reports earlier adoptions through <see cref="FindEarlierAdoptionsAsync"/>, saying
/// how each one matches, and whoever talks to the person asks them what to do.
/// </remarks>
public sealed class AdoptionCatalog
{
    /// <summary>Format identifier written into every <c>app.json</c>. Changes when the record's shape does.</summary>
    public const string RecordFormat = "bohm.adopted/0";

    private const string AdoptedDirectory = "adopted";
    private const string RecordFile = "app.json";
    private const string HtmlFile = "app.html";
    private const string StorageDirectory = "storage";
    private const string UsageFile = "usage.ndjson";
    private const string StagingPrefix = ".staging-";

    private static readonly JsonWriterOptions RecordWriter = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _root;
    private readonly TimeProvider _clock;

    /// <summary>Creates a catalog over <paramref name="dataRoot"/>. The host chooses the root; there is no default.</summary>
    public AdoptionCatalog(string dataRoot, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        _root = Path.Combine(Path.GetFullPath(dataRoot), AdoptedDirectory);
        _clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// Adopts <paramref name="html"/> as a new application. The application appears in the catalog
    /// complete or not at all: it is assembled in a staging folder and moved into place.
    /// </summary>
    public async Task<AdoptedApp> AdoptAsync(ReadOnlyMemory<byte> html, string? originalPath = null, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var app = new AdoptedApp(
            Guid.CreateVersion7(now).ToString("n"),
            now,
            new AdoptionSource(Convert.ToHexStringLower(SHA256.HashData(html.Span)), originalPath, html.Length),
            Protection: "none");

        var staging = Path.Combine(_root, StagingPrefix + app.Id);
        Directory.CreateDirectory(staging);
        try
        {
            await using (var stream = new FileStream(Path.Combine(staging, HtmlFile), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(html, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await DurableFile.WriteAtomicallyAsync(Path.Combine(staging, RecordFile), WriteRecord(app), cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, AppDirectory(app.Id));
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        return app;
    }

    /// <summary>Returns the application with <paramref name="id"/>, or <see langword="null"/> if there is none.</summary>
    public async Task<AdoptedApp?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id)) return null;
        var path = Path.Combine(AppDirectory(id), RecordFile);
        if (!File.Exists(path)) return null;
        return ReadRecord(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>All adopted applications, oldest first. Folders whose record cannot be read are skipped.</summary>
    public async Task<IReadOnlyList<AdoptedApp>> ListAsync(CancellationToken cancellationToken = default)
    {
        var apps = new List<AdoptedApp>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(directory);
            if (!IsValidId(id)) continue; // Includes staging folders left by an interrupted adoption.
            if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is { } app) apps.Add(app);
        }

        apps.Sort((a, b) => a.AdoptedAt != b.AdoptedAt ? a.AdoptedAt.CompareTo(b.AdoptedAt) : string.CompareOrdinal(a.Id, b.Id));
        return apps;
    }

    /// <summary>
    /// Applications adopted earlier from these bytes, or from a file at the same original path,
    /// oldest first. An application matching both is reported as <see cref="AdoptionMatchKind.SameBytes"/>.
    /// </summary>
    /// <remarks>
    /// A path match only means «a file at this path was adopted before»: the bytes differ, so it may
    /// be a revised version of the same application or an unrelated file saved under the same name.
    /// The catalog does not decide which — it reports, and the person decides.
    /// </remarks>
    public async Task<IReadOnlyList<AdoptionMatch>> FindEarlierAdoptionsAsync(ReadOnlyMemory<byte> html, string? originalPath = null, CancellationToken cancellationToken = default)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(html.Span));
        var matches = new List<AdoptionMatch>();
        foreach (var app in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (app.Source.Sha256 == sha256) matches.Add(new AdoptionMatch(app, AdoptionMatchKind.SameBytes));
            else if (SamePath(app.Source.OriginalPath, originalPath)) matches.Add(new AdoptionMatch(app, AdoptionMatchKind.SameOriginalPath));
        }

        return matches;
    }

    /// <summary>
    /// Whether two recorded original paths name the same file. Both are normalized; the comparison
    /// ignores case where the platform's file system usually does.
    /// </summary>
    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalize(a), Normalize(b), comparison);

        static string Normalize(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        }
    }

    /// <summary>The adopted bytes of application <paramref name="id"/>, exactly as they were adopted.</summary>
    public Task<byte[]> ReadHtmlAsync(string id, CancellationToken cancellationToken = default)
    {
        RequireValidId(id);
        return File.ReadAllBytesAsync(Path.Combine(AppDirectory(id), HtmlFile), cancellationToken);
    }

    /// <summary>Opens the data storage of application <paramref name="id"/>.</summary>
    public Task<AppStorage> OpenStorageAsync(string id, StorageOptions? options = null, CancellationToken cancellationToken = default)
    {
        RequireValidId(id);
        if (!Directory.Exists(AppDirectory(id))) throw new KeyNotFoundException($"No adopted application '{id}'.");
        return AppStorage.OpenAsync(Path.Combine(AppDirectory(id), StorageDirectory), options, cancellationToken);
    }

    /// <summary>Opens the cache of code application <paramref name="id"/> loads from other hosts.</summary>
    public AssetCache OpenAssets(string id)
    {
        RequireValidId(id);
        if (!Directory.Exists(AppDirectory(id))) throw new KeyNotFoundException($"No adopted application '{id}'.");
        return AssetCache.Open(Path.Combine(AppDirectory(id), "assets"));
    }

    /// <summary>Opens the local usage record of application <paramref name="id"/>.</summary>
    public UsageLog OpenUsage(string id)
    {
        RequireValidId(id);
        if (!Directory.Exists(AppDirectory(id))) throw new KeyNotFoundException($"No adopted application '{id}'.");
        return UsageLog.Open(Path.Combine(AppDirectory(id), UsageFile), _clock);
    }

    /// <summary>
    /// Whether <paramref name="id"/> has the shape of an issued identifier. Anything else — path
    /// separators, dots, uppercase — is rejected before it can reach the file system.
    /// </summary>
    public static bool IsValidId(string? id) =>
        id is { Length: 32 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private string AppDirectory(string id) => Path.Combine(_root, id);

    private static void RequireValidId(string id)
    {
        if (!IsValidId(id)) throw new ArgumentException($"'{id}' is not an adopted application identifier.", nameof(id));
    }

    private static byte[] WriteRecord(AdoptedApp app)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, RecordWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("format", RecordFormat);
            writer.WriteString("id", app.Id);
            writer.WriteString("adoptedAt", app.AdoptedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteStartObject("source");
            writer.WriteString("sha256", app.Source.Sha256);
            if (app.Source.OriginalPath is null) writer.WriteNull("originalPath");
            else writer.WriteString("originalPath", app.Source.OriginalPath);
            writer.WriteNumber("size", app.Source.Size);
            writer.WriteEndObject();
            writer.WriteString("protection", app.Protection);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static AdoptedApp? ReadRecord(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != RecordFormat) return null;
            var source = root.GetProperty("source");
            return new AdoptedApp(
                root.GetProperty("id").GetString()!,
                DateTimeOffset.Parse(root.GetProperty("adoptedAt").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                new AdoptionSource(
                    source.GetProperty("sha256").GetString()!,
                    source.GetProperty("originalPath").GetString(),
                    source.GetProperty("size").GetInt64()),
                root.GetProperty("protection").GetString()!);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
