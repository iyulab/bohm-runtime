using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// What an application's origin answers when the application cannot be served. A document request
/// gets a short page in the person's language (Korean or English, from <c>Accept-Language</c>), so the
/// tab says why and that nothing was deleted — instead of the browser's own error page. Any other
/// request gets the status alone.
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

    /// <summary>Escapes markup but keeps Korean as characters (the default encoder turns every non-Latin letter into an entity).</summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

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

        var korean = PrefersKorean(request);
        var (title, text) = (reason, korean) switch
        {
            (Reason.Archived, true) => ("보관한 앱입니다", "이 앱은 보관돼 있습니다. 앱과 데이터는 그대로 있고, 되살리면 다시 열립니다."),
            (Reason.Archived, false) => ("This app is archived", "It has been put away. The app and its data are kept; restore it to open it again."),
            (_, true) => ("지금 열 수 없습니다", "이 앱의 파일을 지금 열 수 없습니다. 지운 것은 없습니다. 데이터 폴더가 클라우드 동기화 폴더 안이라면 파일이 클라우드에만 있을 수 있습니다 — 연결된 뒤 다시 여세요."),
            (_, false) => ("This app cannot be opened right now", "Its files could not be opened right now. Nothing was deleted. If the data folder is inside a cloud-synced folder, the files may be only in the cloud — open it again once connected."),
        };
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html lang=\"{(korean ? "ko" : "en")}\"><meta charset=\"utf-8\"><title>{Encoder.Encode(title)}</title>" +
            "<body style=\"font: 15px/1.6 system-ui, sans-serif; max-width: 560px; margin: 64px auto; padding: 0 24px; color: #222\">" +
            $"<h1 style=\"font-size: 20px\">{Encoder.Encode(title)}</h1><p>{Encoder.Encode(text)}</p></body></html>");
        response.Headers.ContentSecurityPolicy = Policy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = body.Length;
        if (HttpMethods.IsGet(request.Method)) await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Whether the language the request prefers most is Korean.</summary>
    private static bool PrefersKorean(HttpRequest request) =>
        request.GetTypedHeaders().AcceptLanguage
            .OrderByDescending(l => l.Quality ?? 1)
            .FirstOrDefault()?.Value.Value?.StartsWith("ko", StringComparison.OrdinalIgnoreCase) == true;
}
