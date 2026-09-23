using System.Reflection;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Serves each adopted application from its own origin, <c>http://&lt;id&gt;.localhost:&lt;port&gt;/</c>.
/// A separate origin per application is what keeps one application's scripts away from another's
/// data; the content security policy is what keeps them from sending that data anywhere else.
/// </summary>
internal static class AdoptedAppServing
{
    public const string SessionCookie = "bohm_session";
    public const string RequestHeader = "X-Bohm-Request";
    public const string StoragePath = "/__bohm/storage";
    public const string UsagePath = "/__bohm/usage";

    /// <summary>
    /// Everything loads from the application's own origin; inline script and style are allowed
    /// because adopted documents are single files that rely on them. Nothing — no fetch, image,
    /// script, style sheet or form submission — may reach another origin.
    /// </summary>
    public const string ContentSecurityPolicy = "default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; form-action 'self'";

    private const string LocalhostSuffix = ".localhost";

    private static readonly Lazy<string> ShimTemplate = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bohm.Runtime.Host.shim.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<int> ShimLineCount = new(() => ShimTemplate.Value.Count(c => c == '\n'));

    /// <summary>The application a request is addressed to, from its <c>Host</c> header.</summary>
    public static string? AppIdOf(HttpRequest request)
    {
        var host = request.Host.Host;
        if (!host.EndsWith(LocalhostSuffix, StringComparison.OrdinalIgnoreCase)) return null;
        var label = host[..^LocalhostSuffix.Length].ToLowerInvariant();
        return AdoptionCatalog.IsValidId(label) ? label : null;
    }

    public static async Task ServeAsync(HttpContext context, string appId)
    {
        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        if (await catalog.GetAsync(appId, context.RequestAborted).ConfigureAwait(false) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var response = context.Response;
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";

        var path = context.Request.Path;
        if (path == StoragePath)
        {
            await StorageEndpoint.HandleAsync(context, appId).ConfigureAwait(false);
            return;
        }

        if (path == UsagePath)
        {
            await UsageEndpoint.HandleAsync(context, appId).ConfigureAwait(false);
            return;
        }

        if (path.StartsWithSegments("/__bohm/asset"))
        {
            var assetApp = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
            await AssetServing.ServeCachedAsync(context, assetApp).ConfigureAwait(false);
            return;
        }

        if (path.StartsWithSegments("/__bohm/llm"))
        {
            var llmApp = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
            await Llm.LlmProxy.HandleAsync(context, appId, llmApp).ConfigureAwait(false);
            return;
        }

        if (path != "/" || !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            // An adopted application is one file. A page that asks for files next to itself (as it
            // could on the site it came from) gets nothing, and the person can be told which.
            // (The browser's own favicon request is not the page asking for anything.)
            if (HttpMethods.IsGet(context.Request.Method) && !path.StartsWithSegments("/__bohm") && path != "/favicon.ico")
            {
                var pathApp = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
                if (await AssetServing.TryServeByPathAsync(context, pathApp).ConfigureAwait(false)) return;
                pathApp.AddMissingFile(path.Value!);
            }
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var sessions = context.RequestServices.GetRequiredService<AppSessions>();
        var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        var html = await catalog.ReadHtmlAsync(appId, context.RequestAborted).ConfigureAwait(false);

        var boot = JsonSerializer.Serialize(new Boot(sessions.IssueTab(appId), app.Storage.GetItems(), Llm.LlmProviders.Placeholder(appId),
            Llm.LlmProviders.All.Select(p => p.Host).ToList(), ShimLineCount.Value), BootJson.Default.Boot);
        var (body, charset) = ShimInjector.Inject(AssetServing.PointAtCache(html, app.Assets),
            ShimTemplate.Value.Replace("__BOHM_BOOT__", boot, StringComparison.Ordinal), before: AssetServing.ImportMap(app.Assets));

        response.Cookies.Append(SessionCookie, sessions.IssueSession(appId), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
        });
        // The document carries a snapshot of the data inline; a cached copy would be a stale one.
        response.Headers.CacheControl = "no-store";
        response.ContentType = $"text/html; charset={charset}";
        response.ContentLength = body.Length;
        if (HttpMethods.IsGet(context.Request.Method))
        {
            app.Usage.Record(UsageSignal.Opened);
            await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <param name="LineOffset">Lines the injected script adds before the document's own first line.</param>
    internal sealed record Boot(string Tab, IReadOnlyDictionary<string, string> Items, string LlmPlaceholder, IReadOnlyList<string> LlmHosts, int LineOffset);
}

/// <summary>
/// Serialization of the boot data. The default encoder escapes every non-ASCII character and the
/// HTML-sensitive ones (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, quotes), so stored values can never close
/// the surrounding script element and the result is ASCII.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AdoptedAppServing.Boot))]
internal sealed partial class BootJson : System.Text.Json.Serialization.JsonSerializerContext;
