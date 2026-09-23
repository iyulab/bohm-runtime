using System.Text.Json;
using Bohm.Runtime.Storage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Receives storage operations from the injected script: <c>POST /__bohm/storage</c> with
/// <c>{ "tab": "...", "ops": [ { "seq": 1, "op": "set", "key": "...", "value": "..." }, ... ] }</c>,
/// answered with <c>{ "ack": n }</c> — the highest sequence number from that tab now on disk.
/// </summary>
/// <remarks>
/// Operations at or below the tab's acknowledged sequence are skipped, so a batch can be resent
/// any number of times. A request must carry the application's session cookie and the
/// <c>X-Bohm-Request</c> header; a page on another origin can send neither (the cookie is scoped
/// to this origin, and the custom header would require a CORS preflight that is never granted).
/// </remarks>
internal static partial class StorageEndpoint
{
    public static async Task HandleAsync(HttpContext context, string appId)
    {
        var request = context.Request;
        var response = context.Response;
        if (!HttpMethods.IsPost(request.Method))
        {
            response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (request.Headers[AdoptedAppServing.RequestHeader] != "1" || !IsSameOriginOrAbsent(request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        Batch? batch;
        try
        {
            batch = await JsonSerializer.DeserializeAsync(request.Body, StorageJson.Default.Batch, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            batch = null;
        }

        var operations = batch?.Ops?.Select(ToOperation).ToList();
        if (batch?.Tab is null || operations is null || operations.Any(o => o.Operation is null))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var sessions = context.RequestServices.GetRequiredService<AppSessions>();
        var tab = sessions.Authorize(appId, request.Cookies[AdoptedAppServing.SessionCookie], batch.Tab);
        if (tab is null)
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StorageEndpoint));
        var storage = await context.RequestServices.GetRequiredService<OpenStorages>().GetAsync(appId).ConfigureAwait(false);

        await tab.Gate.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            var fresh = operations.Where(o => o.Sequence > tab.LastSequence).OrderBy(o => o.Sequence).ToList();
            if (fresh.Count > 0)
            {
                if (fresh[0].Sequence != tab.LastSequence + 1)
                    LogSequenceGap(logger, appId, tab.LastSequence, fresh[0].Sequence);

                await storage.ApplyAsync(fresh.Select(o => o.Operation!).ToList(), CancellationToken.None).ConfigureAwait(false);
                tab.LastSequence = fresh[^1].Sequence;
            }

            await response.WriteAsJsonAsync(new AckResponse(tab.LastSequence), StorageJson.Default.AckResponse, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            tab.Gate.Release();
        }
    }

    private static bool IsSameOriginOrAbsent(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        return origin.Length == 0 || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static (long Sequence, StorageOperation? Operation) ToOperation(WireOperation wire) =>
        (wire.Seq, wire.Op switch
        {
            "set" when wire.Key is not null && wire.Value is not null => StorageOperation.Set(wire.Key, wire.Value),
            "remove" when wire.Key is not null => StorageOperation.Remove(wire.Key),
            "clear" => StorageOperation.Clear(),
            _ => null,
        } is { } operation && wire.Seq > 0 ? operation : null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage operations from a tab of {AppId} skipped from sequence {Last} to {First}; operations may have been lost in transit.")]
    private static partial void LogSequenceGap(ILogger logger, string appId, long last, long first);

    internal sealed record Batch(string? Tab, List<WireOperation>? Ops);

    internal sealed record WireOperation(long Seq, string? Op, string? Key, string? Value);

    internal sealed record AckResponse(long Ack);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(StorageEndpoint.Batch))]
[System.Text.Json.Serialization.JsonSerializable(typeof(StorageEndpoint.AckResponse))]
internal sealed partial class StorageJson : System.Text.Json.Serialization.JsonSerializerContext;
