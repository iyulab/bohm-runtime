using System.Text;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// What an application's origin answers when the application cannot be served. A document request
/// gets an empty page that names the reason in <c>&lt;meta name="bohm-unavailable"&gt;</c>
/// (<c>archived</c> or <c>cannot-open</c>); the browser that shows it writes the words in the
/// language its screen speaks, so the runtime holds no sentences. Any other request gets the status alone.
/// </summary>
internal static class UnavailablePage
{
    public enum Reason
    {
        /// <summary>The application is put away (404) — it is served again once restored.</summary>
        Archived,

        /// <summary>
        /// Its files could not be opened right now (503) — for example files kept only in the cloud
        /// while there is no connection. Nothing was changed; a later request tries again.
        /// </summary>
        CannotOpen,
    }

    private const string Policy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'";

    public static async Task WriteAsync(HttpContext context, Reason reason)
    {
        var response = context.Response;
        response.StatusCode = reason == Reason.Archived ? StatusCodes.Status404NotFound : StatusCodes.Status503ServiceUnavailable;
        response.Headers.CacheControl = "no-store";
        if (reason == Reason.CannotOpen) response.Headers.RetryAfter = "30";
        var request = context.Request;
        var document = request.Path == "/" && (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method));
        if (!document) return;

        var code = reason == Reason.Archived ? "archived" : "cannot-open";
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><meta charset=\"utf-8\"><meta name=\"bohm-unavailable\" content=\"{code}\"><title></title>" +
            "<body style=\"font: 15px/1.6 system-ui, sans-serif; max-width: 560px; margin: 64px auto; padding: 0 24px; color: #222\">" +
            "<h1 style=\"font-size: 20px\"></h1><p></p></body></html>");
        response.Headers.ContentSecurityPolicy = Policy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = body.Length;
        if (HttpMethods.IsGet(request.Method)) await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}
