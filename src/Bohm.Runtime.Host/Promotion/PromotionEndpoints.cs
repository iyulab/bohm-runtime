using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Adoption;
using Bohm.Runtime.Host.Llm;
using Bohm.Runtime.Sources;

namespace Bohm.Runtime.Host.Promotion;

/// <summary>
/// Making a new application — from what the person asked, or from an answer and the pages behind it — for the
/// control API: <c>POST /__control/apps/proposals</c> (a proposal), <c>POST /__control/previews</c> (a look at it
/// with the rows just read) and <c>POST /__control/apps/promotions</c> (taking it in). Nothing is kept before the
/// last — taking it in is the person's permission to read its sources, if it has any.
/// </summary>
internal static class PromotionEndpoints
{
    /// <summary>What the model that writes applications lacks: the model on this computer is too small to write one.</summary>
    public const string NeedsLargerModel = "largerModel";

    public static async Task ProposeAsync(HttpContext context, CancellationToken cancel)
    {
        var response = context.Response;
        if (await ReadAsync(context.Request, PromotionJson.Default.AppRequest, cancel).ConfigureAwait(false) is not { } request
            || string.IsNullOrWhiteSpace(request.Question)
            || (request.Pages ?? []).Any(p => p is null || string.IsNullOrWhiteSpace(p.Url) || p.Tables is null || p.Tables.Any(t => t is null || t.Headers is null || t.Preview is null))
            // A failed proposal is fixed only for an application made from what was asked — one reading pages is made again.
            || request.Broken is { } broken && (!request.FromInstruction || string.IsNullOrWhiteSpace(broken.Html) || broken.Problems is null
                || !broken.Problems.Any(p => !string.IsNullOrWhiteSpace(p))))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // The first problems, each cut short — the rest usually follow from them.
        if (request.Broken is { } told)
            request = request with { Broken = told with { Problems = [.. told.Problems.Where(p => !string.IsNullOrWhiteSpace(p)).Take(10).Select(p => p.Trim() is { Length: > 500 } t ? t[..500] : p.Trim())] } };

        var editModel = context.RequestServices.GetRequiredService<Edit.EditModel>();
        Edit.ChosenEditModel? model;
        try
        {
            model = await editModel.GetAsync(cancel).ConfigureAwait(false);
        }
        catch (LocalModelUnavailableException e)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new Control.ControlPlane.ProposalFailure(e.Failure, null, null), cancel).ConfigureAwait(false);
            return;
        }

        // Writing a whole application is past what the model on this computer does well; it is not asked.
        if (model is null || model.OnThisComputer)
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            await WriteAsync(response, model is null ? editModel.Missing ?? new Edit.EditModelMissing("localModel", null) : new Edit.EditModelMissing(NeedsLargerModel, null), cancel).ConfigureAwait(false);
            return;
        }

        // Asked for lines: one JSON object per line as the model works — { writing, start } for the application's HTML (or a fix's
        // new text) as it is written, { refused } for a proposal sent back to it — then { status: "done", … } with the proposal as
        // the plain answer has it, or, since the status went out before the model was asked, { status: "failed", … } with what a
        // 503 would carry.
        var lines = Control.ControlPlane.AcceptsLines(context.Request);
        Func<Edit.ProposalProgress, CancellationToken, Task>? onProgress = null;
        if (lines)
        {
            response.ContentType = Control.ControlPlane.NdJson;
            await response.StartAsync(cancel).ConfigureAwait(false);
            onProgress = (progress, token) => Control.ControlPlane.WriteLineAsync(response, progress, PromotionJson.Default.ProposalProgress, token);
        }

        AppProposal proposal;
        try
        {
            var vault = context.RequestServices.GetRequiredService<ICredentialVault>();
            var company = context.RequestServices.GetRequiredService<CompanyModel>();
            var keyless = company.Configured || context.RequestServices.GetRequiredService<LocalModel>().Configured;
            // Whether a recording is turned into text without a key — asked only for an application made from an instruction:
            // the speech model on this computer (here, or to be got while some AI answers without a key), or the organization's server's.
            var speech = context.RequestServices.GetRequiredService<LocalSpeech>();
            var speechWithoutKey = request.Broken is null && request.FromInstruction
                && ((keyless && speech.Supported) || await speech.DownloadedAsync(cancel).ConfigureAwait(false)
                    || (company.Configured && await company.TranscriptionModelAsync(cancel).ConfigureAwait(false) is not null));
            proposal = request.Broken is not null
                ? await AppProposals.FixAsync(model.Client, model.Limits, request, cancel, onProgress).ConfigureAwait(false)
                : await AppProposals.ProposeAsync(model.Client, model.Limits, request, cancel,
                    request.FromInstruction ? AppAi.Line(p => !string.IsNullOrEmpty(vault.Read(p.VaultName)), keyless, editModel.Chosen, speechWithoutKey) : null,
                    onProgress).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            var (detail, provider, stopped) = (e.Message, Edit.ProviderRefusal.Of(e), (e as Edit.ProposalFailedException)?.Stopped);
            if (lines)
            {
                await Control.ControlPlane.WriteLineAsync(response, new ProposalFailedLine("failed", null, detail, provider, stopped), PromotionJson.Default.ProposalFailedLine, cancel).ConfigureAwait(false);
                return;
            }

            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new Control.ControlPlane.ProposalFailure(null, detail, provider, stopped), cancel).ConfigureAwait(false);
            return;
        }

        if (lines)
            await Control.ControlPlane.WriteLineAsync(response,
                new AppProposalLine("done", proposal.Title, proposal.Html, proposal.Sources, proposal.Summary, model.Name, proposal.Refused), PromotionJson.Default.AppProposalLine, cancel).ConfigureAwait(false);
        else
            await WriteAsync(response, new AppProposalView(proposal.Title, proposal.Html, proposal.Sources, proposal.Summary, model.Name, proposal.Refused), cancel).ConfigureAwait(false);
    }

    public static async Task PreviewAsync(HttpContext context, int port, CancellationToken cancel)
    {
        var response = context.Response;
        if (await ReadAsync(context.Request, PromotionJson.Default.PreviewRequest, cancel).ConfigureAwait(false) is not { Html: { Length: > 0 } html } request
            || ReadingsOf(request.Readings, context.RequestServices.GetRequiredService<TimeProvider>()) is not { } readings)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var token = context.RequestServices.GetRequiredService<AppPreviews>().CreateNew(Encoding.UTF8.GetBytes(html), readings);
        response.StatusCode = StatusCodes.Status201Created;
        await WriteAsync(response, new Control.ControlPlane.PreviewView(token, AppPreviews.Origin(token, port).AbsoluteUri), cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes in a proposed application with its sources allowed and the rows read for them — everything
    /// checked first, so a refusal leaves nothing behind.
    /// </summary>
    public static async Task<AdoptedApp?> PromoteAsync(HttpContext context, CancellationToken cancel)
    {
        var response = context.Response;
        var request = await ReadAsync(context.Request, PromotionJson.Default.PromotionRequest, cancel).ConfigureAwait(false);
        // An application made from an instruction alone reads no source: its sources are none, not missing.
        if (request is not { Html: { Length: > 0 } html } || !Valid(request))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return null;
        }

        var sources = request.Sources ?? [];

        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        var app = await catalog.AdoptAsync(Encoding.UTF8.GetBytes(html), title: string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim(), cancellationToken: cancel).ConfigureAwait(false);
        var open = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(app.Id).ConfigureAwait(false);
        foreach (var source in sources)
        {
            await open.Sources.DeclareAsync(source.Name!, source.Rule!, granted: true, cancel).ConfigureAwait(false);
            if (request.Readings?.GetValueOrDefault(source.Name!) is { } reading)
                await open.Sources.RecordAsync(source.Name!, reading.Source!, reading.Columns!, reading.Rows!, cancel).ConfigureAwait(false);
        }

        return app;
    }

    private static bool Valid(PromotionRequest request)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in request.Sources ?? [])
        {
            if (source is not { Name: { } name, Rule: { } rule } || !AppSources.IsValidName(name) || !names.Add(name)) return false;
            try
            {
                SourceRule.Validate(rule);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        foreach (var (name, reading) in request.Readings ?? new Dictionary<string, ReadingInput?>())
        {
            var rule = request.Sources?.FirstOrDefault(s => s.Name == name)?.Rule;
            if (rule is null || reading is not { Source: { } page, Columns: { } columns, Rows: { } rows } || rows.Any(r => r is null)
                || AppSources.Check(rule, new SourceGrant(rule.Site, default), page, columns, rows) != RecordOutcome.Recorded)
                return false;
        }

        return true;
    }

    private static Dictionary<string, SourceReading>? ReadingsOf(IReadOnlyDictionary<string, ReadingInput?>? readings, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var result = new Dictionary<string, SourceReading>(StringComparer.Ordinal);
        foreach (var (name, reading) in readings ?? new Dictionary<string, ReadingInput?>())
        {
            if (!AppSources.IsValidName(name) || reading is not { Source: { } page, Columns: { } columns, Rows: { } rows } || rows.Any(r => r is null || r.Count != columns.Count)) return null;
            result[name] = new SourceReading(now, page,
                [.. rows.Select(r => (IReadOnlyDictionary<string, string>)columns.Zip(r).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal))]);
        }

        return result;
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

    private static Task WriteAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken) =>
        response.WriteAsJsonAsync(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)PromotionJson.Default.GetTypeInfo(typeof(T))!, cancellationToken: cancellationToken);

    /// <param name="Title">A short name for the application, in the person's language.</param>
    /// <param name="Html">The whole application.</param>
    /// <param name="Sources">Its sources: each a name, the page (1-based) its rule was made from, and the rule.</param>
    /// <param name="Summary">The model's sentence on what the application shows.</param>
    /// <param name="Model">Which model proposed it.</param>
    internal sealed record AppProposalView(string Title, string Html, IReadOnlyList<ProposedSource> Sources, string Summary, string Model, IReadOnlyList<string> Refused);

    /// <summary>The last line of a proposal made as it goes: <see cref="AppProposalView"/>'s fields, with the status a last line has (<c>done</c>).</summary>
    internal sealed record AppProposalLine(string Status, string Title, string Html, IReadOnlyList<ProposedSource> Sources, string Summary, string Model, IReadOnlyList<string> Refused);

    /// <summary>The last line of a proposal the model could not make: the fields of the 503's body, with the status <c>failed</c>.</summary>
    internal sealed record ProposalFailedLine(string Status, LocalModelFailure? Model, string? Detail, Edit.ProviderRefusal? Provider, string? Stopped);

    /// <summary>What was read for one source: the page, the columns and the rows, one cell per column.</summary>
    internal sealed record ReadingInput(string? Source, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<string>>? Rows);

    internal sealed record PreviewRequest(string? Html, IReadOnlyDictionary<string, ReadingInput?>? Readings);

    internal sealed record SourceInput(string? Name, SourceRule? Rule);

    internal sealed record PromotionRequest(string? Html, string? Title, IReadOnlyList<SourceInput>? Sources, IReadOnlyDictionary<string, ReadingInput?>? Readings);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppRequest))]
[JsonSerializable(typeof(PromotionEndpoints.AppProposalView))]
[JsonSerializable(typeof(PromotionEndpoints.AppProposalLine))]
[JsonSerializable(typeof(PromotionEndpoints.ProposalFailedLine))]
[JsonSerializable(typeof(Edit.ProposalProgress))]
[JsonSerializable(typeof(PromotionEndpoints.PreviewRequest))]
[JsonSerializable(typeof(PromotionEndpoints.PromotionRequest))]
[JsonSerializable(typeof(Control.ControlPlane.ProposalFailure))]
[JsonSerializable(typeof(Control.ControlPlane.PreviewView))]
[JsonSerializable(typeof(Edit.EditModelMissing))]
internal sealed partial class PromotionJson : JsonSerializerContext;
