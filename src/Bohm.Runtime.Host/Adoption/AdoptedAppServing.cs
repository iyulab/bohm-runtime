using System.Reflection;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Usage;
using LocalOrigin.AspNetCore;
using LocalOrigin.AspNetCore.Previews;
using LocalOrigin.AspNetCore.Storage;

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
    public const string ProblemsPath = "/__bohm/problems";

    /// <summary>
    /// The storage channel's names on the wire, kept from earlier versions (the shell reads <c>window.__bohm</c>
    /// before it closes a page), and what a write is recorded as.
    /// </summary>
    public static readonly StorageChannelOptions ChannelOptions = new()
    {
        Path = StoragePath,
        RequestHeader = RequestHeader,
        SessionCookie = SessionCookie,
        HandleName = "__bohm",
        Applied = async (context, appId, _) =>
            (await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false)).Usage.Record(UsageSignal.Wrote),
        // A page whose code was replaced while it was still writing: the write is refused (the new code owns the
        // data now) but it was a write the person made, so it is counted where a host's unconfirmed close is.
        RefusedFromRetiredTab = async (context, appId, _) =>
            (await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false)).Usage.RecordLossSuspected(),
    };

    /// <summary>Where a page reports what stopped it.</summary>
    public static readonly ProblemReportOptions ProblemOptions = new() { Path = ProblemsPath, RequestHeader = RequestHeader };

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
        var channel = context.RequestServices.GetRequiredService<StorageChannel>();
        if (path == StoragePath)
        {
            var openApps = context.RequestServices.GetRequiredService<OpenApps>();
            using var inProgress = context.RequestServices.GetRequiredService<Activity>().Begin();
            await channel.HandleAsync(context, appId, async _ => (await openApps.GetAsync(appId).ConfigureAwait(false)).Storage).ConfigureAwait(false);
            return;
        }

        if (path == ProblemsPath)
        {
            await ReceiveProblemAsync(context, appId).ConfigureAwait(false);
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

        var page = channel.Open(context, appId, app.Storage.GetItems());
        var (body, charset) = Inject(context, html, page, app, appId);

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
    /// A problem the page reported (an error while loading, or something the policy refused), kept on the
    /// application to tell the person, when it comes from a page of this application.
    /// </summary>
    private static async Task ReceiveProblemAsync(HttpContext context, string appId)
    {
        var problem = await context.RequestServices.GetRequiredService<ProblemReports>().ReceiveAsync(context).ConfigureAwait(false);
        if (problem is null || context.RequestServices.GetRequiredService<StorageChannel>().TabOf(context, appId, problem.Tab) is null) return;
        var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        switch (problem)
        {
            case PageBlocked { Blocked: var blocked }:
                app.AddBlocked(blocked.Category.ToString().ToLowerInvariant(), blocked.Host);
                context.RequestServices.GetRequiredService<Egress>().Blocked(appId, blocked.Host);
                break;
            case PageLoadError error:
                app.AddLoadError(error.Message);
                break;
        }
    }

    /// <summary>
    /// <paramref name="html"/> with the injected scripts in front: the storage channel's (seeded with the data as
    /// it is now), the runtime's own, and problem reporting. The one place they are put together, so a page served
    /// for a look (<see cref="PreviewServing"/>) routes the same calls the application's own page would. With no
    /// <paramref name="app"/> (a proposed new application) there is no cached code to point at.
    /// </summary>
    internal static InjectedDocument Inject(HttpContext context, byte[] html, ChannelPage page, OpenApp? app, string appId)
    {
        var problems = context.RequestServices.GetRequiredService<ProblemReports>();
        var before = (app is null ? "" : AssetServing.ImportMap(app.Assets)) + "<script>" + page.Script + "</script><script>" + RuntimeScript(context, appId, page.Tab) + "</script>";
        // Reported lines count every line the injected markup adds before the document's own first line.
        string Markup(int lineOffset) => before + "<script>" + problems.Script(page.Tab, lineOffset) + "</script>";
        var markup = Markup(Markup(0).Count(c => c == '\n'));
        return DocumentInjector.Inject(app is null ? html : AssetServing.PointAtCache(html, app.Assets), markup);
    }

    private static string RuntimeScript(HttpContext context, string appId, string tab)
    {
        var boot = JsonSerializer.Serialize(new Boot(tab, Llm.LlmProviders.Placeholder(appId),
            Llm.LlmProviders.All.Select(p => p.Host).ToList(),
            context.RequestServices.GetRequiredService<Llm.CompanyModel>().Current?.Endpoint.AbsoluteUri), BootJson.Default.Boot);
        return ShimTemplate.Value.Replace("__BOHM_BOOT__", boot, StringComparison.Ordinal);
    }

    /// <param name="CompanyBase">The organization's model server's base address, when one is set: an application written for it calls it directly.</param>
    internal sealed record Boot(string Tab, string LlmPlaceholder, IReadOnlyList<string> LlmHosts, string? CompanyBase);
}

/// <summary>
/// Serialization of the boot data. The default encoder escapes every non-ASCII character and the
/// HTML-sensitive ones (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, quotes), so stored values can never close
/// the surrounding script element and the result is ASCII.
/// </summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AdoptedAppServing.Boot))]
internal sealed partial class BootJson : System.Text.Json.Serialization.JsonSerializerContext;
