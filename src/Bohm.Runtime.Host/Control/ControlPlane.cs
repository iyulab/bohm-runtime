using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Adoption;
using Bohm.Runtime.Host.Llm;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Control;

/// <summary>
/// The API the process that started the runtime uses to drive it — the desktop shell, or a
/// headless deployment's configuration. It answers only on the loopback address itself (never on
/// an application origin) and only to requests bearing the per-launch secret.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><c>GET /__control/apps</c></term><description>Adopted applications, oldest first, each with the last day it was used.</description></item>
/// <item><term><c>GET /__control/apps/unreadable</c></term><description>Application folders that could not be read — kind <c>cannotOpen</c> (the system could not open a file right now, e.g. kept only in the cloud while offline), <c>damaged</c>, or <c>interruptedRemoval</c> (a removal for good stopped before the recycle bin; the folder is still there). Nothing in them is changed; one unreadable application never hides the others.</description></item>
/// <item><term><c>POST /__control/apps/matches</c></term><description>Earlier adoptions of the HTML in the body, or of a file at the same path (optional <c>X-Bohm-Original-Path</c>), each with how it matches.</description></item>
/// <item><term><c>POST /__control/apps</c></term><description>Adopts the HTML in the body (optional <c>X-Bohm-Original-Path</c>, URL-encoded).</description></item>
/// <item><term><c>POST /__control/apps/{id}/revisions</c></term><description>Takes in the HTML in the body as a new revision of the application: same application, same data, new code (optional <c>X-Bohm-Original-Path</c>). Pages still running the old code can no longer write.</description></item>
/// <item><term><c>POST /__control/apps/{id}/archive</c> · <c>/restore</c></term><description>Puts the application away or brings it back. Only a mark on its record changes — code, data, revisions and usage record stay; an archived application is not served. The caller closes its pages first.</description></item>
/// <item><term><c>POST /__control/apps/{id}/export</c></term><description>Copies the application's folder, as it is, to the new folder whose full path is the body — the exchange format is the folder itself. The data is checkpointed first; the original is unchanged. 409 when something with that name is already there or its parent is missing.</description></item>
/// <item><term><c>POST /__control/apps/import</c></term><description>Takes in the exported application folder whose full path is the body, as it is — same identity, data, revisions and usage record. 400 when it is not an application folder; 409 when the application is already here (nothing is replaced).</description></item>
/// <item><term><c>DELETE /__control/apps/{id}</c></term><description>Removes an archived application for good: its folder goes to the recycle bin (the operating system's way back); its usage record stays and keeps appearing in the usage report with the day it was removed. 409 when the application is not archived.</description></item>
/// <item><term><c>POST /__control/apps/{id}/revisions/revert</c></term><description>Goes back to the previous revision, code and data together; what the revision being left wrote is kept aside.</description></item>
/// <item><term><c>GET /__control/apps/{id}/usage</c></term><description>The application's usage record: each recorded day's signals and load failures, its revisions, its first and last day of use and where it stands against the 30-day retention rule. Days are local; nothing leaves this computer.</description></item>
/// <item><term><c>GET /__control/usage-report</c></term><description>Every application's usage record in one document the person can read and choose to hand over: application ids, days, signals, revisions and retention — no names, paths or content. Nothing is sent; the caller decides what happens to it.</description></item>
/// <item><term><c>GET /__control/apps/{id}/tabs/{tab}</c></term><description>The highest write sequence applied from one loaded page (<c>ack</c>) and the highest sequence the page reported having issued (<c>issued</c>); <c>left</c> once the page's report sent after leaving has arrived, which makes <c>issued</c> final. A host closing the page waits until <c>ack</c> reaches both the sequence it read before the page left and <c>issued</c>; 404 when the page is not (or no longer) the application's.</description></item>
/// <item><term><c>POST /__control/apps/{id}/loss-suspected</c></term><description>Records that a closing page of the application went away before its last writes could be confirmed as applied. Counted per day in the usage record.</description></item>
/// <item><term><c>GET /__control/apps/{id}/status</c></term><description>Today's usage signals, load failures, blocked resources, missing files, calls to a server the application expected (method and path) and keys needed.</description></item>
/// <item><term><c>POST /__control/apps/{id}/assets</c></term><description>Fetches (again) the code the application loads from other hosts; answers what was and was not cached.</description></item>
/// <item><term><c>GET /__control/egress</c></term><description>What left this computer since the runtime started: sent, fetched and blocked, by host.</description></item>
/// <item><term><c>GET /__control/llm</c></term><description>AI providers, whether a key is connected (never the key) and whether, without one, the model on this computer answers the provider's chat requests.</description></item>
/// <item><term><c>PUT /__control/llm/{provider}/key</c></term><description>Connects the key in the body, stored in the vault.</description></item>
/// <item><term><c>DELETE /__control/llm/{provider}/key</c></term><description>Disconnects it.</description></item>
/// <item><term><c>GET /__control/llm/local-model</c></term><description>The model on this computer that answers when no key is connected: its file, whether it is loaded or loading, why the last load failed, and whether it was fixed at start.</description></item>
/// <item><term><c>PUT /__control/llm/local-model</c></term><description>Chooses the model file named in the body (a full path to a <c>.gguf</c> file) and remembers it; 400 when there is no such file, 409 when fixed at start.</description></item>
/// <item><term><c>DELETE /__control/llm/local-model</c></term><description>Chooses none.</description></item>
/// <item><term><c>POST /__control/llm/local-model/load</c></term><description>Starts loading the chosen model now instead of on the first request (202 with the model's state — <c>loading</c> until it is loaded or <c>error</c> says why not); 409 when no model is chosen.</description></item>
/// <item><term><c>POST /__control/drain</c></term><description>Waits until no storage write is in progress.</description></item>
/// <item><term><c>POST /__control/shutdown</c></term><description>Drains, then stops the runtime.</description></item>
/// </list>
/// </remarks>
internal static class ControlPlane
{
    public const string PathPrefix = "/__control";
    public const string OriginalPathHeader = "X-Bohm-Original-Path";

    /// <summary>How long no write may be in progress before the runtime counts as drained.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    /// <summary>The longest a drain waits. Past it the caller proceeds and a loss is possible.</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    public static bool IsControlHost(HttpRequest request) =>
        request.Host.Host is "127.0.0.1" or "localhost" && request.Path.StartsWithSegments(PathPrefix);

    public static async Task HandleAsync(HttpContext context, string? secret)
    {
        var request = context.Request;
        var response = context.Response;
        if (secret is null || !Authorized(request, secret))
        {
            response.StatusCode = secret is null ? StatusCodes.Status404NotFound : StatusCodes.Status401Unauthorized;
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        var segments = request.Path.Value![PathPrefix.Length..].Trim('/').Split('/');
        var port = request.Host.Port ?? 80;
        var cancel = context.RequestAborted;

        switch (request.Method, segments)
        {
            case ("GET", ["apps"]):
                // The usage record is read from its file: appends reach it at once, and reading it does not open the application.
                await WriteAsync(response, (await ListAsync(catalog, port, cancel).ConfigureAwait(false)).Apps, cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", "unreadable"]):
                await WriteAsync(response, (await ListAsync(catalog, port, cancel).ConfigureAwait(false)).Unreadable, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", "matches"]):
                var candidate = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                var matches = await catalog.FindEarlierAdoptionsAsync(candidate, OriginalPath(request), cancel).ConfigureAwait(false);
                var views = new List<MatchView>();
                foreach (var m in matches) views.Add(await ViewMatchAsync(catalog, m, port, cancel).ConfigureAwait(false));
                await WriteAsync(response, views, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps"]):
                var html = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                if (html.Length == 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var adopted = await catalog.AdoptAsync(html, OriginalPath(request), cancel).ConfigureAwait(false);
                if (context.RequestServices.GetRequiredService<RuntimeHostOptions>().FetchAssetsOnAdoption)
                    context.RequestServices.GetRequiredService<AssetFetcher>().Start(adopted.Id);
                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(adopted, port, canRevert: false), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", "import"]):
                var source = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                if (!Path.IsPathFullyQualified(source))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                AdoptedApp imported;
                try
                {
                    imported = await catalog.ImportAsync(source, cancel).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(imported, port, await catalog.CanRevertAsync(imported, cancel).ConfigureAwait(false), catalog.OpenUsage(imported.Id).LastUsedOn), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var revisedId, "revisions"]):
                if (await catalog.GetAsync(revisedId, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var revisedHtml = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                if (revisedHtml.Length == 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var revisedPath = OriginalPath(request);
                await ChangeRevisionAsync(context, revisedId, StatusCodes.Status201Created,
                    storage => catalog.ReviseAsync(revisedId, revisedHtml, revisedPath, storage, cancel), reverted: false).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var revertedId, "revisions", "revert"]):
                if (await catalog.GetAsync(revertedId, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await ChangeRevisionAsync(context, revertedId, StatusCodes.Status200OK,
                    storage => catalog.RevertAsync(revertedId, storage, cancel), reverted: true).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var archiveId, "archive" or "restore"]):
                var marked = await catalog.SetArchivedAsync(archiveId, archived: segments[2] == "archive", cancel).ConfigureAwait(false);
                if (marked is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, View(marked, port, await catalog.CanRevertAsync(marked, cancel).ConfigureAwait(false), catalog.OpenUsage(marked.Id).LastUsedOn), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var exportId, "export"]):
                var target = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                if (!Path.IsPathFullyQualified(target))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                AdoptedApp? exported;
                try
                {
                    var open = await catalog.GetAsync(exportId, cancel).ConfigureAwait(false) is { ArchivedAt: null }
                        ? await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(exportId).ConfigureAwait(false)
                        : null;
                    exported = await catalog.ExportAsync(exportId, target, open?.Storage, cancel).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                if (exported is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new ExportedView(exported.Id, Path.GetFullPath(target)), cancel).ConfigureAwait(false);
                break;

            case ("DELETE", ["apps", var removeId]):
                RemovedApp? removed;
                try
                {
                    if (await catalog.GetAsync(removeId, cancel).ConfigureAwait(false) is { ArchivedAt: not null })
                        await context.RequestServices.GetRequiredService<OpenApps>().CloseAsync(removeId).ConfigureAwait(false);
                    removed = await catalog.RemoveAsync(removeId, DiscardOf(context.RequestServices.GetRequiredService<RuntimeHostOptions>()), cancel).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                if (removed is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new RemovedView(removed.Id, removed.RemovedAt), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", var usageFor, "usage"]):
                if (await catalog.GetAsync(usageFor, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                // Read from the file, like the listing: looking at the record does not open the application.
                await WriteAsync(response, UsageOf(catalog.OpenUsage(usageFor)), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", var tabOf, "tabs", var tabId]):
                if (context.RequestServices.GetRequiredService<AppSessions>().Find(tabOf, tabId) is not { } tab)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, new TabView(tab.LastSequence, tab.Issued, tab.Left), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var lossOf, "loss-suspected"]):
                if (await catalog.GetAsync(lossOf, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                (await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(lossOf).ConfigureAwait(false)).Usage.RecordLossSuspected();
                response.StatusCode = StatusCodes.Status204NoContent;
                break;

            case ("GET", ["apps", var id, "status"]):
                if (await catalog.GetAsync(id, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(id).ConfigureAwait(false);
                var today = app.Usage.Today;
                var signals = app.Usage.SignalsOn(today);
                await WriteAsync(response, new AppStatus(
                    today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    signals.Contains(UsageSignal.Opened), signals.Contains(UsageSignal.Input), signals.Contains(UsageSignal.Wrote),
                    app.Usage.LoadErrorsOn(today), app.RecentLoadErrors, app.NeededKeys, app.Blocked, app.MissingFiles, app.MissingApis, app.Assets.Assets.Count), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var assetsFor, "assets"]):
                if (await catalog.GetAsync(assetsFor, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await context.RequestServices.GetRequiredService<AssetFetcher>().FetchAsync(assetsFor, cancel).ConfigureAwait(false);
                var fetchedApp = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(assetsFor).ConfigureAwait(false);
                await WriteAsync(response, new AssetsView(
                    fetchedApp.Assets.Assets.Select(a => new AssetView(a.Url, a.Size)).ToList(),
                    fetchedApp.Assets.Failures.Select(f => new AssetView(f.Url, 0, f.Reason)).ToList()), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["usage-report"]):
                var reported = new List<ReportedApp>();
                foreach (var a in await catalog.ListAsync(cancel).ConfigureAwait(false))
                {
                    var usage = UsageOf(catalog.OpenUsage(a.Id));
                    reported.Add(new ReportedApp(a.Id, Iso(DateOnly.FromDateTime(a.AdoptedAt.ToLocalTime().DateTime)), a.Revision, usage,
                        a.ArchivedAt is { } archivedAt ? Iso(DateOnly.FromDateTime(archivedAt.ToLocalTime().DateTime)) : null));
                }

                foreach (var r in await catalog.ListRemovedAsync(cancel).ConfigureAwait(false))
                {
                    reported.Add(new ReportedApp(r.Id, Iso(DateOnly.FromDateTime(r.AdoptedAt.ToLocalTime().DateTime)), r.Revision, UsageOf(catalog.OpenRemovedUsage(r.Id)),
                        r.ArchivedAt is { } removedArchivedAt ? Iso(DateOnly.FromDateTime(removedArchivedAt.ToLocalTime().DateTime)) : null,
                        Iso(DateOnly.FromDateTime(r.RemovedAt.ToLocalTime().DateTime))));
                }

                await WriteAsync(response, new UsageReport(UsageReport.FormatName, DateTimeOffset.Now, reported), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["egress"]):
                await WriteAsync(response, context.RequestServices.GetRequiredService<Egress>().Snapshot(), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm"]):
                var vault = context.RequestServices.GetRequiredService<ICredentialVault>();
                var local = context.RequestServices.GetRequiredService<LocalModel>();
                await WriteAsync(response, LlmProviders.All
                    .Select(p => ProviderViewOf(p, !string.IsNullOrEmpty(vault.Read(p.VaultName)), local))
                    .ToList(), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm", "local-model"]):
                await WriteAsync(response, LocalModelViewOf(context.RequestServices.GetRequiredService<LocalModel>()), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["llm", "local-model", "load"]):
                var toLoad = context.RequestServices.GetRequiredService<LocalModel>();
                if (!toLoad.StartLoading())
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                response.StatusCode = StatusCodes.Status202Accepted;
                await WriteAsync(response, LocalModelViewOf(toLoad), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", "local-model"]):
                var localModel = context.RequestServices.GetRequiredService<LocalModel>();
                var chosen = request.Method == "DELETE" ? null : Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                try
                {
                    await localModel.ChooseAsync(chosen, cancel).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }
                catch (ArgumentException)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                await WriteAsync(response, LocalModelViewOf(localModel), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", var providerId, "key"]):
                if (LlmProviders.ById(providerId) is not { } provider)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var keys = context.RequestServices.GetRequiredService<ICredentialVault>();
                if (request.Method == "DELETE")
                {
                    keys.Delete(provider.VaultName);
                }
                else
                {
                    var key = System.Text.Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                    if (key.Length == 0)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }

                    keys.Write(provider.VaultName, key);
                }

                await WriteAsync(response, ProviderViewOf(provider, request.Method != "DELETE", context.RequestServices.GetRequiredService<LocalModel>()), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["drain"]):
                await WriteAsync(response, new DrainResult(await DrainAsync(context, cancel).ConfigureAwait(false)), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["shutdown"]):
                var quiet = await DrainAsync(context, cancel).ConfigureAwait(false);
                await WriteAsync(response, new DrainResult(quiet), cancel).ConfigureAwait(false);
                await response.CompleteAsync().ConfigureAwait(false);
                context.RequestServices.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                break;

            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    /// <summary>
    /// Replaces the code an application runs, in an order that loses nothing: writes already on
    /// their way — typically the last one a page sends as it closes — are waited for and applied
    /// first; then pages loaded before can no longer write; only then is the data saved and the
    /// code switched. The application's code from other hosts is fetched again afterwards, since
    /// the new revision may load different code.
    /// </summary>
    private static async Task ChangeRevisionAsync(HttpContext context, string appId, int successStatus,
        Func<Runtime.Storage.AppStorage, Task<AdoptedApp>> change, bool reverted)
    {
        var services = context.RequestServices;
        var response = context.Response;
        var cancel = context.RequestAborted;
        var app = await services.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        await app.RevisionChange.WaitAsync(cancel).ConfigureAwait(false);
        AdoptedApp changed;
        try
        {
            // Drain before revoking: a write the closing page sent must land, not be refused as stale.
            await DrainAsync(context, cancel).ConfigureAwait(false);
            services.GetRequiredService<AppSessions>().RevokeApp(appId);
            try
            {
                changed = await change(app.Storage).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Not a new revision (the same bytes), or nothing to go back to.
                response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            app.ForgetObservations();
            app.Usage.RecordRevision(reverted);
        }
        finally
        {
            app.RevisionChange.Release();
        }

        if (services.GetRequiredService<RuntimeHostOptions>().FetchAssetsOnAdoption)
            services.GetRequiredService<AssetFetcher>().Start(appId);
        var catalog = services.GetRequiredService<AdoptionCatalog>();
        response.StatusCode = successStatus;
        var canRevert = await catalog.CanRevertAsync(changed, cancel).ConfigureAwait(false);
        await WriteAsync(response, View(changed, context.Request.Host.Port ?? 80, canRevert), cancel).ConfigureAwait(false);
    }

    private static Task<bool> DrainAsync(HttpContext context, CancellationToken cancellationToken) =>
        context.RequestServices.GetRequiredService<Activity>().WaitForQuietAsync(Quiet, DrainLimit, cancellationToken);

    private static bool Authorized(HttpRequest request, string secret)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[scheme.Length..]), Encoding.UTF8.GetBytes(secret));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string? OriginalPath(HttpRequest request) =>
        request.Headers[OriginalPathHeader].ToString() is { Length: > 0 } encoded ? Uri.UnescapeDataString(encoded) : null;

    private static async Task<MatchView> ViewMatchAsync(AdoptionCatalog catalog, AdoptionMatch match, int port, CancellationToken cancellationToken) =>
        new(View(match.App, port, await catalog.CanRevertAsync(match.App, cancellationToken).ConfigureAwait(false)), match.Kind switch
        {
            AdoptionMatchKind.SameBytes => "sameBytes",
            AdoptionMatchKind.SameOriginalPath => "sameOriginalPath",
            _ => throw new ArgumentOutOfRangeException(nameof(match)),
        });

    /// <summary>
    /// The applications to list, and the ones that could not be read. An application whose revisions
    /// or usage record cannot be opened moves to the unreadable ones rather than failing the whole list.
    /// </summary>
    private static async Task<(List<AppView> Apps, List<UnreadableApp> Unreadable)> ListAsync(AdoptionCatalog catalog, int port, CancellationToken cancel)
    {
        var listing = await catalog.ReadListingAsync(cancel).ConfigureAwait(false);
        var apps = new List<AppView>();
        var unreadable = new List<UnreadableApp>(listing.Unreadable);
        foreach (var a in listing.Apps)
        {
            try
            {
                apps.Add(View(a, port, await catalog.CanRevertAsync(a, cancel).ConfigureAwait(false), catalog.OpenUsage(a.Id).LastUsedOn));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadable.Add(new UnreadableApp(a.Id, UnreadableApp.CannotOpen, exception.Message));
            }
        }

        return (apps, unreadable);
    }

    private static AppView View(AdoptedApp app, int port, bool canRevert, DateOnly? lastUsed = null) =>
        new(app.Id, RuntimeHost.AppOrigin(app.Id, port).ToString(), app.AdoptedAt, app.Source.Sha256, app.Source.OriginalPath, app.Source.Size,
            app.Revision, app.RevisedAt, canRevert, lastUsed?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), app.ArchivedAt);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static UsageView UsageOf(UsageLog log)
    {
        var used = log.UsedDays;
        var retention = Retention.Of(used, log.Today);
        return new UsageView(
            Iso(log.Today),
            used is [var first, ..] ? Iso(first) : null,
            used is [.., var last] ? Iso(last) : null,
            retention.Day,
            System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(retention.State.ToString()),
            log.Days.Select(d => new UsageDayView(Iso(d.Date), d.Opened, d.Input, d.Wrote, d.LoadErrors, d.LossSuspected, d.Repaired)).ToList(),
            log.Revisions.Select(r => new RevisionEventView(Iso(r.Date), r.Reverted ? "reverted" : "revised")).ToList(),
            log.KeyReports.Select(k => new KeyReportView(k.Revision, Iso(k.Date), k.Missing, k.Unread, k.Seeded)).ToList());
    }

    private static Task WriteAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken) =>
        response.WriteAsJsonAsync(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ControlJson.Default.GetTypeInfo(typeof(T))!, cancellationToken: cancellationToken);

    /// <summary>
    /// An adopted application. <c>Sha256</c>, <c>OriginalPath</c> and <c>Size</c> describe the revision in use;
    /// <c>CanRevert</c> says whether there is a previous revision to go back to. <c>LastUsed</c>
    /// (local <c>yyyy-MM-dd</c>) is filled in the listing only.
    /// </summary>
    internal sealed record AppView(string Id, string Origin, DateTimeOffset AdoptedAt, string Sha256, string? OriginalPath, long Size,
        int Revision, DateTimeOffset? RevisedAt, bool CanRevert, string? LastUsed = null, DateTimeOffset? ArchivedAt = null);

    /// <summary>An earlier adoption and how it matches: <c>"sameBytes"</c> or <c>"sameOriginalPath"</c>.</summary>
    internal sealed record MatchView(AppView App, string Match);

    /// <summary>
    /// Today's facts about one application. Structured only — turning them into sentences for a
    /// person is the caller's job, in the person's language.
    /// </summary>
    internal sealed record AppStatus(string Date, bool Opened, bool Input, bool Wrote, int LoadErrors, IReadOnlyList<string> RecentLoadErrors,
        IReadOnlyList<string> NeedsKey, IReadOnlyList<BlockedResource> Blocked, IReadOnlyList<string> MissingFiles, IReadOnlyList<MissingApi> MissingApis, int CachedAssets);

    /// <summary>
    /// An application's usage record. <c>FirstUsed</c> is day 0 of the retention rule (<c>null</c> until
    /// the first day of use); <c>Day</c> counts from it to <c>Today</c>. <c>Retention</c> is one of
    /// <c>not-started</c>, <c>too-early</c>, <c>in-window</c>, <c>retained</c>, <c>lapsed</c>. <c>Keys</c> is the latest
    /// report per revision of how its pages read the stored data — a revision that asks for keys the data
    /// lacks while leaving stored keys unread may have changed the data's shape.
    /// </summary>
    internal sealed record UsageView(string Today, string? FirstUsed, string? LastUsed, int? Day, string Retention,
        IReadOnlyList<UsageDayView> Days, IReadOnlyList<RevisionEventView> Revisions, IReadOnlyList<KeyReportView> Keys);

    internal sealed record KeyReportView(int Revision, string Date, int Missing, int Unread, int Seeded);

    /// <summary>
    /// Every application's usage record in one document (<c>bohm.usage-report/0</c>). It identifies
    /// applications only by their random ids and carries no names, original paths or data — only when
    /// they were added, which revision runs, and the usage record. <c>GeneratedAt</c> carries the
    /// local offset so days can be read as the person's days.
    /// </summary>
    internal sealed record UsageReport(string Format, DateTimeOffset GeneratedAt, IReadOnlyList<ReportedApp> Apps)
    {
        public const string FormatName = "bohm.usage-report/0";
    }

    /// <param name="ArchivedOn">The day the person put the application away, if they did — a day, like
    /// <c>AdoptedOn</c>, never a time. Someone judging the report reads it next to a lapse: an application
    /// put away may simply have served its purpose.</param>
    /// <param name="RemovedOn">The day the person removed the application for good, if they did. Its usage record is kept for this report.</param>
    internal sealed record ReportedApp(string Id, string AdoptedOn, int Revision, UsageView Usage, string? ArchivedOn = null, string? RemovedOn = null);

    internal sealed record RemovedView(string Id, DateTimeOffset RemovedAt);

    internal sealed record ExportedView(string Id, string Path);

    private static Func<string, CancellationToken, Task> DiscardOf(RuntimeHostOptions options) =>
        options.Discard ?? ((folder, _) =>
        {
            if (Bohm.Runtime.Files.RecycleBin.Available)
            {
                Bohm.Runtime.Files.RecycleBin.Send(folder);
            }
            else
            {
                var discarded = Path.Combine(options.DataRoot, "discarded");
                Directory.CreateDirectory(discarded);
                Directory.Move(folder, Path.Combine(discarded, Path.GetFileName(folder)));
            }

            return Task.CompletedTask;
        });

    internal sealed record UsageDayView(string Date, bool Opened, bool Input, bool Wrote, int LoadErrors, int LossSuspected, int Repaired);

    internal sealed record RevisionEventView(string Date, string Event);

    internal sealed record AssetView(string Url, long Size, string? Reason = null);

    internal sealed record AssetsView(IReadOnlyList<AssetView> Cached, IReadOnlyList<AssetView> NotCached);

    private static LocalModelView LocalModelViewOf(LocalModel local) =>
        new(local.Current?.ModelPath, local.Loaded, local.Fixed, local.Loading, local.LastFailure);

    /// <param name="ModelPath">The model file, or <see langword="null"/> when none is chosen.</param>
    /// <param name="Failure">Why the last load failed — a reason and its values, no sentence — or <see langword="null"/>.</param>
    internal sealed record LocalModelView(string? ModelPath, bool Loaded, bool Fixed, bool Loading, LocalModelFailure? Failure);

    private static ProviderView ProviderViewOf(LlmProvider provider, bool connected, LocalModel local) =>
        new(provider.Id, provider.DisplayName, provider.Host, connected,
            !connected && local.Configured && OpenAIChatBridge.Handles(provider, "POST", "chat/completions"));

    /// <param name="AnsweredLocally">Whether, with no key connected, the model on this computer answers this provider's chat requests.</param>
    internal sealed record ProviderView(string Id, string Name, string Host, bool Connected, bool AnsweredLocally);

    internal sealed record DrainResult(bool Quiet);

    internal sealed record TabView(long Ack, long Issued, bool Left);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AppView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.AppView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.MatchView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<UnreadableApp>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AppStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.UsageView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.UsageReport))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.DrainResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.TabView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(EgressSnapshot))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BlockedResource))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MissingApi))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AssetsView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProviderView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.LocalModelView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.RemovedView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ExportedView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.ProviderView>))]
internal sealed partial class ControlJson : System.Text.Json.Serialization.JsonSerializerContext;
