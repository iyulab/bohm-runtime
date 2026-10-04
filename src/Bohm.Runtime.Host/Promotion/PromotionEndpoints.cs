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
            || (request.Pages ?? []).Any(p => p is null || string.IsNullOrWhiteSpace(p.Url) || p.Tables is null || p.Tables.Any(t => t is null || t.Headers is null || t.Preview is null)))
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

        AppProposal proposal;
        try
        {
            var vault = context.RequestServices.GetRequiredService<ICredentialVault>();
            var keyless = context.RequestServices.GetRequiredService<CompanyModel>().Configured || context.RequestServices.GetRequiredService<LocalModel>().Configured;
            proposal = await AppProposals.ProposeAsync(model.Client, model.Limits, request, cancel,
                request.FromInstruction ? AppAi.Line(p => !string.IsNullOrEmpty(vault.Read(p.VaultName)), keyless, editModel.Chosen) : null).ConfigureAwait(false);
        }
        catch (Exception e) when (!cancel.IsCancellationRequested)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await WriteAsync(response, new Control.ControlPlane.ProposalFailure(null, e.Message, Edit.ProviderRefusal.Of(e), (e as Edit.ProposalFailedException)?.Stopped), cancel).ConfigureAwait(false);
            return;
        }

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

    /// <summary>What was read for one source: the page, the columns and the rows, one cell per column.</summary>
    internal sealed record ReadingInput(string? Source, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<string>>? Rows);

    internal sealed record PreviewRequest(string? Html, IReadOnlyDictionary<string, ReadingInput?>? Readings);

    internal sealed record SourceInput(string? Name, SourceRule? Rule);

    internal sealed record PromotionRequest(string? Html, string? Title, IReadOnlyList<SourceInput>? Sources, IReadOnlyDictionary<string, ReadingInput?>? Readings);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppRequest))]
[JsonSerializable(typeof(PromotionEndpoints.AppProposalView))]
[JsonSerializable(typeof(PromotionEndpoints.PreviewRequest))]
[JsonSerializable(typeof(PromotionEndpoints.PromotionRequest))]
[JsonSerializable(typeof(Control.ControlPlane.ProposalFailure))]
[JsonSerializable(typeof(Control.ControlPlane.PreviewView))]
[JsonSerializable(typeof(Edit.EditModelMissing))]
internal sealed partial class PromotionJson : JsonSerializerContext;
