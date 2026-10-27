using System.Text;
using System.Text.Json;
using DesktopHost.Files;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The providers the person added, kept in <c>providers.json</c> at the data root, and how they join the
/// other sources into the list the person chooses from. Keys are not in the file — they are in the vault.
/// </summary>
internal static class ProviderStore
{
    /// <summary>Format identifier written into <c>providers.json</c>.</summary>
    public const string Format = "bohm.providers/0";

    private const string FileName = "providers.json";

    /// <summary>The most providers one file holds — a bound on what a hand-edited file can ask of the runtime.</summary>
    public const int MaxProviders = 64;

    /// <summary>
    /// The declarations in the file, or none when there is no file or it cannot be read. An entry that
    /// cannot be used is left out and the rest are kept, so one bad entry does not take the others with it.
    /// </summary>
    public static IReadOnlyList<ProviderDeclaration> Read(string dataRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dataRoot, FileName)));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || !format.ValueEquals(Format)
                || !root.TryGetProperty("providers", out var list) || list.ValueKind != JsonValueKind.Array)
                return [];

            var found = new List<ProviderDeclaration>();
            foreach (var entry in list.EnumerateArray())
            {
                if (found.Count >= MaxProviders) break;
                if (ProviderDeclaration.TryParse(entry, ProviderOrigin.Person, out var declaration, out _)
                    && found.All(d => d.Id != declaration!.Id))
                    found.Add(declaration!);
            }

            return found;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // None added, or a file that cannot be used: only the other sources until one is added again.
            return [];
        }
    }

    /// <summary>Replaces the file with <paramref name="providers"/>; an empty list removes it.</summary>
    public static async Task WriteAsync(string dataRoot, IReadOnlyList<ProviderDeclaration> providers, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataRoot, FileName);
        if (providers.Count == 0)
        {
            File.Delete(path);
            return;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("format", Format);
            writer.WriteStartArray("providers");
            foreach (var provider in providers) provider.WriteTo(writer);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        Directory.CreateDirectory(dataRoot);
        await AtomicFile.WriteAllTextAsync(path, Encoding.UTF8.GetString(stream.ToArray()) + "\n", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The providers to choose from: every source together, one per id, and where an id is in more than
    /// one the later origin stands (<see cref="ProviderOrigin"/>: an administrator's over the person's over
    /// the built-in). Built-in order first, then the others in the order they came.
    /// </summary>
    public static IReadOnlyList<ProviderDeclaration> Merge(params IEnumerable<ProviderDeclaration>[] sources)
    {
        var chosen = new Dictionary<string, ProviderDeclaration>();
        var order = new List<string>();
        foreach (var declaration in sources.SelectMany(s => s))
        {
            if (!chosen.TryGetValue(declaration.Id, out var held))
            {
                order.Add(declaration.Id);
            }
            else if (declaration.Origin < held.Origin)
            {
                continue;
            }

            chosen[declaration.Id] = declaration;
        }

        return order.Select(id => chosen[id]).ToList();
    }
}
