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
/// <c>app.json</c> (the record), <c>app.html</c> (the adopted bytes, never modified),
/// <c>storage/</c> (its data, see <see cref="AppStorage"/>), <c>usage.ndjson</c> (its local
/// usage record, see <see cref="UsageLog"/>) and <c>revisions/</c> (see <see cref="ReviseAsync"/>).
/// </summary>
/// <remarks>
/// The catalog does not decide what to do when the same file — or another version of it — is
/// adopted again. It reports earlier adoptions through <see cref="FindEarlierAdoptionsAsync"/>, saying
/// how each one matches, and whoever talks to the person asks them what to do.
/// </remarks>
public sealed class AdoptionCatalog
{
    /// <summary>Format identifier written into every <c>app.json</c>. Changes when the record's shape does.</summary>
    public const string RecordFormat = "bohm.adopted/1";

    /// <summary>The record format before revisions existed. Read as an application at its first revision.</summary>
    private const string FirstRecordFormat = "bohm.adopted/0";

    /// <summary>Format identifier written into every <c>revisions/&lt;n&gt;/revision.json</c>.</summary>
    public const string RevisionFormat = "bohm.revision/0";

    private const string AdoptedDirectory = "adopted";
    private const string RecordFile = "app.json";
    private const string HtmlFile = "app.html";
    private const string StorageDirectory = "storage";
    private const string UsageFile = "usage.ndjson";
    private const string StagingPrefix = ".staging-";
    private const string RevisionsDirectory = "revisions";
    private const string RevisionFile = "revision.json";
    private const string DataBeforeFile = "data-before.json";
    private const string DataUndoneFile = "data-undone.json";
    private const string RemovedDirectory = "removed";
    private const string RemovedFile = "removed.json";
    private const string RemovingPrefix = ".removing-";

    /// <summary>Format identifier written into every <c>removed/&lt;id&gt;/removed.json</c>.</summary>
    public const string RemovedFormat = "bohm.removed/0";

    private static readonly JsonWriterOptions RecordWriter = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _root;
    private readonly string _removed;
    private readonly TimeProvider _clock;

    /// <summary>Creates a catalog over <paramref name="dataRoot"/>. The host chooses the root; there is no default.</summary>
    public AdoptionCatalog(string dataRoot, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        _root = Path.Combine(Path.GetFullPath(dataRoot), AdoptedDirectory);
        _removed = Path.Combine(Path.GetFullPath(dataRoot), RemovedDirectory);
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

    /// <summary>
    /// Puts the application away (<see cref="AdoptedApp.ArchivedAt"/>) or brings it back. Only the
    /// record changes; nothing is moved or deleted. Returns <see langword="null"/> for an unknown id.
    /// Archiving an archived application, or restoring one in use, changes nothing.
    /// </summary>
    public async Task<AdoptedApp?> SetArchivedAsync(string id, bool archived, CancellationToken cancellationToken = default)
    {
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } app) return null;
        if (archived == app.ArchivedAt is not null) return app;
        var changed = app with { ArchivedAt = archived ? _clock.GetUtcNow() : null };
        await DurableFile.WriteAtomicallyAsync(Path.Combine(AppDirectory(id), RecordFile), WriteRecord(changed), cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Removes an archived application for good: its folder — code, data, revisions — leaves the
    /// catalog and is handed to <paramref name="discard"/> (the host sends it to the recycle bin, so
    /// the operating system still has a way back). What stays is its usage record, in
    /// <c>removed/&lt;id&gt;/</c> with the days it was adopted, archived and removed — the record of
    /// how it was used outlives the application, as a judgment of "no longer used" needs it.
    /// </summary>
    /// <returns>The removed application, or <see langword="null"/> for an unknown id.</returns>
    /// <exception cref="InvalidOperationException">The application is not archived: only an application put away can be removed.</exception>
    /// <remarks>
    /// The folder is first renamed out of the catalog, so the application disappears at once and
    /// completely; if <paramref name="discard"/> then fails, it is renamed back and nothing changed.
    /// </remarks>
    public async Task<RemovedApp?> RemoveAsync(string id, Func<string, CancellationToken, Task> discard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discard);
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } app) return null;
        if (app.ArchivedAt is null) throw new InvalidOperationException("Only an archived application can be removed.");

        var removed = new RemovedApp(app.Id, app.AdoptedAt, app.Revision, app.ArchivedAt, _clock.GetUtcNow());
        var kept = Path.Combine(_removed, id);
        Directory.CreateDirectory(kept);
        var usage = Path.Combine(AppDirectory(id), UsageFile);
        if (File.Exists(usage))
            await DurableFile.WriteAtomicallyAsync(Path.Combine(kept, UsageFile), await File.ReadAllBytesAsync(usage, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        await DurableFile.WriteAtomicallyAsync(Path.Combine(kept, RemovedFile), WriteRemoved(removed), cancellationToken).ConfigureAwait(false);

        var leaving = Path.Combine(_root, RemovingPrefix + id);
        Directory.Move(AppDirectory(id), leaving);
        try
        {
            await discard(leaving, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (Directory.Exists(leaving)) Directory.Move(leaving, AppDirectory(id));
            Directory.Delete(kept, recursive: true);
            throw;
        }

        return removed;
    }

    /// <summary>
    /// Copies the application's folder, as it is, to <paramref name="target"/> — a new folder the
    /// caller names. The folder is the exchange format: everything that makes the application —
    /// record, adopted bytes, data, revisions, fetched code, usage record — with nothing added or
    /// converted. Pass the open storage, if any, so its journal is folded into the snapshot first and
    /// the copy's data reads without replaying operations. The original is not changed.
    /// </summary>
    /// <returns>The application, or <see langword="null"/> for an unknown id.</returns>
    /// <exception cref="IOException"><paramref name="target"/> already exists, or its parent does not.</exception>
    /// <remarks>The copy is made under a temporary name beside the target and renamed at the end, so a half-made copy is never where the person looks.</remarks>
    public async Task<AdoptedApp?> ExportAsync(string id, string target, AppStorage? openStorage = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } app) return null;
        target = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent)) throw new IOException("The folder to export into does not exist.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Something with that name is already there.");

        if (openStorage is not null) await openStorage.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        var partial = target + ".partial-" + Guid.NewGuid().ToString("n")[..8];
        try
        {
            await CopyFolderAsync(AppDirectory(id), partial, cancellationToken).ConfigureAwait(false);
            Directory.Move(partial, target);
        }
        catch
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            throw;
        }

        return app;
    }

    /// <summary>
    /// Takes in an application folder exported by <see cref="ExportAsync"/> — from this computer or
    /// another — as it is: same application, same identity, same data, revisions and usage record.
    /// The folder's own name is not used; its <c>app.json</c> says which application it is. The source
    /// folder is only read.
    /// </summary>
    /// <exception cref="InvalidDataException">The folder is not an application folder (no readable <c>app.json</c> and <c>app.html</c>).</exception>
    /// <exception cref="InvalidOperationException">This application is already here — nothing is replaced.</exception>
    public async Task<AdoptedApp> ImportAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        folder = Path.GetFullPath(folder);
        var recordPath = Path.Combine(folder, RecordFile);
        if (!Directory.Exists(folder) || !File.Exists(recordPath) || !File.Exists(Path.Combine(folder, HtmlFile)))
            throw new InvalidDataException("Not an application folder.");
        AdoptedApp? app;
        try
        {
            app = ReadRecord(await File.ReadAllBytesAsync(recordPath, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("The application record cannot be read.", e);
        }

        if (app is null || !IsValidId(app.Id)) throw new InvalidDataException("The application record is not in a known format.");
        if (Directory.Exists(AppDirectory(app.Id))) throw new InvalidOperationException("This application is already here.");

        var staging = Path.Combine(_root, StagingPrefix + app.Id);
        try
        {
            await CopyFolderAsync(folder, staging, cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, AppDirectory(app.Id));
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        return app;
    }

    private static async Task CopyFolderAsync(string from, string to, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            // Opened for reading while the runtime may still append (the usage record, a journal):
            // share write so the copy never blocks or breaks the application.
            await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using var destination = new FileStream(Path.Combine(to, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
        }

        foreach (var folder in Directory.EnumerateDirectories(from))
            await CopyFolderAsync(folder, Path.Combine(to, Path.GetFileName(folder)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applications removed for good, oldest removal first, with what was kept of them.</summary>
    public async Task<IReadOnlyList<RemovedApp>> ListRemovedAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<RemovedApp>();
        if (!Directory.Exists(_removed)) return list;
        foreach (var folder in Directory.EnumerateDirectories(_removed))
        {
            var path = Path.Combine(folder, RemovedFile);
            // An application removed and later taken in again is in use: its kept record is not reported twice.
            if (!IsValidId(Path.GetFileName(folder)) || !File.Exists(path) || Directory.Exists(AppDirectory(Path.GetFileName(folder)))) continue;
            try
            {
                if (ReadRemoved(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)) is { } app) list.Add(app);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // A kept record that cannot be read leaves the report short by one; nothing is deleted.
            }
        }

        return [.. list.OrderBy(a => a.RemovedAt)];
    }

    /// <summary>The usage record kept of a removed application.</summary>
    public UsageLog OpenRemovedUsage(string id)
    {
        RequireValidId(id);
        var folder = Path.Combine(_removed, id);
        if (!Directory.Exists(folder)) throw new KeyNotFoundException($"No removed application '{id}'.");
        return UsageLog.Open(Path.Combine(folder, UsageFile), _clock);
    }

    /// <summary>
    /// All adopted applications, oldest first — archived ones included (see <see cref="AdoptedApp.ArchivedAt"/>).
    /// Folders whose record cannot be read are left out; <see cref="ReadListingAsync"/> reports them.
    /// </summary>
    public async Task<IReadOnlyList<AdoptedApp>> ListAsync(CancellationToken cancellationToken = default) =>
        (await ReadListingAsync(cancellationToken).ConfigureAwait(false)).Apps;

    /// <summary>
    /// All adopted applications, and every application folder whose record could not be read. One
    /// unreadable folder — a file the operating system cannot open right now, such as one kept only
    /// in the cloud while offline, or a damaged record — never hides the others, and is never deleted
    /// or rewritten: its data is still there, and the person needs to be told so.
    /// </summary>
    public async Task<CatalogListing> ReadListingAsync(CancellationToken cancellationToken = default)
    {
        var apps = new List<AdoptedApp>();
        var unreadable = new List<UnreadableApp>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var id = Path.GetFileName(directory);
            // A removal stopped between leaving the catalog and reaching the recycle bin: the folder,
            // with everything in it, is still here. Said so, rather than left silently out of sight.
            if (id.StartsWith(RemovingPrefix, StringComparison.Ordinal) && IsValidId(id[RemovingPrefix.Length..]))
            {
                unreadable.Add(new UnreadableApp(id[RemovingPrefix.Length..], UnreadableApp.InterruptedRemoval, $"The folder is still in {Path.GetFileName(_root)}{Path.DirectorySeparatorChar}{id}."));
                continue;
            }

            if (!IsValidId(id)) continue; // Includes staging folders left by an interrupted adoption.
            try
            {
                if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is { } app) apps.Add(app);
                else unreadable.Add(new UnreadableApp(id, UnreadableApp.Damaged, "The record is missing or not in a known format."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(new UnreadableApp(id, UnreadableApp.CannotOpen, exception.Message));
            }
        }

        apps.Sort((a, b) => a.AdoptedAt != b.AdoptedAt ? a.AdoptedAt.CompareTo(b.AdoptedAt) : string.CompareOrdinal(a.Id, b.Id));
        unreadable.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new CatalogListing(apps, unreadable);
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

    /// <summary>The bytes of the revision of application <paramref name="id"/> in use, exactly as they were taken in.</summary>
    public async Task<byte[]> ReadHtmlAsync(string id, CancellationToken cancellationToken = default)
    {
        RequireValidId(id);
        var app = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No adopted application '{id}'.");
        return await File.ReadAllBytesAsync(HtmlPath(app), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes in <paramref name="html"/> as a new revision of application <paramref name="id"/>: the
    /// same application, with the same data, running new code. Before the code changes, the data is
    /// saved as it stands, so <see cref="RevertAsync"/> can put code and data back together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A revision is assembled in a staging folder — its bytes, its record and the saved data — and
    /// moved to <c>revisions/&lt;n&gt;/</c>; only then is <c>app.json</c> rewritten to point at it.
    /// Interrupted at any point, the application is left at the revision it was at. Revision numbers
    /// only grow, and a number whose folder exists is never reused.
    /// </para>
    /// <para>
    /// <paramref name="storage"/> must be this application's open storage — the one writer of its
    /// files. The caller makes sure no page is still running the code being replaced.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The bytes are the revision already in use.</exception>
    public async Task<AdoptedApp> ReviseAsync(string id, ReadOnlyMemory<byte> html, string? originalPath, AppStorage storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        RequireValidId(id);
        var app = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No adopted application '{id}'.");
        var source = new AdoptionSource(Convert.ToHexStringLower(SHA256.HashData(html.Span)), originalPath, html.Length);
        if (source.Sha256 == app.Source.Sha256) throw new InvalidOperationException("These bytes are the revision already in use.");

        var revisions = Path.Combine(AppDirectory(id), RevisionsDirectory);
        Directory.CreateDirectory(revisions);
        // The first revision has no record of its own until it is replaced: its source is the application's.
        var current = Path.Combine(RevisionFolder(id, app.Revision), RevisionFile);
        if (!File.Exists(current))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(current)!);
            await DurableFile.WriteAtomicallyAsync(current, WriteRevision(app.Revision, null, app.RevisedAt, app.Source), cancellationToken).ConfigureAwait(false);
        }

        var now = _clock.GetUtcNow();
        var number = Math.Max(app.Revision, HighestRevisionFolder(revisions)) + 1;
        var staging = Path.Combine(revisions, StagingPrefix + number.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            await using (var stream = new FileStream(Path.Combine(staging, HtmlFile), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(html, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await DurableFile.WriteAtomicallyAsync(Path.Combine(staging, RevisionFile), WriteRevision(number, app.Revision, now, source), cancellationToken).ConfigureAwait(false);
            await storage.SaveSnapshotAsync(Path.Combine(staging, DataBeforeFile), cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, RevisionFolder(id, number));
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        var revised = app with { Source = source, Revision = number, RevisedAt = now };
        await DurableFile.WriteAtomicallyAsync(Path.Combine(AppDirectory(id), RecordFile), WriteRecord(revised), cancellationToken).ConfigureAwait(false);
        return revised;
    }

    /// <summary>
    /// Puts application <paramref name="id"/> back to the revision it was at before the one in use,
    /// with the data it had at that moment. What the revision being left wrote is not lost: it is kept
    /// as <c>revisions/&lt;n&gt;/data-undone.json</c>, and it also becomes a previous snapshot of the storage.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no earlier revision to go back to.</exception>
    public async Task<AdoptedApp> RevertAsync(string id, AppStorage storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        RequireValidId(id);
        var app = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No adopted application '{id}'.");
        var target = await RevertTargetAsync(app, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("There is no earlier revision to go back to.");

        var folder = RevisionFolder(id, app.Revision);
        // Written once: a revert interrupted after restoring must not overwrite it with the restored data.
        var undone = Path.Combine(folder, DataUndoneFile);
        if (!File.Exists(undone)) await storage.SaveSnapshotAsync(undone, cancellationToken).ConfigureAwait(false);
        await storage.RestoreAsync(Path.Combine(folder, DataBeforeFile), cancellationToken).ConfigureAwait(false);

        var reverted = app with { Source = target.Source, Revision = target.Revision, RevisedAt = target.TakenInAt };
        await DurableFile.WriteAtomicallyAsync(Path.Combine(AppDirectory(id), RecordFile), WriteRecord(reverted), cancellationToken).ConfigureAwait(false);
        return reverted;
    }

    /// <summary>Whether <see cref="RevertAsync"/> has an earlier revision to go back to.</summary>
    public async Task<bool> CanRevertAsync(string id, CancellationToken cancellationToken = default) =>
        await GetAsync(id, cancellationToken).ConfigureAwait(false) is { } app && await CanRevertAsync(app, cancellationToken).ConfigureAwait(false);

    /// <summary>Whether <see cref="RevertAsync"/> has an earlier revision to go back to, for a record already read.</summary>
    public async Task<bool> CanRevertAsync(AdoptedApp app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        return await RevertTargetAsync(app, cancellationToken).ConfigureAwait(false) is not null;
    }

    private sealed record RevisionRecord(int Revision, int? Previous, DateTimeOffset? TakenInAt, AdoptionSource Source);

    private async Task<RevisionRecord?> RevertTargetAsync(AdoptedApp app, CancellationToken cancellationToken)
    {
        if (app.Revision <= 1) return null;
        var folder = RevisionFolder(app.Id, app.Revision);
        if (!File.Exists(Path.Combine(folder, DataBeforeFile))) return null;
        var current = await ReadRevisionAsync(folder, cancellationToken).ConfigureAwait(false);
        if (current?.Previous is not { } previous) return null;
        return await ReadRevisionAsync(RevisionFolder(app.Id, previous), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RevisionRecord?> ReadRevisionAsync(string folder, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, RevisionFile);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != RevisionFormat) return null;
            var previous = root.GetProperty("previous");
            var takenInAt = root.GetProperty("takenInAt");
            return new RevisionRecord(
                root.GetProperty("revision").GetInt32(),
                previous.ValueKind == JsonValueKind.Null ? null : previous.GetInt32(),
                takenInAt.ValueKind == JsonValueKind.Null ? null : ParseTime(takenInAt.GetString()!),
                ReadSource(root.GetProperty("source")));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The highest number among the revision folders, including one left by an interrupted revision.</summary>
    private static int HighestRevisionFolder(string revisions) =>
        Directory.EnumerateDirectories(revisions)
            .Select(d => int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

    private string RevisionFolder(string id, int revision) =>
        Path.Combine(AppDirectory(id), RevisionsDirectory, revision.ToString(CultureInfo.InvariantCulture));

    private string HtmlPath(AdoptedApp app) =>
        app.Revision <= 1 ? Path.Combine(AppDirectory(app.Id), HtmlFile) : Path.Combine(RevisionFolder(app.Id, app.Revision), HtmlFile);

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

    private static byte[] WriteRemoved(RemovedApp app)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, RecordWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("format", RemovedFormat);
            writer.WriteString("id", app.Id);
            writer.WriteString("adoptedAt", app.AdoptedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("revision", app.Revision);
            WriteTime(writer, "archivedAt", app.ArchivedAt);
            writer.WriteString("removedAt", app.RemovedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static RemovedApp? ReadRemoved(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || !format.ValueEquals(RemovedFormat)) return null;
        DateTimeOffset? Time(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture) : null;
        return new RemovedApp(root.GetProperty("id").GetString()!, Time("adoptedAt")!.Value, root.GetProperty("revision").GetInt32(), Time("archivedAt"), Time("removedAt")!.Value);
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
            writer.WriteNumber("revision", app.Revision);
            WriteTime(writer, "revisedAt", app.RevisedAt);
            WriteSource(writer, app.Source);
            writer.WriteString("protection", app.Protection);
            if (app.ArchivedAt is not null) WriteTime(writer, "archivedAt", app.ArchivedAt);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static byte[] WriteRevision(int revision, int? previous, DateTimeOffset? takenInAt, AdoptionSource source)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, RecordWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("format", RevisionFormat);
            writer.WriteNumber("revision", revision);
            if (previous is null) writer.WriteNull("previous");
            else writer.WriteNumber("previous", previous.Value);
            WriteTime(writer, "takenInAt", takenInAt);
            WriteSource(writer, source);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static void WriteTime(Utf8JsonWriter writer, string name, DateTimeOffset? time)
    {
        if (time is null) writer.WriteNull(name);
        else writer.WriteString(name, time.Value.ToString("O", CultureInfo.InvariantCulture));
    }

    private static void WriteSource(Utf8JsonWriter writer, AdoptionSource source)
    {
        writer.WriteStartObject("source");
        writer.WriteString("sha256", source.Sha256);
        if (source.OriginalPath is null) writer.WriteNull("originalPath");
        else writer.WriteString("originalPath", source.OriginalPath);
        writer.WriteNumber("size", source.Size);
        writer.WriteEndObject();
    }

    private static AdoptionSource ReadSource(JsonElement source) =>
        new(source.GetProperty("sha256").GetString()!, source.GetProperty("originalPath").GetString(), source.GetProperty("size").GetInt64());

    private static DateTimeOffset ParseTime(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static AdoptedApp? ReadRecord(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var format = root.GetProperty("format").GetString();
            if (format is not (RecordFormat or FirstRecordFormat)) return null;
            var app = new AdoptedApp(
                root.GetProperty("id").GetString()!,
                ParseTime(root.GetProperty("adoptedAt").GetString()!),
                ReadSource(root.GetProperty("source")),
                root.GetProperty("protection").GetString()!);
            if (format == FirstRecordFormat) return app;
            var revisedAt = root.GetProperty("revisedAt");
            // Absent in records written before archiving existed — and while the application is in use.
            var archivedAt = root.TryGetProperty("archivedAt", out var archived) && archived.ValueKind == JsonValueKind.String ? ParseTime(archived.GetString()!) : (DateTimeOffset?)null;
            return app with
            {
                Revision = root.GetProperty("revision").GetInt32(),
                RevisedAt = revisedAt.ValueKind == JsonValueKind.Null ? null : ParseTime(revisedAt.GetString()!),
                ArchivedAt = archivedAt,
            };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
