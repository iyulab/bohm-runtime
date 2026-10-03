using System.Text.Json;
using System.Text.Json.Serialization;
using Bohm.Runtime.Pages;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// The pages people sent to an application, over HTTP. The application reads, on its own origin:
/// <c>GET /__bohm/pages</c> — every page received, oldest first, each <c>{ id, receivedAt, url, title,
/// lang, byline, excerpt }</c> — and <c>GET /__bohm/pages/&lt;id&gt;</c> — one page in full, <c>{ id,
/// receivedAt, url, title, text, html, lang, byline }</c>, where <c>html</c> is <c>null</c> when it did
/// not fit. The application is opened with <c>bohm_page=&lt;id&gt;</c> in its query when a page arrives.
/// The shell hands over the pages through the control API (see <see cref="Control.ControlPlane"/>).
/// </summary>
/// <remarks>
/// The application only reads: a page arrives because the person sent it from the shell, which read
/// it from the web tab. The application cannot ask for a page — it has no way to reach the tabs.
/// </remarks>
internal static class PagesServing
{
    public const string PathPrefix = "/__bohm/pages";

    /// <summary>Serves a read to the application's own page.</summary>
    public static async Task ServeToAppAsync(HttpContext context, string appId, OpenApp app)
    {
        if (!PageRequests.IsFromTheApp(context, appId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await ServeAsync(context, app.Pages).ConfigureAwait(false);
    }

    /// <summary>Serves a read to a preview of the application: its pages as they are, the same way.</summary>
    public static Task ServeToPreviewAsync(HttpContext context, OpenApp app) => ServeAsync(context, app.Pages);

    /// <summary>Serves a read to a preview of a proposed new application, which has received no page.</summary>
    public static Task ServeEmptyAsync(HttpContext context) => ServeAsync(context, null);

    private static async Task ServeAsync(HttpContext context, AppPages? pages)
    {
        var request = context.Request;
        var response = context.Response;
        var cancel = context.RequestAborted;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var rest = request.Path.Value![PathPrefix.Length..].Trim('/');
        response.Headers.CacheControl = "no-store";
        if (rest.Length == 0)
        {
            var list = pages is null ? [] : (await pages.ListAsync(cancel).ConfigureAwait(false)).ToList();
            await response.WriteAsJsonAsync(list, PagesHttpJson.Default.ListReceivedPageSummary, cancellationToken: cancel).ConfigureAwait(false);
            return;
        }

        if (pages is null || await pages.GetAsync(rest, cancel).ConfigureAwait(false) is not { } page)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await response.WriteAsJsonAsync(page, PagesHttpJson.Default.ReceivedPage, cancellationToken: cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// The control API's <c>POST /apps/{id}/pages</c>, once the application is known and open: keeps a page
    /// the person sent — <c>{ url, title, text, html, lang, byline }</c> — and answers <c>{ page, withoutHtml }</c>
    /// with the page's summary. A missing address or text is 400; text larger than a page may be is 413.
    /// </summary>
    public static async Task HandleControlAsync(HttpContext context, OpenApp app, string[] rest)
    {
        var request = context.Request;
        var response = context.Response;
        var cancel = context.RequestAborted;
        if (request.Method != "POST" || rest.Length != 0)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        PageRequest? sent;
        try
        {
            sent = await JsonSerializer.DeserializeAsync(request.Body, PagesHttpJson.Default.PageRequest, cancel).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            sent = null;
        }

        if (sent is not { Url: { } url, Text: { } text })
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var (outcome, page) = await app.Pages.ReceiveAsync(url, sent.Title, text, sent.Html, sent.Lang, sent.Byline, cancel).ConfigureAwait(false);
        switch (outcome)
        {
            case ReceiveOutcome.Received or ReceiveOutcome.ReceivedWithoutHtml:
                await response.WriteAsJsonAsync(new PageReceived(AppPages.SummaryOf(page!), outcome == ReceiveOutcome.ReceivedWithoutHtml), PagesHttpJson.Default.PageReceived, cancellationToken: cancel).ConfigureAwait(false);
                return;
            case ReceiveOutcome.TooLarge:
                response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            default:
                response.StatusCode = StatusCodes.Status400BadRequest;
                return;
        }
    }

    /// <summary>A page the shell read from a web tab and the person sent.</summary>
    internal sealed record PageRequest(string? Url, string? Title, string? Text, string? Html, string? Lang, string? Byline);

    /// <summary>The page as kept, and whether its HTML was left out for size.</summary>
    internal sealed record PageReceived(ReceivedPageSummary Page, bool WithoutHtml);

    /// <summary>An application that receives shared pages, and the query parameters its manifest names for them.</summary>
    internal sealed record PageTarget(string Id, ShareTarget Params);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReceivedPage))]
[JsonSerializable(typeof(List<ReceivedPageSummary>))]
[JsonSerializable(typeof(PagesServing.PageRequest))]
[JsonSerializable(typeof(PagesServing.PageReceived))]
[JsonSerializable(typeof(List<PagesServing.PageTarget>))]
internal sealed partial class PagesHttpJson : JsonSerializerContext;
