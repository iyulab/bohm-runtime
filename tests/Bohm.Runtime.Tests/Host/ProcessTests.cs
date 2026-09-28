using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Bohm.Runtime.Credentials;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

/// <summary>The runtime as a separate process, started the way the desktop shell starts it.</summary>
public sealed class ProcessTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("bohm-process-").FullName;

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    [Fact]
    public async Task The_process_announces_its_port_then_adopts_and_shuts_down_on_request()
    {
        const string secret = "launch-secret";
        using var process = Start(secret);
        try
        {
            var port = await ReadReadyPortAsync(process);
            using var control = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            using var adopted = await control.PostAsync("/__control/apps", new ByteArrayContent(Encoding.UTF8.GetBytes("<!doctype html><p>hi</p>")));
            Assert.True(adopted.IsSuccessStatusCode);

            using var shutdown = await control.PostAsync("/__control/shutdown", null);
            Assert.True(JsonDocument.Parse(await shutdown.Content.ReadAsStringAsync()).RootElement.GetProperty("quiet").GetBoolean());

            using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(exited.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task A_relaunch_keeps_the_port_and_the_ready_line_reports_when_it_could_not()
    {
        var first = await LaunchAndStopAsync();
        Assert.Equal(first.Port, (await LaunchAndStopAsync()).Port);

        using var squatter = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, first.Port);
        squatter.Start();
        var moved = await LaunchAndStopAsync();

        Assert.NotEqual(first.Port, moved.Port);
        Assert.Equal(first.Port, moved.PreviousPort);
        Assert.Null(first.PreviousPort);
    }

    private async Task<(int Port, int? PreviousPort)> LaunchAndStopAsync()
    {
        using var process = Start("s");
        try
        {
            var ready = await ReadReadyAsync(process);
            return (ready.GetProperty("port").GetInt32(), ready.TryGetProperty("previousPort", out var previous) ? previous.GetInt32() : null);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Standard_output_carries_only_the_ready_line()
    {
        using var process = Start("s");
        try
        {
            await ReadReadyPortAsync(process);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Equal("", (await process.StandardOutput.ReadToEndAsync()).Trim());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task The_runtime_stops_by_itself_when_the_process_that_started_it_ends()
    {
        // A stand-in for the shell. It ends when the test ends it, once the runtime is ready — a parent that
        // ended on its own after a fixed time raced the runtime's start on a busy machine (the runtime then
        // saw no parent and stopped before it was ready).
        using var parent = OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!
            : Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        using var process = Start("s", parent.Id);
        try
        {
            await ReadReadyPortAsync(process);
            parent.Kill(entireProcessTree: true);
            using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(exited.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!parent.HasExited) parent.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task The_organizations_model_server_given_at_start_is_fixed()
    {
        const string secret = "launch-secret";
        using var process = Start(secret, extra: ["--company-model-endpoint", "http://models.example:8000/v1", "--company-model", "qwen"]);
        try
        {
            var port = await ReadReadyPortAsync(process);
            using var control = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            var state = JsonDocument.Parse(await control.GetStringAsync("/__control/llm/company-model")).RootElement;
            Assert.Equal("http://models.example:8000/v1/", state.GetProperty("endpoint").GetString());
            Assert.Equal("qwen", state.GetProperty("model").GetString());
            Assert.True(state.GetProperty("fixed").GetBoolean());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Theory]
    [InlineData("--company-model-endpoint", "http://models.example:8000/v1")] // no model's name
    [InlineData("--company-model", "qwen")] // no address
    [InlineData("--company-model-endpoint", "ftp://models.example/v1", "--company-model", "qwen")]
    public async Task A_model_server_given_only_in_part_or_unusable_stops_the_runtime_with_its_usage(params string[] extra)
    {
        using var process = Start("launch-secret", extra: extra);
        using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(exited.Token);
        Assert.Equal(2, process.ExitCode);
    }

    [Fact]
    public async Task A_verification_run_keeps_its_keys_under_its_own_vault_prefix_and_never_in_the_persons()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows Credential Manager is the vault only on Windows.");
            return;
        }

        const string secret = "launch-secret";
        var prefix = $"Bohm-verify-{Guid.NewGuid():N}";
        var theirs = new WindowsCredentialVault(prefix);
        var persons = new WindowsCredentialVault();
        var personsBefore = persons.Read("llm/mistral");
        using var process = Start(secret, environment: new() { ["BOHM_VAULT_PREFIX"] = prefix });
        try
        {
            var port = await ReadReadyPortAsync(process);
            using var control = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            control.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

            using (var put = await control.PutAsync("/__control/llm/mistral/key", new StringContent("verify-key"))) HttpAssert.Status(HttpStatusCode.OK, put);

            Assert.Equal("verify-key", theirs.Read("llm/mistral"));
            Assert.Equal(personsBefore, persons.Read("llm/mistral")); // the person's own key, if any, is untouched
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            theirs.Delete("llm/mistral");
            // Should the prefix not hold, the person's slot is put back as it was, not left with the test's key.
            if (persons.Read("llm/mistral") != personsBefore)
            {
                if (personsBefore is null) persons.Delete("llm/mistral");
                else persons.Write("llm/mistral", personsBefore);
            }
        }
    }

    private Process Start(string secret, int? parentPid = null, string[]? extra = null, Dictionary<string, string>? environment = null)
    {
        var host = Path.Combine(AppContext.BaseDirectory, "Bohm.Runtime.Host.dll");
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(host);
        info.ArgumentList.Add("--data-root");
        info.ArgumentList.Add(_dataRoot);
        if (parentPid is { } pid)
        {
            info.ArgumentList.Add("--parent-pid");
            info.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var argument in extra ?? []) info.ArgumentList.Add(argument);

        info.Environment["BOHM_RUNTIME_SECRET"] = secret;
        foreach (var (name, value) in environment ?? []) info.Environment[name] = value;
        var process = Process.Start(info)!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<int> ReadReadyPortAsync(Process process) =>
        (await ReadReadyAsync(process)).GetProperty("port").GetInt32();

    private static async Task<JsonElement> ReadReadyAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        var ready = JsonDocument.Parse(line!).RootElement.Clone();
        Assert.Equal("ready", ready.GetProperty("event").GetString());
        return ready;
    }
}
