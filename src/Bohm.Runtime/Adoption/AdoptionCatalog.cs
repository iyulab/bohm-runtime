using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bohm.Runtime.Assets;
using Bohm.Runtime.Sources;
using Bohm.Runtime.Storage;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// The adopted applications under one data root. Each lives in its own folder, which is
/// everything needed to move it elsewhere:
/// <c>app.json</c> (the record), <c>app.html</c> (the adopted bytes, never modified),
/// <c>storage/</c> (its data, see <see cref="AppStorage"/>), <c>usage.ndjson</c> (its local
/// usage record, see <see cref="UsageLog"/>), <c>revisions/</c> (see <see cref="ReviseAsync"/>) and,
/// for an application that reads web pages, <c>sources.json</c> and <c>sources/</c> (see <see cref="AppSources"/>).
/// </summary>
/// <remarks>
/// The catalog does not decide what to do when the same file — or another version of it — is
/// adopted again. It reports earlier adoptions through <see cref="FindEarlierAdoptionsAsync"/>, saying
/// how each one matches, and whoever talks to the person asks them what to do.
/// </remarks>
public sealed partial class AdoptionCatalog
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
    private const string AssetsDirectory = "assets";
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

    /// <summary>How long an unsaved application stays after the person left it, before <see cref="SweepUnsavedAsync"/> removes it.</summary>
    public static readonly TimeSpan UnsavedRetention = TimeSpan.FromDays(14);

    /// <summary>
    /// Adopts <paramref name="html"/> as a new application. The application appears in the catalog
    /// complete or not at all: it is assembled in a staging folder and moved into place.
    /// </summary>
    /// <param name="unsaved">A result made for the person, kept only if they keep it (<see cref="AdoptedApp.Unsaved"/>).</param>
    /// <param name="title">The name of an application the runtime made (<see cref="AdoptedApp.Title"/>).</param>
    public async Task<AdoptedApp> AdoptAsync(ReadOnlyMemory<byte> html, string? originalPath = null, bool unsaved = false, string? title = null,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var app = new AdoptedApp(
            Guid.CreateVersion7(now).ToString("n"),
            now,
            new AdoptionSource(Convert.ToHexStringLower(SHA256.HashData(html.Span)), originalPath, html.Length),
            Protection: "none",
            Unsaved: unsaved,
            Title: title);

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
        return ReadRecord(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Puts the application away (<see cref="AdoptedApp.ArchivedAt"/>) or brings it back. Only the
    /// record changes; nothing is moved or deleted. Returns <see langword="null"/> for an unknown id.
    /// Archiving an archived application, or restoring one in use, changes nothing.
    /// Putting away is keeping: archiving an unsaved result also keeps it (<see cref="KeepAsync"/>),
    /// so a record is never both put away and waiting to be swept.
    /// </summary>
    public async Task<AdoptedApp?> SetArchivedAsync(string id, bool archived, CancellationToken cancellationToken = default)
    {
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } app) return null;
        if (archived == app.ArchivedAt is not null) return app;
        return await WriteAsync(archived
            ? app with { ArchivedAt = _clock.GetUtcNow(), Unsaved = false, LeftAt = null }
            : app with { ArchivedAt = null }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AdoptedApp> WriteAsync(AdoptedApp changed, CancellationToken cancellationToken)
    {
        await DurableFile.WriteAtomicallyAsync(Path.Combine(AppDirectory(changed.Id), RecordFile), WriteRecord(changed), cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Keeps an unsaved application: from now on it is one of the person's applications like any
    /// other, with its data. Returns <see langword="null"/> for an unknown id; keeping a saved one changes nothing.
    /// </summary>
    public async Task<AdoptedApp?> KeepAsync(string id, CancellationToken cancellationToken = default)
    {
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } app) return null;
        if (!app.Unsaved) return app;
        return await WriteAsync(app with { Unsaved = false, LeftAt = null }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that the person left an unsaved application (<paramref name="left"/>) or came back to it.
    /// A saved application has nothing to record. Returns <see langword="null"/> for an unknown id.
    /// </summary>
    public async Task<AdoptedApp?> SetLeftAsync(string id, bool left, CancellationToken cancellationToken = default)
    {
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } app) return null;
        if (!app.Unsaved || left == app.LeftAt is not null) return app;
        return await WriteAsync(app with { LeftAt = left ? _clock.GetUtcNow() : null }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the unsaved applications left for <see cref="UnsavedRetention"/> or longer, the way
    /// <see cref="RemoveAsync"/> does: the folder goes to <paramref name="discard"/> (the recycle bin),
    /// the usage record stays. An unsaved application with no mark of being left is marked now. An
    /// archived one is never swept — putting away is keeping, even on a record written before archiving implied it.
    /// </summary>
    /// <remarks>
    /// The host runs this when it starts, before any application can be open: nothing open is ever
    /// removed, and one whose tab was still open when the shell ended counts from this start.
    /// </remarks>
    /// <returns>The applications removed.</returns>
    public async Task<IReadOnlyList<RemovedApp>> SweepUnsavedAsync(Func<string, CancellationToken, Task> discard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discard);
        var now = _clock.GetUtcNow();
        var removed = new List<RemovedApp>();
        foreach (var app in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!app.Unsaved || app.ArchivedAt is not null) continue;
            if (app.LeftAt is not { } leftAt)
                await WriteAsync(app with { LeftAt = now }, cancellationToken).ConfigureAwait(false);
            else if (now - leftAt >= UnsavedRetention && await RemoveAsync(app.Id, discard, cancellationToken).ConfigureAwait(false) is { } gone)
                removed.Add(gone);
        }

        return removed;
    }

    /// <summary>
    /// Removes an archived application for good: its folder — code, data, revisions — leaves the
    /// catalog and is handed to <paramref name="discard"/> (the host sends it to the recycle bin, so
    /// the operating system still has a way back). What stays is its usage record, in
    /// <c>removed/&lt;id&gt;/</c> with the days it was adopted, archived and removed — the record of
    /// how it was used outlives the application, as a judgment of "no longer used" needs it.
    /// </summary>
    /// <returns>The removed application, or <see langword="null"/> for an unknown id.</returns>
    /// <exception cref="InvalidOperationException">The application is neither archived nor unsaved: only an application put away, or a result never kept, can be removed.</exception>
    /// <remarks>
    /// The folder is first renamed out of the catalog, so the application disappears at once and
    /// completely; if <paramref name="discard"/> then fails, it is renamed back and nothing changed.
    /// </remarks>
    public async Task<RemovedApp?> RemoveAsync(string id, Func<string, CancellationToken, Task> discard, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discard);
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } app) return null;
        if (app.ArchivedAt is null && !app.Unsaved) throw new InvalidOperationException("Only an archived or unsaved application can be removed.");

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
    public async Task<AdoptedApp?> ExportAsync(string id, string target, AppStorage? openStorage = null, CancellationToken cancellationToken = default) =>
        await ExportAsync(id, target, openStorage, withData: true, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Exports application <paramref name="id"/> as <see cref="ExportAsync(string, string, AppStorage?, CancellationToken)"/> does, or —
    /// with <paramref name="withData"/> false — only what makes it the application: its record, its code in
    /// every revision, the code it loads from other hosts and the rules of its sources. Its data, what it read,
    /// its usage record and the permissions to read are left out; whoever takes it in starts with none of them.
    /// </summary>
    public async Task<AdoptedApp?> ExportAsync(string id, string target, AppStorage? openStorage, bool withData, CancellationToken cancellationToken = default)
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
            if (withData) await CopyFolderAsync(AppDirectory(id), partial, cancellationToken).ConfigureAwait(false);
            else await CopyWithoutDataAsync(AppDirectory(id), partial, cancellationToken).ConfigureAwait(false);
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
            app = ReadRecord(await DurableFile.ReadAsync(recordPath, cancellationToken).ConfigureAwait(false));
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

    /// <summary>
    /// Copies what makes the application and nothing it gathered — a list of what to take, so a file
    /// added to the folder later stays behind until someone decides it belongs here.
    /// </summary>
    private static async Task CopyWithoutDataAsync(string from, string to, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(to);
        foreach (var file in new[] { RecordFile, HtmlFile })
            await CopyFileAsync(Path.Combine(from, file), Path.Combine(to, file), cancellationToken).ConfigureAwait(false);

        var revisions = Path.Combine(from, RevisionsDirectory);
        if (Directory.Exists(revisions))
        {
            foreach (var revision in Directory.EnumerateDirectories(revisions))
            {
                var copy = Path.Combine(to, RevisionsDirectory, Path.GetFileName(revision));
                Directory.CreateDirectory(copy);
                foreach (var file in new[] { HtmlFile, RevisionFile })
                    if (File.Exists(Path.Combine(revision, file)))
                        await CopyFileAsync(Path.Combine(revision, file), Path.Combine(copy, file), cancellationToken).ConfigureAwait(false);
            }
        }

        if (Directory.Exists(Path.Combine(from, AssetsDirectory)))
            await CopyFolderAsync(Path.Combine(from, AssetsDirectory), Path.Combine(to, AssetsDirectory), cancellationToken).ConfigureAwait(false);

        using var sources = AppSources.Open(from);
        await sources.CopyRulesAsync(to, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyFileAsync(string from, string to, CancellationToken cancellationToken)
    {
        // Opened for reading while the runtime may still append (the usage record, a journal):
        // share write so the copy never blocks or breaks the application.
        await using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var destination = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task CopyFolderAsync(string from, string to, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            await CopyFileAsync(file, Path.Combine(to, Path.GetFileName(file)), cancellationToken).ConfigureAwait(false);

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
                if (ReadRemoved(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false)) is { } app) list.Add(app);
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
    /// Applications adopted earlier from these bytes, from a file at the same original path, from a
    /// file in the same folder under the same name but for a browser's download number, or whose stored
    /// data these bytes would read (every stored key named in the source), oldest first.
    /// Each application is reported once, by its closest match: bytes, then path, then name, then keys.
    /// </summary>
    /// <remarks>
    /// A path match only means «a file at this path was adopted before»: the bytes differ, so it may
    /// be a revised version of the same application or an unrelated file saved under the same name.
    /// The catalog does not decide which — it reports, and the person decides. Every file an
    /// application was taken in from counts, not only its current revision's: a revision made without
    /// a file (an applied change) must not cut the application off from the file it came from.
    /// </remarks>
    public async Task<IReadOnlyList<AdoptionMatch>> FindEarlierAdoptionsAsync(ReadOnlyMemory<byte> html, string? originalPath = null, CancellationToken cancellationToken = default)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(html.Span));
        string? source = null;   // decoded only when a stored-keys comparison is reached
        var matches = new List<AdoptionMatch>();
        foreach (var app in await ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (app.Source.Sha256 == sha256) { matches.Add(new AdoptionMatch(app, AdoptionMatchKind.SameBytes)); continue; }
            if (originalPath is not null && await PathsTakenInAsync(app, cancellationToken).ConfigureAwait(false) is { Count: > 0 } paths)
            {
                if (paths.Any(p => SamePath(p, originalPath))) { matches.Add(new AdoptionMatch(app, AdoptionMatchKind.SameOriginalPath)); continue; }
                if (paths.Any(p => SameName(p, originalPath))) { matches.Add(new AdoptionMatch(app, AdoptionMatchKind.SameName)); continue; }
            }

            var keys = await AppStorage.PeekKeysAsync(Path.Combine(AppDirectory(app.Id), StorageDirectory), cancellationToken).ConfigureAwait(false);
            source ??= Encoding.UTF8.GetString(html.Span);
            if (UsesStoredKeys(source, keys)) matches.Add(new AdoptionMatch(app, AdoptionMatchKind.SameStoredKeys));
        }

        return matches;
    }

    /// <summary>
    /// Whether <paramref name="source"/> names every one of an application's stored <paramref name="keys"/>
    /// as a quoted string literal (<c>"key"</c>, <c>'key'</c> or <c>`key`</c>) — code that would read the data
    /// that application left. An application that stored nothing matches no file: there is no data to
    /// carry, and an empty set would match everything.
    /// </summary>
    /// <remarks>
    /// Every key, not any: a revised file keeps reading its data, while an unrelated file can share one
    /// common word. A quoted literal, not a substring: <c>items</c> appears in almost any page's text.
    /// </remarks>
    private static bool UsesStoredKeys(string source, IReadOnlySet<string> keys) =>
        keys.Count > 0 && keys.All(key => source.Contains($"\"{key}\"", StringComparison.Ordinal)
            || source.Contains($"'{key}'", StringComparison.Ordinal) || source.Contains($"`{key}`", StringComparison.Ordinal));

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

    /// <summary>
    /// Whether two paths name files in the same folder with the same name once the number a browser adds
    /// to a repeated download is set aside — <c>loans (1).html</c>, <c>loans(2).html</c> and <c>loans.html</c>
    /// share one. Equal paths are <see cref="SamePath"/>'s.
    /// </summary>
    private static bool SameName(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        try
        {
            return string.Equals(Path.GetDirectoryName(Path.GetFullPath(a)), Path.GetDirectoryName(Path.GetFullPath(b)), comparison)
                && string.Equals(Path.GetExtension(a), Path.GetExtension(b), comparison)
                && string.Equals(Stem(a), Stem(b), comparison);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        static string Stem(string path) => DownloadNumber().Replace(Path.GetFileNameWithoutExtension(path), "");
    }

    /// <summary>The number browsers append to a repeated download: <c> (1)</c>, or <c>(1)</c> without the space.</summary>
    [GeneratedRegex(@"\s?\(\d{1,4}\)$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadNumber();

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

        // A new revision is the same application, so it keeps the name it had — whether the revision came
        // from no file (an applied change) or from a file under another name («loans (1).html» downloaded
        // again). From the first revision on, the name is held in Title and later revisions do not rename it.
        var title = app.Title ?? NameOf(await PathsTakenInAsync(app, cancellationToken).ConfigureAwait(false));
        var revised = app with { Source = source, Revision = number, RevisedAt = now, Title = title };
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

    /// <summary>
    /// Every revision application <paramref name="id"/> has had, oldest first — including revisions it was
    /// put back from, which stay on disk with the data they wrote. An application never revised has one.
    /// A revision folder whose record cannot be read is left out rather than guessed at.
    /// </summary>
    public async Task<IReadOnlyList<AppRevision>> ListRevisionsAsync(string id, CancellationToken cancellationToken = default)
    {
        RequireValidId(id);
        var app = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No adopted application '{id}'.");
        var revisions = new List<AppRevision>();
        var directory = Path.Combine(AppDirectory(id), RevisionsDirectory);
        if (Directory.Exists(directory))
        {
            var numbers = Directory.EnumerateDirectories(directory)
                .Select(d => int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .Where(n => n > 0)
                .Order();
            IReadOnlyDictionary<string, string>? now = null;   // read once, only if some revision kept data aside
            foreach (var number in numbers)
            {
                var folder = RevisionFolder(id, number);
                if (await ReadRevisionAsync(folder, cancellationToken).ConfigureAwait(false) is not { } record) continue;
                UndoneData? undone = null;
                if (File.Exists(Path.Combine(folder, DataUndoneFile)))
                {
                    now ??= await AppStorage.PeekAsync(Path.Combine(AppDirectory(id), StorageDirectory), cancellationToken).ConfigureAwait(false);
                    undone = await UndoneStateAsync(app, number, now, cancellationToken).ConfigureAwait(false);
                }

                revisions.Add(new AppRevision(record.Revision, record.Previous, record.TakenInAt ?? app.AdoptedAt, record.Source, record.Revision == app.Revision, undone));
            }
        }

        // The first revision has no folder of its own until another replaces it.
        if (!revisions.Exists(r => r.Revision == app.Revision))
            revisions.Add(new AppRevision(app.Revision, null, app.RevisedAt ?? app.AdoptedAt, app.Source, true, null));
        revisions.Sort((a, b) => a.Revision.CompareTo(b.Revision));
        return revisions;
    }

    /// <summary>
    /// Takes the data revision <paramref name="revision"/> wrote — kept aside when the application was put
    /// back from it — back in, replacing the data now. Only while that loses nothing: nothing was written
    /// since going back, and the code in use reads every key of the kept data (<see cref="UndoneData.Importable"/>).
    /// The data it replaces stays in the storage's previous snapshots and is what <see cref="UndoImportAsync"/> restores.
    /// </summary>
    /// <remarks><paramref name="storage"/> must be this application's open storage — the one writer of its files.</remarks>
    /// <exception cref="InvalidOperationException">The kept data is not <see cref="UndoneData.Importable"/>.</exception>
    public async Task<AdoptedApp> ImportUndoneAsync(string id, int revision, AppStorage storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var app = await UndoneAppAsync(id, revision, storage, UndoneData.Importable, cancellationToken).ConfigureAwait(false);
        await storage.RestoreAsync(Path.Combine(RevisionFolder(id, revision), DataUndoneFile), cancellationToken).ConfigureAwait(false);
        return app;
    }

    /// <summary>
    /// Undoes <see cref="ImportUndoneAsync"/>: while the data is still exactly what was taken back in, puts
    /// back the data it replaced — the data the application was restored to when it went back from <paramref name="revision"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The data is not <see cref="UndoneData.Imported"/> (it changed since, or was never taken back in).</exception>
    public async Task<AdoptedApp> UndoImportAsync(string id, int revision, AppStorage storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var app = await UndoneAppAsync(id, revision, storage, UndoneData.Imported, cancellationToken).ConfigureAwait(false);
        await storage.RestoreAsync(Path.Combine(RevisionFolder(id, revision), DataBeforeFile), cancellationToken).ConfigureAwait(false);
        return app;
    }

    private async Task<AdoptedApp> UndoneAppAsync(string id, int revision, AppStorage storage, UndoneData required, CancellationToken cancellationToken)
    {
        RequireValidId(id);
        var app = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No adopted application '{id}'.");
        if (revision <= 0 || !File.Exists(Path.Combine(RevisionFolder(id, revision), DataUndoneFile)))
            throw new InvalidOperationException("No data was kept aside for this revision.");
        var state = await UndoneStateAsync(app, revision, storage.GetItems(), cancellationToken).ConfigureAwait(false);
        return state == required ? app : throw new InvalidOperationException($"The kept data is {state}, not {required}.");
    }

    /// <summary>
    /// Where the data revision <paramref name="revision"/> kept aside stands against <paramref name="now"/>.
    /// Going back from it restored <c>data-before.json</c>; data still equal to that means nothing was
    /// written since. The code in use reads a key when the restored data had it or its source names it as
    /// a quoted literal (<see cref="UsesStoredKeys"/>). A kept file that cannot be read counts as diverged.
    /// </summary>
    private async Task<UndoneData> UndoneStateAsync(AdoptedApp app, int revision, IReadOnlyDictionary<string, string> now, CancellationToken cancellationToken)
    {
        var folder = RevisionFolder(app.Id, revision);
        if (await ReadSnapshotAsync(Path.Combine(folder, DataUndoneFile), cancellationToken).ConfigureAwait(false) is not { } undone
            || await ReadSnapshotAsync(Path.Combine(folder, DataBeforeFile), cancellationToken).ConfigureAwait(false) is not { } before)
            return UndoneData.Diverged;
        if (SameItems(now, undone)) return UndoneData.Imported;
        if (!SameItems(now, before)) return UndoneData.Diverged;
        var unread = new SortedSet<string>(undone.Keys.Where(k => !before.ContainsKey(k)), StringComparer.Ordinal);
        if (unread.Count == 0) return UndoneData.Importable;
        var source = Encoding.UTF8.GetString(await ReadHtmlAsync(app.Id, cancellationToken).ConfigureAwait(false));
        return UsesStoredKeys(source, unread) ? UndoneData.Importable : UndoneData.Diverged;

        static bool SameItems(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
            a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyDictionary<string, string>?> ReadSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return StorageFormat.TryReadSnapshot(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false), out _, out var items) ? items : null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
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
            using var document = JsonDocument.Parse(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false));
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

    /// <summary>
    /// The files application <paramref name="app"/> was taken in from, oldest revision first: the
    /// original path of every recorded revision and of the one in use. Revisions made without a file add nothing.
    /// </summary>
    private async Task<IReadOnlyList<string>> PathsTakenInAsync(AdoptedApp app, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var revisions = Path.Combine(AppDirectory(app.Id), RevisionsDirectory);
        if (Directory.Exists(revisions))
        {
            var numbers = Directory.EnumerateDirectories(revisions)
                .Select(d => int.TryParse(Path.GetFileName(d), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
                .Where(n => n > 0)
                .Order();
            foreach (var number in numbers)
                if ((await ReadRevisionAsync(RevisionFolder(app.Id, number), cancellationToken).ConfigureAwait(false))?.Source.OriginalPath is { Length: > 0 } path)
                    paths.Add(path);
        }

        if (app.Source.OriginalPath is { Length: > 0 } current) paths.Add(current);
        return paths;
    }

    /// <summary>The name an application is known by from the last file it was taken in from, or <see langword="null"/> when it never came from one.</summary>
    private static string? NameOf(IReadOnlyList<string> pathsTakenIn) =>
        pathsTakenIn.Count == 0 ? null : Path.GetFileNameWithoutExtension(pathsTakenIn[^1]) is { Length: > 0 } name ? name : null;

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
        return AssetCache.Open(Path.Combine(AppDirectory(id), AssetsDirectory));
    }

    /// <summary>Opens the sources of application <paramref name="id"/> — the web pages it reads and what was read.</summary>
    public AppSources OpenSources(string id)
    {
        RequireValidId(id);
        if (!Directory.Exists(AppDirectory(id))) throw new KeyNotFoundException($"No adopted application '{id}'.");
        return AppSources.Open(AppDirectory(id), _clock);
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
            if (app.Title is not null) writer.WriteString("title", app.Title);
            if (app.Unsaved)
            {
                writer.WriteBoolean("unsaved", true);
                WriteTime(writer, "leftAt", app.LeftAt);
            }

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
            // Absent for every application the person adopted themselves, and in records written before unsaved results existed.
            var unsaved = root.TryGetProperty("unsaved", out var unsavedMark) && unsavedMark.ValueKind == JsonValueKind.True;
            var leftAt = unsaved && root.TryGetProperty("leftAt", out var left) && left.ValueKind == JsonValueKind.String ? ParseTime(left.GetString()!) : (DateTimeOffset?)null;
            return app with
            {
                Revision = root.GetProperty("revision").GetInt32(),
                RevisedAt = revisedAt.ValueKind == JsonValueKind.Null ? null : ParseTime(revisedAt.GetString()!),
                ArchivedAt = archivedAt,
                Unsaved = unsaved,
                LeftAt = leftAt,
                Title = root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null,
            };
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
