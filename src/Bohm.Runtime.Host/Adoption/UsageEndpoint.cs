using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Receives the two usage facts only the page can observe: <c>POST /__bohm/usage</c> with
/// <c>{ "tab": "...", "kind": "input" }</c> the first time the person types or points, and
/// <c>{ "tab": "...", "kind": "keys", "missing": 1, "unread": 3, "seeded": 3 }</c> once per page load:
/// how the page's reads matched the data it was given (counts only). Opening and writing are
/// observed by the host itself; what stopped the page arrives as a problem report
/// (<see cref="AdoptedAppServing.ProblemsPath"/>).
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

        if (report is null || report.Kind is not ("input" or "keys")
            || report.Kind == "keys" && !(Count(report.Missing) && Count(report.Unread) && Count(report.Seeded) && report.Unread <= report.Seeded))
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
        switch (report.Kind)
        {
            case "input": app.Usage.Record(UsageSignal.Input); break;
            case "keys":
                // Recorded against the revision serving now; a page of an older revision cannot write any more.
                if (await context.RequestServices.GetRequiredService<AdoptionCatalog>().GetAsync(appId, context.RequestAborted).ConfigureAwait(false) is { } adopted)
                    app.Usage.RecordKeys(adopted.Revision, report.Missing!.Value, report.Unread!.Value, report.Seeded!.Value);
                break;
        }
        // 200 with a body rather than 204: an answered-with-204 fetch was observed to keep the
        // Chromium-family browser from shutting down cleanly (its close never completed).
        await response.WriteAsync("{}", context.RequestAborted).ConfigureAwait(false);
    }

    private static bool Count(int? n) => n is >= 0 and <= 100_000;

    internal sealed record Report(string? Tab, string? Kind, int? Missing = null, int? Unread = null, int? Seeded = null);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(UsageEndpoint.Report))]
internal sealed partial class UsageJson : System.Text.Json.Serialization.JsonSerializerContext;
