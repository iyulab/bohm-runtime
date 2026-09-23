using System.Diagnostics;
using System.Net.Http.Headers;
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
        // A stand-in for the shell that ends on its own after a few seconds.
        using var parent = OperatingSystem.IsWindows()
            ? Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 4 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!
            : Process.Start(new ProcessStartInfo("sleep", "3") { UseShellExecute = false })!;
        using var process = Start("s", parent.Id);
        try
        {
            await ReadReadyPortAsync(process);
            using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(exited.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private Process Start(string secret, int? parentPid = null)
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

        info.Environment["BOHM_RUNTIME_SECRET"] = secret;
        var process = Process.Start(info)!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<int> ReadReadyPortAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        var ready = JsonDocument.Parse(line!).RootElement;
        Assert.Equal("ready", ready.GetProperty("event").GetString());
        return ready.GetProperty("port").GetInt32();
    }
}
