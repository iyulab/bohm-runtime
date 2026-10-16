using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Bohm.Runtime.Host.Control;

/// <summary>
/// Errors on the control surface say what went wrong and what to do next, in one shape: an RFC 9457
/// problem (<c>application/problem+json</c>) with <c>status</c>, <c>title</c>, a <c>reason</c> code a caller can
/// branch on, and a <c>detail</c> line a person can act on. Routes that know more answer with their own
/// <c>reason</c>; any error a route answers with no body gets the general one for its status here.
/// </summary>
internal static class ControlProblems
{
    /// <summary>Fills in the general <c>reason</c> and <c>detail</c> of a control error that has none.</summary>
    public static void Customize(ProblemDetailsContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments(ControlPlane.PathPrefix)) return;
        var problem = context.ProblemDetails;
        var (reason, detail) = General(problem.Status ?? context.HttpContext.Response.StatusCode);
        problem.Extensions.TryAdd("reason", reason);
        problem.Detail ??= detail;
        problem.Instance = null; // the path may carry ids; the caller knows what it asked
    }

    /// <summary>The general reason and next step for an error status.</summary>
    public static (string Reason, string Detail) General(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ("bad-request", "The request is not in the shape this route takes — see the route's description."),
        StatusCodes.Status401Unauthorized => ("unauthorized", "Send the credential this runtime was started with as a Bearer token."),
        StatusCodes.Status403Forbidden => ("forbidden", "This is turned off here — by policy, or for this caller."),
        StatusCodes.Status404NotFound => ("not-found", "Nothing is at this address: an unknown route, or no application or item with that id."),
        StatusCodes.Status405MethodNotAllowed => ("method-not-allowed", "This route does not take that method."),
        StatusCodes.Status409Conflict => ("conflict", "This conflicts with the current state — read it again, then retry."),
        StatusCodes.Status413PayloadTooLarge => ("too-large", "The body is larger than this route takes."),
        StatusCodes.Status415UnsupportedMediaType => ("unsupported-type", "This route does not take a body of that type."),
        StatusCodes.Status503ServiceUnavailable => ("unavailable", "Not available right now — try again in a moment."),
        >= 500 => ("internal", "Something inside the runtime failed — its log has the details."),
        _ => ("error", "The request was refused."),
    };

    /// <summary>Answers a refusal with its own reason and next step.</summary>
    public static Task RefuseAsync(HttpContext context, int status, string reason, string detail) =>
        Results.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["reason"] = reason }).ExecuteAsync(context);

    /// <summary>
    /// Answers a refusal with the status the route already set: its <paramref name="reason"/>, and what else the route
    /// knows about it as members of the same problem — the columns a reading should have had, the notes on a document,
    /// the model that failed. A <c>detail</c> the data carries becomes the problem's own; otherwise the general next step
    /// for the status. Null members are left out.
    /// </summary>
    public static Task RefuseAsync<T>(HttpResponse response, string reason, T data)
    {
        var extensions = new Dictionary<string, object?> { ["reason"] = reason };
        string? detail = null;
        if (data is not null && JsonSerializer.SerializeToElement(data, Members) is { ValueKind: JsonValueKind.Object } members)
            foreach (var member in members.EnumerateObject())
            {
                if (member.Value.ValueKind == JsonValueKind.Null || member.NameEquals("reason")) continue;
                if (member.NameEquals("detail") && member.Value.ValueKind == JsonValueKind.String) detail = member.Value.GetString();
                else extensions[member.Name] = member.Value.Clone();
            }

        return Results.Problem(detail ?? General(response.StatusCode).Detail, statusCode: response.StatusCode, extensions: extensions).ExecuteAsync(response.HttpContext);
    }

    /// <summary>Answers a refusal with the status the route already set and its <paramref name="reason"/> alone.</summary>
    public static Task RefuseAsync(HttpResponse response, string reason) => RefuseAsync<object?>(response, reason, null);

    /// <summary>A refusal's data as members — the control surface's own spelling (camelCase).</summary>
    private static readonly JsonSerializerOptions Members = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Whether a body to take in as an application is an HTML document: text with at least one tag.
    /// A body with a NUL byte (a binary file) or no tag at all (plain text, JSON) is not one.
    /// </summary>
    public static bool IsHtml(ReadOnlySpan<byte> body)
    {
        if (body.IndexOf((byte)0) >= 0) return false;
        var text = Encoding.UTF8.GetString(body[..Math.Min(body.Length, 64 * 1024)]);
        for (var i = text.IndexOf('<'); i >= 0 && i + 1 < text.Length; i = text.IndexOf('<', i + 1))
            if (char.IsAsciiLetter(text[i + 1]) || text[i + 1] is '!' or '?') return true;
        return false;
    }
}
