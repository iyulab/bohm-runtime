using Bohm.Runtime.Storage;
using Bohm.Runtime.TableImports;
using LocalOrigin.Storage;

namespace Bohm.Runtime.Adoption;

/// <summary>One collection written out as a table file.</summary>
/// <param name="Collection">The storage key whose records it holds.</param>
/// <param name="File">The file's name in the folder.</param>
/// <param name="Records">How many rows it has, the header aside.</param>
/// <param name="Fields">Its columns, in order.</param>
public sealed record TableExportRecord(string Collection, string File, int Records, IReadOnlyList<string> Fields);

public sealed partial class AdoptionCatalog
{
    /// <summary>
    /// Writes application <paramref name="id"/>'s records out as tables a spreadsheet opens: every collection
    /// in its data (<see cref="TableImport.Collections"/>) as one CSV (<see cref="TableExport.Csv"/>) in the new
    /// folder <paramref name="target"/>. An application with no collection gets no folder and an empty list —
    /// there is nothing to open. The application and its data are only read.
    /// </summary>
    /// <param name="openStorage">The application's storage when it is open — its one writer, read as it is now; otherwise the files are read.</param>
    /// <returns>The tables written, or <see langword="null"/> when there is no such application.</returns>
    /// <exception cref="IOException">The folder's parent does not exist, or something with that name is already there.</exception>
    public async Task<IReadOnlyList<TableExportRecord>?> ExportTablesAsync(string id, string target, KeyValueStore? openStorage = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is null) return null;
        target = Path.GetFullPath(target);
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent)) throw new IOException("The folder to write the tables into does not exist.");
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Something with that name is already there.");

        var items = openStorage?.GetItems()
            ?? await KeyValueStore.PeekAsync(Path.Combine(AppDirectory(id), StorageDirectory), AppStorageFormat.Options(), cancellationToken).ConfigureAwait(false);
        var collections = TableImport.Collections(items);
        if (collections.Count == 0) return [];

        var written = new List<TableExportRecord>(collections.Count);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var partial = target + ".partial-" + Guid.NewGuid().ToString("n")[..8];
        try
        {
            Directory.CreateDirectory(partial);
            foreach (var collection in collections)
            {
                var key = collection.Declaration.Collection;
                var file = TableExport.FileName(key);
                // Two keys that differ only where a file name cannot hold them, or only in case, still get a file each.
                for (var n = 2; !names.Add(file); n++) file = Path.GetFileNameWithoutExtension(TableExport.FileName(key)) + $" {n}.csv";
                await File.WriteAllBytesAsync(Path.Combine(partial, file), TableExport.Csv(collection, items[key]), cancellationToken).ConfigureAwait(false);
                written.Add(new TableExportRecord(key, file, collection.Records, [.. collection.Declaration.Fields.Select(f => f.Name)]));
            }

            Directory.Move(partial, target);
        }
        catch
        {
            if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
            throw;
        }

        return written;
    }
}
