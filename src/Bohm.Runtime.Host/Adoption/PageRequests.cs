namespace Bohm.Runtime.Host.Adoption;

/// <summary>Checks shared by every request the injected script makes back to the runtime.</summary>
internal static class PageRequests
{
    /// <summary>
    /// Whether the request carries the custom header only the page's own script sends, and — when
    /// the browser names an origin — that origin is this application's. A page on another origin
    /// cannot add the header without a CORS preflight, which is never granted.
    /// </summary>
    public static bool IsFromThePage(HttpRequest request)
    {
        if (request.Headers[AdoptedAppServing.RequestHeader] != "1") return false;
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tab named by the request, if it and the session cookie both belong to <paramref name="appId"/>.</summary>
    public static AppSessions.Tab? Tab(HttpContext context, string appId, string? tabId) =>
        context.RequestServices.GetRequiredService<AppSessions>()
            .Authorize(appId, context.Request.Cookies[AdoptedAppServing.SessionCookie], tabId);
}
