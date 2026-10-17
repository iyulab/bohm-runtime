using System.Diagnostics;
using System.Globalization;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host;
using TauriKit.Sidecar.Loopback;

// Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>] [--parent-pid <pid>] [--llama-server <path>]
//        [--company-models <json> | --company-model-endpoint <url> --company-model <name>] [--no-mcp]
// The control API is enabled by passing a per-launch secret (32 characters or more) in the BOHM_RUNTIME_SECRET
// environment variable (an environment variable, not an argument, so it does not show up in
// process listings). Once listening, the host writes one line to standard output —
// `bohm-runtime ready port=<n>` — so the process that started it learns the port it chose.
// Without --port the data root keeps its port across launches (remembered in host.json); when that
// port is taken the host starts on a new one and the line says so: `bohm-runtime ready port=<n> previousPort=<m>`.
// --port 0 lets the operating system choose every time.
// With --parent-pid the host stops by itself when that process ends, so a companion runtime is
// never left running after the application that started it crashed or was killed.
// --llama-server names the executable that runs a model chosen on this computer; without it, one
// shipped next to this executable (llama-server\llama-server.exe) is used when present, so a model
// runs with nothing downloaded.
// --company-models lists the organization's model servers and their models for this run (an
// administrator's policy, passed on by whoever starts the runtime — see CompanyModelList for its
// shape): the person chooses among them and cannot set another. What in it cannot be used is left
// out; when nothing can, the runtime starts without it and says so on standard error, so a mistaken
// policy does not keep the person's applications from opening.
// --company-model-endpoint and --company-model are the older way to list one server and one model —
// its OpenAI-compatible base address and the model's name. Given together or not at all, and not
// together with --company-models.
// --no-mcp turns MCP servers off for this run (an administrator's policy): none is listed, connected or
// offered to the AI, and the person cannot add one.
// BOHM_LLM_ENDPOINTS sends a provider's traffic to a gateway that speaks the same API instead of the provider
// itself: `host=address` pairs separated by `;` (https, or http on this computer) — see LlmEndpointsVariable.
// Pairs that cannot be used are left out and named on standard error.
// BOHM_REVIEW_MODEL (verification — the setting that offers it comes later) names a model, `<provider>/<model>`, that
// reads a newly written application once before it is polished; its review goes to the model that polishes it.
// The provider's key is the one connected in the vault; without it, or when the review cannot be had, no review.
// BOHM_CLOUD is the Bohm cloud's base address (https, or http on this computer): with it the person may choose a model
// their Bohm account pays for — see CloudAccount. Without it (the default) nothing is ever sent there. Only builds that carry
// the account read it (Debug by default; BohmCloud in the project file) — a release build ignores it.
// BOHM_DISCARD_DIR (verification only) sends applications removed for good to that folder instead of
// the recycle bin, so an automated check does not fill the person's recycle bin.
// BOHM_VAULT_PREFIX (verification only) keeps the keys under that prefix in the Windows Credential
// Manager instead of Bohm/, so an automated check never reads, overwrites or deletes the person's keys
// — and what a stopped check leaves behind is in its own space, not theirs.
const string usage = "Usage: Bohm.Runtime.Host --data-root <directory> [--port <n>] [--parent-pid <pid>] [--llama-server <path>] [--company-models <json> | --company-model-endpoint <url> --company-model <name>] [--no-mcp]";
string? dataRoot = null;
int? port = null;
int? parentPid = null;
string? llamaServer = null;
var (llmEndpoints, unusableEndpoints) = Bohm.Runtime.Host.Llm.LlmEndpointsVariable.Parse(Environment.GetEnvironmentVariable(Bohm.Runtime.Host.Llm.LlmEndpointsVariable.Name));
// Named by its provider host only: an address that was refused may carry a user name and password.
foreach (var pair in unusableEndpoints)
    await Console.Error.WriteLineAsync($"{Bohm.Runtime.Host.Llm.LlmEndpointsVariable.Name}: the pair for «{pair.Split('=', 2)[0].Trim()}» is not a known provider host with an https address (or http on this computer); left out.");
var reviewVariable = Environment.GetEnvironmentVariable("BOHM_REVIEW_MODEL");
var reviewModel = reviewVariable?.Split('/', 2) is [var reviewProvider, var reviewName]
    && Bohm.Runtime.Host.Llm.LlmProviders.ById(reviewProvider.Trim()) is not null && reviewName.Trim().Length > 0
    ? new Bohm.Runtime.Host.Edit.EditModelChoice(reviewProvider.Trim(), reviewName.Trim())
    : null;
if (reviewModel is null && !string.IsNullOrWhiteSpace(reviewVariable))
    await Console.Error.WriteLineAsync("BOHM_REVIEW_MODEL is not <provider>/<model> with a known provider; no review this run.");
#if BOHM_CLOUD
var cloudVariable = Environment.GetEnvironmentVariable(Bohm.Runtime.Host.Llm.CloudAccount.Variable);
#else
// A build that does not carry the Bohm account (a release build, until the cloud service is ready — see the BohmCloud
// property in the project file) never takes an address from the variable: the account is not offered and nothing is sent
// there. Said on standard error, so a check of a release build can see that it ignored the variable.
string? cloudVariable = null;
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Bohm.Runtime.Host.Llm.CloudAccount.Variable)))
    await Console.Error.WriteLineAsync($"{Bohm.Runtime.Host.Llm.CloudAccount.Variable} is ignored: this build does not carry the Bohm account.");
#endif
var cloudEndpoint = Bohm.Runtime.Host.Llm.CloudAccount.EndpointOf(cloudVariable);
if (cloudEndpoint is null && !string.IsNullOrWhiteSpace(cloudVariable))
    await Console.Error.WriteLineAsync($"{Bohm.Runtime.Host.Llm.CloudAccount.Variable} is not an https address (or http on this computer) without a user name, query or fragment; no Bohm account this run.");
string? companyEndpoint = null;
string? companyModelName = null;
string? companyModelsJson = null;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--data-root": dataRoot = args[++i]; break;
        case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--parent-pid": parentPid = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--llama-server": llamaServer = args[++i]; break;
        case "--company-model-endpoint": companyEndpoint = args[++i]; break;
        case "--company-model": companyModelName = args[++i]; break;
        case "--company-models": companyModelsJson = args[++i]; break;
    }
}

Bohm.Runtime.Host.Llm.CompanyModelOptions? companyModel = null;
if (dataRoot is null
    || (companyEndpoint ?? companyModelName) is not null
        && (companyModelsJson is not null
            || !Bohm.Runtime.Host.Llm.CompanyModelOptions.TryCreate(companyEndpoint, companyModelName, out companyModel)))
{
    await Console.Error.WriteLineAsync(usage);
    return 2;
}

var companyModels = companyModel is not null ? Bohm.Runtime.Host.Llm.CompanyModelList.Of(companyModel) : null;
if (companyModelsJson is not null && !Bohm.Runtime.Host.Llm.CompanyModelList.TryParse(companyModelsJson, out companyModels))
    await Console.Error.WriteLineAsync("--company-models lists no usable OpenAI-compatible server and model; starting without it.");

var shipped = Path.Combine(AppContext.BaseDirectory, "llama-server", OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
llamaServer ??= File.Exists(shipped) ? shipped : null;

// The native ONNX Runtime the speech model runs on ships next to the runtime (kept out of the single file so it can be
// loaded from a directory); a build without it runs no speech model.
var speechRuntime = new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native") }
    .FirstOrDefault(d => File.Exists(Path.Combine(d, OperatingSystem.IsWindows() ? "onnxruntime.dll" : "libonnxruntime.so")));

// A parent that is already gone: say so and stop before serving anything (the watch below would stop the host
// too, but only after it had started and announced itself ready).
if (parentPid is { } pid)
{
    try
    {
        using var parent = Process.GetProcessById(pid);
    }
    catch (ArgumentException)
    {
        await Console.Error.WriteLineAsync($"Parent process {pid} is not running; stopping.");
        return 3;
    }
}

// Standard output carries only the protocol line below; every log line goes to standard error.
// Per-request lines from ASP.NET Core are kept at Warning (its usual production level): the caller
// polls the control API every few seconds, so at Information an idle runtime grows its log for as long
// as it runs.
var started = await RuntimeHost.StartAsync(new RuntimeHostOptions
{
    DataRoot = dataRoot,
    Port = port,
    ControlSecret = LoopbackCredentials.ReadToken("BOHM_RUNTIME_SECRET"),
    LlamaServerPath = llamaServer,
    SpeechRuntimeDirectory = speechRuntime,
    CompanyModels = companyModels,
    McpAllowed = !args.Contains("--no-mcp"),
    LlmEndpoints = llmEndpoints.Count > 0 ? llmEndpoints : null,
    ReviewModel = reviewModel,
    CloudEndpoint = cloudEndpoint,
    Vault = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("BOHM_VAULT_PREFIX") is { Length: > 0 } vaultPrefix
        ? new WindowsCredentialVault(vaultPrefix)
        : null,
    Discard =Environment.GetEnvironmentVariable("BOHM_DISCARD_DIR") is { Length: > 0 } discardDir
        ? (folder, _) =>
        {
            Directory.CreateDirectory(discardDir);
            Directory.Move(folder, Path.Combine(discardDir, Bohm.Runtime.Host.Control.ControlPlane.DiscardedName(folder)));
            return Task.CompletedTask;
        }
        : null,
},
    builder =>
    {
        builder.Logging
            .AddFilter("Microsoft.AspNetCore", LogLevel.Warning)
            .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        // A normal stop when the parent ends, so storage is closed cleanly. A process that took the parent's id
        // after the parent ended does not count as the parent.
        if (parentPid is { } watched) builder.Services.AddParentProcessWatch(watched);
    });
await using var app = started.App;

Console.WriteLine(LoopbackHost.ReadyLine(RuntimeHost.ReadyPrefix, started.Port, started.PreviousPort is { } previous
    ? [new("previousPort", previous.ToString(CultureInfo.InvariantCulture))]
    : null));
await app.WaitForShutdownAsync();
return 0;
