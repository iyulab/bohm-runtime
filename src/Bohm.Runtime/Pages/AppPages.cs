using LocalOrigin.Storage;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.Pages;

/// <summary>
/// A web page a person sent to an application: where it was, its title, its readable text and — when
/// it fits — the same text as cleaned HTML, with the language and author the page declared.
/// </summary>
/// <param name="Id">The page's id in this application: sixteen lowercase hex digits, ordered by when it arrived.</param>
/// <param name="ReceivedAt">When the application received it.</param>
/// <param name="Url">The address of the page.</param>
/// <param name="Title">The page's title; its address when it had none.</param>
/// <param name="Text">The readable text of the page.</param>
/// <param name="Html">The readable part of the page as HTML, or <see langword="null"/> when it was too large to keep beside the text.</param>
/// <param name="Lang">The language the page declared, if any.</param>
/// <param name="Byline">The author the page named, if any.</param>
public sealed record ReceivedPage(string Id, DateTimeOffset ReceivedAt, string Url, string Title, string Text, string? Html, string? Lang, string? Byline);

/// <summary>A received page without its text and HTML: what a list of pages shows.</summary>
/// <param name="Excerpt">The first words of the text, on one line.</param>
public sealed record ReceivedPageSummary(string Id, DateTimeOffset ReceivedAt, string Url, string Title, string? Lang, string? Byline, string Excerpt);

/// <summary>What became of a page offered to <see cref="AppPages.ReceiveAsync"/>.</summary>
public enum ReceiveOutcome
{
    /// <summary>The page was kept, with its HTML.</summary>
    Received,

    /// <summary>The page was kept without its HTML, which did not fit beside the text.</summary>
    ReceivedWithoutHtml,

    /// <summary>The address is not a web address, or the page has no text.</summary>
    Invalid,

    /// <summary>The text alone is larger than a page may be; nothing was kept.</summary>
    TooLarge,
}

/// <summary>
/// The pages people sent to one receiver: what a page must be to be kept, how it is named and summarized.
/// Where the pages are kept is the subclass's — <see cref="AppPages"/> in an application's folder,
/// <see cref="HeldPages"/> in memory for a preview. Calls are serialized: a subclass keeps, lists and gets
/// one at a time.
/// </summary>
/// <remarks>
/// A page is a fact about what the person chose to keep at that moment, so pages are only added: the
/// application reads them and keeps in its own storage whatever it makes of them.
/// </remarks>
public abstract partial class ReceivedPages : IDisposable
{
    /// <summary>The most a page may weigh, its text and HTML together, in UTF-8 bytes. The HTML is the first to go.</summary>
    public const int MaxPageBytes = 2 * 1024 * 1024;

    private const int ExcerptLength = 200;

    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _lastStamp;
    private int _lastSequence;

    private protected ReceivedPages(TimeProvider clock) => _clock = clock;

    /// <summary>Whether <paramref name="id"/> can name a received page.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>
    /// Keeps a page sent to the receiver: a web address (<c>http</c> or <c>https</c>) and non-blank
    /// text are required; a blank title becomes the address. When text and HTML together are larger
    /// than <see cref="MaxPageBytes"/> the HTML is left out; when the text alone is, nothing is kept.
    /// </summary>
    public async Task<(ReceiveOutcome Outcome, ReceivedPage? Page)> ReceiveAsync(
        string url, string? title, string text, string? html, string? lang, string? byline, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(text))
            return (ReceiveOutcome.Invalid, null);
        var textBytes = Encoding.UTF8.GetByteCount(text);
        if (textBytes > MaxPageBytes) return (ReceiveOutcome.TooLarge, null);
        var withoutHtml = html is not null && textBytes + Encoding.UTF8.GetByteCount(html) > MaxPageBytes;
        if (withoutHtml || string.IsNullOrWhiteSpace(html)) html = null;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var receivedAt = _clock.GetUtcNow();
            var page = new ReceivedPage(NewId(receivedAt), receivedAt, url, string.IsNullOrWhiteSpace(title) ? url : title.Trim(), text, html,
                Blank(lang), Blank(byline));
            await KeepAsync(page, cancellationToken).ConfigureAwait(false);
            return (withoutHtml ? ReceiveOutcome.ReceivedWithoutHtml : ReceiveOutcome.Received, page);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The received pages, oldest first, without their text and HTML.</summary>
    public async Task<IReadOnlyList<ReceivedPageSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ListKeptAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Received page <paramref name="id"/>, or <see langword="null"/> when there is no such page.</summary>
    public async Task<ReceivedPage?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id)) return null;
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetKeptAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Keeps <paramref name="page"/>, valid and newly named. Called one at a time.</summary>
    private protected abstract Task KeepAsync(ReceivedPage page, CancellationToken cancellationToken);

    /// <summary>The kept pages' summaries, oldest first. Called one at a time.</summary>
    private protected abstract Task<IReadOnlyList<ReceivedPageSummary>> ListKeptAsync(CancellationToken cancellationToken);

    /// <summary>Kept page <paramref name="id"/> — a valid id — if there is one. Called one at a time.</summary>
    private protected abstract Task<ReceivedPage?> GetKeptAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// A new id: the milliseconds since 1970 in twelve hex digits, then four more — random for the first
    /// page of a millisecond, one up for each next one — so ids sort as pages arrived and two pages in the
    /// same millisecond still differ. Never earlier than the last one.
    /// </summary>
    private string NewId(DateTimeOffset at)
    {
        var stamp = at.ToUnixTimeMilliseconds();
        if (stamp > _lastStamp)
        {
            _lastStamp = stamp;
            _lastSequence = RandomNumberGenerator.GetInt32(0x8000);
        }
        else if (++_lastSequence > 0xffff)
        {
            _lastStamp++;
            _lastSequence = 0;
        }

        return _lastStamp.ToString("x12", System.Globalization.CultureInfo.InvariantCulture) + _lastSequence.ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>What a list of pages shows of <paramref name="page"/>.</summary>
    public static ReceivedPageSummary SummaryOf(ReceivedPage page) =>
        new(page.Id, page.ReceivedAt, page.Url, page.Title, page.Lang, page.Byline, Excerpt(page.Text));

    private static string Excerpt(string text)
    {
        var line = WhiteSpace().Replace(text, " ").Trim();
        if (line.Length <= ExcerptLength) return line;
        var cut = ExcerptLength;
        if (char.IsLowSurrogate(line[cut])) cut--;
        return line[..cut].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The pages people sent to one application, in its folder: <c>pages/&lt;id&gt;.json</c>, one file per
/// page, and <c>pages/index.ndjson</c>, one summary line per page, oldest first.
/// </summary>
/// <remarks>
/// The page file is written before its index line, so a crash between the two leaves an unlisted file,
/// never a listed page that cannot be read.
/// </remarks>
public sealed class AppPages : ReceivedPages
{
    /// <summary>The folder holding the pages, in the application's folder.</summary>
    public const string Directory = "pages";

    private const string IndexFile = "index.ndjson";

    private readonly string _folder;

    private AppPages(string appFolder, TimeProvider clock) : base(clock) => _folder = Path.Combine(appFolder, Directory);

    /// <summary>Opens the pages of the application whose folder is <paramref name="appFolder"/>. Nothing is created until a page arrives.</summary>
    public static AppPages Open(string appFolder, TimeProvider? clock = null) => new(appFolder, clock ?? TimeProvider.System);

    private protected override async Task KeepAsync(ReceivedPage page, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(_folder);
        await DurableFile.WriteAtomicallyAsync(PagePath(page.Id), JsonSerializer.SerializeToUtf8Bytes(page, PagesJson.Default.ReceivedPage), cancellationToken).ConfigureAwait(false);
        await AppendSummaryAsync(SummaryOf(page), cancellationToken).ConfigureAwait(false);
    }

    private protected override async Task<IReadOnlyList<ReceivedPageSummary>> ListKeptAsync(CancellationToken cancellationToken)
    {
        var index = Path.Combine(_folder, IndexFile);
        if (!File.Exists(index)) return [];
        var pages = new List<ReceivedPageSummary>();
        foreach (var line in Encoding.UTF8.GetString(await DurableFile.ReadAsync(index, cancellationToken).ConfigureAwait(false)).Split('\n'))
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, PagesJson.Default.ReceivedPageSummary) is { } summary) pages.Add(summary);
            }
            catch (JsonException)
            {
                // An incomplete line — the end of an append a crash interrupted — is not a page.
            }
        }

        return pages;
    }

    private protected override async Task<ReceivedPage?> GetKeptAsync(string id, CancellationToken cancellationToken)
    {
        var path = PagePath(id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize(await DurableFile.ReadAsync(path, cancellationToken).ConfigureAwait(false), PagesJson.Default.ReceivedPage);
    }

    private string PagePath(string id) => Path.Combine(_folder, id + ".json");

    private async Task AppendSummaryAsync(ReceivedPageSummary summary, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(summary, PagesJson.Default.ReceivedPageSummary);
        await using var stream = new FileStream(Path.Combine(_folder, IndexFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
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
}

/// <summary>
/// Pages sent to something that keeps nothing — a preview of a proposed application, tried before it is
/// taken in: held in memory, gone with it.
/// </summary>
public sealed class HeldPages(TimeProvider? clock = null) : ReceivedPages(clock ?? TimeProvider.System)
{
    private readonly List<ReceivedPage> _pages = [];
    private int _count;

    /// <summary>Whether a page has been sent here.</summary>
    public bool Any => Volatile.Read(ref _count) > 0;

    private protected override Task KeepAsync(ReceivedPage page, CancellationToken cancellationToken)
    {
        _pages.Add(page);
        Volatile.Write(ref _count, _pages.Count);
        return Task.CompletedTask;
    }

    private protected override Task<IReadOnlyList<ReceivedPageSummary>> ListKeptAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReceivedPageSummary>>([.. _pages.Select(SummaryOf)]);

    private protected override Task<ReceivedPage?> GetKeptAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_pages.Find(p => p.Id == id));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(ReceivedPage))]
[JsonSerializable(typeof(ReceivedPageSummary))]
internal sealed partial class PagesJson : JsonSerializerContext;
