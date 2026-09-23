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

    /// <summary>How deep module imports and style sheet imports are followed.</summary>
    public int MaxDepth { get; init; } = 4;
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
    public CachedAsset? Find(string url)
    {
        lock (_lock) return _byUrl.GetValueOrDefault(url);
    }

    /// <summary>
    /// The cached file whose path and query equal <paramref name="pathAndQuery"/>, when exactly one
    /// does. Modules often import their dependencies by absolute path (<c>/npm/x/+esm</c>), which a
    /// module served from this origin resolves against this origin.
    /// </summary>
    public CachedAsset? FindByPath(string pathAndQuery)
    {
        lock (_lock)
        {
            var matches = _assets.Where(a => new Uri(a.FinalUrl).PathAndQuery == pathAndQuery || new Uri(a.Url).PathAndQuery == pathAndQuery).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }

    /// <summary>Opens a cached file's bytes.</summary>
    public Stream OpenRead(CachedAsset asset) => File.OpenRead(Path.Combine(_directory, asset.Sha256));

    /// <summary>
    /// Fetches everything <paramref name="html"/> refers to on other hosts, following module and
    /// style sheet imports, and replaces the cache with the result. Files already cached are kept
    /// without fetching them again.
    /// </summary>
    public async Task<(int Cached, int Failed)> FetchAsync(string html, HttpClient http, TimeProvider clock, AssetCacheLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        limits ??= new AssetCacheLimits();
        Directory.CreateDirectory(_directory);

        var assets = new List<CachedAsset>();
        var failures = new List<AssetFailure>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(AssetReference Reference, int Depth)>(AssetScanner.ScanHtml(html).Select(r => (r, 0)));
        long total = 0;

        while (queue.Count > 0 && assets.Count < limits.MaxFiles)
        {
            var (reference, depth) = queue.Dequeue();
            var url = reference.Url.AbsoluteUri;
            if (!seen.Add(url)) continue;

            var known = Find(url);
            byte[] bytes;
            CachedAsset asset;
            if (known is not null && File.Exists(Path.Combine(_directory, known.Sha256)))
            {
                asset = known;
                bytes = await File.ReadAllBytesAsync(Path.Combine(_directory, known.Sha256), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var (fetched, failure) = await FetchOneAsync(http, reference, clock, limits, cancellationToken).ConfigureAwait(false);
                if (fetched is null)
                {
                    failures.Add(new AssetFailure(url, failure!));
                    continue;
                }

                (asset, bytes) = fetched.Value;
                if (total + bytes.Length > limits.MaxTotalBytes)
                {
                    failures.Add(new AssetFailure(url, "the application's cache is full"));
                    continue;
                }

                var path = Path.Combine(_directory, asset.Sha256);
                if (!File.Exists(path))
                    await DurableFile.WriteAtomicallyAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }

            total += bytes.Length;
            assets.Add(asset);
            if (depth >= limits.MaxDepth) continue;

            var text = Encoding.UTF8.GetString(bytes);
            var finalUrl = new Uri(asset.FinalUrl);
            IEnumerable<AssetReference> next = reference.Kind switch
            {
                AssetKind.Module => AssetScanner.ScanModule(text, finalUrl),
                AssetKind.Style => AssetScanner.ScanCss(text, finalUrl),
                _ => [],
            };
            foreach (var child in next) queue.Enqueue((child, depth + 1));
        }

        await WriteIndexAsync(assets, failures, cancellationToken).ConfigureAwait(false);
        lock (_lock) Load(assets, failures);
        RemoveUnreferencedFiles(assets);
        return (assets.Count, failures.Count);
    }

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
