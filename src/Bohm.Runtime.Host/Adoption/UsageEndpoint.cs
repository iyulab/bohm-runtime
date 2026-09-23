using System.Text.Json;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Receives the two usage facts only the page can observe: <c>POST /__bohm/usage</c> with
/// <c>{ "tab": "...", "kind": "input" }</c> the first time the person types or points, and
/// <c>{ "tab": "...", "kind": "load-error", "message": "..." }</c> when the application fails
/// while loading. Opening and writing are observed by the host itself.
/// </summary>
internal static class UsageEndpoint
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

        if (!PageRequests.IsFromThePage(request))
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        Report? report;
        try
        {
            report = await JsonSerializer.DeserializeAsync(request.Body, UsageJson.Default.Report, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            report = null;
        }

        if (report is null || report.Kind is not ("input" or "load-error"))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (PageRequests.Tab(context, appId, report.Tab) is null)
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        if (report.Kind == "input") app.Usage.Record(UsageSignal.Input);
        else app.AddLoadError(report.Message ?? "");
        // 200 with a body rather than 204: an answered-with-204 fetch was observed to keep the
        // Chromium-family browser from shutting down cleanly (its close never completed).
        await response.WriteAsync("{}", context.RequestAborted).ConfigureAwait(false);
    }

    internal sealed record Report(string? Tab, string? Kind, string? Message);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(UsageEndpoint.Report))]
internal sealed partial class UsageJson : System.Text.Json.Serialization.JsonSerializerContext;
