using System.Diagnostics;
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
        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        var memory = await catalog.ImportMemoryAsync(appId, cancel).ConfigureAwait(false);
        var kinds = await catalog.GetCollectionsAsync(appId, cancel).ConfigureAwait(false);
        var collections = TableImport.Collections(app.Storage.GetItems())
            .Select(c => c with { Declaration = PersonNames.Recall(c.Declaration, kinds) })   // a table written out with the person's names comes back in
            .Select(c => memory.TryGetValue(c.Declaration.Collection, out var settled) ? c with { Declaration = settled.Recall(c.Declaration) } : c)
            .Select(c => PersonNames.For(kinds, c.Declaration.Collection) is var kind
                ? new CollectionView(c.Declaration.Collection, c.Records,
                    [.. c.Declaration.Fields.Select(f => new FieldView(f.Name, f.Kind, kind?.Fields.FirstOrDefault(n => n.Key == f.Name).Value))], c.Declaration.Identity, kind?.Label)
                : throw new UnreachableException())
            .ToList();
        var imports = await catalog.TableImportsAsync(appId, cancel).ConfigureAwait(false);
        var fileTypes = AppFileInputs.Accepts(System.Text.Encoding.UTF8.GetString(await catalog.ReadHtmlAsync(appId, cancel).ConfigureAwait(false)));
        await WriteAsync(context.Response, new ImportsView(collections, imports, fileTypes), TableImportJson.Default.ImportsView, cancel).ConfigureAwait(false);
    }

    public static async Task PreviewAsync(HttpContext context, string appId, CancellationToken cancel)
    {
        var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        var items = app.Storage.GetItems();
        if (await ReadRequestAsync(context, appId, items, cancel).ConfigureAwait(false) is not { } request) return;
        var plan = TableImport.Plan(request.Declaration, request.File, items.GetValueOrDefault(request.Declaration.Collection), request.SameRecord, request.Columns);
        await WriteAsync(context.Response, PlanView.Of(plan, request.File.CodePage), TableImportJson.Default.PlanView, cancel).ConfigureAwait(false);
    }

    public static async Task ImportAsync(HttpContext context, string appId, CancellationToken cancel)
    {
        var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(appId).ConfigureAwait(false);
        if (await ReadRequestAsync(context, appId, app.Storage.GetItems(), cancel).ConfigureAwait(false) is not { } request) return;
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
    private static async Task<ImportRequest?> ReadRequestAsync(HttpContext context, string appId, IReadOnlyDictionary<string, string> items, CancellationToken cancel)
    {
        var memory = await context.RequestServices.GetRequiredService<AdoptionCatalog>().ImportMemoryAsync(appId, cancel).ConfigureAwait(false);
        string? problem;
        try
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancel).ConfigureAwait(false);
            var root = body.RootElement;
            var file = root.GetProperty("file");
            var name = file.GetProperty("name").GetString() ?? "";
            // Bytes that are not UTF-8 are read in the code page the person chose (`codePage`), else this computer's double-byte one.
            var legacy = file.TryGetProperty("codePage", out var page) && page.ValueKind == JsonValueKind.Number ? TableFile.StrictLegacy(page.GetInt32()) : LegacyText.OfThisComputer();
            var table = TableFile.Read(file.GetProperty("content").GetBytesFromBase64(), name, legacy);

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
            // What earlier imports settled (headers put into fields of another name) matches columns by name again.
            var named = found is null ? null : PersonNames.Recall(found.Declaration, await context.RequestServices.GetRequiredService<AdoptionCatalog>().GetCollectionsAsync(appId, cancel).ConfigureAwait(false));
            var declaration = named is not null && memory.TryGetValue(named.Collection, out var settled) ? settled.Recall(named) : named;
            if (problem is null) return new ImportRequest(declaration! with { Identity = identity }, table, name, sameRecord, columns);
        }
        catch (TableFileException e)
        {
            problem = e.Problem;
        }
        catch (FormatException)
        {
            // The one thing a caller cannot guess from the field's name: the file goes in as its bytes, base64-encoded.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await Control.ControlProblems.RefuseAsync(context.Response, "bad-request", new { Detail = "file.content is not base64: send the file's bytes base64-encoded." }).ConfigureAwait(false);
            return null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            problem = "bad-request";
        }

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await Control.ControlProblems.RefuseAsync(context.Response, problem).ConfigureAwait(false);
        return null;
    }

    private static Task WriteAsync<T>(HttpResponse response, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancel)
    {
        response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(response.Body, value, type, cancel);
    }

    /// <param name="Label">The person's name for the field (<see cref="PersonNames"/>), when the application keeps one.</param>
    internal sealed record FieldView(string Name, string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null);

    /// <param name="Identity">The field earlier imports told the same record apart by, or <see langword="null"/>.</param>
    /// <param name="Label">The person's name for the records (<see cref="PersonNames"/>), when the application keeps one.</param>
    internal sealed record CollectionView(string Collection, int Records, IReadOnlyList<FieldView> Fields, string? Identity,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Label = null);

    /// <param name="FileTypes">The file extensions the application's own file inputs take (<see cref="AppFileInputs"/>) — a table file of one of them is better taken in by the application itself.</param>
    internal sealed record ImportsView(IReadOnlyList<CollectionView> Collections, IReadOnlyList<TableImportRecord> Imports, IReadOnlyList<string> FileTypes);

    /// <param name="CodePage">The legacy code page the file was read in — <see langword="null"/> for UTF-8.</param>
    internal sealed record PlanView(IReadOnlyList<ColumnMapping> Columns, IReadOnlyList<string> Unfilled, int Added, int Replaced, int Skipped,
        IReadOnlyList<InvalidRow> Invalid, IReadOnlyList<JsonObject> Sample, string? Generated, int? CodePage)
    {
        public static PlanView Of(ImportPlan plan, int? codePage) => new(plan.Columns, plan.Unfilled, plan.Added, plan.Replaced, plan.Skipped, plan.Invalid, plan.Sample, plan.Generated, codePage);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TableImportEndpoints.ImportsView))]
[JsonSerializable(typeof(TableImportEndpoints.PlanView))]
[JsonSerializable(typeof(TableImportRecord))]
internal sealed partial class TableImportJson : JsonSerializerContext;
