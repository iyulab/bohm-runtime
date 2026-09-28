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
/// <item><term><c>GET /__control/runtime</c></term><description>Facts about this runtime: <c>contract</c>, the edition of the application contract it serves (<see cref="AppContract"/>).</description></item>
/// <item><term><c>GET /__control/apps</c></term><description>Adopted applications, oldest first, each with the last day it was used; an unsaved result says so, with when it was left and when it expires.</description></item>
/// <item><term><c>GET /__control/apps/unreadable</c></term><description>Application folders that could not be read — kind <c>cannotOpen</c> (the system could not open a file right now, e.g. kept only in the cloud while offline), <c>damaged</c>, or <c>interruptedRemoval</c> (a removal for good stopped before the recycle bin; the folder is still there). Nothing in them is changed; one unreadable application never hides the others.</description></item>
/// <item><term><c>POST /__control/apps/matches</c></term><description>Earlier adoptions of the HTML in the body, or of a file at the same path (optional <c>X-Bohm-Original-Path</c>), each with how it matches.</description></item>
/// <item><term><c>POST /__control/apps</c></term><description>Adopts the HTML in the body (optional <c>X-Bohm-Original-Path</c>, URL-encoded).</description></item>
/// <item><term><c>POST /__control/apps/{id}/revisions</c></term><description>Takes in the HTML in the body as a new revision of the application: same application, same data, new code (optional <c>X-Bohm-Original-Path</c>). Pages still running the old code can no longer write.</description></item>
/// <item><term><c>POST /__control/apps/{id}/archive</c> · <c>/restore</c></term><description>Puts the application away or brings it back. Only a mark on its record changes — code, data, revisions and usage record stay; an archived application is not served. The caller closes its pages first.</description></item>
/// <item><term><c>POST /__control/results</c></term><description>Makes a page out of an answer the person was given — <c>{ title, text, sources?: [{ name, url? }], lang?, sourcesHeading? }</c> — and adds it as an unsaved result (201, the application). The page is the text as read, escaped, with its sources; no script. 400 when the body is not that shape.</description></item>
/// <item><term><c>POST /__control/apps/{id}/keep</c></term><description>Keeps an unsaved result: it becomes one of the person's applications, with its data. 404 for an unknown id.</description></item>
/// <item><term><c>POST /__control/apps/{id}/left</c></term><description>The person left an unsaved result (closed its tab): its retention counts from now, and serving its page again ends it. Nothing changes for a saved application.</description></item>
/// <item><term><c>POST /__control/apps/{id}/export</c></term><description>Copies the application's folder, as it is, to the new folder whose full path is the body — the exchange format is the folder itself. The data is checkpointed first; the original is unchanged. 409 when something with that name is already there or its parent is missing.</description></item>
/// <item><term><c>POST /__control/apps/import</c></term><description>Takes in the exported application folder whose full path is the body, as it is — same identity, data, revisions and usage record. 400 when it is not an application folder; 409 when the application is already here (nothing is replaced).</description></item>
/// <item><term><c>DELETE /__control/apps/{id}</c></term><description>Removes an archived application, or an unsaved result, for good: its folder goes to the recycle bin (the operating system's way back); its usage record stays and keeps appearing in the usage report with the day it was removed. 409 when the application is not archived.</description></item>
/// <item><term><c>POST /__control/apps/{id}/proposals</c></term><description>Proposes a change to the application's current source: the body is <c>{ instruction, target: { html, text? } }</c> — what the person asked and the element they pointed at. Answers <c>{ html, summary, edits: [{ old, new }], model }</c>; nothing is applied (taking it in is a new revision). Made with the model chosen for proposals (<c>/__control/edit/model</c>). 409 with what is missing (<c>{ needs: "localModel" | "key", provider }</c>), 503 with why when the model cannot run or stops.</description></item>
/// <item><term><c>POST /__control/apps/{id}/revisions/revert</c></term><description>Goes back to the previous revision, code and data together; what the revision being left wrote is kept aside.</description></item>
/// <item><term><c>GET /__control/apps/{id}/usage</c></term><description>The application's usage record: each recorded day's signals and load failures, its revisions, its first and last day of use and where it stands against the 30-day retention rule. Days are local; nothing leaves this computer.</description></item>
/// <item><term><c>GET /__control/usage-report</c></term><description>Every application's usage record in one document the person can read and choose to hand over: application ids, days, signals, revisions and retention — no names, paths or content. Nothing is sent; the caller decides what happens to it.</description></item>
/// <item><term><c>GET /__control/apps/{id}/tabs/{tab}</c></term><description>The highest write sequence applied from one loaded page (<c>ack</c>) and the highest sequence the page reported having issued (<c>issued</c>); <c>left</c> once the page's report sent after leaving has arrived, which makes <c>issued</c> final. A host closing the page waits until <c>ack</c> reaches both the sequence it read before the page left and <c>issued</c>; 404 when the page is not (or no longer) the application's.</description></item>
/// <item><term><c>POST /__control/apps/{id}/loss-suspected</c></term><description>Records that a closing page of the application went away before its last writes could be confirmed as applied. Counted per day in the usage record.</description></item>
/// <item><term><c>GET /__control/apps/{id}/status</c></term><description>Today's usage signals, load failures, blocked resources, missing files, calls to a server the application expected (method and path) and keys needed.</description></item>
/// <item><term><c>POST /__control/apps/{id}/assets</c></term><description>Fetches (again) the code the application loads from other hosts; answers what was and was not cached.</description></item>
/// <item><term><c>GET /__control/egress</c></term><description>What left this computer since the runtime started: sent, fetched and blocked, by host.</description></item>
/// <item><term><c>GET /__control/edit/model</c></term><description>The model proposals are made with: <c>{ provider, model, missing }</c> — no provider for the model on this computer (the default); <c>missing</c> says what must be connected first.</description></item>
/// <item><term><c>PUT /__control/edit/model</c></term><description>Chooses a connected provider's model for proposals, from <c>{ provider, model }</c>, and remembers it. The application's source then goes to that provider with each proposal, counted as sent. 400 for an unknown provider or no model name.</description></item>
/// <item><term><c>DELETE /__control/edit/model</c></term><description>Goes back to the model on this computer.</description></item>
/// <item><term><c>GET /__control/llm</c></term><description>AI providers, whether a key is connected (never the key) and what, without one, answers the provider's chat requests: <c>company</c> (the organization's model server), <c>local</c> (the model on this computer) or nothing.</description></item>
/// <item><term><c>PUT /__control/llm/{provider}/key</c></term><description>Connects the key in the body, stored in the vault.</description></item>
/// <item><term><c>DELETE /__control/llm/{provider}/key</c></term><description>Disconnects it.</description></item>
/// <item><term><c>GET /__control/llm/local-model</c></term><description>The model on this computer that answers when no key is connected: its file, whether it is loaded or loading, why the last load failed, and whether it was fixed at start.</description></item>
/// <item><term><c>PUT /__control/llm/local-model</c></term><description>Chooses the model file named in the body (a full path to a <c>.gguf</c> file) and remembers it; 400 when there is no such file, 409 when fixed at start.</description></item>
/// <item><term><c>DELETE /__control/llm/local-model</c></term><description>Chooses none.</description></item>
/// <item><term><c>POST /__control/llm/local-model/load</c></term><description>Starts loading the chosen model now instead of on the first request (202 with the model's state — <c>loading</c> until it is loaded or <c>error</c> says why not); 409 when no model is chosen.</description></item>
/// <item><term><c>GET /__control/llm/company-model</c></term><description>The organization's model server: <c>{ endpoint, model, fixed, keyConnected }</c> — <c>endpoint</c> and <c>model</c> are null when none is set; <c>fixed</c> when it was set at start.</description></item>
/// <item><term><c>PUT /__control/llm/company-model</c></term><description>Sets the server from <c>{ endpoint, model }</c> — its OpenAI-compatible base address (http or https) and a model's name — and remembers it; 400 when either is not usable, 409 when fixed at start.</description></item>
/// <item><term><c>DELETE /__control/llm/company-model</c></term><description>Sets none; 409 when fixed at start.</description></item>
/// <item><term><c>PUT /__control/llm/company-model/key</c> · <c>DELETE</c></term><description>Connects the key in the body for the server, stored in the vault, or disconnects it. Many servers want none.</description></item>
/// <item><term><c>GET /__control/agent/model</c> · <c>PUT</c> · <c>DELETE</c></term><description>The model questions about web pages go to, the same way as <c>/__control/edit/model</c>: by default the organization's model server or the model on this computer; a connected provider's model only when the person chooses one — the pages' text then goes to that provider, counted as sent.</description></item>
/// <item><term><c>POST /__control/agent/turns</c></term><description>One turn of a question about the open web pages: the body is the whole conversation, <c>{ messages: [ { role: "user", text } | { role: "assistant", text?, toolCalls } | { role: "tool", toolCallId, text } ] }</c>, ending with the question or with the results of the calls the last turn asked for. Answers <c>{ status: "done", text, model }</c>, or <c>{ status: "requires_action", text?, toolCalls: [{ id, name, arguments }], model }</c> — calls to <c>list_tabs</c> or <c>read_page</c> for the caller to make and send back. Nothing is kept between turns. Asked of the model chosen at <c>/__control/agent/model</c>; 409 with what is missing (<c>{ needs: "localModel" | "key", provider }</c>), 503 with why when it cannot run or stops.</description></item>
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

                var adopted = await catalog.AdoptAsync(html, OriginalPath(request), cancellationToken: cancel).ConfigureAwait(false);
                if (context.RequestServices.GetRequiredService<RuntimeHostOptions>().FetchAssetsOnAdoption)
                    context.RequestServices.GetRequiredService<AssetFetcher>().Start(adopted.Id);
                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(adopted, port, canRevert: false), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["results"]):
                if (ResultPage.Read(await ReadBodyAsync(request, cancel).ConfigureAwait(false)) is not { } page)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var result = await catalog.AdoptAsync(page.Render(), originalPath: null, unsaved: true, cancel).ConfigureAwait(false);
                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(result, port, canRevert: false), cancel).ConfigureAwait(false);
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

            case ("POST", ["agent", "turns"]):
                await AgentTurnAsync(context, cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var proposalFor, "proposals"]):
                await ProposeAsync(context, proposalFor, cancel).ConfigureAwait(false);
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

            case ("POST", ["apps", var keptId, "keep"]):
                var kept = await catalog.KeepAsync(keptId, cancel).ConfigureAwait(false);
                if (kept is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, View(kept, port, await catalog.CanRevertAsync(kept, cancel).ConfigureAwait(false), catalog.OpenUsage(kept.Id).LastUsedOn), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var leftId, "left"]):
                var left = await catalog.SetLeftAsync(leftId, left: true, cancel).ConfigureAwait(false);
                if (left is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await WriteAsync(response, View(left, port, await catalog.CanRevertAsync(left, cancel).ConfigureAwait(false), catalog.OpenUsage(left.Id).LastUsedOn), cancel).ConfigureAwait(false);
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
                    if (await catalog.GetAsync(removeId, cancel).ConfigureAwait(false) is { ArchivedAt: not null } or { Unsaved: true })
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
                var onlineOnly = OnlineStorage.OnlyOnline(Encoding.UTF8.GetString(await catalog.ReadHtmlAsync(id, cancel).ConfigureAwait(false)));
                var today = app.Usage.Today;
                var signals = app.Usage.SignalsOn(today);
                await WriteAsync(response, new AppStatus(
                    today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    signals.Contains(UsageSignal.Opened), signals.Contains(UsageSignal.Input), signals.Contains(UsageSignal.Wrote),
                    app.Usage.LoadErrorsOn(today), app.RecentLoadErrors, app.NeededKeys, app.Blocked, app.MissingFiles, app.MissingApis, app.Assets.Assets.Count, onlineOnly), cancel).ConfigureAwait(false);
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

            case ("GET", ["runtime"]):
                await WriteAsync(response, new RuntimeFacts(AppContract.Edition), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["egress"]):
                await WriteAsync(response, context.RequestServices.GetRequiredService<Egress>().Snapshot(), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["edit" or "agent", "model"]):
                await WriteAsync(response, EditModelViewOf(ChoiceFor(context, segments[0])), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["edit" or "agent", "model"]):
                var editModel = ChoiceFor(context, segments[0]);
                if (request.Method == "PUT")
                {
                    try
                    {
                        using var body = JsonDocument.Parse(await ReadBodyAsync(request, cancel).ConfigureAwait(false));
                        await editModel.ChooseAsync(new(body.RootElement.GetProperty("provider").GetString() ?? "", body.RootElement.GetProperty("model").GetString() ?? ""), cancel).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }
                }
                else
                {
                    await editModel.ChooseAsync(null, cancel).ConfigureAwait(false);
                }

                await WriteAsync(response, EditModelViewOf(editModel), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm"]):
                var vault = context.RequestServices.GetRequiredService<ICredentialVault>();
                await WriteAsync(response, LlmProviders.All
                    .Select(p => ProviderViewOf(p, !string.IsNullOrEmpty(vault.Read(p.VaultName)), context.RequestServices))
                    .ToList(), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm", "company-model"]):
                await WriteAsync(response, CompanyModelViewOf(context.RequestServices.GetRequiredService<CompanyModel>()), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", "company-model"]):
                var companyModel = context.RequestServices.GetRequiredService<CompanyModel>();
                CompanyModelOptions? setTo = null;
                if (request.Method == "PUT")
                {
                    try
                    {
                        using var body = JsonDocument.Parse(await ReadBodyAsync(request, cancel).ConfigureAwait(false));
                        if (!CompanyModelOptions.TryCreate(body.RootElement.GetProperty("endpoint").GetString(), body.RootElement.GetProperty("model").GetString(), out setTo))
                        {
                            response.StatusCode = StatusCodes.Status400BadRequest;
                            break;
                        }
                    }
                    catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }
                }

                try
                {
                    await companyModel.ChooseAsync(setTo, cancel).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    response.StatusCode = StatusCodes.Status409Conflict;
                    break;
                }

                await WriteAsync(response, CompanyModelViewOf(companyModel), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", "company-model", "key"]):
                var companyKeys = context.RequestServices.GetRequiredService<ICredentialVault>();
                if (request.Method == "DELETE")
                {
                    companyKeys.Delete(CompanyModel.VaultName);
                }
                else
                {
                    var companyKey = Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                    if (companyKey.Length == 0)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }

                    companyKeys.Write(CompanyModel.VaultName, companyKey);
                }

                await WriteAsync(response, CompanyModelViewOf(context.RequestServices.GetRequiredService<CompanyModel>()), cancel).ConfigureAwait(false);
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

                await WriteAsync(response, ProviderViewOf(provider, request.Method != "DELETE", context.RequestServices), cancel).ConfigureAwait(false);
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
            app.Revision, app.RevisedAt, canRevert, lastUsed?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), app.ArchivedAt,
            app.Unsaved, app.LeftAt, app.LeftAt + AdoptionCatalog.UnsavedRetention);

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
    /// (local <c>yyyy-MM-dd</c>) is filled in the listing only. <c>Unsaved</c> marks a result not kept yet;
    /// <c>LeftAt</c> is when the person last left it and <c>ExpiresAt</c> when the next start after it removes it.
    /// </summary>
    internal sealed record AppView(string Id, string Origin, DateTimeOffset AdoptedAt, string Sha256, string? OriginalPath, long Size,
        int Revision, DateTimeOffset? RevisedAt, bool CanRevert, string? LastUsed = null, DateTimeOffset? ArchivedAt = null,
        bool Unsaved = false, DateTimeOffset? LeftAt = null, DateTimeOffset? ExpiresAt = null);

    /// <summary>An earlier adoption and how it matches: <c>"sameBytes"</c> or <c>"sameOriginalPath"</c>.</summary>
    internal sealed record MatchView(AppView App, string Match);

    /// <param name="Contract">The edition of the application contract this runtime serves.</param>
    internal sealed record RuntimeFacts(int Contract);

    /// <summary>
    /// Today's facts about one application. Structured only — turning them into sentences for a
    /// person is the caller's job, in the person's language. <c>OnlineOnlyStorage</c> names the online
    /// database the application keeps its data in when it has no local storage of its own — what it
    /// writes there is not kept (<see cref="OnlineStorage"/>).
    /// </summary>
    internal sealed record AppStatus(string Date, bool Opened, bool Input, bool Wrote, int LoadErrors, IReadOnlyList<string> RecentLoadErrors,
        IReadOnlyList<string> NeedsKey, IReadOnlyList<BlockedResource> Blocked, IReadOnlyList<string> MissingFiles, IReadOnlyList<MissingApi> MissingApis, int CachedAssets,
        string? OnlineOnlyStorage);

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

    internal static Func<string, CancellationToken, Task> DiscardOf(RuntimeHostOptions options) =>
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

    /// <summary>
    /// A proposal for the application's current source, from the model on this computer. Nothing is
    /// kept: the proposal is the answer, and taking it in is the caller's next request.
    /// </summary>
    private static async Task AgentTurnAsync(HttpContext context, CancellationToken cancel)
    {
        var response = context.Response;
        List<Microsoft.Extensions.AI.ChatMessage> conversation;
        try
        {
            using var body = JsonDocument.Parse(await ReadBodyAsync(context.Request, cancel).ConfigureAwait(false));
            conversation = Agent.WebAgent.ParseConversation(body.RootElement);
        }
        catch (Exception e) when (e is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var agentModel = context.RequestServices.GetRequiredService<Edit.AgentModel>();
        Edit.ChosenEditModel? model;
        try
        {
            model = await agentModel.GetAsync(cancel).ConfigureAwait(false);
        }
        catch (LocalModelUnavailableException e)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new ProposalFailure(e.Failure, null, null), cancel).ConfigureAwait(false);
            return;
        }

        if (model is not { } chosen)
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            await WriteAsync(response, agentModel.Missing ?? new Edit.EditModelMissing("localModel", null), cancel).ConfigureAwait(false);
            return;
        }

        Agent.TurnResult turn;
        try
        {
            turn = await Agent.WebAgent.RunTurnAsync(chosen.Client, chosen.Name, chosen.OnThisComputer, conversation, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new ProposalFailure(null, e.Message, Edit.ProviderRefusal.Of(e)), cancel).ConfigureAwait(false);
            return;
        }

        await WriteAsync(response, turn, cancel).ConfigureAwait(false);
    }

    private static async Task ProposeAsync(HttpContext context, string appId, CancellationToken cancel)
    {
        var response = context.Response;
        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        if (await catalog.GetAsync(appId, cancel).ConfigureAwait(false) is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Either «change this» on an element — an instruction and a target — or a named fix the runtime
        // words itself: `{"fix": "local-storage"}` moves an application that keeps its data only online.
        string instruction = "";
        Edit.EditTarget? target = null;
        var source = Encoding.UTF8.GetString(await catalog.ReadHtmlAsync(appId, cancel).ConfigureAwait(false));
        try
        {
            using var body = JsonDocument.Parse(await ReadBodyAsync(context.Request, cancel).ConfigureAwait(false));
            var root = body.RootElement;
            if (root.TryGetProperty("fix", out var fix))
            {
                // Only an application that has the problem: the proposal would otherwise rewrite working storage.
                if (fix.GetString() != "local-storage" || OnlineStorage.OnlyOnline(source) is null)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
            else
            {
                instruction = root.GetProperty("instruction").GetString() ?? "";
                var element = root.GetProperty("target");
                target = new Edit.EditTarget(element.GetProperty("html").GetString() ?? "",
                    element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null);
                if (string.IsNullOrWhiteSpace(instruction))
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var editModel = context.RequestServices.GetRequiredService<Edit.EditModel>();
        Edit.ChosenEditModel? model;
        try
        {
            model = await editModel.GetAsync(cancel).ConfigureAwait(false);
        }
        catch (LocalModelUnavailableException e)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new ProposalFailure(e.Failure, null, null), cancel).ConfigureAwait(false);
            return;
        }

        if (model is null)
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            await WriteAsync(response, editModel.Missing ?? new Edit.EditModelMissing("localModel", null), cancel).ConfigureAwait(false);
            return;
        }

        Edit.EditProposal proposal;
        try
        {
            proposal = await (target is null
                ? Edit.EditProposals.ProposeLocalStorageAsync(model.Client, model.OnThisComputer, source, cancel)
                : Edit.EditProposals.ProposeAsync(model.Client, model.OnThisComputer, source, target, instruction, cancel)).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            // The model stopped without finishing — the local server's request limit, or a provider's refusal,
            // which carries the provider's own status and message so the person can see why.
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new ProposalFailure(null, e.Message, Edit.ProviderRefusal.Of(e)), cancel).ConfigureAwait(false);
            return;
        }

        await WriteAsync(response, new ProposalView(proposal.Html, proposal.Summary, proposal.Edits, model.Name, proposal.Complete, proposal.Left), cancel).ConfigureAwait(false);
    }

    /// <param name="Html">The whole source with the edits made — what to take in as a new revision.</param>
    /// <param name="Summary">The model's sentence on what it changed.</param>
    /// <param name="Edits">Each exact piece replaced and its replacement, in order; empty when nothing changed.</param>
    /// <param name="Model">Which model proposed it: <c>local</c>, or the provider and model name (<c>openai/…</c>).</param>
    /// <param name="Complete">For a named fix, whether it finished — a proposal that stopped halfway is not one to apply. <c>null</c> for a change the person asked for.</param>
    /// <param name="Remaining">For a named fix that did not finish, what it left.</param>
    internal sealed record ProposalView(string Html, string Summary, IReadOnlyList<Edit.SourceEdit> Edits, string Model, bool? Complete, Edit.StorageLeft? Remaining);

    /// <param name="Provider">The chosen provider's id, or <see langword="null"/> for the model on this computer.</param>
    /// <param name="Model">The chosen model's name, or <see langword="null"/>.</param>
    /// <param name="Missing">What is missing before a proposal can be made, or <see langword="null"/>.</param>
    internal sealed record EditModelView(string? Provider, string? Model, Edit.EditModelMissing? Missing);

    private static EditModelView EditModelViewOf(Edit.ProviderChoice model) => new(model.Chosen?.Provider, model.Chosen?.Model, model.Missing);

    /// <summary>The model choice behind <c>/__control/edit/model</c> (proposals) or <c>/__control/agent/model</c> (questions about web pages).</summary>
    private static Edit.ProviderChoice ChoiceFor(HttpContext context, string which) => which == "agent"
        ? context.RequestServices.GetRequiredService<Edit.AgentModel>()
        : context.RequestServices.GetRequiredService<Edit.EditModel>();

    /// <param name="Model">Why the model could not start, when that is why.</param>
    /// <param name="Detail">What stopped the model, when it started and did not finish.</param>
    /// <param name="Provider">The provider's refusal — its status and own message — when a provider refused.</param>
    internal sealed record ProposalFailure(LocalModelFailure? Model, string? Detail, Edit.ProviderRefusal? Provider);

    private static ProviderView ProviderViewOf(LlmProvider provider, bool connected, IServiceProvider services) =>
        new(provider.Id, provider.DisplayName, provider.Host, connected,
            connected || !ChatBridges.AnswersChat(provider) ? null
            : services.GetRequiredService<CompanyModel>().Configured ? "company"
            : services.GetRequiredService<LocalModel>().Configured ? "local"
            : null);

    /// <param name="AnsweredBy">What, with no key connected, answers this provider's chat requests: <c>company</c> (the organization's model server), <c>local</c> (the model on this computer) or <see langword="null"/> (nothing — the application is told to connect a key).</param>
    internal sealed record ProviderView(string Id, string Name, string Host, bool Connected, string? AnsweredBy);

    private static CompanyModelView CompanyModelViewOf(CompanyModel company) =>
        new(company.Current?.Endpoint.AbsoluteUri, company.Current?.Model, company.Fixed, company.KeyConnected);

    /// <param name="Endpoint">The server's OpenAI-compatible base address, or <see langword="null"/> when none is set.</param>
    internal sealed record CompanyModelView(string? Endpoint, string? Model, bool Fixed, bool KeyConnected);

    internal sealed record DrainResult(bool Quiet);

    internal sealed record TabView(long Ack, long Issued, bool Left);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AppView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.AppView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.MatchView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<UnreadableApp>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AppStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.RuntimeFacts))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.UsageView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.UsageReport))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.DrainResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.TabView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(EgressSnapshot))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BlockedResource))]
[System.Text.Json.Serialization.JsonSerializable(typeof(MissingApi))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProposalView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProposalFailure))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.EditModelView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Edit.EditModelMissing))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AssetsView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProviderView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.LocalModelView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.CompanyModelView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(Agent.TurnResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.RemovedView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ExportedView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.ProviderView>))]
internal sealed partial class ControlJson : System.Text.Json.Serialization.JsonSerializerContext;
