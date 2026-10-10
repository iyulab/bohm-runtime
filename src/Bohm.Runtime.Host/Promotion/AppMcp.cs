using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using IronHive.Agent.Mcp;
using Microsoft.Extensions.Logging;

namespace Bohm.Runtime.Host.Promotion;

/// <summary>
/// The MCP servers the person gave the AI that makes and changes applications: <c>mcp.json</c> in the data root, in the shape an
/// MCP server's documentation gives (<c>mcpServers</c> — <c>servers</c> and <c>plugins</c> are read too), read by IronHive's reader
/// as it is. The runtime connects to the ones turned on when the list is looked at — starting a local command, or reaching an
/// address — and closes them when it ends. Applications never reach them: only the AI's tool calls do.
/// </summary>
internal sealed class AppMcp(RuntimeHostOptions options, ILoggerFactory loggers) : IAsyncDisposable
{
    public const string FileName = "mcp.json";
    private const string OffFile = "mcp-off.json";
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly McpPluginManager _manager = new(loggers.CreateLogger<McpPluginManager>());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, Task<int>> _connections = new(StringComparer.Ordinal);

    /// <summary>Where the document is.</summary>
    public string FilePath => Path.Combine(options.DataRoot, FileName);

    /// <summary>The document as the person wrote it — empty when there is none.</summary>
    public string Text()
    {
        try
        {
            return File.ReadAllText(FilePath);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return "";
        }
    }

    /// <summary>
    /// Every server in the document with its state, starting the connection to each one turned on that has none yet; then the
    /// entries the reader left out. A document that is not JSON lists one entry with problem <c>invalid</c>.
    /// </summary>
    public IReadOnlyList<McpEntry> List()
    {
        McpPluginsConfig config;
        try
        {
            config = Read(Text());
        }
        catch (JsonException)
        {
            return [new McpEntry(FileName, Enabled: false, State: "refused", Detail: null, Tools: null, Problem: "invalid")];
        }

        var off = Off();
        var entries = new List<McpEntry>();
        foreach (var (name, server) in config.Plugins)
        {
            var enabled = !off.Contains(name) && !config.ExcludePlugins.Contains(name);
            if (!enabled)
            {
                entries.Add(new McpEntry(name, false, "off", Detail(server), null, null));
                continue;
            }

            var connecting = _connections.GetOrAdd(name, _ => ConnectAsync(name, server));
            entries.Add(connecting.Status switch
            {
                TaskStatus.RanToCompletion => new McpEntry(name, true, "connected", Detail(server), connecting.Result, null),
                TaskStatus.Faulted or TaskStatus.Canceled => new McpEntry(name, true, "failed", Detail(server), null,
                    connecting.Exception?.InnerException?.Message ?? "timed-out"),
                _ => new McpEntry(name, true, "connecting", Detail(server), null, null),
            });
        }

        foreach (var issue in config.Issues.Where(i => i.Kind == McpConfigIssueKind.Refused && i.Entry is not null))
            entries.Add(new McpEntry(issue.Entry!, false, "refused", null, null, issue.Message));
        return entries;
    }

    /// <summary>
    /// Replaces the document with <paramref name="text"/> — after reading it the way it will be read: text that is not a JSON
    /// object, or an entry the reader would leave out, is refused and nothing changes. Empty text removes the document. Every
    /// connection is closed; the list reconnects. Answers what the reader noted but kept (an ignored key).
    /// </summary>
    /// <exception cref="InvalidMcpDocumentException">The text is not a document, or an entry in it would not be used.</exception>
    public async Task<IReadOnlyList<McpConfigIssue>> SaveAsync(string text, CancellationToken cancellationToken)
    {
        McpPluginsConfig config;
        try
        {
            config = Read(text);
        }
        catch (JsonException)
        {
            throw new InvalidMcpDocumentException([]);
        }

        if (config.Issues.Any(i => i.Kind == McpConfigIssueKind.Refused))
            throw new InvalidMcpDocumentException([.. config.Issues.Where(i => i.Kind == McpConfigIssueKind.Refused)]);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                File.Delete(FilePath);
            else
                await WriteAsync(FilePath, text.EndsWith('\n') ? text : text + "\n", cancellationToken).ConfigureAwait(false);
            await DisconnectAllAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return [.. config.Issues];
    }

    /// <summary>Turns server <paramref name="name"/> on or off; off closes its connection.</summary>
    /// <exception cref="KeyNotFoundException">The document has no server of that name.</exception>
    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken cancellationToken)
    {
        McpPluginsConfig config;
        try
        {
            config = Read(Text());
        }
        catch (JsonException)
        {
            throw new KeyNotFoundException($"No MCP server '{name}'.");
        }

        if (!config.Plugins.ContainsKey(name)) throw new KeyNotFoundException($"No MCP server '{name}'.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var off = new SortedSet<string>(Off(), StringComparer.Ordinal);
            if (enabled) off.Remove(name);
            else off.Add(name);
            await WriteAsync(Path.Combine(options.DataRoot, OffFile),
                JsonSerializer.Serialize(off.ToArray(), SkillsJson.Default.StringArray) + "\n", cancellationToken).ConfigureAwait(false);
            if (_connections.TryRemove(name, out _)) await _manager.DisconnectAsync(name, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _manager.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private static McpPluginsConfig Read(string text) =>
        string.IsNullOrWhiteSpace(text) ? new McpPluginsConfig() : McpPluginsConfigLoader.Parse(text);

    private async Task<int> ConnectAsync(string name, McpPluginConfig server)
    {
        // Off the caller's request: a list answers at once and shows «connecting» until this settles.
        await Task.Yield();
        // The reader already gave an entry without its own timeout the document's default.
        var timeout = server.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(server.TimeoutMs) : DefaultConnectTimeout;
        using var within = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        within.CancelAfter(timeout);
        await _manager.ConnectAsync(name, server, within.Token).ConfigureAwait(false);
        return (await _manager.GetToolsAsync(name, within.Token).ConfigureAwait(false)).Count;
    }

    private async Task DisconnectAllAsync()
    {
        _connections.Clear();
        await _manager.DisconnectAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// What the list shows of where a server is: the command and its arguments, with the names of the environment variables it
    /// is given but never their values; or the address without its query or credentials.
    /// </summary>
    private static string? Detail(McpPluginConfig server)
    {
        if (server.Url is { Length: > 0 } url)
            return Uri.TryCreate(url, UriKind.Absolute, out var at) ? at.GetLeftPart(UriPartial.Path) : null;
        if (server.Command is not { Length: > 0 } command) return null;
        var line = new StringBuilder(command);
        foreach (var argument in server.Arguments ?? []) line.Append(' ').Append(argument);
        if (server.Environment is { Count: > 0 } environment) line.Append(" (").Append(string.Join(", ", environment.Keys.Order(StringComparer.Ordinal))).Append(')');
        return line.ToString();
    }

    private HashSet<string> Off()
    {
        try
        {
            return new HashSet<string>(JsonSerializer.Deserialize(File.ReadAllBytes(Path.Combine(options.DataRoot, OffFile)), SkillsJson.Default.StringArray) ?? [],
                StringComparer.Ordinal);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private async Task WriteAsync(string path, string text, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataRoot);
        var aside = path + ".tmp";
        await File.WriteAllTextAsync(aside, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(aside, path, overwrite: true);
    }
}

/// <summary>
/// One MCP server in the list: <paramref name="State"/> is <c>off</c>, <c>connecting</c>, <c>connected</c> (with how many
/// <paramref name="Tools"/>), <c>failed</c> (<paramref name="Problem"/> says why) or <c>refused</c> (the reader left it out —
/// <paramref name="Problem"/> says why); <paramref name="Detail"/> is the command or the address, with no secret values.
/// </summary>
internal sealed record McpEntry(string Name, bool Enabled, string State, string? Detail, int? Tools, string? Problem);

/// <summary>An MCP document that would not be read as written: not JSON (no <see cref="Issues"/>), or entries left out.</summary>
internal sealed class InvalidMcpDocumentException(IReadOnlyList<McpConfigIssue> issues) : Exception("The MCP document would not be read as written.")
{
    public IReadOnlyList<McpConfigIssue> Issues { get; } = issues;
}
