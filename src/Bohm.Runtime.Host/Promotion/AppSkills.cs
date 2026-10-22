using System.Text;
using System.Text.Json;
using DesktopHost.Files;
using IronHive.Agent.Skills;

namespace Bohm.Runtime.Host.Promotion;

/// <summary>
/// The skills the AI that makes and changes applications may read (and the web question, the person's own — <see cref="UserConfig"/>): the ones shipped beside the runtime, then the
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
    /// The person's own skills only, turned-off ones left out — for the web question, which the built-in skills (written
    /// for making applications) have nothing to say to. Both roots are still read, so a skill named like a built-in one
    /// stays shadowed here as everywhere.
    /// </summary>
    public SkillsConfig UserConfig() =>
        new() { Roots = [BuiltinRoot, UserRoot], Exclude = [.. Off().Order(StringComparer.Ordinal)], Filter = skill => !IsBuiltin(skill.Root) };

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
            await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(off.ToArray(), SkillsJson.Default.StringArray) + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Adds the person's own skill <paramref name="name"/>: writes its <c>SKILL.md</c> (the frontmatter from
    /// <paramref name="name"/> and <paramref name="description"/>, then <paramref name="instructions"/>) into a folder
    /// beside <see cref="UserRoot"/>, checks it the way the skills are read, and only then moves it in — a skill that would
    /// not be used never lands. The person approved it before this is called; nothing here asks.
    /// </summary>
    /// <exception cref="SkillExistsException">A skill (built-in or the person's) already has that name.</exception>
    /// <exception cref="InvalidSkillException">The name, the description or the instructions do not make a skill.</exception>
    public async Task<SkillEntry> AddAsync(string name, string description, string instructions, CancellationToken cancellationToken)
    {
        // Only a plain folder name reaches the disk; the format's own rules (pattern, length) are the discovery's below.
        if (name.Length is 0 or > 64 || name != Path.GetFileName(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidSkillException("name");
        if (string.IsNullOrWhiteSpace(instructions)) throw new InvalidSkillException("instructions");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(options.DataRoot, "skills-adding-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (List().Any(s => string.Equals(s.Name, name, StringComparison.Ordinal))) throw new SkillExistsException(name);
            var folder = Path.Combine(staging, name);
            Directory.CreateDirectory(folder);
            var text = $"---\nname: {name}\ndescription: {Quoted(description)}\n---\n\n{instructions.Trim()}\n";
            await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            var found = SkillDiscovery.Discover(new SkillsConfig { Roots = [staging] });
            if (found.Diagnostics.FirstOrDefault(d => d.Severity == SkillDiagnosticSeverity.Error) is { } problem)
                throw new InvalidSkillException(problem.Code switch
                {
                    SkillDiagnosticCode.MissingName or SkillDiagnosticCode.InvalidName or SkillDiagnosticCode.NameDoesNotMatchDirectory => "name",
                    SkillDiagnosticCode.MissingDescription => "description",
                    _ => "format",
                });
            Directory.CreateDirectory(UserRoot);
            var target = Path.Combine(UserRoot, name);
            if (Directory.Exists(target)) throw new SkillExistsException(name);
            Directory.Move(folder, target);
            return List().First(s => s.Problem is null && string.Equals(s.Name, name, StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder left behind is harmless: it is not under a root the skills are read from.
            }

            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>A YAML double-quoted scalar on one line — a description may hold a colon, a quote or a hash.</summary>
    private static string Quoted(string text) =>
        "\"" + string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
            .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

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

/// <summary>A skill to add has a name a skill already has.</summary>
internal sealed class SkillExistsException(string name) : Exception($"A skill named '{name}' exists.");

/// <summary>What to add does not make a skill: <see cref="Part"/> is <c>name</c>, <c>description</c>, <c>instructions</c> or <c>format</c>.</summary>
internal sealed class InvalidSkillException(string part) : Exception($"The skill's {part} is not valid.")
{
    public string Part { get; } = part;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]), TypeInfoPropertyName = "StringArray")]
internal sealed partial class SkillsJson : System.Text.Json.Serialization.JsonSerializerContext;
