using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IronHive.Agent.Mcp;
using Microsoft.Extensions.AI;
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

    private readonly ILogger<McpPluginManager> _log = loggers.CreateLogger<McpPluginManager>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, Task<int>> _connections = new(StringComparer.Ordinal);

    // Closing a connection can take seconds (a stdio server is given time to exit), and nobody should wait for it: a save
    // or a switch answers at once. A changed document retires the whole manager and starts a new one; a server turned off
    // is disconnected in the background, and turning it on again waits for that first.
    private McpPluginManager _manager = new(loggers.CreateLogger<McpPluginManager>());
    private readonly ConcurrentDictionary<string, Task> _closing = new(StringComparer.Ordinal);
    private readonly List<Task> _retiring = [];

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

            Task<int> connecting;
            lock (_sync) connecting = _connections.GetOrAdd(name, _ => ConnectAsync(_manager, name, server));
            entries.Add(connecting.Status switch
            {
                TaskStatus.RanToCompletion => new McpEntry(name, true, "connected", Detail(server), connecting.Result, null),
                TaskStatus.Faulted or TaskStatus.Canceled => new McpEntry(name, true, "failed", Detail(server), null, Why(connecting.Exception)),
                _ => new McpEntry(name, true, "connecting", Detail(server), null, null),
            });
        }

        foreach (var issue in config.Issues.Where(i => i.Kind == McpConfigIssueKind.Refused && i.Entry is not null))
            entries.Add(new McpEntry(issue.Entry!, false, "refused", null, null, issue.Message));
        return entries;
    }

    /// <summary>
    /// The tools of the servers turned on, for the AI that makes or changes an application: connections are started as the list
    /// starts them, and one still connecting is waited for briefly (<see cref="ToolsWait"/>) — a server that is not up by then is
    /// left out of this request rather than holding it. Each tool is named <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>.
    /// </summary>
    public async Task<IReadOnlyList<McpTool>> ToolsAsync(CancellationToken cancellationToken)
    {
        McpPluginsConfig config;
        try
        {
            config = Read(Text());
        }
        catch (JsonException)
        {
            return [];
        }

        List();   // starts what is turned on and not yet connecting
        var tools = new List<McpTool>();
        foreach (var (name, server) in config.Plugins)
        {
            Task<int>? connecting;
            McpPluginManager manager;
            lock (_sync)
            {
                if (!_connections.TryGetValue(name, out connecting)) continue;   // turned off
                manager = _manager;
            }

            try
            {
                await connecting.WaitAsync(ToolsWait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or not OperationCanceledException)
            {
                continue;   // not up in time, or failed: the list says why
            }

            foreach (var tool in await manager.GetToolsAsync(name, cancellationToken).ConfigureAwait(false))
            {
                if (tool is AIFunction function)
                    tools.Add(new McpTool(name, function.Name, server.Url is { Length: > 0 }, new Named(function, McpTool.NameOf(name, function.Name))));
            }
        }

        return tools;
    }

    /// <summary>How long a request waits for a server still connecting.</summary>
    internal static readonly TimeSpan ToolsWait = TimeSpan.FromSeconds(5);

    /// <summary>A server's tool under the name the model sees.</summary>
    private sealed class Named(AIFunction inner, string name) : DelegatingAIFunction(inner)
    {
        public override string Name => name;
    }

    /// <summary>
    /// Replaces the document with <paramref name="text"/> — after reading it the way it will be read: text that is not a JSON
    /// object, or an entry the reader would leave out, is refused and nothing changes. Empty text removes the document. Every
    /// connection is closed; the list reconnects. Answers what the reader noted but kept (an ignored key).
    /// </summary>
    /// <exception cref="InvalidMcpDocumentException">The text is not a document, or an entry in it would not be used.</exception>
    public async Task<IReadOnlyList<McpConfigIssue>> SaveAsync(string text, CancellationToken cancellationToken)
    {
        var config = Checked(text);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplaceAsync(text, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return [.. config.Issues];
    }

    /// <summary>
    /// Adds one server to the document — <paramref name="server"/> is its entry as an MCP server's documentation writes it
    /// (<c>command</c> · <c>args</c> · <c>env</c>, or <c>url</c> · <c>headers</c>) — under the document's <c>mcpServers</c> (or the
    /// <c>servers</c> or <c>plugins</c> object it already has), and saves it as <see cref="SaveAsync"/> does. Only after the person
    /// approved it. The rest of the document is kept; it is written back indented.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty, or the entry is not a JSON object.</exception>
    /// <exception cref="McpServerExistsException">The document has a server of that name.</exception>
    /// <exception cref="InvalidMcpDocumentException">The document is not JSON, or the entry (or another) would not be used.</exception>
    public async Task<IReadOnlyList<McpConfigIssue>> AddAsync(string name, JsonElement server, CancellationToken cancellationToken)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Any(char.IsControl)) throw new ArgumentException("An MCP server needs a name.", nameof(name));
        if (server.ValueKind != JsonValueKind.Object) throw new ArgumentException("An MCP server's entry is a JSON object.", nameof(server));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            JsonObject document;
            try
            {
                var current = Text();
                document = string.IsNullOrWhiteSpace(current) ? [] : JsonNode.Parse(current) as JsonObject ?? throw new InvalidMcpDocumentException([]);
            }
            catch (JsonException)
            {
                throw new InvalidMcpDocumentException([]);
            }

            var servers = ServerKeys.Select(key => document[key] as JsonObject).FirstOrDefault(o => o is not null);
            if (servers is null) document["mcpServers"] = servers = [];
            if (servers.ContainsKey(name)) throw new McpServerExistsException(name);
            servers[name] = JsonNode.Parse(server.GetRawText());
            var text = document.ToJsonString(Indented);
            var config = Checked(text);
            await ReplaceAsync(text, cancellationToken).ConfigureAwait(false);
            return [.. config.Issues];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Where a document keeps its servers, in the order a new one is added to the first present.</summary>
    private static readonly string[] ServerKeys = ["mcpServers", "servers", "plugins"];

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The document read the way it will be read — refused when it is not JSON or an entry in it would be left out.</summary>
    private static McpPluginsConfig Checked(string text)
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
        return config;
    }

    /// <summary>Writes (or, for empty text, removes) the document and starts the connections over. Under the gate.</summary>
    private async Task ReplaceAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            File.Delete(FilePath);
        else
            await WriteAsync(FilePath, text.EndsWith('\n') ? text : text + "\n", cancellationToken).ConfigureAwait(false);
        RetireConnections();
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
            lock (_sync)
            {
                if (_connections.TryRemove(name, out _)) _closing[name] = _manager.DisconnectAsync(name, CancellationToken.None);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task[] pending;
        lock (_sync) pending = [.. _closing.Values, .. _retiring];
        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A server that would not close cleanly is closed by disposing what is left below.
        }

        await _manager.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private static McpPluginsConfig Read(string text) =>
        string.IsNullOrWhiteSpace(text) ? new McpPluginsConfig() : McpPluginsConfigLoader.Parse(text);

    private async Task<int> ConnectAsync(McpPluginManager manager, string name, McpPluginConfig server)
    {
        // Off the caller's request: a list answers at once and shows «connecting» until this settles.
        await Task.Yield();
        if (_closing.TryRemove(name, out var closing))
        {
            try
            {
                await closing.ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // It is gone either way; connect anew.
            }
        }

        // A command that is not there fails now, by name: the SDK starts a local command through the system shell on Windows,
        // so a missing one would otherwise start «successfully» and surface as a closed stream or the connect timeout.
        if (server.Command is { Length: > 0 } command && !CommandExists(server))
            throw new McpCommandNotFoundException(command);

        // The reader already gave an entry without its own timeout the document's default.
        var timeout = server.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(server.TimeoutMs) : DefaultConnectTimeout;
        using var within = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        within.CancelAfter(timeout);
        await manager.ConnectAsync(name, server, within.Token).ConfigureAwait(false);
        return (await manager.GetToolsAsync(name, within.Token).ConfigureAwait(false)).Count;
    }

    /// <summary>Starts over with a new manager; the old one closes its connections in the background.</summary>
    private void RetireConnections()
    {
        lock (_sync)
        {
            var old = _manager;
            _manager = new McpPluginManager(_log);
            _connections.Clear();
            _closing.Clear();
            _retiring.RemoveAll(t => t.IsCompleted);
            _retiring.Add(old.DisposeAsync().AsTask());
        }
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

    /// <summary>
    /// Why a connection failed, as a code for the causes a person fixes themselves — <c>command-not-found</c> (the command is
    /// not installed or not on the path), <c>timed-out</c>, <c>unreachable</c> (nothing answers at the address) and
    /// <c>unauthorized</c> (the server refused the credentials) — and otherwise the failure's own message.
    /// </summary>
    internal static string Why(Exception? failure)
    {
        if (failure is null) return "timed-out";   // cancelled by the connect timeout
        string? message = null;
        for (var e = failure; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case McpCommandNotFoundException:
                case System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 }:   // file or path not found
                    return "command-not-found";
                case OperationCanceledException or TimeoutException:
                    return "timed-out";
                case System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused or System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.NetworkUnreachable }:
                    return "unreachable";
                case HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }:
                    return "unauthorized";
                case AggregateException:
                    break;
                default:
                    message ??= e.Message;
                    break;
            }
        }

        return message ?? failure.Message;
    }

    /// <summary>
    /// Whether the server's command names a file that can be started: a path (with a directory in it) as it is — relative to the
    /// entry's working directory when it gives one — or a bare name looked up on <c>PATH</c> (the entry's own <c>PATH</c> if it
    /// sets one) and, on Windows, also with each <c>PATHEXT</c> extension (<c>npx</c> is <c>npx.cmd</c>).
    /// </summary>
    internal static bool CommandExists(McpPluginConfig server)
    {
        var command = server.Command ?? "";
        // On Windows a name without one of the PATHEXT extensions is tried as written and with each of them — a dot alone does
        // not make an extension that starts (python3.11, My.Server).
        var pathExt = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];
        string[] extensions = pathExt.Any(x => command.EndsWith(x, StringComparison.OrdinalIgnoreCase)) ? [""] : ["", .. pathExt];
        bool Exists(string path) => extensions.Any(x => File.Exists(path + x));

        if (command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 || Path.IsPathRooted(command))
            return Exists(Path.IsPathRooted(command) || server.WorkingDirectory is not { Length: > 0 } at ? command : Path.Combine(at, command));

        var path = server.Environment?.FirstOrDefault(p => string.Equals(p.Key, "PATH",
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;

        return (path ?? Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(directory => Exists(Path.Combine(directory, command)));
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

/// <summary>The MCP document already has a server of that name.</summary>
internal sealed class McpServerExistsException(string name) : Exception($"An MCP server named '{name}' exists already.");

/// <summary>A server's command is not a file that can be started — not on the path, or not at the path written.</summary>
internal sealed class McpCommandNotFoundException(string command) : Exception($"The command '{command}' was not found.");

/// <summary>An MCP document that would not be read as written: not JSON (no <see cref="Issues"/>), or entries left out.</summary>
internal sealed class InvalidMcpDocumentException(IReadOnlyList<McpConfigIssue> issues) : Exception("The MCP document would not be read as written.")
{
    public IReadOnlyList<McpConfigIssue> Issues { get; } = issues;
}
