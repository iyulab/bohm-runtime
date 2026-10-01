using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Host.Control;
using Bohm.Runtime.TableImports;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Importing a table file into an application's records, for the control API — decided by what the
/// application's stored data shows and what the person chose, never by a model:
/// <c>GET /__control/apps/{id}/imports</c> (what it could go into, and the imports so far),
/// <c>POST …/imports/preview</c> (what an import would do — nothing is written),
/// <c>POST …/imports</c> (doing it) and <c>POST …/imports/{n}/undo</c>.
/// </summary>
/// <remarks>
/// The body of a preview or an import is <c>{ file: { name, content }, collection, identity?, sameRecord?, columns? }</c>:
/// the file's name and its bytes in base64, the storage key its rows go into, the field that tells the same
/// record apart (none: every row is added), <c>skip</c> (the default) or <c>replace</c> for a row whose record is
/// already there, and the columns the person moved or left out (<c>{ header: field | null }</c>).
/// </remarks>
internal static class TableImportEndpoints
{
    public static async Task ListAsync(HttpContext context, string appId, CancellationToken cancel)
    {
        var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        var collections = TableImport.Collections(app.Storage.GetItems())
            .Select(c => new CollectionView(c.Declaration.Collection, c.Records, [.. c.Declaration.Fields.Select(f => new FieldView(f.Name, f.Kind))]))
            .ToList();
        var imports = await context.RequestServices.GetRequiredService<AdoptionCatalog>().TableImportsAsync(appId, cancel).ConfigureAwait(false);
        await WriteAsync(context.Response, new ImportsView(collections, imports), TableImportJson.Default.ImportsView, cancel).ConfigureAwait(false);
    }

    public static async Task PreviewAsync(HttpContext context, string appId, CancellationToken cancel)
    {
        var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        var items = app.Storage.GetItems();
        if (await ReadRequestAsync(context, items, cancel).ConfigureAwait(false) is not { } request) return;
        var plan = TableImport.Plan(request.Declaration, request.File, items.GetValueOrDefault(request.Declaration.Collection), request.SameRecord, request.Columns);
        await WriteAsync(context.Response, PlanView.Of(plan), TableImportJson.Default.PlanView, cancel).ConfigureAwait(false);
    }

    public static async Task ImportAsync(HttpContext context, string appId, CancellationToken cancel)
    {
        var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        if (await ReadRequestAsync(context, app.Storage.GetItems(), cancel).ConfigureAwait(false) is not { } request) return;
        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();

        // Nothing to import is a 409, nothing written.
        var record = await ControlPlane.ChangeDataAsync(context, appId, storage => catalog.ImportTableAsync(appId, storage, request.Declaration, request.File,
            request.FileName, request.SameRecord, request.Columns, cancel)).ConfigureAwait(false);
        if (record is null) return;
        context.Response.StatusCode = StatusCodes.Status201Created;
        await WriteAsync(context.Response, record, TableImportJson.Default.TableImportRecord, cancel).ConfigureAwait(false);
    }

    public static async Task UndoAsync(HttpContext context, string appId, int number, CancellationToken cancel)
    {
        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        if (!(await catalog.TableImportsAsync(appId, cancel).ConfigureAwait(false)).Any(i => i.Number == number))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // The data changed since the import, or it was undone already: a 409, nothing written.
        var undone = await ControlPlane.ChangeDataAsync(context, appId, storage => catalog.UndoTableImportAsync(appId, number, storage, cancel)).ConfigureAwait(false);
        if (undone is null) return;
        await WriteAsync(context.Response, undone, TableImportJson.Default.TableImportRecord, cancel).ConfigureAwait(false);
    }

    private sealed record ImportRequest(ImportDeclaration Declaration, TableFile File, string FileName, string SameRecord, IReadOnlyDictionary<string, string?>? Columns);

    /// <summary>The request, or <see langword="null"/> with a 400 written — <c>{ problem }</c> saying what is wrong with the file or the choice.</summary>
    private static async Task<ImportRequest?> ReadRequestAsync(HttpContext context, IReadOnlyDictionary<string, string> items, CancellationToken cancel)
    {
        string? problem;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancel).ConfigureAwait(false);
            var root = body.RootElement;
            var file = root.GetProperty("file");
            var name = file.GetProperty("name").GetString() ?? "";
            var table = TableFile.Read(file.GetProperty("content").GetBytesFromBase64(), name);

            var collection = root.GetProperty("collection").GetString();
            var identity = root.TryGetProperty("identity", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var sameRecord = root.TryGetProperty("sameRecord", out var same) && same.ValueKind == JsonValueKind.String ? same.GetString()! : SameRecord.Skip;
            Dictionary<string, string?>? columns = null;
            if (root.TryGetProperty("columns", out var chosen) && chosen.ValueKind == JsonValueKind.Object)
            {
                columns = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var column in chosen.EnumerateObject())
                    columns[column.Name] = column.Value.ValueKind == JsonValueKind.String ? column.Value.GetString() : null;
            }

            var found = TableImport.Collections(items).FirstOrDefault(c => c.Declaration.Collection == collection);
            problem = found is null ? "unknown-collection"
                : identity is not null && !found.Declaration.Fields.Any(f => f.Name == identity) ? "unknown-identity"
                : sameRecord is not (SameRecord.Skip or SameRecord.Replace) ? "unknown-same-record"
                : columns?.Values.Any(f => f is not null && !found.Declaration.Fields.Any(d => d.Name == f)) == true ? "unknown-field"
                : null;
            if (problem is null) return new ImportRequest(found!.Declaration with { Identity = identity }, table, name, sameRecord, columns);
        }
        catch (TableFileException e)
        {
            problem = e.Problem;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            problem = "bad-request";
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await WriteAsync(context.Response, new ProblemView(problem), TableImportJson.Default.ProblemView, cancel).ConfigureAwait(false);
        return null;
    }

    private static Task WriteAsync<T>(HttpResponse response, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancel)
    {
        response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(response.Body, value, type, cancel);
    }

    internal sealed record FieldView(string Name, string Kind);

    internal sealed record CollectionView(string Collection, int Records, IReadOnlyList<FieldView> Fields);

    internal sealed record ImportsView(IReadOnlyList<CollectionView> Collections, IReadOnlyList<TableImportRecord> Imports);

    internal sealed record ProblemView(string Problem);

    internal sealed record PlanView(IReadOnlyList<ColumnMapping> Columns, IReadOnlyList<string> Unfilled, int Added, int Replaced, int Skipped,
        IReadOnlyList<InvalidRow> Invalid, IReadOnlyList<JsonObject> Sample)
    {
        public static PlanView Of(ImportPlan plan) => new(plan.Columns, plan.Unfilled, plan.Added, plan.Replaced, plan.Skipped, plan.Invalid, plan.Sample);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TableImportEndpoints.ImportsView))]
[JsonSerializable(typeof(TableImportEndpoints.PlanView))]
[JsonSerializable(typeof(TableImportEndpoints.ProblemView))]
[JsonSerializable(typeof(TableImportRecord))]
internal sealed partial class TableImportJson : JsonSerializerContext;
