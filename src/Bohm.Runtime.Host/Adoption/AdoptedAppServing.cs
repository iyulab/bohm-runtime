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
    /// script, style sheet or form submission — may reach another origin. Nor may another origin
    /// embed the application: a page elsewhere (any browser on this computer can reach the loopback
    /// address) could otherwise frame it — to trick clicks inside it, or to make it look used.
    /// </summary>
    public const string ContentSecurityPolicy = "default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; form-action 'self'; frame-ancestors 'self'";

    /// <summary>
    /// Headers every response from an application's origin carries, next to the policy above.
    /// <list type="bullet">
    /// <item>No <c>Referer</c> leaves the application: following a link out would otherwise tell the
    /// other site the application's local address.</item>
    /// <item>Only the application's own origin may use its responses. The document carries a
    /// snapshot of the data inline, and a page elsewhere — another application, or a page in any
    /// browser on this computer — could otherwise pull it in as an image or script it cannot read
    /// but can still make the browser load.</item>
    /// </list>
    /// No <c>Cross-Origin-Opener-Policy</c>: with <c>same-origin</c>, leaving the page for another
    /// document swaps its browsing context group, and the writes a page sends as it closes (in
    /// <c>pagehide</c>) were lost — every time, in the embedded browser. Keeping those writes comes first.
    /// </summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> IsolationHeaders =
    [
        new("Referrer-Policy", "no-referrer"),
        new("Cross-Origin-Resource-Policy", "same-origin"),
    ];

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
        // An archived application is put away: nothing at its origin is served until it is restored.
        if (await catalog.GetAsync(appId, context.RequestAborted).ConfigureAwait(false) is not { ArchivedAt: null })
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var response = context.Response;
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        foreach (var (name, value) in IsolationHeaders) response.Headers[name] = value;

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
