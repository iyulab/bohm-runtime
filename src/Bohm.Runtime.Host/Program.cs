using System.Diagnostics;
using System.Globalization;
using Bohm.Runtime.Host;

// Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>] [--parent-pid <pid>]
// The control API is enabled by passing a per-launch secret in the BOHM_RUNTIME_SECRET
// environment variable (an environment variable, not an argument, so it does not show up in
// process listings). Once listening, the host writes one line to standard output —
// {"event":"ready","port":<n>} — so the process that started it learns the port it chose.
// With --parent-pid the host stops by itself when that process ends, so a companion runtime is
// never left running after the application that started it crashed or was killed.
const string usage = "Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>] [--parent-pid <pid>]";
string? dataRoot = null;
var port = 0;
int? parentPid = null;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--data-root": dataRoot = args[++i]; break;
        case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--parent-pid": parentPid = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
    }
}

if (dataRoot is null)
{
    await Console.Error.WriteLineAsync(usage);
    return 2;
}

// Standard output carries only the protocol line below; every log line goes to standard error.
await using var app = RuntimeHost.Build(new RuntimeHostOptions { DataRoot = dataRoot, Port = port, ControlSecret = Environment.GetEnvironmentVariable("BOHM_RUNTIME_SECRET") },
    builder => builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));
await app.StartAsync();

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

Console.WriteLine($$"""{"event":"ready","port":{{app.ListeningPort()}}}""");
await app.WaitForShutdownAsync();
return 0;
