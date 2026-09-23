using System.Collections.Concurrent;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Storage;
using Bohm.Runtime.Usage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>An adopted application while the host is running: its storage, its usage record and recent load failures.</summary>
internal sealed class OpenApp(AppStorage storage, UsageLog usage)
{
    private const int KeptLoadErrors = 5;
    private readonly Queue<string> _loadErrors = new();
    private readonly Lock _lock = new();
    private readonly HashSet<string> _neededKeys = new(StringComparer.Ordinal);

    public AppStorage Storage { get; } = storage;
    public UsageLog Usage { get; } = usage;

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

    public Task<OpenApp> GetAsync(string appId) =>
        _open.GetOrAdd(appId, id => new Lazy<Task<OpenApp>>(() => OpenAsync(id))).Value;

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
        return new OpenApp(storage, catalog.OpenUsage(appId));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _open.Values.Where(l => l.IsValueCreated))
        {
            try
            {
                await (await entry.Value.ConfigureAwait(false)).Storage.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogCloseFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage of {AppId} repaired on load: {Kind} — {Detail}")]
    private static partial void LogRepair(ILogger logger, string appId, StorageRecoveryKind kind, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Closing an application's storage failed.")]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);
}
