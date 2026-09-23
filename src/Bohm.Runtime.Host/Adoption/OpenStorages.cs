using System.Collections.Concurrent;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Storage;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Holds exactly one open <see cref="AppStorage"/> per application for the life of the host.
/// <see cref="AppStorage"/> assumes it is the only writer of its files, so every request for an
/// application goes through the same instance.
/// </summary>
internal sealed partial class OpenStorages(AdoptionCatalog catalog, ILogger<OpenStorages> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<AppStorage>>> _open = new(StringComparer.Ordinal);

    public Task<AppStorage> GetAsync(string appId) =>
        _open.GetOrAdd(appId, id => new Lazy<Task<AppStorage>>(() => OpenAsync(id))).Value;

    private async Task<AppStorage> OpenAsync(string appId)
    {
        var storage = await catalog.OpenStorageAsync(appId).ConfigureAwait(false);
        foreach (var repair in storage.Recovery)
            LogRepair(logger, appId, repair.Kind, repair.Detail);
        return storage;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _open.Values.Where(l => l.IsValueCreated))
        {
            try
            {
                await (await entry.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
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
