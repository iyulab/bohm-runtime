using System.Net;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Adoption;
using Bohm.Runtime.Host.Control;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Bohm.Runtime.Host;

/// <summary>Settings for <see cref="RuntimeHost"/>.</summary>
public sealed record RuntimeHostOptions
{
    /// <summary>Directory holding everything the runtime stores. Chosen by whoever starts the runtime.</summary>
    public required string DataRoot { get; init; }

    /// <summary>
    /// Loopback port to listen on. <see langword="null"/> (the default) serves the data root on the
    /// port it was served on last time, choosing and remembering a free one when there is none or it
    /// is taken — see <see cref="RuntimeHost.StartAsync"/>. <c>0</c> lets the operating system choose
    /// every time; any other value is used as given.
    /// </summary>
    public int? Port { get; init; }

    /// <summary>
    /// Secret that control requests must bear. Chosen by whoever starts the runtime, per launch.
    /// Without one, the control API does not exist.
    /// </summary>
    public string? ControlSecret { get; init; }

    /// <summary>
    /// Where AI provider keys are kept. Defaults to the Windows Credential Manager on Windows and to
    /// memory elsewhere (a headless deployment supplies its own).
    /// </summary>
    public ICredentialVault? Vault { get; init; }

    /// <summary>
    /// Sends a provider's traffic to another base address instead of the provider itself — a
    /// company gateway that speaks the same API, or a stand-in during tests. Keyed by provider host.
    /// </summary>
    public IReadOnlyDictionary<string, Uri>? LlmEndpoints { get; init; }

    /// <summary>
    /// Whether adopting an application also fetches, in the background, the code it loads from
    /// other hosts. On by default; the fetch needs the network once, at adoption.
    /// </summary>
    public bool FetchAssetsOnAdoption { get; init; } = true;

    /// <summary>Replaces the network for asset fetching — for tests.</summary>
    public Func<HttpMessageHandler>? AssetHttpHandler { get; init; }
}

/// <summary>
/// The runtime as an HTTP server bound to the loopback interface only. The desktop shell starts it
/// as a companion process; the same host runs without any shell.
/// </summary>
public static class RuntimeHost
{
    /// <summary>Builds (but does not start) the host.</summary>
    public static WebApplication Build(RuntimeHostOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port ?? HostAddress.Read(options.DataRoot) ?? 0));
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(new AdoptionCatalog(options.DataRoot));
        builder.Services.AddSingleton(options.Vault ?? (OperatingSystem.IsWindows() ? new WindowsCredentialVault() : new MemoryCredentialVault()));
        var assets = builder.Services.AddHttpClient("assets", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(20);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Bohm-Runtime");
        });
        assets.ConfigurePrimaryHttpMessageHandler(options.AssetHttpHandler ?? PublicOnlyHandler);
        // No overall timeout: a streamed answer can legitimately run for minutes.
        builder.Services.AddHttpClient(nameof(Llm.LlmProxy), client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        builder.Services.AddSingleton<AppSessions>();
        builder.Services.AddSingleton<OpenApps>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<Activity>();
        builder.Services.AddSingleton<AssetFetcher>();
        configure?.Invoke(builder);

        var app = builder.Build();
        app.Run(context =>
        {
            // Only names under .localhost reach an application. A request naming any other host —
            // including a public name rebound to the loopback address — is not served.
            if (AdoptedAppServing.AppIdOf(context.Request) is { } appId)
                return AdoptedAppServing.ServeAsync(context, appId);

            if (ControlPlane.IsControlHost(context.Request))
                return ControlPlane.HandleAsync(context, options.ControlSecret);

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        return app;
    }

    /// <summary>
    /// The network for fetching applications' code: it connects only to public addresses. The
    /// cache already refuses URLs that name a local address; this also stops a public name that
    /// resolves to one — including a redirect to such a name — at the moment of connecting.
    /// </summary>
    private static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        UseCookies = false,
        MaxAutomaticRedirections = 5,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            var target = addresses.FirstOrDefault(a => Bohm.Runtime.Assets.AssetCache.IsPublicHttps(new Uri($"https://{(a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString())}/")))
                ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} does not resolve to a public address.");
            var socket = new System.Net.Sockets.Socket(target.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(target, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>
    /// Builds and starts the host. With <see cref="RuntimeHostOptions.Port"/> left unset, the data
    /// root keeps its address: the remembered port is used again, and only when it is taken does the
    /// host start on a new one — reported in <see cref="StartedRuntime.PreviousPort"/> so the caller
    /// can tell the person that the applications' addresses changed.
    /// </summary>
    public static async Task<StartedRuntime> StartAsync(RuntimeHostOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Port is not null)
        {
            var fixedApp = Build(options, configure);
            await fixedApp.StartAsync().ConfigureAwait(false);
            return new StartedRuntime(fixedApp, fixedApp.ListeningPort(), null);
        }

        var remembered = HostAddress.Read(options.DataRoot);
        WebApplication? app = null;
        if (remembered is { } port)
        {
            app = Build(options with { Port = port }, configure);
            try
            {
                await app.StartAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Taken by something else since last time; start on a new port below.
                await app.DisposeAsync().ConfigureAwait(false);
                app = null;
            }
        }

        if (app is null)
        {
            app = Build(options with { Port = 0 }, configure);
            await app.StartAsync().ConfigureAwait(false);
        }

        var listening = app.ListeningPort();
        if (listening != remembered) HostAddress.Write(options.DataRoot, listening);
        return new StartedRuntime(app, listening, remembered is { } before && before != listening ? before : null);
    }

    /// <summary>The port a started host is listening on.</summary>
    public static int ListeningPort(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address).Port;
    }

    /// <summary>The address an adopted application is served at.</summary>
    public static Uri AppOrigin(string appId, int port) => new($"http://{appId}.localhost:{port}/");
}

/// <summary>A started runtime host.</summary>
/// <param name="App">The running host; dispose it to stop.</param>
/// <param name="Port">The loopback port it listens on.</param>
/// <param name="PreviousPort">
/// The port the data root was served on before, when it could not be used again — the
/// applications' addresses changed. <see langword="null"/> when they did not.
/// </param>
public sealed record StartedRuntime(WebApplication App, int Port, int? PreviousPort);
