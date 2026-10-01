using System.Reflection;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Usage;
using LocalOrigin.AspNetCore;

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
    /// The headers of every response from an application's origin, and the requests it refuses: everything
    /// loads from the application's own origin (inline script and style allowed — adopted documents are single
    /// files that rely on them), nothing reaches another origin, no other origin may embed or pull in the
    /// application, and no <c>Referer</c> leaves it. Requests another site's page makes — another application
    /// included — are refused. See <see cref="OriginSecurityProfile"/>.
    /// </summary>
    public static readonly OriginSecurityProfile Profile = new();

    private const string LocalhostSuffix = ".localhost";

    internal static readonly Lazy<string> ShimTemplate = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bohm.Runtime.Host.shim.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    internal static readonly Lazy<int> ShimLineCount = new(() => ShimTemplate.Value.Count(c => c == '\n'));

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
        AdoptedApp? record;
        try
        {
            record = await catalog.GetAsync(appId, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await UnavailablePage.WriteAsync(context, UnavailablePage.Reason.CannotOpen).ConfigureAwait(false);
            return;
        }

        if (record is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // An archived application is put away: nothing at its origin is served until it is restored.
        if (record.ArchivedAt is not null)
        {
            await UnavailablePage.WriteAsync(context, UnavailablePage.Reason.Archived).ConfigureAwait(false);
            return;
        }

        var response = context.Response;
        Profile.Apply(response);
        if (Profile.Refuses(context.Request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

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

        if (path.StartsWithSegments(SourcesServing.PathPrefix))
        {
            var sourcesApp = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
            await SourcesServing.ServeToAppAsync(context, appId, sourcesApp).ConfigureAwait(false);
            return;
        }

        if (path != "/" || !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            // An adopted application is one file. A page that asks for files next to itself (as it
            // could on the site it came from) gets nothing, and the person can be told which. A
            // call to a server there — fetch or XMLHttpRequest (Sec-Fetch-Dest: empty), or any
            // method but GET and HEAD — is told apart from a file, with its method.
            // (The browser's own favicon request is not the page asking for anything.)
            var method = context.Request.Method;
            if (!path.StartsWithSegments("/__bohm") && path != "/favicon.ico")
            {
                var pathApp = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
                var get = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
                if (get && await AssetServing.TryServeByPathAsync(context, pathApp).ConfigureAwait(false)) return;
                if (!get || context.Request.Headers["Sec-Fetch-Dest"] == "empty") pathApp.AddMissingApi(method, path.Value!);
                else pathApp.AddMissingFile(path.Value!);
            }
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var sessions = context.RequestServices.GetRequiredService<AppSessions>();
        OpenApp app;
        byte[] html;
        try
        {
            app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
            html = await catalog.ReadHtmlAsync(appId, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The headers set above are for the application's page; the notice sets its own.
            response.Headers.Remove("Content-Security-Policy");
            await UnavailablePage.WriteAsync(context, UnavailablePage.Reason.CannotOpen).ConfigureAwait(false);
            return;
        }

        var (body, charset) = InjectShim(context, html, app, appId, sessions.IssueTab(appId));

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
            // An unsaved result opened again is no longer left: its retention starts over when it is next left.
            if (record.Unsaved && record.LeftAt is not null)
                await catalog.SetLeftAsync(appId, left: false, context.RequestAborted).ConfigureAwait(false);
            await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <paramref name="html"/> with the injected script in front, booted with <paramref name="appId"/>'s
    /// data as it is now. The one place the boot is put together, so a page served for a look
    /// (<see cref="PreviewServing"/>) routes the same calls the application's own page would.
    /// </summary>
    internal static InjectedDocument InjectShim(HttpContext context, byte[] html, OpenApp app, string appId, string tab)
    {
        var boot = BootScript(context, app.Storage.GetItems(), appId, tab);
        return DocumentInjector.Inject(AssetServing.PointAtCache(html, app.Assets), AssetServing.ImportMap(app.Assets) + "<script>" + boot + "</script>");
    }

    /// <summary>The injected script for a document with no application behind it yet — no data, no cached code.</summary>
    internal static InjectedDocument InjectShim(HttpContext context, byte[] html, string placeholderId, string tab) =>
        DocumentInjector.Inject(html, "<script>" + BootScript(context, new Dictionary<string, string>(), placeholderId, tab) + "</script>");

    private static string BootScript(HttpContext context, IReadOnlyDictionary<string, string> items, string appId, string tab)
    {
        var boot = JsonSerializer.Serialize(new Boot(tab, items, Llm.LlmProviders.Placeholder(appId),
            Llm.LlmProviders.All.Select(p => p.Host).ToList(), ShimLineCount.Value,
            context.RequestServices.GetRequiredService<Llm.CompanyModel>().Current?.Endpoint.AbsoluteUri), BootJson.Default.Boot);
        return ShimTemplate.Value.Replace("__BOHM_BOOT__", boot, StringComparison.Ordinal);
    }

    /// <param name="LineOffset">Lines the injected script adds before the document's own first line.</param>
    /// <param name="CompanyBase">The organization's model server's base address, when one is set — an application written for it calls it directly.</param>
    internal sealed record Boot(string Tab, IReadOnlyDictionary<string, string> Items, string LlmPlaceholder, IReadOnlyList<string> LlmHosts, int LineOffset, string? CompanyBase);
}

/// <summary>
/// Serialization of the boot data. The default encoder escapes every non-ASCII character and the
/// HTML-sensitive ones (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, quotes), so stored values can never close
/// the surrounding script element and the result is ASCII.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AdoptedAppServing.Boot))]
internal sealed partial class BootJson : System.Text.Json.Serialization.JsonSerializerContext;
