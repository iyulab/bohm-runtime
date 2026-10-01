using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bohm.Runtime.TableImports;

/// <summary>What a field holds; a cell is read by the kind of the field it goes into.</summary>
public static class FieldKind
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Boolean = "boolean";
}

/// <summary>One field of the records in a collection.</summary>
/// <param name="Name">The key the application stores it under.</param>
/// <param name="Kind">One of <see cref="FieldKind"/>.</param>
/// <param name="Required">Whether a record without it is left out.</param>
/// <param name="Aliases">Other column headers that mean this field.</param>
public sealed record ImportField(string Name, string Kind, bool Required = false, IReadOnlyList<string>? Aliases = null);

/// <summary>
/// Where rows from a table file go: the storage key holding an array of records, the fields of those
/// records, and the field that tells the same record apart, if any.
/// </summary>
/// <param name="Collection">The storage key whose value is the array of records.</param>
/// <param name="Identity">The field by which a row is the same record as one already there; <see langword="null"/> when rows are only added.</param>
public sealed record ImportDeclaration(string Collection, IReadOnlyList<ImportField> Fields, string? Identity = null);

/// <summary>A collection found in an application's stored data, with its fields as the records show them.</summary>
/// <param name="Records">How many records it holds now.</param>
public sealed record CollectionCandidate(ImportDeclaration Declaration, int Records);

/// <summary>What to do with a row whose identity matches a record already there.</summary>
public static class SameRecord
{
    /// <summary>Leave the record as it is and the row out.</summary>
    public const string Skip = "skip";

    /// <summary>Write the row's cells over the record's fields; fields the file has no column for are kept.</summary>
    public const string Replace = "replace";
}

/// <summary>What one import would do — shown before anything is written.</summary>
/// <param name="Columns">Each column of the file and the field it goes into, <see langword="null"/> for a column that is left out.</param>
/// <param name="Unfilled">Fields no column goes into.</param>
/// <param name="Added">Rows that become new records.</param>
/// <param name="Replaced">Rows that are written over records already there.</param>
/// <param name="Skipped">Rows left out because the record is already there (or repeated in the file).</param>
/// <param name="Invalid">Rows left out, with the row number in the file (header = row 1) and why.</param>
/// <param name="Sample">The first new or replaced records, as they will be stored.</param>
public sealed record ImportPlan(
    IReadOnlyList<ColumnMapping> Columns,
    IReadOnlyList<string> Unfilled,
    int Added,
    int Replaced,
    int Skipped,
    IReadOnlyList<InvalidRow> Invalid,
    IReadOnlyList<JsonObject> Sample);

/// <param name="Column">The header as the file has it.</param>
/// <param name="Field">The field it goes into, or <see langword="null"/>.</param>
public sealed record ColumnMapping(string Column, string? Field);

/// <param name="Row">The row's number in the file, the header being row 1.</param>
/// <param name="Reason">What was wrong, naming the field.</param>
public sealed record InvalidRow(int Row, string Reason);

/// <summary>
/// Rows of a table file put into a collection of an application's records — decided by the declaration
/// and the file alone, never by a model, so the same file into the same data gives the same result.
/// </summary>
public static partial class TableImport
{
    /// <summary>How many records a plan shows.</summary>
    public const int SampleSize = 5;

    private static readonly string[] IdentityNames = ["id", "_id", "uuid", "key"];

    /// <summary>
    /// The collections in an application's stored data: every key whose value is a JSON array of
    /// objects (stored as JSON text, the way pages keep data), with fields in the order the records
    /// first show them and each field's kind as every record agrees on it (text when they do not).
    /// A collection with no records yet has no fields to show and is left out.
    /// </summary>
    public static IReadOnlyList<CollectionCandidate> Collections(IEnumerable<KeyValuePair<string, string>> stored)
    {
        var found = new List<CollectionCandidate>();
        foreach (var (key, value) in stored.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (Records(value) is not { Count: > 0 } records) continue;
            var kinds = new Dictionary<string, string?>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var record in records)
            {
                foreach (var (name, node) in record)
                {
                    var kind = KindOf(node);
                    if (!kinds.TryGetValue(name, out var known)) { kinds[name] = kind; order.Add(name); }
                    else if (known != kind && kind is not null) kinds[name] = known is null ? kind : FieldKind.Text;
                }
            }

            var fields = order.Select(n => new ImportField(n, kinds[n] ?? FieldKind.Text)).ToList();
            found.Add(new CollectionCandidate(new ImportDeclaration(key, fields), records.Count));
        }

        return found;
    }

    /// <summary>
    /// What importing <paramref name="file"/> into the collection would do, given the collection's
    /// stored value (<see langword="null"/> when the key holds nothing). Nothing is written.
    /// </summary>
    /// <param name="columns">Columns the person moved or left out, by header — overriding the matching by name.</param>
    public static ImportPlan Plan(ImportDeclaration declaration, TableFile file, string? stored, string sameRecord = SameRecord.Skip,
        IReadOnlyDictionary<string, string?>? columns = null) =>
        Run(declaration, file, stored, sameRecord, columns).Plan;

    /// <summary>
    /// The collection's new stored value after importing <paramref name="file"/> — JSON text of the
    /// array, the records already there first, in their order, then the new ones in file order.
    /// </summary>
    public static string Apply(ImportDeclaration declaration, TableFile file, string? stored, string sameRecord = SameRecord.Skip,
        IReadOnlyDictionary<string, string?>? columns = null) =>
        Run(declaration, file, stored, sameRecord, columns).Result.ToJsonString();

    private static (ImportPlan Plan, JsonArray Result) Run(ImportDeclaration declaration, TableFile file, string? stored, string sameRecord,
        IReadOnlyDictionary<string, string?>? chosen)
    {
        if (sameRecord is not (SameRecord.Skip or SameRecord.Replace)) throw new ArgumentOutOfRangeException(nameof(sameRecord));
        var existing = stored is null ? [] : Records(stored) ?? throw new InvalidOperationException($"\"{declaration.Collection}\" does not hold a list of records.");

        var mapping = Match(declaration, file.Headers, chosen);
        var fieldsByName = declaration.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var unfilled = declaration.Fields.Where(f => !mapping.Any(m => m.Field == f.Name)).Select(f => f.Name).ToList();

        var result = new JsonArray();
        var index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var record in existing)
        {
            var copy = (JsonObject)record.DeepClone();
            result.Add(copy);
            if (declaration.Identity is { } identity && IdentityText(copy[identity]) is { } id) index.TryAdd(id, copy);
        }

        var generated = GeneratedId(declaration, existing, mapping);
        var nextNumber = generated is { Numeric: true } ? existing.Select(r => r[generated.Value.Field]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Max() : 0;
        var fileTag = generated is { Numeric: false } ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", file.Rows.Select(r => string.Join("\t", r)))))[..4]) : "";

        int added = 0, replaced = 0, skipped = 0;
        var newRecords = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        var invalid = new List<InvalidRow>();
        var sample = new List<JsonObject>();
        for (var r = 0; r < file.Rows.Count; r++)
        {
            var row = file.Rows[r];
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var record = new JsonObject();
            string? problem = null;
            for (var c = 0; c < mapping.Count && problem is null; c++)
            {
                if (mapping[c].Field is not { } name) continue;
                var cell = row[c].Trim();
                if (cell.Length == 0) continue;
                if (Value(fieldsByName[name].Kind, cell) is { } value) record[name] = value;
                else problem = $"\"{cell}\" is not a {fieldsByName[name].Kind} for {name}";
            }

            problem ??= declaration.Fields.FirstOrDefault(f => f.Required && record[f.Name] is null) is { } missing ? $"{missing.Name} is empty" : null;
            if (problem is not null) { invalid.Add(new InvalidRow(r + 2, problem)); continue; }

            if (declaration.Identity is { } key && IdentityText(record[key]) is { } id && index.TryGetValue(id, out var there))
            {
                // Already there, or earlier in the same file: left out, or written over (the later row wins).
                if (sameRecord == SameRecord.Skip) { skipped++; continue; }
                foreach (var (name, value) in record) there[name] = value?.DeepClone();
                if (!newRecords.Contains(there))
                {
                    replaced++;
                    if (sample.Count < SampleSize) sample.Add(there);
                }

                continue;
            }

            if (generated is { } g)
            {
                record[g.Field] = g.Numeric ? JsonValue.Create(++nextNumber) : JsonValue.Create($"import-{fileTag}-{r + 2}");
            }

            result.Add(record);
            newRecords.Add(record);
            if (declaration.Identity is { } k && IdentityText(record[k]) is { } newId) index.TryAdd(newId, record);
            added++;
            if (sample.Count < SampleSize) sample.Add(record);
        }

        var plan = new ImportPlan(mapping, unfilled, added, replaced, skipped, invalid, [.. sample.Select(s => (JsonObject)s.DeepClone())]);
        return (plan, result);
    }

    /// <summary>
    /// Each column's field: the person's choice when there is one, otherwise the field whose name or
    /// alias is the header — exactly, then ignoring case, then ignoring spaces and punctuation. A field
    /// takes one column; a later column that would match a taken field is left out.
    /// </summary>
    private static List<ColumnMapping> Match(ImportDeclaration declaration, IReadOnlyList<string> headers, IReadOnlyDictionary<string, string?>? chosen)
    {
        var names = declaration.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        if (chosen is not null) foreach (var field in chosen.Values) if (field is not null) taken.Add(field);

        var mapping = new List<ColumnMapping>(headers.Count);
        foreach (var header in headers)
        {
            if (chosen is not null && chosen.TryGetValue(header, out var picked))
            {
                mapping.Add(new ColumnMapping(header, picked is not null && names.Contains(picked) ? picked : null));
                continue;
            }

            var field = FindField(declaration, header, taken);
            if (field is not null) taken.Add(field);
            mapping.Add(new ColumnMapping(header, field));
        }

        return mapping;
    }

    private static string? FindField(ImportDeclaration declaration, string header, HashSet<string> taken)
    {
        Func<string, string, bool>[] ways =
        [
            (a, b) => string.Equals(a, b, StringComparison.Ordinal),
            (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase),
            (a, b) => Loose(a) == Loose(b) && Loose(a).Length > 0,
        ];
        foreach (var same in ways)
        {
            foreach (var field in declaration.Fields)
            {
                if (taken.Contains(field.Name)) continue;
                if (same(field.Name, header) || field.Aliases?.Any(a => same(a, header)) == true) return field.Name;
            }
        }

        return null;
    }

    private static string Loose(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>
    /// A field the application fills with its own unique key (named like <c>id</c>, every record has
    /// one and no two share it) that no column fills: new records get the next number, or a key made
    /// from the file's content and the row's number — the same file gives the same keys.
    /// </summary>
    private static (string Field, bool Numeric)? GeneratedId(ImportDeclaration declaration, List<JsonObject> existing, List<ColumnMapping> mapping)
    {
        if (existing.Count == 0) return null;
        foreach (var name in IdentityNames)
        {
            var field = declaration.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (field is null || mapping.Any(m => m.Field == field.Name)) continue;
            var values = existing.Select(r => r[field.Name]).ToList();
            if (values.Any(v => v is null) || values.Select(IdentityText).Distinct().Count() != values.Count) continue;
            if (values.All(v => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number)) return (field.Name, true);
            if (values.All(v => v is JsonValue j && j.GetValueKind() == JsonValueKind.String)) return (field.Name, false);
        }

        return null;
    }

    /// <summary>The records in a stored value — a JSON array of objects — or <see langword="null"/> when it is not one.</summary>
    private static List<JsonObject>? Records(string value)
    {
        try
        {
            if (JsonNode.Parse(value) is not JsonArray array) return null;
            var records = new List<JsonObject>(array.Count);
            foreach (var item in array)
            {
                if (item is not JsonObject record) return null;
                records.Add(record);
            }

            return records;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? KindOf(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => FieldKind.Number,
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => FieldKind.Boolean,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => IsoDate().IsMatch(v.GetValue<string>()) ? FieldKind.Date : FieldKind.Text,
        null => null,
        _ => FieldKind.Text,
    };

    private static string? IdentityText(JsonNode? node) => node is JsonValue v ? v.ToJsonString() : null;

    /// <summary>A cell as the field's kind, or <see langword="null"/> when it is not one.</summary>
    private static JsonValue? Value(string kind, string cell) => kind switch
    {
        FieldKind.Number => double.TryParse(Grouped().IsMatch(cell) ? cell.Replace(",", "", StringComparison.Ordinal) : cell,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number)
            ? (number == Math.Floor(number) && Math.Abs(number) < 1e15 ? JsonValue.Create((long)number) : JsonValue.Create(number))
            : null,
        FieldKind.Boolean => cell.ToLowerInvariant() switch
        {
            "true" or "yes" or "y" or "1" or "o" or "예" or "네" => JsonValue.Create(true),
            "false" or "no" or "n" or "0" or "x" or "아니오" or "아니요" => JsonValue.Create(false),
            _ => null,
        },
        FieldKind.Date => DateTime.TryParseExact(cell.Replace('/', '-').Replace('.', '-'), ["yyyy-M-d", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : null,
        _ => JsonValue.Create(cell),
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"^[+-]?\d{1,3}(,\d{3})+(\.\d+)?$")]
    private static partial Regex Grouped();
}
