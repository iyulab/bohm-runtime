using System.Globalization;
using System.Text.Json;
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

public sealed partial class AdoptionCatalog
{
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
