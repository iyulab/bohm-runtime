using System.Globalization;
using Bohm.Runtime.Host;

// Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>]
// The control API is enabled by passing a per-launch secret in the BOHM_RUNTIME_SECRET
// environment variable (an environment variable, not an argument, so it does not show up in
// process listings). Once listening, the host writes one line to standard output —
// {"event":"ready","port":<n>} — so the process that started it learns the port it chose.
string? dataRoot = null;
var port = 0;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--data-root": dataRoot = args[++i]; break;
        case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
    }
}

if (dataRoot is null)
{
    await Console.Error.WriteLineAsync("Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>]");
    return 2;
}

// Standard output carries only the protocol line below; every log line goes to standard error.
await using var app = RuntimeHost.Build(new RuntimeHostOptions { DataRoot = dataRoot, Port = port, ControlSecret = Environment.GetEnvironmentVariable("BOHM_RUNTIME_SECRET") },
    builder => builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));
await app.StartAsync();
Console.WriteLine($$"""{"event":"ready","port":{{app.ListeningPort()}}}""");
await app.WaitForShutdownAsync();
return 0;
