using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Assets;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Serves an application's cached code from the application's own origin, so it loads under a
/// policy that lets nothing reach another origin — and loads the same offline.
/// </summary>
/// <remarks>
/// The stored document is never changed. The served copy points each cached URL at its local path
/// (<c>/__bohm/asset/https/cdn.example/lib.js</c>), and an import map sends module imports from a
/// cached host there too. The same rule applies to every application; it is environment, like the
/// injected compatibility script.
/// </remarks>
internal static partial class AssetServing
{
    public const string PathPrefix = "/__bohm/asset/";

    /// <summary>Serves <c>/__bohm/asset/&lt;scheme&gt;/&lt;authority&gt;/&lt;path&gt;</c>, or answers 404.</summary>
    public static async Task ServeCachedAsync(HttpContext context, OpenApp app)
    {
        var rest = context.Request.Path.Value![PathPrefix.Length..];
        var parts = rest.Split('/', 3);
        CachedAsset? asset = null;
        if (parts.Length == 3 && parts[0] is "http" or "https")
            asset = app.Assets.Find($"{parts[0]}://{parts[1]}/{parts[2]}{context.Request.QueryString}");
        if (asset is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await WriteAsync(context, app, asset).ConfigureAwait(false);
    }

    /// <summary>
    /// For a path the document did not have (<c>/npm/dep/+esm</c>): serves the cached file with that
    /// path, when exactly one exists. Returns whether it did.
    /// </summary>
    public static async Task<bool> TryServeByPathAsync(HttpContext context, OpenApp app)
    {
        var asset = app.Assets.FindByPath(context.Request.Path.Value + context.Request.QueryString);
        if (asset is null) return false;
        await WriteAsync(context, app, asset).ConfigureAwait(false);
        return true;
    }

    private static async Task WriteAsync(HttpContext context, OpenApp app, CachedAsset asset)
    {
        var response = context.Response;
        response.ContentType = asset.ContentType;
        response.ContentLength = asset.Size;
        // Content-addressed and immutable for the life of this cache entry.
        response.Headers.CacheControl = "private, max-age=31536000, immutable";
        await using var stream = app.Assets.OpenRead(asset);
        await stream.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// The served copy of a document: every cached URL written in it points at its local path.
    /// Returns the document unchanged when nothing is cached or it is not ASCII-compatible.
    /// </summary>
    public static byte[] PointAtCache(byte[] document, AssetCache cache)
    {
        var assets = cache.Assets;
        if (assets.Count == 0 || document.Length >= 2 && (document[0] is 0xFF or 0xFE)) return document;

        // Latin-1 maps bytes to characters one to one, so replacing ASCII URLs is byte-exact.
        var text = Encoding.Latin1.GetString(document);
        var changed = false;
        foreach (var url in assets.Select(a => a.Url).Distinct(StringComparer.Ordinal).OrderByDescending(u => u.Length))
        {
            var local = AssetUrls.LocalPath(new Uri(url));
            foreach (var written in new[] { url, url.Replace("&", "&amp;", StringComparison.Ordinal) }.Distinct(StringComparer.Ordinal))
            {
                if (!text.Contains(written, StringComparison.Ordinal)) continue;
                text = text.Replace(written, local, StringComparison.Ordinal);
                changed = true;
            }
        }

        return changed ? Encoding.Latin1.GetBytes(text) : document;
    }

    /// <summary>
    /// An import map sending every cached host's modules to their local paths, so an import a
    /// cached module makes — or one built at run time — finds the cached copy. Empty when nothing is cached.
    /// </summary>
    public static string ImportMap(AssetCache cache)
    {
        var hosts = cache.Assets
            .SelectMany(a => new[] { new Uri(a.Url), new Uri(a.FinalUrl) })
            .Select(u => (Prefix: $"{u.Scheme}://{u.Authority}/", Local: $"{PathPrefix}{u.Scheme}/{u.Authority}/"))
            .Distinct()
            .ToDictionary(h => h.Prefix, h => h.Local, StringComparer.Ordinal);
        if (hosts.Count == 0) return "";
        // The default encoder escapes '<', '>' and '&', so the map cannot close its script element.
        return "<script type=\"importmap\">" + JsonSerializer.Serialize(new ImportMapDocument(hosts), AssetJson.Default.ImportMapDocument) + "</script>";
    }

    internal sealed record ImportMapDocument(IReadOnlyDictionary<string, string> Imports);
}

/// <summary>Fetches applications' code, in the foreground or in the background after adoption.</summary>
/// <remarks>
/// Background fetches are owned by the host: stopping it cancels them, and disposing waits for them,
/// so none is left writing into an application's folder — or opening its storage — after the host is gone.
/// </remarks>
internal sealed partial class AssetFetcher(IServiceProvider services, IHostApplicationLifetime lifetime, ILogger<AssetFetcher> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Task, byte> _background = new();

    /// <summary>Starts fetching in the background; failures are logged, never thrown.</summary>
    public void Start(string appId)
    {
        var stopping = lifetime.ApplicationStopping;
        var task = Task.Run(async () =>
        {
            try
            {
                var (cached, failed) = await FetchAsync(appId, stopping).ConfigureAwait(false);
                LogFetched(logger, appId, cached, failed);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // The host is stopping; the next adoption or an explicit fetch tries again.
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
            {
                LogFetchFailed(logger, exception, appId);
            }
        }, CancellationToken.None);
        _background.TryAdd(task, 0);
        task.ContinueWith(t => _background.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Waits for background fetches, which the host's stopping has already cancelled.</summary>
    public async ValueTask DisposeAsync() => await Task.WhenAll(_background.Keys).ConfigureAwait(false);

    /// <summary>Fetches the code an application refers to. One fetch at a time per application.</summary>
    public async Task<(int Cached, int Failed)> FetchAsync(string appId, CancellationToken cancellationToken)
    {
        var app = await services.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        var html = await services.GetRequiredService<AdoptionCatalog>().ReadHtmlAsync(appId, cancellationToken).ConfigureAwait(false);
        var http = services.GetRequiredService<IHttpClientFactory>().CreateClient("assets");
        var clock = services.GetRequiredService<TimeProvider>();
        await app.AssetFetch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var started = clock.GetUtcNow();
            var result = await app.Assets.FetchAsync(Encoding.UTF8.GetString(html), http, clock, cancellationToken: cancellationToken).ConfigureAwait(false);
            var egress = services.GetRequiredService<Egress>();
            foreach (var host in app.Assets.Assets.Where(a => a.FetchedAt >= started).GroupBy(a => new Uri(a.Url).Host, StringComparer.OrdinalIgnoreCase))
                egress.Fetched(host.Key, host.Count());
            return result;
        }
        finally
        {
            app.AssetFetch.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cached code for {AppId}: {Cached} file(s), {Failed} not cached.")]
    private static partial void LogFetched(ILogger logger, string appId, int cached, int failed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Caching code for {AppId} failed.")]
    private static partial void LogFetchFailed(ILogger logger, Exception exception, string appId);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AssetServing.ImportMapDocument))]
internal sealed partial class AssetJson : System.Text.Json.Serialization.JsonSerializerContext;
