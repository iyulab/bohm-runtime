using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bohm.Runtime.Storage;
using Bohm.Runtime.TableImports;
using LocalOrigin.Storage;

namespace Bohm.Runtime.Adoption;

/// <summary>One table file imported into an application's data.</summary>
/// <param name="Number">Its number among the application's imports, from 1.</param>
/// <param name="File">The file's name as the person had it.</param>
/// <param name="Collection">The storage key the rows went into.</param>
/// <param name="Undone">Whether the data was put back to how it was before.</param>
public sealed record TableImportRecord(int Number, string File, string Collection, int Added, int Replaced, int Skipped, int Invalid,
    DateTimeOffset TakenAt, bool Undone);

/// <summary>
/// What the person settled for one collection in earlier imports: the field that told the same record
/// apart, and the column headers they put into each field under another name. The next import of a file
/// like it starts from there — the inferred shape grows into a declaration as it is confirmed.
/// </summary>
public sealed record ImportMemory(string? Identity, IReadOnlyDictionary<string, IReadOnlyList<string>> Aliases)
{
    /// <summary><paramref name="inferred"/> with the remembered headers as aliases of its fields, and the remembered identity when it is still a field.</summary>
    public ImportDeclaration Recall(ImportDeclaration inferred) => inferred with
    {
        Fields = [.. inferred.Fields.Select(f => Aliases.TryGetValue(f.Name, out var names) ? f with { Aliases = [.. f.Aliases ?? [], .. names] } : f)],
        Identity = inferred.Identity ?? (inferred.Fields.Any(f => f.Name == Identity) ? Identity : null),
    };
}

public sealed partial class AdoptionCatalog
{
    /// <summary>Format identifier of <c>imports/memory.json</c>.</summary>
    public const string ImportMemoryFormat = "bohm.import-memory/0";

    private const string ImportMemoryFile = "memory.json";
    /// <summary>Format identifier written into every <c>imports/&lt;n&gt;/import.json</c>.</summary>
    public const string ImportFormat = "bohm.import/0";

    private const string ImportsDirectory = "imports";
    private const string ImportFile = "import.json";
    private const string DataAfterFile = "data-after.json";

    /// <summary>
    /// Imports the rows of <paramref name="file"/> into application <paramref name="id"/>'s collection as
    /// <see cref="TableImport.Apply"/> decides, in one journaled write. The data as it was before, and as
    /// it is after, are kept in <c>imports/&lt;n&gt;/</c>, so the import can be undone while nothing has been
    /// written since.
    /// </summary>
    /// <remarks><paramref name="storage"/> must be this application's open storage — the one writer of its files.</remarks>
    /// <exception cref="InvalidOperationException">No row would be added or written over, or the key does not hold a list of records.</exception>
    public async Task<TableImportRecord> ImportTableAsync(string id, KeyValueStore storage, ImportDeclaration declaration, TableFile file, string fileName,
        string sameRecord = SameRecord.Skip, IReadOnlyDictionary<string, string?>? columns = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(declaration);
        RequireValidId(id);
        _ = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"No adopted application '{id}'.");

        var stored = storage.GetItems().GetValueOrDefault(declaration.Collection);
        var plan = TableImport.Plan(declaration, file, stored, sameRecord, columns);
        if (plan.Added + plan.Replaced == 0) throw new InvalidOperationException("No row would be imported.");
        var value = TableImport.Apply(declaration, file, stored, sameRecord, columns);

        var imports = Path.Combine(AppDirectory(id), ImportsDirectory);
        Directory.CreateDirectory(imports);
        var number = HighestRevisionFolder(imports) + 1;
        var record = new TableImportRecord(number, Path.GetFileName(fileName), declaration.Collection, plan.Added, plan.Replaced, plan.Skipped,
            plan.Invalid.Count, DateTimeOffset.UtcNow, Undone: false);

        // Kept aside first, written second: an import interrupted between the two leaves the data as it was.
        var staging = Path.Combine(imports, StagingPrefix + number.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        await storage.SaveSnapshotAsync(Path.Combine(staging, DataBeforeFile), cancellationToken).ConfigureAwait(false);
        await storage.ApplyAsync([KeyValueOperation.Set(declaration.Collection, value)], cancellationToken).ConfigureAwait(false);
        await storage.SaveSnapshotAsync(Path.Combine(staging, DataAfterFile), cancellationToken).ConfigureAwait(false);
        await DurableFile.WriteAtomicallyAsync(Path.Combine(staging, ImportFile), WriteImport(record), cancellationToken).ConfigureAwait(false);
        Directory.Move(staging, ImportFolder(id, number));
        await RememberAsync(id, declaration, plan.Columns, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>
    /// Puts application <paramref name="id"/>'s data back to how it was before import <paramref name="number"/> —
    /// only while it is still exactly what the import left, so nothing written since is lost.
    /// </summary>
    /// <exception cref="InvalidOperationException">The data changed since the import, or it was already undone.</exception>
    public async Task<TableImportRecord> UndoTableImportAsync(string id, int number, KeyValueStore storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        RequireValidId(id);
        var folder = ImportFolder(id, number);
        if (number <= 0 || await ReadImportAsync(folder, cancellationToken).ConfigureAwait(false) is not { } record)
            throw new KeyNotFoundException($"No import {number} of '{id}'.");
        if (record.Undone) throw new InvalidOperationException("The import was already undone.");
        if (await ReadSnapshotAsync(Path.Combine(folder, DataAfterFile), cancellationToken).ConfigureAwait(false) is not { } after
            || !SameItems(storage.GetItems(), after))
            throw new InvalidOperationException("The data changed since the import.");

        await storage.RestoreAsync(Path.Combine(folder, DataBeforeFile), cancellationToken).ConfigureAwait(false);
        var undone = record with { Undone = true };
        await DurableFile.WriteAtomicallyAsync(Path.Combine(folder, ImportFile), WriteImport(undone), cancellationToken).ConfigureAwait(false);
        return undone;
    }

    /// <summary>Every table import into application <paramref name="id"/>, oldest first.</summary>
    public async Task<IReadOnlyList<TableImportRecord>> TableImportsAsync(string id, CancellationToken cancellationToken = default)
    {
        RequireValidId(id);
        var imports = Path.Combine(AppDirectory(id), ImportsDirectory);
        if (!Directory.Exists(imports)) return [];
        var records = new List<TableImportRecord>();
        for (var n = 1; n <= HighestRevisionFolder(imports); n++)
            if (await ReadImportAsync(ImportFolder(id, n), cancellationToken).ConfigureAwait(false) is { } record) records.Add(record);
        return records;
    }

    /// <summary>What was settled for each collection of application <paramref name="id"/> in earlier imports — none when nothing was, or it cannot be read.</summary>
    public async Task<IReadOnlyDictionary<string, ImportMemory>> ImportMemoryAsync(string id, CancellationToken cancellationToken = default)
    {
        RequireValidId(id);
        var memory = new Dictionary<string, ImportMemory>(StringComparer.Ordinal);
        try
        {
            var root = JsonNode.Parse(await DurableFile.ReadAsync(Path.Combine(AppDirectory(id), ImportsDirectory, ImportMemoryFile), cancellationToken).ConfigureAwait(false));
            if (root?["format"]?.GetValue<string>() != ImportMemoryFormat || root["collections"] is not JsonObject collections) return memory;
            foreach (var (key, node) in collections)
            {
                var aliases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                if (node?["aliases"] is JsonObject fields)
                    foreach (var (field, names) in fields)
                        if (names is JsonArray list) aliases[field] = [.. list.Select(n => n?.GetValue<string>()).OfType<string>()];
                memory[key] = new ImportMemory(node?["identity"]?.GetValue<string>(), aliases);
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidOperationException or FormatException)
        {
            // Nothing settled yet, or a file that cannot be used: start again from the stored shape.
        }

        return memory;
    }

    /// <summary>Keeps what this import settled: its identity, and each column the person put into a field of another name.</summary>
    private async Task RememberAsync(string id, ImportDeclaration declaration, IReadOnlyList<ColumnMapping> columns, CancellationToken cancellationToken)
    {
        var all = new Dictionary<string, ImportMemory>(await ImportMemoryAsync(id, cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
        var aliases = all.TryGetValue(declaration.Collection, out var known)
            ? known.Aliases.ToDictionary(p => p.Key, p => p.Value.ToList(), StringComparer.Ordinal)
            : new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (column.Field is not { } field || string.Equals(column.Column, field, StringComparison.Ordinal)) continue;
            if (!aliases.TryGetValue(field, out var names)) aliases[field] = names = [];
            if (!names.Contains(column.Column, StringComparer.Ordinal)) names.Add(column.Column);
        }

        all[declaration.Collection] = new ImportMemory(declaration.Identity, aliases.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal));
        var collections = new JsonObject();
        foreach (var (key, memory) in all.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var fields = new JsonObject();
            foreach (var (field, names) in memory.Aliases.OrderBy(p => p.Key, StringComparer.Ordinal)) fields[field] = new JsonArray([.. names.Select(n => (JsonNode?)JsonValue.Create(n))]);
            collections[key] = new JsonObject { ["identity"] = memory.Identity, ["aliases"] = fields };
        }

        var document = new JsonObject { ["format"] = ImportMemoryFormat, ["collections"] = collections };
        await DurableFile.WriteAtomicallyAsync(Path.Combine(AppDirectory(id), ImportsDirectory, ImportMemoryFile),
            JsonSerializer.SerializeToUtf8Bytes(document, MemoryWriter), cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions MemoryWriter = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private string ImportFolder(string id, int number) =>
        Path.Combine(AppDirectory(id), ImportsDirectory, number.ToString(CultureInfo.InvariantCulture));

    private static bool SameItems(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var v) && string.Equals(v, p.Value, StringComparison.Ordinal));

    private static byte[] WriteImport(TableImportRecord record)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, RecordWriter))
        {
            json.WriteStartObject();
            json.WriteString("format", ImportFormat);
            json.WriteNumber("number", record.Number);
            json.WriteString("file", record.File);
            json.WriteString("collection", record.Collection);
            json.WriteNumber("added", record.Added);
            json.WriteNumber("replaced", record.Replaced);
            json.WriteNumber("skipped", record.Skipped);
            json.WriteNumber("invalid", record.Invalid);
            json.WriteString("takenAt", record.TakenAt);
            json.WriteBoolean("undone", record.Undone);
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>The import kept in <paramref name="folder"/>, or <see langword="null"/> when there is none or it cannot be read.</summary>
    private static async Task<TableImportRecord?> ReadImportAsync(string folder, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await DurableFile.ReadAsync(Path.Combine(folder, ImportFile), cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (!root.TryGetProperty("format", out var format) || !format.ValueEquals(ImportFormat)) return null;
            return new TableImportRecord(root.GetProperty("number").GetInt32(), root.GetProperty("file").GetString() ?? "", root.GetProperty("collection").GetString() ?? "",
                root.GetProperty("added").GetInt32(), root.GetProperty("replaced").GetInt32(), root.GetProperty("skipped").GetInt32(), root.GetProperty("invalid").GetInt32(),
                root.GetProperty("takenAt").GetDateTimeOffset(), root.GetProperty("undone").GetBoolean());
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
