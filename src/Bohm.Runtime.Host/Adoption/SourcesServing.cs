using System.Text.Json;
using System.Text.Json.Serialization;
using Bohm.Runtime.Sources;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// An application's sources over HTTP. The application reads, on its own origin:
/// <c>GET /__bohm/sources/&lt;name&gt;</c> — the latest reading, <c>{ readAt, source, rows }</c>, each row an
/// object with one string per column name (<c>readAt</c> and <c>source</c> are <c>null</c> and <c>rows</c> empty
/// until the source is first read) — and <c>GET /__bohm/sources/&lt;name&gt;/readings</c>, every reading, oldest first.
/// The shell declares sources and hands over what it read through the control API (see <see cref="Control.ControlPlane"/>).
/// </summary>
/// <remarks>
/// The page only reads: the rows come from the shell, which reads the web page, and this runtime checks
/// each reading against the person's permission and the rule before keeping it (<see cref="AppSources.RecordAsync"/>).
/// Requests come from the application's own <c>fetch</c> calls, so the session cookie and the Origin
/// check stand in for the script's custom header.
/// </remarks>
internal static class SourcesServing
{
    public const string PathPrefix = "/__bohm/sources";

    /// <summary>Serves a read to the application's own page.</summary>
    public static async Task ServeToAppAsync(HttpContext context, string appId, OpenApp app)
    {
        if (!PageRequests.IsFromTheApp(context, appId))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await ServeAsync(context, app.Sources).ConfigureAwait(false);
    }

    /// <summary>Serves a read to a preview of the application: its sources as they are, the same way.</summary>
    public static Task ServeToPreviewAsync(HttpContext context, OpenApp app) => ServeAsync(context, app.Sources);

    private static async Task ServeAsync(HttpContext context, AppSources sources)
    {
        var request = context.Request;
        var response = context.Response;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var rest = request.Path.Value![PathPrefix.Length..].Trim('/').Split('/');
        var (name, all) = rest switch
        {
            [var n] => (n, false),
            [var n, "readings"] => (n, true),
            _ => (null, false),
        };
        if (name is null || await sources.ReadingsAsync(name, context.RequestAborted).ConfigureAwait(false) is not { } readings)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        response.Headers.CacheControl = "no-store";
        if (all)
            await response.WriteAsJsonAsync(readings.Select(ViewOf).ToList(), SourcesHttpJson.Default.ListReadingView, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        else
            await response.WriteAsJsonAsync(readings is [.., var last] ? ViewOf(last) : new ReadingView(null, null, []), SourcesHttpJson.Default.ReadingView, cancellationToken: context.RequestAborted).ConfigureAwait(false);
    }

    private static ReadingView ViewOf(SourceReading reading) => new(reading.ReadAt, reading.Source, reading.Rows);

    /// <summary>The control API's <c>/apps/{id}/sources…</c> routes, once the application is known and open.</summary>
    public static async Task HandleControlAsync(HttpContext context, OpenApp app, string[] rest)
    {
        var request = context.Request;
        var response = context.Response;
        var cancel = context.RequestAborted;
        var sources = app.Sources;
        switch (request.Method, rest)
        {
            case ("GET", []):
                await response.WriteAsJsonAsync((await sources.ListAsync(cancel).ConfigureAwait(false)).ToList(), SourcesHttpJson.Default.ListAppSource, cancellationToken: cancel).ConfigureAwait(false);
                return;

            case ("PUT", [var name]):
                if (!AppSources.IsValidName(name) || await ReadAsync(request, SourcesHttpJson.Default.DeclareRequest, cancel).ConfigureAwait(false) is not { Rule: { } rule } declare)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                AppSource declared;
                try
                {
                    declared = await sources.DeclareAsync(name, rule, declare.Granted, cancel).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                await response.WriteAsJsonAsync(declared, SourcesHttpJson.Default.AppSource, cancellationToken: cancel).ConfigureAwait(false);
                return;

            case ("POST", [var name, "readings"]):
                if (await ReadAsync(request, SourcesHttpJson.Default.ReadingRequest, cancel).ConfigureAwait(false) is not { Source: { } source, Columns: { } columns, Rows: { } rows }
                    || rows.Any(r => r is null))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var (outcome, reading) = await sources.RecordAsync(name, source, columns, rows, cancel).ConfigureAwait(false);
                switch (outcome)
                {
                    case RecordOutcome.Recorded:
                        await response.WriteAsJsonAsync(ViewOf(reading!), SourcesHttpJson.Default.ReadingView, cancellationToken: cancel).ConfigureAwait(false);
                        return;
                    case RecordOutcome.Unknown:
                        response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    case RecordOutcome.NotGranted:
                        await RefuseAsync(response, StatusCodes.Status403Forbidden, new SourceRefusal("not-granted", null), cancel).ConfigureAwait(false);
                        return;
                    case RecordOutcome.OutsideGrant:
                        await RefuseAsync(response, StatusCodes.Status403Forbidden, new SourceRefusal("outside-grant", null), cancel).ConfigureAwait(false);
                        return;
                    default:
                        var expected = (await sources.ListAsync(cancel).ConfigureAwait(false)).FirstOrDefault(s => s.Name == name)?.Rule.Columns;
                        await RefuseAsync(response, StatusCodes.Status409Conflict, new SourceRefusal("shape-mismatch", expected), cancel).ConfigureAwait(false);
                        return;
                }

            case ("DELETE", [var name, "grant"]):
                response.StatusCode = await sources.RevokeAsync(name, cancel).ConfigureAwait(false) ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
                return;

            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private static Task RefuseAsync(HttpResponse response, int status, SourceRefusal refusal, CancellationToken cancellationToken)
    {
        response.StatusCode = status;
        return response.WriteAsJsonAsync(refusal, SourcesHttpJson.Default.SourceRefusal, cancellationToken: cancellationToken);
    }

    private static async Task<T?> ReadAsync<T>(HttpRequest request, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(request.Body, type, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>A reading as the application receives it.</summary>
    internal sealed record ReadingView(DateTimeOffset? ReadAt, string? Source, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows);

    /// <summary>Declares a source: its rule, and whether the person has just allowed its site to be read.</summary>
    internal sealed record DeclareRequest(SourceRule? Rule, bool Granted);

    /// <summary>What the shell read: the page, the columns it found and the rows, one cell per column.</summary>
    internal sealed record ReadingRequest(string? Source, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<string>>? Rows);

    /// <summary>Why a reading was not kept; for a shape mismatch, the columns the rule expects.</summary>
    internal sealed record SourceRefusal(string Code, IReadOnlyList<string>? Columns);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourcesServing.ReadingView))]
[JsonSerializable(typeof(List<SourcesServing.ReadingView>))]
[JsonSerializable(typeof(SourcesServing.DeclareRequest))]
[JsonSerializable(typeof(SourcesServing.ReadingRequest))]
[JsonSerializable(typeof(SourcesServing.SourceRefusal))]
[JsonSerializable(typeof(AppSource))]
[JsonSerializable(typeof(List<AppSource>))]
internal sealed partial class SourcesHttpJson : JsonSerializerContext;
