using System.Text.Json;
using Bohm.Runtime.Adoption;
using LocalOrigin.AspNetCore.Previews;
using LocalOrigin.AspNetCore.Storage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Serves a proposed document (<see cref="AppPreviews"/>) from its own origin, the way the
/// application's document is served — same injected script, same policy, the application's data
/// as it is now — so that whatever breaks while it loads shows before anyone takes it in.
/// </summary>
/// <remarks>
/// Nothing the preview does reaches the application: it has another origin (its own browser
/// storage, no session cookie of the application), its writes are acknowledged and dropped, it
/// reads the application's sources as they are,
/// records no use, asks no model (a call to one is declined and noted) and adds nothing to what the application is told it is missing.
/// Load errors and refused requests are kept on the preview, for the caller to read.
/// </remarks>
internal static class PreviewServing
{
    public static async Task ServeAsync(HttpContext context, string token)
    {
        var response = context.Response;
        var previews = context.RequestServices.GetRequiredService<AppPreviews>();
        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        if (previews.Find(token) is not { } preview
            || preview.AppId is { } previewed && await catalog.GetAsync(previewed, context.RequestAborted).ConfigureAwait(false) is not { ArchivedAt: null })
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        AdoptedAppServing.Profile.Apply(response);
        if (AdoptedAppServing.Profile.Refuses(context.Request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var request = context.Request;
        var path = request.Path;
        // A proposed new application has none behind it: no data, no cached code, the rows just read for its sources.
        var app = preview.AppId is { } appId ? await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false) : null;
        if (path == AdoptedAppServing.StoragePath)
        {
            // Acknowledged as applied, so the page goes on as it would, and kept nowhere.
            await context.RequestServices.GetRequiredService<StorageChannel>().DiscardAsync(context).ConfigureAwait(false);
            return;
        }

        if (path == AdoptedAppServing.ProblemsPath)
        {
            if (await context.RequestServices.GetRequiredService<ProblemReports>().ReceiveAsync(context).ConfigureAwait(false) is { } problem)
                ProblemReports.AddTo(preview.Report, problem);
            return;
        }

        if (path == AdoptedAppServing.UsagePath)
        {
            await IgnoreUsageAsync(context).ConfigureAwait(false);
            return;
        }

        if (path.StartsWithSegments("/__bohm/llm"))
        {
            await DeclineModelAsync(context, preview).ConfigureAwait(false);
            return;
        }

        if (path.StartsWithSegments("/__bohm/asset"))
        {
            if (app is null) response.StatusCode = StatusCodes.Status404NotFound;
            else await AssetServing.ServeCachedAsync(context, app).ConfigureAwait(false);
            return;
        }

        if (path.StartsWithSegments(SourcesServing.PathPrefix))
        {
            await (app is null ? SourcesServing.ServeToPreviewAsync(context, preview.Readings) : SourcesServing.ServeToPreviewAsync(context, app)).ConfigureAwait(false);
            return;
        }

        if (path != "/" || !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var channel = context.RequestServices.GetRequiredService<StorageChannel>();
        var page = new ChannelPage("preview", channel.Script("preview", app is null ? new Dictionary<string, string>() : app.Storage.GetItems()));
        var (body, charset) = AdoptedAppServing.Inject(context, preview.Html, page, app, preview.AppId ?? "preview");
        response.Headers.CacheControl = "no-store";
        response.ContentType = $"text/html; charset={charset}";
        response.ContentLength = body.Length;
        if (HttpMethods.IsGet(request.Method))
            await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// A call the application's page would relay to a model. The preview relays none — it would spend
    /// the person's key and send their data for a look — and answers at once, the way a provider
    /// answers a request it cannot serve, so the page goes on as it does when a model is unavailable.
    /// That it asked is kept on the preview: what followed from the answer is not what taking it in would show.
    /// </summary>
    private static async Task DeclineModelAsync(HttpContext context, AppPreviews.Preview preview)
    {
        preview.MarkAskedModel();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("""{"error":{"type":"preview","message":"No model is asked while a revision is previewed."}}""",
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Use the page reports is not use of the application: answered (see <see cref="UsageEndpoint"/> for why with a body) and dropped.</summary>
    private static async Task IgnoreUsageAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !PageRequests.IsFromThePage(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await context.Response.WriteAsync("{}", context.RequestAborted).ConfigureAwait(false);
    }
}
