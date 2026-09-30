namespace Bohm.Runtime.Host.Adoption;

/// <summary>Checks shared by every request the injected script makes back to the runtime.</summary>
internal static class PageRequests
{
    /// <summary>
    /// Whether the request carries the custom header only the page's own script sends, and — when
    /// the browser names an origin — that origin is this application's. A page on another origin
    /// cannot add the header without a CORS preflight, which is never granted.
    /// </summary>
    public static bool IsFromThePage(HttpRequest request) =>
        request.Headers[AdoptedAppServing.RequestHeader] == "1" && SameOriginOrAbsent(request);

    /// <summary>
    /// Whether the request comes from a page of <paramref name="appId"/> — its session cookie, and its
    /// origin when the browser names one — for requests the application's own code makes with a plain
    /// <c>fetch</c>, which cannot carry the script's custom header.
    /// </summary>
    public static bool IsFromTheApp(HttpContext context, string appId) =>
        context.RequestServices.GetRequiredService<AppSessions>().IsSession(appId, context.Request.Cookies[AdoptedAppServing.SessionCookie])
        && SameOriginOrAbsent(context.Request);

    private static bool SameOriginOrAbsent(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tab named by the request, if it and the session cookie both belong to <paramref name="appId"/>.</summary>
    public static AppSessions.Tab? Tab(HttpContext context, string appId, string? tabId) =>
        context.RequestServices.GetRequiredService<AppSessions>()
            .Authorize(appId, context.Request.Cookies[AdoptedAppServing.SessionCookie], tabId);
}
