using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bohm.Runtime.Storage;

namespace Bohm.Runtime.Assets;

/// <summary>One cached file.</summary>
/// <param name="Url">The URL the application refers to.</param>
/// <param name="FinalUrl">Where it was actually fetched from, after redirects.</param>
/// <param name="Sha256">Lowercase hexadecimal SHA-256 of the bytes; also the file's name in the cache.</param>
/// <param name="ContentType">The media type the server gave it.</param>
/// <param name="Size">Length of the bytes.</param>
/// <param name="FetchedAt">When it was fetched.</param>
public sealed record CachedAsset(string Url, string FinalUrl, string Sha256, string ContentType, long Size, DateTimeOffset FetchedAt);

/// <summary>A referenced file that could not be cached, and why.</summary>
public sealed record AssetFailure(string Url, string Reason);

/// <summary>Limits on what one application's cache may hold.</summary>
public sealed record AssetCacheLimits
{
    /// <summary>At most this many files.</summary>
    public int MaxFiles { get; init; } = 60;

    /// <summary>A single file larger than this is not kept.</summary>
    public long MaxFileBytes { get; init; } = 15 * 1024 * 1024;

    /// <summary>The whole cache stops growing past this.</summary>
    public long MaxTotalBytes { get; init; } = 80 * 1024 * 1024;

    /// <summary>
    /// How deep module imports and style sheet imports are followed. Some CDNs answer a package
    /// name with a small module that re-exports a pinned build, so each package costs two depths;
    /// the file, request and time limits are what bound a wide graph.
    /// </summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>At most this many requests at once.</summary>
    public int MaxConcurrency { get; init; } = 6;

    /// <summary>At most this many requests at once to one host.</summary>
    public int MaxConcurrencyPerHost { get; init; } = 2;

    /// <summary>
    /// At most this many requests in one fetch, whether they succeed or not — a document naming
    /// many files that fail would otherwise never reach <see cref="MaxFiles"/>.
    /// </summary>
    public int MaxRequests { get; init; } = 120;

    /// <summary>
    /// How long one fetch may spend on the network. What was fetched by then is kept; files not
    /// reached are recorded as failures, and the next fetch picks them up.
    /// </summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// The code an adopted application loads from other hosts, fetched once while online and kept in
/// the application's folder (<c>assets/</c>) so it runs the same offline and under a policy that
/// lets nothing leave the application's origin.
/// </summary>
/// <remarks>
/// Only code is kept — scripts, modules, style sheets and fonts. Data an application fetches is
/// never cached: showing yesterday's answer as if it were today's would be a silent lie. Files are
/// stored under their SHA-256 and listed in <c>assets/index.json</c> with the URL they came from and
/// when, so a person can see exactly what was taken and from where.
/// </remarks>
public sealed class AssetCache
{
    public const string IndexFormat = "bohm.assets/0";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory;
    private readonly Lock _lock = new();
    private Dictionary<string, CachedAsset> _byUrl = new(StringComparer.Ordinal);
    private List<CachedAsset> _assets = [];
    private List<AssetFailure> _failures = [];

    private AssetCache(string directory) => _directory = directory;

    /// <summary>Opens the cache in <paramref name="directory"/>, reading its index if there is one.</summary>
    public static AssetCache Open(string directory)
    {
        var cache = new AssetCache(directory);
        var index = Path.Combine(directory, "index.json");
        if (File.Exists(index))
        {
            try
            {
                var document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllBytes(index), Json);
                if (document?.Format == IndexFormat) cache.Load(document.Assets ?? [], document.Failures ?? []);
            }
            catch (JsonException)
            {
                // An unreadable index caches nothing; the next fetch rewrites it.
            }
        }

        return cache;
    }

    /// <summary>Cached files, in the order they were found.</summary>
    public IReadOnlyList<CachedAsset> Assets
    {
        get { lock (_lock) return _assets.ToList(); }
    }

    /// <summary>Referenced files that could not be cached.</summary>
    public IReadOnlyList<AssetFailure> Failures
    {
        get { lock (_lock) return _failures.ToList(); }
    }

    /// <summary>The cached file for <paramref name="url"/> (as referenced, or as finally fetched), if any.</summary>
    /// <remarks>
    /// Matched by what the address means, not how it is spelled: files are known by their
    /// normalized address, and a request or a document may spell the same one differently
    /// (<c>react@^18</c> for <c>react@%5E18</c>, an upper-case host).
    /// </remarks>
    public CachedAsset? Find(string url)
    {
        lock (_lock) return _byUrl.GetValueOrDefault(url) ?? _byUrl.GetValueOrDefault(Normalized(url));
    }

    /// <summary>
    /// The cached file whose path and query equal <paramref name="pathAndQuery"/>, when exactly one
    /// does. Modules often import their dependencies by absolute path (<c>/npm/x/+esm</c>), which a
    /// module served from this origin resolves against this origin.
    /// </summary>
    public CachedAsset? FindByPath(string pathAndQuery)
    {
        // A request's path arrives decoded (react@^18); cached paths are escaped (react@%5E18).
        var wanted = Uri.TryCreate(PathBase, pathAndQuery, out var resolved) ? resolved.PathAndQuery : pathAndQuery;
        lock (_lock)
        {
            var matches = _assets.Where(a => new Uri(a.FinalUrl).PathAndQuery == wanted || new Uri(a.Url).PathAndQuery == wanted).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }

    private static readonly Uri PathBase = new("http://path.invalid/");

    private static string Normalized(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? new UriBuilder(parsed) { Fragment = "" }.Uri.AbsoluteUri : url;

    /// <summary>Opens a cached file's bytes.</summary>
    public Stream OpenRead(CachedAsset asset) => File.OpenRead(Path.Combine(_directory, asset.Sha256));

    /// <summary>
    /// Fetches everything <paramref name="html"/> refers to on other hosts, following module and
    /// style sheet imports, and replaces the cache with the result. Files already cached are kept
    /// without fetching them again.
    /// </summary>
    /// <remarks>
    /// Imports are followed one depth at a time. The files of one depth are requested together,
    /// within <see cref="AssetCacheLimits.MaxConcurrency"/> and
    /// <see cref="AssetCacheLimits.MaxConcurrencyPerHost"/>, and taken in the order they were found,
    /// so the result does not depend on which answer came first. Once
    /// <see cref="AssetCacheLimits.TimeBudget"/> runs out or <see cref="AssetCacheLimits.MaxRequests"/>
    /// is reached nothing more is requested, but files already in the cache are still followed from
    /// disk — a fetch cut short never loses what an earlier one kept.
    /// </remarks>
    public async Task<(int Cached, int Failed)> FetchAsync(string html, HttpClient http, TimeProvider clock, AssetCacheLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(clock);
        limits ??= new AssetCacheLimits();
        Directory.CreateDirectory(_directory);

        var assets = new List<CachedAsset>();
        var failures = new List<AssetFailure>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        var requests = 0;

        using var budget = new CancellationTokenSource(limits.TimeBudget, clock);
        using var network = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        using var all = new SemaphoreSlim(Math.Max(1, limits.MaxConcurrency));
        var perHost = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var level = AssetScanner.ScanHtml(html).ToList();
            for (var depth = 0; level.Count > 0 && assets.Count < limits.MaxFiles; depth++)
            {
                var next = new List<AssetReference>();
                var pending = new Queue<AssetReference>(level.Where(r => seen.Add(r.Url.AbsoluteUri)));
                while (pending.Count > 0 && assets.Count < limits.MaxFiles)
                {
                    // No more at once than the files still allowed, so a nearly full cache is not
                    // overshot by a whole depth of requests.
                    var batch = new List<AssetReference>();
                    while (pending.Count > 0 && batch.Count < limits.MaxFiles - assets.Count) batch.Add(pending.Dequeue());

                    var results = await Task.WhenAll(batch.Select(ObtainAsync)).ConfigureAwait(false);
                    for (var i = 0; i < batch.Count && assets.Count < limits.MaxFiles; i++)
                    {
                        var reference = batch[i];
                        var (asset, bytes, failure) = results[i];
                        if (asset is null || bytes is null)
                        {
                            failures.Add(new AssetFailure(reference.Url.AbsoluteUri, failure!));
                            continue;
                        }

                        if (total + bytes.Length > limits.MaxTotalBytes)
                        {
                            failures.Add(new AssetFailure(reference.Url.AbsoluteUri, "the application's cache is full"));
                            continue;
                        }

                        var path = Path.Combine(_directory, asset.Sha256);
                        if (!File.Exists(path))
                            await DurableFile.WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);

                        total += bytes.Length;
                        assets.Add(asset);
                        if (depth >= limits.MaxDepth) continue;

                        var text = Encoding.UTF8.GetString(bytes);
                        var finalUrl = new Uri(asset.FinalUrl);
                        next.AddRange(reference.Kind switch
                        {
                            AssetKind.Module => AssetScanner.ScanModule(text, finalUrl),
                            AssetKind.Style => AssetScanner.ScanCss(text, finalUrl),
                            _ => [],
                        });
                    }
                }

                level = next;
            }
        }
        finally
        {
            foreach (var gate in perHost.Values) gate.Dispose();
        }

        await WriteIndexAsync(assets, failures, cancellationToken).ConfigureAwait(false);
        lock (_lock) Load(assets, failures);
        RemoveUnreferencedFiles(assets);
        return (assets.Count, failures.Count);

        async Task<(CachedAsset? Asset, byte[]? Bytes, string? Failure)> ObtainAsync(AssetReference reference)
        {
            var known = Find(reference.Url.AbsoluteUri);
            if (known is not null)
            {
                var knownPath = Path.Combine(_directory, known.Sha256);
                if (File.Exists(knownPath))
                    return (known, await File.ReadAllBytesAsync(knownPath, cancellationToken).ConfigureAwait(false), null);
            }

            if (budget.IsCancellationRequested) return (null, null, BudgetRanOut);
            if (Interlocked.Increment(ref requests) > limits.MaxRequests) return (null, null, "too many requests for one fetch");

            SemaphoreSlim host;
            lock (perHost)
            {
                if (!perHost.TryGetValue(reference.Url.Host, out host!))
                    perHost[reference.Url.Host] = host = new SemaphoreSlim(Math.Max(1, limits.MaxConcurrencyPerHost));
            }

            try
            {
                await all.WaitAsync(network.Token).ConfigureAwait(false);
                try
                {
                    await host.WaitAsync(network.Token).ConfigureAwait(false);
                    try
                    {
                        var (fetched, failure) = await FetchOneAsync(http, reference, clock, limits, network.Token).ConfigureAwait(false);
                        if (fetched is { } found) return (found.Item1, found.Item2, null);
                        return (null, null, budget.IsCancellationRequested ? BudgetRanOut : failure);
                    }
                    finally
                    {
                        host.Release();
                    }
                }
                finally
                {
                    all.Release();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (null, null, BudgetRanOut);
            }
        }
    }

    private const string BudgetRanOut = "the time budget ran out";

    /// <summary>
    /// Whether <paramref name="url"/> may be requested on an application's behalf: HTTPS to a public
    /// name or address. A document is untrusted input — fetching whatever it names would let it make
    /// this computer request addresses on its own network (loopback, private and link-local
    /// ranges, single-label and <c>.local</c>/<c>.internal</c> names). This checks what is written;
    /// a public name that resolves to a private address is not caught here.
    /// </summary>
    public static bool IsPublicHttps(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps) return false;
        var host = url.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address)) return IsPublic(address);
        return host.Contains('.', StringComparison.Ordinal)
            && host != "localhost" && !host.EndsWith(".localhost", StringComparison.Ordinal)
            && !host.EndsWith(".local", StringComparison.Ordinal) && !host.EndsWith(".internal", StringComparison.Ordinal)
            && !host.EndsWith(".lan", StringComparison.Ordinal) && !host.EndsWith(".home.arpa", StringComparison.Ordinal);
    }

    private static bool IsPublic(System.Net.IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast) return false;
        if (address.Equals(System.Net.IPAddress.Any) || address.Equals(System.Net.IPAddress.IPv6Any)) return false;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return true;
        var b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224
            || b[0] == 172 && b[1] >= 16 && b[1] <= 31
            || b[0] == 192 && b[1] == 168
            || b[0] == 169 && b[1] == 254
            || b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    private static async Task<((CachedAsset, byte[])? Asset, string? Failure)> FetchOneAsync(
        HttpClient http, AssetReference reference, TimeProvider clock, AssetCacheLimits limits, CancellationToken cancellationToken)
    {
        if (!IsPublicHttps(reference.Url)) return (null, "not a public https address");
        try
        {
            using var response = await http.GetAsync(reference.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } landed && !IsPublicHttps(landed)) return (null, "redirected to a non-public address");
            if (!response.IsSuccessStatusCode) return (null, $"the server answered {(int)response.StatusCode}");

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            if (!IsCode(contentType, reference.Kind)) return (null, $"not code ({contentType})");
            if (response.Content.Headers.ContentLength > limits.MaxFileBytes) return (null, "too large");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limits.MaxFileBytes) return (null, "too large");
                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            var finalUrl = (response.RequestMessage?.RequestUri ?? reference.Url).AbsoluteUri;
            var asset = new CachedAsset(reference.Url.AbsoluteUri, finalUrl, Convert.ToHexStringLower(SHA256.HashData(bytes)), contentType, bytes.Length, clock.GetUtcNow());
            return ((asset, bytes), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return (null, exception is TaskCanceledException ? "timed out" : "could not be reached");
        }
    }

    /// <summary>
    /// Whether the server's media type is one of the code types this cache keeps. A response that
    /// is really a page (an error page, a login wall) is not kept as code.
    /// </summary>
    private static bool IsCode(string contentType, AssetKind kind)
    {
        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        return kind switch
        {
            AssetKind.Script or AssetKind.Module => type is "application/javascript" or "text/javascript" or "application/x-javascript" or "application/ecmascript" or "text/ecmascript",
            AssetKind.Style => type == "text/css",
            AssetKind.Font => type.StartsWith("font/", StringComparison.Ordinal) || type is "application/font-woff" or "application/font-woff2" or "application/x-font-ttf" or "application/vnd.ms-fontobject" or "application/octet-stream",
            _ => false,
        };
    }

    private void Load(List<CachedAsset> assets, List<AssetFailure> failures)
    {
        _assets = assets;
        _failures = failures;
        _byUrl = new Dictionary<string, CachedAsset>(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            _byUrl.TryAdd(asset.Url, asset);
            _byUrl.TryAdd(asset.FinalUrl, asset);
        }
    }

    private Task WriteIndexAsync(List<CachedAsset> assets, List<AssetFailure> failures, CancellationToken cancellationToken) =>
        DurableFile.WriteAtomicallyAsync(
            Path.Combine(_directory, "index.json"),
            JsonSerializer.SerializeToUtf8Bytes(new IndexDocument(IndexFormat, assets, failures), Json),
            cancellationToken);

    private void RemoveUnreferencedFiles(List<CachedAsset> assets)
    {
        var keep = assets.Select(a => a.Sha256).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(file);
            if (name.Length == 64 && name.All(char.IsAsciiHexDigitLower) && !keep.Contains(name)) File.Delete(file);
        }
    }

    private sealed record IndexDocument(string Format, List<CachedAsset>? Assets, List<AssetFailure>? Failures);
}

/// <summary>Formatting helpers shared by the cache's users.</summary>
public static class AssetUrls
{
    /// <summary>The path, on an application's own origin, that serves the cached copy of <paramref name="url"/>.</summary>
    public static string LocalPath(Uri url) =>
        string.Create(CultureInfo.InvariantCulture, $"/__bohm/asset/{url.Scheme}/{url.Authority}{url.PathAndQuery}");
}
