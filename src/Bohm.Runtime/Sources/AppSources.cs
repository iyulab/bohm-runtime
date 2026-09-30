using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bohm.Runtime.Storage;

namespace Bohm.Runtime.Sources;

/// <summary>
/// How to read one table from a web page, so the same table can be read again the same way:
/// the pages it may be read from (<paramref name="Site"/> — an address, and every page under it),
/// the table on the page (<paramref name="Selector"/>) and the header names of the columns to keep,
/// in order.
/// </summary>
public sealed record SourceRule(string Site, string Selector, IReadOnlyList<string> Columns)
{
    /// <summary>Throws <see cref="ArgumentException"/> unless the rule names a web address, a table and distinct, non-blank columns.</summary>
    public static void Validate(SourceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!Uri.TryCreate(rule.Site, UriKind.Absolute, out var site) || site.Scheme is not ("http" or "https"))
            throw new ArgumentException($"'{rule.Site}' is not a web address.", nameof(rule));
        if (string.IsNullOrWhiteSpace(rule.Selector)) throw new ArgumentException("The rule names no table.", nameof(rule));
        if (rule.Columns is not { Count: > 0 }) throw new ArgumentException("The rule keeps no columns.", nameof(rule));
        if (rule.Columns.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("A column has no name.", nameof(rule));
        if (rule.Columns.Distinct(StringComparer.Ordinal).Count() != rule.Columns.Count)
            throw new ArgumentException("Two columns have the same name.", nameof(rule));
    }
}

/// <summary>The person's permission to read a source: which pages, and when it was given.</summary>
public sealed record SourceGrant(string Site, DateTimeOffset GrantedAt);

/// <summary>A source an application reads from: its rule, the permission for it (if any) and when it was last read.</summary>
public sealed record AppSource(string Name, SourceRule Rule, SourceGrant? Grant, DateTimeOffset? LastReadAt);

/// <summary>What was read from a source at one time: the page it came from and its rows, each a cell per column name.</summary>
public sealed record SourceReading(DateTimeOffset ReadAt, string Source, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

/// <summary>What became of a reading offered to <see cref="AppSources.RecordAsync"/>.</summary>
public enum RecordOutcome
{
    /// <summary>The reading was kept.</summary>
    Recorded,

    /// <summary>The application has no source by that name.</summary>
    Unknown,

    /// <summary>The source has no permission — never given, or taken back.</summary>
    NotGranted,

    /// <summary>The page is not under the site the permission names.</summary>
    OutsideGrant,

    /// <summary>The columns are not the rule's columns, in the rule's order, or a row does not have one cell per column.</summary>
    ShapeMismatch,
}

/// <summary>
/// The sources of one application, in its folder: <c>sources.json</c> (the rules, the permissions and
/// when each was last read) and <c>sources/&lt;name&gt;.ndjson</c> (the readings, one per line, oldest first).
/// </summary>
/// <remarks>
/// Readings are appended and never rewritten: what was read from a page is a fact about that page at
/// that time, kept even after the permission is taken back. This class checks every reading before
/// keeping it — the permission, the page and the columns — so nothing outside what the person allowed,
/// and nothing of another shape, is stored.
/// </remarks>
public sealed partial class AppSources : IDisposable
{
    /// <summary>Format identifier written into every <c>sources.json</c>. Changes when its shape does.</summary>
    public const string Format = "bohm.sources/1";

    /// <summary>The file holding the rules, in the application's folder.</summary>
    public const string RulesFile = "sources.json";

    /// <summary>The folder holding the readings, in the application's folder.</summary>
    public const string ReadingsDirectory = "sources";

    private readonly string _folder;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private AppSources(string folder, TimeProvider clock)
    {
        _folder = folder;
        _clock = clock;
    }

    /// <summary>Opens the sources of the application whose folder is <paramref name="appFolder"/>. Nothing is created until something is declared.</summary>
    public static AppSources Open(string appFolder, TimeProvider? clock = null) => new(appFolder, clock ?? TimeProvider.System);

    /// <summary>Whether <paramref name="name"/> can name a source: a lowercase letter or digit, then up to 39 more of those or hyphens.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>The application's sources, in the order they were declared.</summary>
    public async Task<IReadOnlyList<AppSource>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Declares source <paramref name="name"/>, or replaces its rule. <paramref name="granted"/> says the
    /// person has just allowed the rule's site to be read; without it the source is declared but cannot be
    /// read until it is declared again with permission. Readings already kept stay.
    /// </summary>
    public async Task<AppSource> DeclareAsync(string name, SourceRule rule, bool granted, CancellationToken cancellationToken = default)
    {
        RequireValidName(name);
        SourceRule.Validate(rule);
        var declared = new AppSource(name, rule with { Columns = [.. rule.Columns] }, granted ? new SourceGrant(rule.Site, _clock.GetUtcNow()) : null, null);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = (await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var at = sources.FindIndex(s => s.Name == name);
            if (at < 0) sources.Add(declared);
            else sources[at] = declared with { LastReadAt = sources[at].LastReadAt };
            await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
            return at < 0 ? declared : sources[at];
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Takes back the permission for source <paramref name="name"/>. Its rule and its readings stay. False when there is no such source.</summary>
    public async Task<bool> RevokeAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsValidName(name)) return false;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = (await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var at = sources.FindIndex(s => s.Name == name);
            if (at < 0) return false;
            sources[at] = sources[at] with { Grant = null };
            await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Keeps a reading of source <paramref name="name"/> taken from page <paramref name="source"/>, if the
    /// source has permission, the page is under the permitted site and the columns are the rule's — in its
    /// order, with one cell per column in every row. Anything else is refused and nothing is kept.
    /// </summary>
    public async Task<(RecordOutcome Outcome, SourceReading? Reading)> RecordAsync(
        string name, string source, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default)
    {
        if (!IsValidName(name)) return (RecordOutcome.Unknown, null);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sources = (await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var at = sources.FindIndex(s => s.Name == name);
            if (at < 0) return (RecordOutcome.Unknown, null);
            var declared = sources[at];
            if (Check(declared.Rule, declared.Grant, source, columns, rows) is not RecordOutcome.Recorded and var refused) return (refused, null);

            var reading = new SourceReading(
                _clock.GetUtcNow(),
                source,
                [.. rows.Select(r => (IReadOnlyDictionary<string, string>)columns.Zip(r).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal))]);
            await AppendReadingAsync(name, reading, cancellationToken).ConfigureAwait(false);
            sources[at] = declared with { LastReadAt = reading.ReadAt };
            await WriteRulesAsync(sources, cancellationToken).ConfigureAwait(false);
            return (RecordOutcome.Recorded, reading);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Whether a reading would be kept for a source with <paramref name="rule"/> and <paramref name="grant"/> —
    /// <see cref="RecordOutcome.Recorded"/> — or why not, without keeping anything: the check
    /// <see cref="RecordAsync"/> makes, for checking before a source exists.
    /// </summary>
    public static RecordOutcome Check(SourceRule rule, SourceGrant? grant, string source, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (grant is null) return RecordOutcome.NotGranted;
        if (!IsUnder(source, grant.Site)) return RecordOutcome.OutsideGrant;
        if (!columns.SequenceEqual(rule.Columns, StringComparer.Ordinal) || rows.Any(r => r.Count != columns.Count)) return RecordOutcome.ShapeMismatch;
        return RecordOutcome.Recorded;
    }

    /// <summary>The readings of source <paramref name="name"/>, oldest first, or <see langword="null"/> when there is no such source.</summary>
    public async Task<IReadOnlyList<SourceReading>?> ReadingsAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!IsValidName(name)) return null;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if ((await ReadRulesAsync(cancellationToken).ConfigureAwait(false)).All(s => s.Name != name)) return null;
            var path = ReadingsPath(name);
            if (!File.Exists(path)) return [];
            var text = Encoding.UTF8.GetString(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false));
            var readings = new List<SourceReading>();
            foreach (var line in text.Split('\n'))
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize(line, SourcesJson.Default.StoredReading) is { } stored)
                        readings.Add(new SourceReading(stored.ReadAt, stored.Source, stored.Rows));
                }
                catch (JsonException)
                {
                    // An incomplete line — the end of an append a crash interrupted — is not a reading.
                }
            }

            return readings;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Writes the rules alone into the application folder <paramref name="appFolder"/> — without the
    /// permissions and without when each source was last read — for a copy of the application that
    /// leaves its data behind. Nothing is written when there are no sources.
    /// </summary>
    public async Task CopyRulesAsync(string appFolder, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AppSource> sources;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            sources = await ReadRulesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        if (sources.Count == 0) return;
        using var copy = Open(appFolder, _clock);
        await copy.WriteRulesAsync([.. sources.Select(s => s with { Grant = null, LastReadAt = null })], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="page"/> is <paramref name="site"/> or a page under it: same scheme, host and
    /// port, and a path that continues the site's at a <c>/</c>, <c>?</c> or <c>#</c> — so a host or path that
    /// merely begins with the same letters is outside.
    /// </summary>
    private static bool IsUnder(string page, string site)
    {
        if (!page.StartsWith(site, StringComparison.Ordinal)) return false;
        if (page.Length == site.Length || site.EndsWith('/')) return true;
        return page[site.Length] is '/' or '?' or '#';
    }

    private string RulesPath => Path.Combine(_folder, RulesFile);

    private string ReadingsPath(string name) => Path.Combine(_folder, ReadingsDirectory, name + ".ndjson");

    private async Task<IReadOnlyList<AppSource>> ReadRulesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(RulesPath)) return [];
        var stored = JsonSerializer.Deserialize(await DurableFile.ReadAsync(RulesPath, cancellationToken).ConfigureAwait(false), SourcesJson.Default.StoredRules);
        if (stored is not { Format: Format }) throw new InvalidDataException($"'{RulesPath}' is not a {Format} file.");
        return stored.Sources;
    }

    private async Task WriteRulesAsync(IReadOnlyList<AppSource> sources, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredRules(Format, sources), SourcesJson.Default.StoredRules);
        await DurableFile.WriteAtomicallyAsync(RulesPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendReadingAsync(string name, SourceReading reading, CancellationToken cancellationToken)
    {
        var path = ReadingsPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.SerializeToUtf8Bytes(new StoredReading(reading.ReadAt, reading.Source, reading.Rows), SourcesJson.Default.StoredReading);
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        // A line a crash left unfinished must not swallow this one: start on a line of its own.
        var startsOwnLine = stream.Length == 0;
        if (!startsOwnLine)
        {
            stream.Seek(-1, SeekOrigin.End);
            startsOwnLine = stream.ReadByte() == '\n';
        }

        stream.Seek(0, SeekOrigin.End);
        if (!startsOwnLine) stream.WriteByte((byte)'\n');
        await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    public void Dispose() => _lock.Dispose();

    private static void RequireValidName(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"'{name}' cannot name a source.", nameof(name));
    }

    internal sealed record StoredRules(string Format, IReadOnlyList<AppSource> Sources);

    internal sealed record StoredReading(DateTimeOffset ReadAt, string Source, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(AppSources.StoredRules))]
[JsonSerializable(typeof(AppSources.StoredReading))]
internal sealed partial class SourcesJson : JsonSerializerContext;
