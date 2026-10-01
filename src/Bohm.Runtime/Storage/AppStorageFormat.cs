using LocalOrigin.Storage;

namespace Bohm.Runtime.Storage;

/// <summary>
/// How an application's data is kept on disk: a <see cref="KeyValueStore"/> whose snapshots carry
/// <see cref="Format"/>. Every store of an application's data is opened through <see cref="Options"/>, so
/// data written by any earlier version of the runtime stays readable — and data written now stays readable
/// by an earlier version.
/// </summary>
public static class AppStorageFormat
{
    /// <summary>The format identifier of every snapshot of an application's data.</summary>
    public const string Format = "bohm.storage/0";

    /// <summary><paramref name="tuning"/> (or the defaults) with the application data format.</summary>
    public static KeyValueStoreOptions Options(KeyValueStoreOptions? tuning = null) => (tuning ?? new KeyValueStoreOptions()) with { Format = Format };
}
