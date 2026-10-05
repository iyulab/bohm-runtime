using LocalOrigin.Storage;
using System.Collections.Concurrent;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Assets;
using Bohm.Runtime.Pages;
using Bohm.Runtime.Sources;
using Bohm.Runtime.Storage;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>Something from another host that the content security policy refused.</summary>
/// <param name="Category"><c>library</c> (script, style, font), <c>data</c> (anything fetched or shown) or <c>form</c>.</param>
/// <param name="Host">The host it would have come from.</param>
internal sealed record BlockedResource(string Category, string Host);

/// <summary>
/// A request the page made to its own origin for something other than a file — a <c>fetch</c> or
/// <c>XMLHttpRequest</c>, or a method other than GET — which the server it came from would have
/// answered. Path only: the query can carry what the person typed.
/// </summary>
/// <param name="Method">The request method, upper case.</param>
/// <param name="Path">The path asked for.</param>
internal sealed record MissingApi(string Method, string Path);

/// <summary>An adopted application while the host is running: its storage, its usage record, its sources, the pages sent to it and recent errors.</summary>
internal sealed class OpenApp(KeyValueStore storage, UsageLog usage, AssetCache assets, AppSources sources, AppPages pages)
{
    private const int KeptLoadErrors = 5;
    private readonly Queue<string> _loadErrors = new();
    private readonly Queue<string> _errors = new();
    private readonly Lock _lock = new();
    private readonly HashSet<string> _neededKeys = new(StringComparer.Ordinal);
    private readonly List<BlockedResource> _blocked = [];
    private readonly List<string> _missingFiles = [];
    private readonly List<MissingApi> _missingApis = [];

    public KeyValueStore Storage { get; } = storage;
    public UsageLog Usage { get; } = usage;
    public AssetCache Assets { get; } = assets;
    public AppSources Sources { get; } = sources;
    public AppPages Pages { get; } = pages;

    /// <summary>One asset fetch at a time per application — an adoption's background fetch and an explicit one must not interleave.</summary>
    public SemaphoreSlim AssetFetch { get; } = new(1, 1);

    /// <summary>One change of revision at a time per application.</summary>
    public SemaphoreSlim RevisionChange { get; } = new(1, 1);

    /// <summary>
    /// Forgets what was observed about the code that was running — load failures, later errors, blocked resources,
    /// missing files, keys needed — once another revision takes its place. The usage record stays:
    /// it belongs to the application, not to a revision.
    /// </summary>
    public void ForgetObservations()
    {
        lock (_lock)
        {
            _loadErrors.Clear();
            _errors.Clear();
            _neededKeys.Clear();
            _blocked.Clear();
            _missingFiles.Clear();
            _missingApis.Clear();
        }
    }

    /// <summary>
    /// The most recent load-failure messages, for showing the person what went wrong. Kept in memory
    /// only: a message can quote the application's own data, so it is never written to disk.
    /// </summary>
    public IReadOnlyList<string> RecentLoadErrors
    {
        get { lock (_lock) return _loadErrors.ToList(); }
    }

    /// <summary>
    /// The most recent errors after the application started — thrown by its code (say on a click) or reported by
    /// it with console.error. Memory only, like <see cref="RecentLoadErrors"/>: a message can quote its data.
    /// Not counted in the usage record — the application ran; one thing it does went wrong.
    /// </summary>
    public IReadOnlyList<string> RecentErrors
    {
        get { lock (_lock) return _errors.ToList(); }
    }

    /// <summary>Providers this application tried to use while no key was connected.</summary>
    public IReadOnlyList<string> NeededKeys
    {
        get { lock (_lock) return _neededKeys.Order(StringComparer.Ordinal).ToList(); }
    }

    public void NeedsKey(string providerId)
    {
        lock (_lock) _neededKeys.Add(providerId);
    }

    /// <summary>
    /// Things from other hosts the content security policy refused, in the order first seen. A
    /// blocked library usually stops the application; blocked data leaves parts of it empty.
    /// </summary>
    public IReadOnlyList<BlockedResource> Blocked
    {
        get { lock (_lock) return _blocked.ToList(); }
    }

    /// <summary>Files the page asked for next to itself that were not part of what was adopted.</summary>
    public IReadOnlyList<string> MissingFiles
    {
        get { lock (_lock) return _missingFiles.ToList(); }
    }

    /// <summary>Calls the page made to a server it expected on its own origin, which is not here.</summary>
    public IReadOnlyList<MissingApi> MissingApis
    {
        get { lock (_lock) return _missingApis.ToList(); }
    }

    public void AddBlocked(string category, string host)
    {
        var first = false;
        lock (_lock)
        {
            if (_blocked.Count < 20 && !_blocked.Contains(new BlockedResource(category, host)))
            {
                _blocked.Add(new BlockedResource(category, host));
                first = true;
            }
        }

        // A library the application cannot load is a failure to load; missing data is not.
        if (first && category == "library") Usage.RecordLoadError();
    }

    public void AddMissingFile(string path)
    {
        lock (_lock)
        {
            if (_missingFiles.Count < 20 && !_missingFiles.Contains(path)) _missingFiles.Add(path);
        }
    }

    public void AddMissingApi(string method, string path)
    {
        var call = new MissingApi(method.ToUpperInvariant(), path);
        lock (_lock)
        {
            if (_missingApis.Count < 20 && !_missingApis.Contains(call)) _missingApis.Add(call);
        }
    }

    public void AddLoadError(string message)
    {
        Usage.RecordLoadError();
        lock (_lock)
        {
            _loadErrors.Enqueue(message.Length > 500 ? message[..500] : message);
            while (_loadErrors.Count > KeptLoadErrors) _loadErrors.Dequeue();
        }
    }

    public void AddError(string message)
    {
        lock (_lock)
        {
            if (_errors.Contains(message)) return;
            _errors.Enqueue(message.Length > 500 ? message[..500] : message);
            while (_errors.Count > KeptLoadErrors) _errors.Dequeue();
        }
    }
}

/// <summary>
/// Holds exactly one <see cref="OpenApp"/> per application for the life of the host.
/// <see cref="KeyValueStore"/> assumes it is the only writer of its files, so every request for an
/// application goes through the same instance.
/// </summary>
internal sealed partial class OpenApps(AdoptionCatalog catalog, ILogger<OpenApps> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<OpenApp>>> _open = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    /// <summary>
    /// The one open instance of <paramref name="appId"/>. Refuses once the host has closed the
    /// applications — storage opened after that would never be closed.
    /// </summary>
    public Task<OpenApp> GetAsync(string appId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entry = _open.GetOrAdd(appId, id => new Lazy<Task<OpenApp>>(() => OpenAsync(id)));
        // A failed opening is not kept: its files may open later (a file kept only in the cloud, once
        // the connection is back), and the application must not stay broken until the host restarts.
        if (entry.Value.IsFaulted && _open.TryRemove(KeyValuePair.Create(appId, entry)))
            entry = _open.GetOrAdd(appId, id => new Lazy<Task<OpenApp>>(() => OpenAsync(id)));
        var app = entry.Value;
        // Disposal may have run between the check and the insertion and missed this entry.
        if (_disposed) _ = CloseAsync(app);
        return app;
    }

    /// <summary>
    /// Closes <paramref name="appId"/>'s open instance, if there is one, so its files are no longer
    /// held — before its folder leaves the catalog. The application must not be served meanwhile
    /// (an archived application is not).
    /// </summary>
    public async Task CloseAsync(string appId)
    {
        if (_open.TryRemove(appId, out var entry) && entry.IsValueCreated) await CloseAsync(entry.Value).ConfigureAwait(false);
    }

    /// <summary>The applications opened so far.</summary>
    public async Task<IReadOnlyList<OpenApp>> OpenedAsync()
    {
        var apps = new List<OpenApp>();
        foreach (var entry in _open.Values.Where(l => l.IsValueCreated)) apps.Add(await entry.Value.ConfigureAwait(false));
        return apps;
    }

    private async Task<OpenApp> OpenAsync(string appId)
    {
        var storage = await catalog.OpenStorageAsync(appId).ConfigureAwait(false);
        var usage = catalog.OpenUsage(appId);
        // The detail goes to the log; the usage record counts it, so the person and whoever judges the
        // record can see that something on disk was set aside — not only someone reading the log.
        foreach (var repair in storage.Recovery)
        {
            LogRepair(logger, appId, repair.Kind, repair.Detail);
            usage.RecordRepaired();
        }
        return new OpenApp(storage, usage, catalog.OpenAssets(appId), catalog.OpenSources(appId), catalog.OpenPages(appId));
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var entry in _open.Values.Where(l => l.IsValueCreated)) await CloseAsync(entry.Value).ConfigureAwait(false);
    }

    private async Task CloseAsync(Task<OpenApp> opening)
    {
        // An application whose opening failed has nothing to close; the failure went to its caller.
        await ((Task)opening).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!opening.IsCompletedSuccessfully) return;
        try
        {
            opening.Result.Sources.Dispose();
            opening.Result.Pages.Dispose();
            await opening.Result.Storage.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            LogCloseFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage of {AppId} repaired on load: {Kind} — {Detail}")]
    private static partial void LogRepair(ILogger logger, string appId, StoreRecoveryKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Closing an application's storage failed.")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);
}
