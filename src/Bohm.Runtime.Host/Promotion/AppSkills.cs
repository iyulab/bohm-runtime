using System.Text;
using System.Text.Json;
using IronHive.Agent.Skills;

namespace Bohm.Runtime.Host.Promotion;

/// <summary>
/// The skills the AI that makes and changes applications may read: the ones shipped beside the runtime, then the
/// person's own under <c>skills/</c> in the data root — each a folder with its <c>SKILL.md</c>, in the Agent Skills
/// format as it is (nothing of this runtime's own in it). Any of them can be turned off; the person's own can be
/// removed by removing the folder. When two have the same name the shipped one is used and the person's is listed
/// as shadowed — a shipped skill is never replaced without anyone seeing it.
/// </summary>
internal sealed class AppSkills(RuntimeHostOptions options) : IDisposable
{
    private const string OffFile = "skills-off.json";
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Where the built-in skills are: one folder per skill, each with its SKILL.md, shipped beside the runtime.</summary>
    public static string BuiltinRoot => Path.Combine(AppContext.BaseDirectory, "Skills");

    /// <summary>The built-in skills only, all of them — for a caller with no data root (and its tests).</summary>
    public static SkillsConfig Builtin => new() { Roots = [BuiltinRoot] };

    /// <summary>Where the person's own skills are.</summary>
    public string UserRoot => Path.Combine(options.DataRoot, "skills");

    /// <summary>The skills a proposal may read now: both roots, without the ones turned off.</summary>
    public SkillsConfig Config() => new() { Roots = [BuiltinRoot, UserRoot], Exclude = [.. Off().Order(StringComparer.Ordinal)] };

    /// <summary>
    /// Every skill found, in the order the model sees them, then what could not be used under the person's folder:
    /// a <c>SKILL.md</c> the format rejects, or a name a built-in skill already has.
    /// </summary>
    public IReadOnlyList<SkillEntry> List()
    {
        var off = Off();
        var found = SkillDiscovery.Discover(new SkillsConfig { Roots = [BuiltinRoot, UserRoot] });
        var entries = found.Skills
            .Select(s => new SkillEntry(s.Name, s.Description, IsBuiltin(s.Root), !off.Contains(s.Name), null))
            .ToList();
        foreach (var problem in found.Diagnostics.Where(d => d.Code != SkillDiagnosticCode.RootNotFound && Under(d.Path, UserRoot)))
            entries.Add(new SkillEntry(NameOf(problem.Path), null, Builtin: false, Enabled: false, Problem: Problem(problem)));
        return entries;
    }

    /// <summary>Turns skill <paramref name="name"/> on or off; a name no skill has is refused.</summary>
    /// <exception cref="KeyNotFoundException">No skill has that name.</exception>
    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        if (!List().Any(s => s.Problem is null && string.Equals(s.Name, name, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"No skill '{name}'.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var off = new SortedSet<string>(Off(), StringComparer.Ordinal);
            if (enabled) off.Remove(name);
            else off.Add(name);
            Directory.CreateDirectory(options.DataRoot);
            var path = Path.Combine(options.DataRoot, OffFile);
            var aside = path + ".tmp";
            await File.WriteAllTextAsync(aside, JsonSerializer.Serialize(off.ToArray(), SkillsJson.Default.StringArray) + "\n", new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(aside, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private HashSet<string> Off()
    {
        try
        {
            return new HashSet<string>(JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(options.DataRoot, OffFile)), SkillsJson.Default.StringArray) ?? [],
                StringComparer.Ordinal);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // Nothing turned off yet — or a file nobody can read, which turns nothing off rather than guessing.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static bool IsBuiltin(string root) => string.Equals(Path.GetFullPath(root), Path.GetFullPath(BuiltinRoot), StringComparison.OrdinalIgnoreCase);

    private static bool Under(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>The skill's folder name, from the path a diagnostic names (its folder or its SKILL.md).</summary>
    private static string NameOf(string path) =>
        Path.GetFileName(path) is "SKILL.md" ? Path.GetFileName(Path.GetDirectoryName(path)) ?? "" : Path.GetFileName(path);

    private static string Problem(SkillDiagnostic diagnostic) => diagnostic.Code switch
    {
        SkillDiagnosticCode.Shadowed => "shadowed",
        _ => "invalid",
    };
}

/// <summary>
/// One skill in the list: whether it is <paramref name="Builtin"/> and <paramref name="Enabled"/>; <paramref name="Problem"/> is
/// <c>"shadowed"</c> (a built-in skill has its name) or <c>"invalid"</c> (its <c>SKILL.md</c> is not in the format) for one that is not used.
/// </summary>
internal sealed record SkillEntry(string Name, string? Description, bool Builtin, bool Enabled, string? Problem);

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]), TypeInfoPropertyName = "StringArray")]
internal sealed partial class SkillsJson : System.Text.Json.Serialization.JsonSerializerContext;
