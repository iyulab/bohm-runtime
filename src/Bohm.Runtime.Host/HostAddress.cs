using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Host;

/// <summary>
/// The loopback port a data root was last served on, kept in <c>host.json</c> at the data root.
/// An application's address — its origin — includes the port, and everything a browser keeps per
/// origin (granted permissions, anything stored outside the runtime) is lost when it changes. So the
/// runtime serves the same data root on the same port for as long as it can.
/// </summary>
internal static class HostAddress
{
    /// <summary>Format identifier written into <c>host.json</c>.</summary>
    public const string Format = "bohm.host/0";

    private const string FileName = "host.json";

    /// <summary>The remembered port, or <see langword="null"/> when there is none or it cannot be read.</summary>
    public static int? Read(string dataRoot)
    {
        var path = Path.Combine(dataRoot, FileName);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("format", out var format) && format.ValueEquals(Format)
                && root.TryGetProperty("port", out var port) && port.TryGetInt32(out var value)
                && value is > 0 and <= 65535)
                return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // No file yet, or one that cannot be used: a new port is chosen and remembered.
        }

        return null;
    }

    /// <summary>Remembers <paramref name="port"/>. Written aside and moved into place, so a reader never sees half a file.</summary>
    public static void Write(string dataRoot, int port)
    {
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, FileName);
        var aside = path + ".tmp";
        var json = string.Create(CultureInfo.InvariantCulture, $"{{ \"format\": \"{Format}\", \"port\": {port} }}\n");
        File.WriteAllText(aside, json, new UTF8Encoding(false));
        File.Move(aside, path, overwrite: true);
    }
}
