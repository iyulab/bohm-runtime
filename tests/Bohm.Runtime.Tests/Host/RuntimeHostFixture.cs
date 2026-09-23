using System.Net;
using System.Text;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bohm.Runtime.Tests.Host;

/// <summary>A real runtime host on a free loopback port, over a throwaway data root.</summary>
public sealed class RunningHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RunningHost(WebApplication app, string dataRoot, int port)
    {
        _app = app;
        DataRoot = dataRoot;
        Port = port;
        Catalog = app.Services.GetRequiredService<AdoptionCatalog>();
    }

    public string DataRoot { get; }
    public int Port { get; }
    public AdoptionCatalog Catalog { get; }

    public const string Secret = "per-launch-secret";

    public static async Task<RunningHost> StartAsync(string? dataRoot = null, string? secret = Secret)
    {
        dataRoot ??= Directory.CreateTempSubdirectory("bohm-host-").FullName;
        var app = RuntimeHost.Build(new RuntimeHostOptions { DataRoot = dataRoot, ControlSecret = secret }, b => b.Logging.ClearProviders());
        await app.StartAsync();
        return new RunningHost(app, dataRoot, app.ListeningPort());
    }

    public async Task<string> AdoptAsync(string html) => (await Catalog.AdoptAsync(Encoding.UTF8.GetBytes(html))).Id;

    /// <summary>A client that talks to the loopback port while naming <paramref name="host"/> in the Host header.</summary>
    public HttpClient ClientFor(string host)
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{Port}/"),
        };
        client.DefaultRequestHeaders.Host = $"{host}:{Port}";
        return client;
    }

    public HttpClient ClientForApp(string appId) => ClientFor($"{appId}.localhost");

    /// <summary>A client for the control API, bearing the per-launch secret.</summary>
    public HttpClient ControlClient(string? secret = Secret)
    {
        var client = ClientFor("127.0.0.1");
        if (secret is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(DataRoot)) Directory.Delete(DataRoot, recursive: true);
    }

    public async Task StopKeepingDataAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal static class HttpAssert
{
    public static void Status(HttpStatusCode expected, HttpResponseMessage response) => Assert.Equal(expected, response.StatusCode);
}
