using System.Reflection;

namespace Bohm.Runtime.Tests.Host;

public sealed class ShimSourceTests
{
    /// <summary>
    /// The injected script goes into documents whose character encoding is unknown, so the injector
    /// accepts ASCII only — and refuses every page otherwise. One non-ASCII character in a comment
    /// is enough to stop every application from being served; this says so directly.
    /// </summary>
    [Fact]
    public void The_injected_script_is_ascii()
    {
        using var stream = typeof(Bohm.Runtime.Host.RuntimeHost).Assembly.GetManifestResourceStream("Bohm.Runtime.Host.shim.js")!;
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd().Split('\n');

        var offending = lines.Select((line, i) => (line, number: i + 1)).Where(l => l.line.Any(c => c > 0x7F)).Select(l => $"line {l.number}: {l.line.Trim()}").ToList();

        Assert.True(offending.Count == 0, "shim.js must be ASCII:\n" + string.Join('\n', offending));
    }
}
