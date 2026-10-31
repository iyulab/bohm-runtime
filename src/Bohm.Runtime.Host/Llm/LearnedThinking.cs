using System.Text.Json;
using DesktopHost.Files;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The models of servers that were learned to think, kept in <c>company-model-learned.json</c> at the data root so the next start
/// knows it before a long task's first request. A model that thinks without being asked spends a long task's whole answer thinking the
/// first time — minutes on a server's model — before it is known; once an answer shows it, it is remembered here for every provider
/// that reaches the same address with the same model name, whichever way it was set up.
/// </summary>
internal sealed class LearnedThinking
{
    /// <summary>Format identifier written into <c>company-model-learned.json</c>.</summary>
    public const string Format = "bohm.company-model-learned/0";

    private const string FileName = "company-model-learned.json";

    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _thinks;

    public LearnedThinking(RuntimeHostOptions options)
    {
        _path = Path.Combine(options.DataRoot, FileName);
        _thinks = Read(_path);
    }

    /// <summary>How a model is named among what was learned: its server's address and its name there.</summary>
    internal static string NameOf(Uri address, string model) => address.AbsoluteUri + " " + model;

    /// <summary>Whether <paramref name="model"/> at <paramref name="address"/> was learned to think.</summary>
    public bool Thinks(Uri address, string model)
    {
        lock (_lock) return _thinks.Contains(NameOf(address, model));
    }

    /// <summary>
    /// Remembers that <paramref name="model"/> at <paramref name="address"/> thinks, so the next start knows it. Written whole each
    /// time; a file that cannot be written leaves it learned for this run only.
    /// </summary>
    public void Remember(Uri address, string model)
    {
        string[] thinks;
        lock (_lock)
        {
            if (!_thinks.Add(NameOf(address, model))) return;
            thinks = [.. _thinks.Order(StringComparer.Ordinal)];
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new Stored(Format, thinks), LearnedThinkingJson.Default.Stored) + "\n");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Known for this run; asked again after the next start.
        }
    }

    /// <summary>The models learned to think, or none when there is no file or it cannot be read.</summary>
    private static HashSet<string> Read(string path)
    {
        try
        {
            var learned = JsonSerializer.Deserialize(File.ReadAllBytes(path), LearnedThinkingJson.Default.Stored);
            if (learned is { Format: Format, Thinks: { } thinks }) return [.. thinks.Where(t => !string.IsNullOrEmpty(t))];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing learned yet, or a file that cannot be used: learned again by asking.
        }

        return [];
    }

    /// <summary><c>company-model-learned.json</c>: the models (server address and model name) learned to think.</summary>
    internal sealed record Stored(string Format, string[]? Thinks);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LearnedThinking.Stored))]
internal sealed partial class LearnedThinkingJson : System.Text.Json.Serialization.JsonSerializerContext;
