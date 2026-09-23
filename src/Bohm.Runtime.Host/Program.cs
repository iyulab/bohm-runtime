using System.Globalization;
using Bohm.Runtime.Host;

// Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>]
// Once listening, writes one line to standard output — {"event":"ready","port":<n>} — so the
// process that started the runtime learns the port it chose.
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
await using var app = RuntimeHost.Build(new RuntimeHostOptions { DataRoot = dataRoot, Port = port },
    builder => builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace));
await app.StartAsync();
Console.WriteLine($$"""{"event":"ready","port":{{app.ListeningPort()}}}""");
await app.WaitForShutdownAsync();
return 0;
