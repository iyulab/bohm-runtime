using System.Text.Json;
using Bohm.Runtime.Adoption;

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
            await DropWritesAsync(context).ConfigureAwait(false);
            return;
        }

        if (path == AdoptedAppServing.UsagePath)
        {
            await CollectReportAsync(context, preview).ConfigureAwait(false);
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

        var (body, charset) = app is null
            ? AdoptedAppServing.InjectShim(context, preview.Html, "preview", "preview")
            : AdoptedAppServing.InjectShim(context, preview.Html, app, preview.AppId!, "preview");
        response.Headers.CacheControl = "no-store";
        response.ContentType = $"text/html; charset={charset}";
        response.ContentLength = body.Length;
        if (HttpMethods.IsGet(request.Method))
            await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Acknowledges every write as applied, so the page goes on as it would, and keeps none.</summary>
    private static async Task DropWritesAsync(HttpContext context)
    {
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method) || !PageRequests.IsFromThePage(request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        long ack = 0;
        try
        {
            using var batch = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            if (batch.RootElement.TryGetProperty("ops", out var ops) && ops.ValueKind == JsonValueKind.Array)
                foreach (var op in ops.EnumerateArray())
                    if (op.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var n)) ack = Math.Max(ack, n);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await context.Response.WriteAsync($$"""{"ack":{{ack}}}""", context.RequestAborted).ConfigureAwait(false);
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

    private static async Task CollectReportAsync(HttpContext context, AppPreviews.Preview preview)
    {
        var request = context.Request;
        if (!HttpMethods.IsPost(request.Method) || !PageRequests.IsFromThePage(request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        UsageEndpoint.Report? report;
        try
        {
            report = await JsonSerializer.DeserializeAsync(request.Body, UsageJson.Default.Report, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            report = null;
        }

        switch (report)
        {
            case { Kind: "load-error", Message: { Length: > 0 } message }:
                preview.AddError(message.Length > 500 ? message[..500] : message);
                break;
            case { Kind: "blocked", Category: "library" or "data" or "form", Host: { Length: > 0 and <= 255 } host }:
                preview.AddBlocked(report.Category!, host);
                break;
        }
        // Same answer as the application's own endpoint (see there for why not 204).
        await context.Response.WriteAsync("{}", context.RequestAborted).ConfigureAwait(false);
    }
}
