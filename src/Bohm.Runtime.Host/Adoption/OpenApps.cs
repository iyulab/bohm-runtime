using System.Collections.Concurrent;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Assets;
using Bohm.Runtime.Storage;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>Something from another host that the content security policy refused.</summary>
/// <param name="Category"><c>library</c> (script, style, font), <c>data</c> (anything fetched or shown) or <c>form</c>.</param>
/// <param name="Host">The host it would have come from.</param>
internal sealed record BlockedResource(string Category, string Host);

/// <summary>An adopted application while the host is running: its storage, its usage record and recent load failures.</summary>
internal sealed class OpenApp(AppStorage storage, UsageLog usage, AssetCache assets)
{
    private const int KeptLoadErrors = 5;
    private readonly Queue<string> _loadErrors = new();
    private readonly Lock _lock = new();
    private readonly HashSet<string> _neededKeys = new(StringComparer.Ordinal);
    private readonly List<BlockedResource> _blocked = [];
    private readonly List<string> _missingFiles = [];

    public AppStorage Storage { get; } = storage;
    public UsageLog Usage { get; } = usage;
    public AssetCache Assets { get; } = assets;

    /// <summary>One asset fetch at a time per application — an adoption's background fetch and an explicit one must not interleave.</summary>
    public SemaphoreSlim AssetFetch { get; } = new(1, 1);

    /// <summary>One change of revision at a time per application.</summary>
    public SemaphoreSlim RevisionChange { get; } = new(1, 1);

    /// <summary>
    /// Forgets what was observed about the code that was running — load failures, blocked resources,
    /// missing files, keys needed — once another revision takes its place. The usage record stays:
    /// it belongs to the application, not to a revision.
    /// </summary>
    public void ForgetObservations()
    {
        lock (_lock)
        {
            _loadErrors.Clear();
            _neededKeys.Clear();
            _blocked.Clear();
            _missingFiles.Clear();
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

    public void AddLoadError(string message)
    {
        Usage.RecordLoadError();
        lock (_lock)
        {
            _loadErrors.Enqueue(message.Length > 500 ? message[..500] : message);
            while (_loadErrors.Count > KeptLoadErrors) _loadErrors.Dequeue();
        }
    }
}

/// <summary>
/// Holds exactly one <see cref="OpenApp"/> per application for the life of the host.
/// <see cref="AppStorage"/> assumes it is the only writer of its files, so every request for an
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
        var app = _open.GetOrAdd(appId, id => new Lazy<Task<OpenApp>>(() => OpenAsync(id))).Value;
        // Disposal may have run between the check and the insertion and missed this entry.
        if (_disposed) _ = CloseAsync(app);
        return app;
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
        foreach (var repair in storage.Recovery)
            LogRepair(logger, appId, repair.Kind, repair.Detail);
        return new OpenApp(storage, catalog.OpenUsage(appId), catalog.OpenAssets(appId));
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
            await opening.Result.Storage.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            LogCloseFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage of {AppId} repaired on load: {Kind} — {Detail}")]
    private static partial void LogRepair(ILogger logger, string appId, StorageRecoveryKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Closing an application's storage failed.")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);
}
