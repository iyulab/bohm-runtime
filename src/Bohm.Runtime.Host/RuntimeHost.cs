using System.Net;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Host.Adoption;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Bohm.Runtime.Host;

/// <summary>Settings for <see cref="RuntimeHost"/>.</summary>
public sealed record RuntimeHostOptions
{
    /// <summary>Directory holding everything the runtime stores. Chosen by whoever starts the runtime.</summary>
    public required string DataRoot { get; init; }

    /// <summary>Loopback port to listen on; 0 lets the operating system choose a free one.</summary>
    public int Port { get; init; }
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
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
        builder.Services.AddSingleton(new AdoptionCatalog(options.DataRoot));
        builder.Services.AddSingleton<AppSessions>();
        builder.Services.AddSingleton<OpenStorages>();
        configure?.Invoke(builder);

        var app = builder.Build();
        app.Run(context =>
        {
            // Only names under .localhost reach an application. A request naming any other host —
            // including a public name rebound to the loopback address — is not served.
            if (AdoptedAppServing.AppIdOf(context.Request) is { } appId)
                return AdoptedAppServing.ServeAsync(context, appId);

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
        return app;
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
