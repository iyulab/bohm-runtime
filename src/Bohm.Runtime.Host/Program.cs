using System.Diagnostics;
using System.Globalization;
using Bohm.Runtime.Host;

// Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>] [--parent-pid <pid>] [--llama-server <path>]
// The control API is enabled by passing a per-launch secret in the BOHM_RUNTIME_SECRET
// environment variable (an environment variable, not an argument, so it does not show up in
// process listings). Once listening, the host writes one line to standard output —
// {"event":"ready","port":<n>} — so the process that started it learns the port it chose.
// Without --port the data root keeps its port across launches (remembered in host.json); when that
// port is taken the host starts on a new one and the line says so: {"event":"ready","port":<n>,"previousPort":<m>}.
// --port 0 lets the operating system choose every time.
// With --parent-pid the host stops by itself when that process ends, so a companion runtime is
// never left running after the application that started it crashed or was killed.
// --llama-server names the executable that runs a model chosen on this computer; without it, one
// shipped next to this executable (llama-server\llama-server.exe) is used when present, so a model
// runs with nothing downloaded.
// BOHM_DISCARD_DIR (verification only) sends applications removed for good to that folder instead of
// the recycle bin, so an automated check does not fill the person's recycle bin.
const string usage = "Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>] [--parent-pid <pid>] [--llama-server <path>]";
string? dataRoot = null;
int? port = null;
int? parentPid = null;
string? llamaServer = null;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--data-root": dataRoot = args[++i]; break;
        case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--parent-pid": parentPid = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--llama-server": llamaServer = args[++i]; break;
    }
}

if (dataRoot is null)
{
    await Console.Error.WriteLineAsync(usage);
    return 2;
}

var shipped = Path.Combine(AppContext.BaseDirectory, "llama-server", OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
llamaServer ??= File.Exists(shipped) ? shipped : null;

// Standard output carries only the protocol line below; every log line goes to standard error.
var started = await RuntimeHost.StartAsync(new RuntimeHostOptions
{
    DataRoot = dataRoot,
    Port = port,
    ControlSecret = Environment.GetEnvironmentVariable("BOHM_RUNTIME_SECRET"),
    LlamaServerPath = llamaServer,
    Discard = Environment.GetEnvironmentVariable("BOHM_DISCARD_DIR") is { Length: > 0 } discardDir
        ? (folder, _) =>
        {
            Directory.CreateDirectory(discardDir);
            Directory.Move(folder, Path.Combine(discardDir, Path.GetFileName(folder)));
            return Task.CompletedTask;
        }
        : null,
},
    builder => builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));
await using var app = started.App;

if (parentPid is { } pid)
{
    Process parent;
    try
    {
        parent = Process.GetProcessById(pid);
    }
    catch (ArgumentException)
    {
        await Console.Error.WriteLineAsync($"Parent process {pid} is not running; stopping.");
        await app.StopAsync();
        return 3;
    }

    // A normal stop, so storage is closed cleanly.
    _ = parent.WaitForExitAsync().ContinueWith(_ => app.Lifetime.StopApplication(), TaskScheduler.Default);
}

Console.WriteLine(started.PreviousPort is { } previous
    ? $$"""{"event":"ready","port":{{started.Port}},"previousPort":{{previous}}}"""
    : $$"""{"event":"ready","port":{{started.Port}}}""");
await app.WaitForShutdownAsync();
return 0;
