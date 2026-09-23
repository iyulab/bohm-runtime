using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Credentials;
using Bohm.Runtime.Host.Adoption;
using Bohm.Runtime.Host.Llm;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Control;

/// <summary>
/// The API the process that started the runtime uses to drive it — the desktop shell, or a
/// headless deployment's configuration. It answers only on the loopback address itself (never on
/// an application origin) and only to requests bearing the per-launch secret.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><c>GET /__control/apps</c></term><description>Adopted applications, oldest first, each with the last day it was used.</description></item>
/// <item><term><c>POST /__control/apps/matches</c></term><description>Earlier adoptions of the HTML in the body, or of a file at the same path (optional <c>X-Bohm-Original-Path</c>), each with how it matches.</description></item>
/// <item><term><c>POST /__control/apps</c></term><description>Adopts the HTML in the body (optional <c>X-Bohm-Original-Path</c>, URL-encoded).</description></item>
/// <item><term><c>GET /__control/apps/{id}/status</c></term><description>Today's usage signals, load failures, blocked resources, missing files and keys needed.</description></item>
/// <item><term><c>POST /__control/apps/{id}/assets</c></term><description>Fetches (again) the code the application loads from other hosts; answers what was and was not cached.</description></item>
/// <item><term><c>GET /__control/llm</c></term><description>AI providers and whether a key is connected (never the key).</description></item>
/// <item><term><c>PUT /__control/llm/{provider}/key</c></term><description>Connects the key in the body, stored in the vault.</description></item>
/// <item><term><c>DELETE /__control/llm/{provider}/key</c></term><description>Disconnects it.</description></item>
/// <item><term><c>POST /__control/drain</c></term><description>Waits until no storage write is in progress.</description></item>
/// <item><term><c>POST /__control/shutdown</c></term><description>Drains, then stops the runtime.</description></item>
/// </list>
/// </remarks>
internal static class ControlPlane
{
    public const string PathPrefix = "/__control";
    public const string OriginalPathHeader = "X-Bohm-Original-Path";

    /// <summary>How long no write may be in progress before the runtime counts as drained.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);

    /// <summary>The longest a drain waits. Past it the caller proceeds and a loss is possible.</summary>
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(2);

    public static bool IsControlHost(HttpRequest request) =>
        request.Host.Host is "127.0.0.1" or "localhost" && request.Path.StartsWithSegments(PathPrefix);

    public static async Task HandleAsync(HttpContext context, string? secret)
    {
        var request = context.Request;
        var response = context.Response;
        if (secret is null || !Authorized(request, secret))
        {
            response.StatusCode = secret is null ? StatusCodes.Status404NotFound : StatusCodes.Status401Unauthorized;
            return;
        }

        var catalog = context.RequestServices.GetRequiredService<AdoptionCatalog>();
        var segments = request.Path.Value![PathPrefix.Length..].Trim('/').Split('/');
        var port = request.Host.Port ?? 80;
        var cancel = context.RequestAborted;

        switch (request.Method, segments)
        {
            case ("GET", ["apps"]):
                // The usage record is read from its file: appends reach it at once, and reading it does not open the application.
                await WriteAsync(response, (await catalog.ListAsync(cancel).ConfigureAwait(false))
                    .Select(a => View(a, port, catalog.OpenUsage(a.Id).LastUsedOn)).ToList(), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", "matches"]):
                var candidate = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                var matches = await catalog.FindEarlierAdoptionsAsync(candidate, OriginalPath(request), cancel).ConfigureAwait(false);
                await WriteAsync(response, matches.Select(m => ViewMatch(m, port)).ToList(), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps"]):
                var html = await ReadBodyAsync(request, cancel).ConfigureAwait(false);
                if (html.Length == 0)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    break;
                }

                var adopted = await catalog.AdoptAsync(html, OriginalPath(request), cancel).ConfigureAwait(false);
                if (context.RequestServices.GetRequiredService<RuntimeHostOptions>().FetchAssetsOnAdoption)
                    context.RequestServices.GetRequiredService<AssetFetcher>().Start(adopted.Id);
                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(adopted, port), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["apps", var id, "status"]):
                if (await catalog.GetAsync(id, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var app = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(id).ConfigureAwait(false);
                var today = app.Usage.Today;
                var signals = app.Usage.SignalsOn(today);
                await WriteAsync(response, new AppStatus(
                    today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    signals.Contains(UsageSignal.Opened), signals.Contains(UsageSignal.Input), signals.Contains(UsageSignal.Wrote),
                    app.Usage.LoadErrorsOn(today), app.RecentLoadErrors, app.NeededKeys, app.Blocked, app.MissingFiles, app.Assets.Assets.Count), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["apps", var assetsFor, "assets"]):
                if (await catalog.GetAsync(assetsFor, cancel).ConfigureAwait(false) is null)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                await context.RequestServices.GetRequiredService<AssetFetcher>().FetchAsync(assetsFor, cancel).ConfigureAwait(false);
                var fetchedApp = await context.RequestServices.GetRequiredService<OpenApps>().GetAsync(assetsFor).ConfigureAwait(false);
                await WriteAsync(response, new AssetsView(
                    fetchedApp.Assets.Assets.Select(a => new AssetView(a.Url, a.Size)).ToList(),
                    fetchedApp.Assets.Failures.Select(f => new AssetView(f.Url, 0, f.Reason)).ToList()), cancel).ConfigureAwait(false);
                break;

            case ("GET", ["llm"]):
                var vault = context.RequestServices.GetRequiredService<ICredentialVault>();
                await WriteAsync(response, LlmProviders.All
                    .Select(p => new ProviderView(p.Id, p.DisplayName, p.Host, !string.IsNullOrEmpty(vault.Read(p.VaultName))))
                    .ToList(), cancel).ConfigureAwait(false);
                break;

            case ("PUT" or "DELETE", ["llm", var providerId, "key"]):
                if (LlmProviders.ById(providerId) is not { } provider)
                {
                    response.StatusCode = StatusCodes.Status404NotFound;
                    break;
                }

                var keys = context.RequestServices.GetRequiredService<ICredentialVault>();
                if (request.Method == "DELETE")
                {
                    keys.Delete(provider.VaultName);
                }
                else
                {
                    var key = System.Text.Encoding.UTF8.GetString(await ReadBodyAsync(request, cancel).ConfigureAwait(false)).Trim();
                    if (key.Length == 0)
                    {
                        response.StatusCode = StatusCodes.Status400BadRequest;
                        break;
                    }

                    keys.Write(provider.VaultName, key);
                }

                await WriteAsync(response, new ProviderView(provider.Id, provider.DisplayName, provider.Host, request.Method != "DELETE"), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["drain"]):
                await WriteAsync(response, new DrainResult(await DrainAsync(context, cancel).ConfigureAwait(false)), cancel).ConfigureAwait(false);
                break;

            case ("POST", ["shutdown"]):
                var quiet = await DrainAsync(context, cancel).ConfigureAwait(false);
                await WriteAsync(response, new DrainResult(quiet), cancel).ConfigureAwait(false);
                await response.CompleteAsync().ConfigureAwait(false);
                context.RequestServices.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                break;

            default:
                response.StatusCode = StatusCodes.Status404NotFound;
                break;
        }
    }

    private static Task<bool> DrainAsync(HttpContext context, CancellationToken cancellationToken) =>
        context.RequestServices.GetRequiredService<Activity>().WaitForQuietAsync(Quiet, DrainLimit, cancellationToken);

    private static bool Authorized(HttpRequest request, string secret)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[scheme.Length..]), Encoding.UTF8.GetBytes(secret));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static string? OriginalPath(HttpRequest request) =>
        request.Headers[OriginalPathHeader].ToString() is { Length: > 0 } encoded ? Uri.UnescapeDataString(encoded) : null;

    private static MatchView ViewMatch(AdoptionMatch match, int port) =>
        new(View(match.App, port), match.Kind switch
        {
            AdoptionMatchKind.SameBytes => "sameBytes",
            AdoptionMatchKind.SameOriginalPath => "sameOriginalPath",
            _ => throw new ArgumentOutOfRangeException(nameof(match)),
        });

    private static AppView View(AdoptedApp app, int port, DateOnly? lastUsed = null) =>
        new(app.Id, RuntimeHost.AppOrigin(app.Id, port).ToString(), app.AdoptedAt, app.Source.Sha256, app.Source.OriginalPath, app.Source.Size,
            lastUsed?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private static Task WriteAsync<T>(HttpResponse response, T value, CancellationToken cancellationToken) =>
        response.WriteAsJsonAsync(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)ControlJson.Default.GetTypeInfo(typeof(T))!, cancellationToken: cancellationToken);

    /// <summary>An adopted application. <c>LastUsed</c> (local <c>yyyy-MM-dd</c>) is filled in the listing only.</summary>
    internal sealed record AppView(string Id, string Origin, DateTimeOffset AdoptedAt, string Sha256, string? OriginalPath, long Size, string? LastUsed = null);

    /// <summary>An earlier adoption and how it matches: <c>"sameBytes"</c> or <c>"sameOriginalPath"</c>.</summary>
    internal sealed record MatchView(AppView App, string Match);

    /// <summary>
    /// Today's facts about one application. Structured only — turning them into sentences for a
    /// person is the caller's job, in the person's language.
    /// </summary>
    internal sealed record AppStatus(string Date, bool Opened, bool Input, bool Wrote, int LoadErrors, IReadOnlyList<string> RecentLoadErrors,
        IReadOnlyList<string> NeedsKey, IReadOnlyList<BlockedResource> Blocked, IReadOnlyList<string> MissingFiles, int CachedAssets);

    internal sealed record AssetView(string Url, long Size, string? Reason = null);

    internal sealed record AssetsView(IReadOnlyList<AssetView> Cached, IReadOnlyList<AssetView> NotCached);

    internal sealed record ProviderView(string Id, string Name, string Host, bool Connected);

    internal sealed record DrainResult(bool Quiet);
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AppView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.AppView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.MatchView>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AppStatus))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.DrainResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(BlockedResource))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.AssetsView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ControlPlane.ProviderView))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ControlPlane.ProviderView>))]
internal sealed partial class ControlJson : System.Text.Json.Serialization.JsonSerializerContext;
