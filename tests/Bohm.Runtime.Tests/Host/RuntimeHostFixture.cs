using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bohm.Runtime.Tests.Host;

/// <summary>A real runtime host on a free loopback port, over a throwaway data root.</summary>
public sealed partial class RunningHost : IAsyncDisposable
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

    /// <summary>The port the data root was served on before, when it could not be used again.</summary>
    public int? PreviousPort { get; private init; }

    public const string Secret = "per-launch-secret";

    public static async Task<RunningHost> StartAsync(string? dataRoot = null, string? secret = Secret, Func<RuntimeHostOptions, RuntimeHostOptions>? configure = null)
    {
        dataRoot ??= Directory.CreateTempSubdirectory("bohm-host-").FullName;
        // Tests never touch the real credential store.
        var options = new RuntimeHostOptions { DataRoot = dataRoot, ControlSecret = secret, Vault = new global::Bohm.Runtime.Credentials.MemoryCredentialVault() };
        var started = await RuntimeHost.StartAsync(configure?.Invoke(options) ?? options, b => b.Logging.ClearProviders());
        return new RunningHost(started.App, dataRoot, started.Port) { PreviousPort = started.PreviousPort };
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

    /// <summary>A loaded page of an application: its session cookie, its tab and the items it was given.</summary>
    public sealed record LoadedPage(string Cookie, string Tab, string Items);

    /// <summary>Loads the application's document as a browser would and returns what the page receives.</summary>
    public async Task<LoadedPage> LoadAsync(string appId)
    {
        using var response = await ClientForApp(appId).GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var boot = JsonDocument.Parse(BootData().Match(body).Groups[1].Value).RootElement;
        return new LoadedPage(cookie[..cookie.IndexOf(';', StringComparison.Ordinal)], boot.GetProperty("tab").GetString()!, boot.GetProperty("items").GetRawText());
    }

    /// <summary>Sends a storage batch from <paramref name="page"/>; <c>TAB</c> in <paramref name="json"/> is replaced with its tab.</summary>
    public async Task<HttpResponseMessage> PostStorageAsync(string appId, LoadedPage page, string json, bool header = true, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/storage")
        {
            Content = new StringContent(json.Replace("TAB", page.Tab, StringComparison.Ordinal), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Cookie", page.Cookie);
        if (header) request.Headers.Add("X-Bohm-Request", "1");
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await ClientForApp(appId).SendAsync(request);
    }

    [GeneratedRegex("var boot = (\\{.*?\\});", RegexOptions.Singleline)]
    private static partial Regex BootData();

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
