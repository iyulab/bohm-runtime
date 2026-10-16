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

        await ServeAsync(context, app.Sources.ReadingsAsync).ConfigureAwait(false);
    }

    /// <summary>Serves a read to a preview of the application: its sources as they are, the same way.</summary>
    public static Task ServeToPreviewAsync(HttpContext context, OpenApp app) => ServeAsync(context, app.Sources.ReadingsAsync);

    /// <summary>Serves a read to a preview of a proposed new application: each source's rows as just read.</summary>
    public static Task ServeToPreviewAsync(HttpContext context, IReadOnlyDictionary<string, SourceReading> readings) =>
        ServeAsync(context, (name, _) => Task.FromResult<IReadOnlyList<SourceReading>?>(readings.TryGetValue(name, out var reading) ? [reading] : null));

    private static async Task ServeAsync(HttpContext context, Func<string, CancellationToken, Task<IReadOnlyList<SourceReading>?>> readingsOf)
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
        if (name is null || await readingsOf(name, context.RequestAborted).ConfigureAwait(false) is not { } readings)
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
                if (await ReadAsync(request, SourcesHttpJson.Default.ReadingRequest, cancel).ConfigureAwait(false) is not { Source: { } source, Columns: { } columns, Rows: { } rows } read
                    || rows.Any(r => r is null))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var (outcome, reading) = read.Scheduled
                    ? await sources.RecordScheduledAsync(name, source, columns, rows, cancel).ConfigureAwait(false)
                    : await sources.RecordAsync(name, source, columns, rows, cancel).ConfigureAwait(false);
                switch (outcome)
                {
                    case RecordOutcome.Recorded or RecordOutcome.Unchanged when read.Scheduled:
                        await response.WriteAsJsonAsync(new ScheduledReadingView(outcome is RecordOutcome.Recorded, ViewOf(reading!)), SourcesHttpJson.Default.ScheduledReadingView, cancellationToken: cancel).ConfigureAwait(false);
                        return;
                    case RecordOutcome.Recorded:
                        await response.WriteAsJsonAsync(ViewOf(reading!), SourcesHttpJson.Default.ReadingView, cancellationToken: cancel).ConfigureAwait(false);
                        return;
                    case RecordOutcome.Unknown:
                        response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    case RecordOutcome.NotGranted:
                        await RefuseAsync(response, StatusCodes.Status403Forbidden, "not-granted", null).ConfigureAwait(false);
                        return;
                    case RecordOutcome.OutsideGrant:
                        await RefuseAsync(response, StatusCodes.Status403Forbidden, "outside-grant", null).ConfigureAwait(false);
                        return;
                    default:
                        var expected = (await sources.ListAsync(cancel).ConfigureAwait(false)).FirstOrDefault(s => s.Name == name)?.Rule.Columns;
                        await RefuseAsync(response, StatusCodes.Status409Conflict, "shape-mismatch", expected).ConfigureAwait(false);
                        return;
                }

            case ("PUT", [var name, "schedule"]):
                if (await ReadAsync(request, SourcesHttpJson.Default.ScheduleRequest, cancel).ConfigureAwait(false) is not { } schedule)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                var (scheduled, scheduledSource) = await sources.ScheduleAsync(name, schedule.EveryMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, cancel).ConfigureAwait(false);
                switch (scheduled)
                {
                    case ScheduleOutcome.Scheduled:
                        await response.WriteAsJsonAsync(scheduledSource!, SourcesHttpJson.Default.AppSource, cancellationToken: cancel).ConfigureAwait(false);
                        return;
                    case ScheduleOutcome.TooOften:
                        await RefuseAsync(response, StatusCodes.Status400BadRequest, "too-often", null).ConfigureAwait(false);
                        return;
                    case ScheduleOutcome.NotGranted:
                        await RefuseAsync(response, StatusCodes.Status409Conflict, "not-granted", null).ConfigureAwait(false);
                        return;
                    default:
                        response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                }

            case ("POST", [var name, "failures"]):
                if (await ReadAsync(request, SourcesHttpJson.Default.FailureRequest, cancel).ConfigureAwait(false) is not { Kind: { } kind } failure
                    || failure.RetryAfterSeconds is < 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                if (await sources.ReportFailureAsync(name, kind, failure.Reason, failure.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, cancel).ConfigureAwait(false) is not { } failed)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await response.WriteAsJsonAsync(failed, SourcesHttpJson.Default.AppSource, cancellationToken: cancel).ConfigureAwait(false);
                return;

            case ("DELETE", [var name, "grant"]):
                response.StatusCode = await sources.RevokeAsync(name, cancel).ConfigureAwait(false) ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
                return;

            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    /// <summary>Why a reading was not kept, as a control problem (<c>reason</c>); for a shape mismatch, the <c>columns</c> the rule expects.</summary>
    private static Task RefuseAsync(HttpResponse response, int status, string reason, IReadOnlyList<string>? columns)
    {
        response.StatusCode = status;
        return Control.ControlProblems.RefuseAsync(response, reason, new { columns });
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

    /// <summary>What the shell read: the page, the columns it found and the rows, one cell per column — and whether it read on the source's schedule.</summary>
    internal sealed record ReadingRequest(string? Source, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<string>>? Rows, bool Scheduled = false);

    /// <summary>A scheduled reading as kept: whether the rows changed (nothing new is kept when they did not), and the reading.</summary>
    internal sealed record ScheduledReadingView(bool Changed, ReadingView Reading);

    /// <summary>How often the person wants a source read on its own, in minutes; <see langword="null"/> stops it.</summary>
    internal sealed record ScheduleRequest(int? EveryMinutes);

    /// <summary>A scheduled reading that failed: how, why, and the wait the site asked for, if any.</summary>
    internal sealed record FailureRequest(SourceFailure? Kind, string? Reason, int? RetryAfterSeconds);

    /// <summary>A source due to be read on its schedule, and its application.</summary>
    internal sealed record DueSource(string App, AppSource Source);

}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SourcesServing.ReadingView))]
[JsonSerializable(typeof(List<SourcesServing.ReadingView>))]
[JsonSerializable(typeof(SourcesServing.DeclareRequest))]
[JsonSerializable(typeof(SourcesServing.ReadingRequest))]
[JsonSerializable(typeof(AppSource))]
[JsonSerializable(typeof(List<AppSource>))]
[JsonSerializable(typeof(SourcesServing.ScheduledReadingView))]
[JsonSerializable(typeof(SourcesServing.ScheduleRequest))]
[JsonSerializable(typeof(SourcesServing.FailureRequest))]
[JsonSerializable(typeof(List<SourcesServing.DueSource>))]
internal sealed partial class SourcesHttpJson : JsonSerializerContext;
