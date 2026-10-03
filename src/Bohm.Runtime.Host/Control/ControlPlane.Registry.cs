using System.Text;
using System.Text.Json;
using Bohm.Runtime.Adoption;

namespace Bohm.Runtime.Host.Control;

/// <summary>
/// Application registries (see <see cref="AppRegistry"/>):
/// <list type="table">
/// <item><term><c>POST /__control/registries/read</c></term><description>Reads the registry whose root (a full folder path) is the body:
/// <c>{ state, index }</c> — <c>state</c> is <c>ok</c> (with the index), <c>unreachable</c> (not reachable now — offline is a state, not an
/// error), <c>expired</c> (past its <c>validUntil</c>) or <c>unknown-format</c>. 400 for a path that is not full.</description></item>
/// <item><term><c>POST /__control/registries/install</c></term><description>Installs <c>{ registry, id, version? }</c> — the given version, or the
/// highest on the default channel. The package is fetched, held to the hash the index lists, then taken in as a package is: a new
/// application (201), or — when the application is already here with other code — its new revision, its data here unchanged (201, as
/// <c>revisions</c>). Either way the application records where it came from (<c>installedFrom</c>). 400 <c>{ reason }</c>: the registry's
/// (<c>unreachable</c>, <c>expired</c>, <c>unknown-format</c>, <c>not-listed</c>, <c>bad-entry</c>) or a package's (<c>damaged</c> when it is not
/// the package the index lists); 409 when that code is already the revision in use.</description></item>
/// </list>
/// </summary>
internal static partial class ControlPlane
{
    private static readonly string[] RegistryStates = ["ok", "unreachable", "expired", "unknown-format"];

    private static async Task ReadRegistryAsync(HttpContext context, AdoptionCatalog catalog, CancellationToken cancel)
    {
        var root = Encoding.UTF8.GetString(await ReadBodyAsync(context.Request, cancel).ConfigureAwait(false)).Trim();
        if (!Path.IsPathFullyQualified(root))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var read = await catalog.ReadRegistryAsync(root, AppRegistry.ReachLimit, cancel).ConfigureAwait(false);
        await WriteAsync(context.Response, new RegistryReadView(RegistryStates[(int)read.State], read.Index), cancel).ConfigureAwait(false);
    }

    private static async Task InstallFromRegistryAsync(HttpContext context, AdoptionCatalog catalog, int port, CancellationToken cancel)
    {
        var response = context.Response;
        string? root, id, version;
        try
        {
            using var body = JsonDocument.Parse(await ReadBodyAsync(context.Request, cancel).ConfigureAwait(false));
            root = body.RootElement.GetProperty("registry").GetString();
            id = body.RootElement.GetProperty("id").GetString();
            version = body.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (root is null || !Path.IsPathFullyQualified(root) || !AdoptionCatalog.IsValidId(id))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        FetchedPackage fetched;
        try
        {
            fetched = await catalog.FetchFromRegistryAsync(root, id!, version, AppRegistry.ReachLimit, cancel).ConfigureAwait(false);
        }
        catch (RegistryRefusedException e)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteAsync(response, new PackageRefusal(e.Reason), cancel).ConfigureAwait(false);
            return;
        }
        catch (InvalidPackageException e)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await WriteAsync(response, Refusal(e), cancel).ConfigureAwait(false);
            return;
        }

        try
        {
            if (await catalog.GetAsync(id!, cancel).ConfigureAwait(false) is null)
            {
                AdoptedApp installed;
                try
                {
                    await catalog.ImportPackageAsync(fetched.File, cancel).ConfigureAwait(false);
                    installed = await catalog.SetInstalledFromAsync(id!, fetched.Install, cancel).ConfigureAwait(false);
                }
                catch (InvalidPackageException e)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteAsync(response, Refusal(e), cancel).ConfigureAwait(false);
                    return;
                }
                catch (AppAlreadyHereException e)
                {
                    // Taken in by another request since the check above.
                    response.StatusCode = StatusCodes.Status409Conflict;
                    await WriteAsync(response, new AlreadyHereView(e.Id, e.SameCode, e.SameCodeInUse), cancel).ConfigureAwait(false);
                    return;
                }

                response.StatusCode = StatusCodes.Status201Created;
                await WriteAsync(response, View(installed, port, await catalog.CanRevertAsync(installed, cancel).ConfigureAwait(false)), cancel).ConfigureAwait(false);
                return;
            }

            // Already here: the registry's newer code arrives the way any package of an application here does — as its new
            // revision, the data here kept, «previous revision» taking it back.
            byte[] page;
            try
            {
                var (manifest, packedPage) = await catalog.ReadPackagePageAsync(fetched.File, cancel).ConfigureAwait(false);
                if (manifest.Id != id)
                {
                    response.StatusCode = StatusCodes.Status400BadRequest;
                    await WriteAsync(response, new PackageRefusal(RegistryRefusedException.BadEntry), cancel).ConfigureAwait(false);
                    return;
                }

                page = packedPage;
            }
            catch (InvalidPackageException e)
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteAsync(response, Refusal(e), cancel).ConfigureAwait(false);
                return;
            }

            var source = fetched.Version.Src[(fetched.Version.Src.LastIndexOf('/') + 1)..];
            await ChangeRevisionAsync(context, id!, StatusCodes.Status201Created, async storage =>
            {
                await catalog.ReviseAsync(id!, page, source, storage, cancel).ConfigureAwait(false);
                return await catalog.SetInstalledFromAsync(id!, fetched.Install, cancel).ConfigureAwait(false);
            }, app => app.Usage.RecordRevision(reverted: false)).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(fetched.File);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A finished install stays finished; the copy's name keeps it out of every listing.
            }
        }
    }

    /// <summary>A registry read: its state, and the index when it is <c>ok</c>.</summary>
    internal sealed record RegistryReadView(string State, RegistryIndex? Index);
}
