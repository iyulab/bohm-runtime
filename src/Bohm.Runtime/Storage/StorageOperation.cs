namespace Bohm.Runtime.Storage;

/// <summary>The kind of change a <see cref="StorageOperation"/> makes to an application's key-value storage.</summary>
public enum StorageOperationKind
{
    /// <summary>Store <c>Value</c> under <c>Key</c>, replacing any previous value.</summary>
    Set,

    /// <summary>Remove <c>Key</c>. Removing an absent key is not an error.</summary>
    Remove,

    /// <summary>Remove every key.</summary>
    Clear,
}

/// <summary>
/// One change to an application's key-value storage, mirroring the three mutating calls of the
/// Web Storage API (<c>setItem</c>, <c>removeItem</c>, <c>clear</c>). Keys and values are strings,
/// exactly as that API defines them.
/// </summary>
public sealed record StorageOperation
{
    private StorageOperation(StorageOperationKind kind, string? key, string? value)
    {
        Kind = kind;
        Key = key;
        Value = value;
    }

    /// <summary>What this operation does.</summary>
    public StorageOperationKind Kind { get; }

    /// <summary>The key affected; <see langword="null"/> only for <see cref="StorageOperationKind.Clear"/>.</summary>
    public string? Key { get; }

    /// <summary>The stored value; non-<see langword="null"/> only for <see cref="StorageOperationKind.Set"/>.</summary>
    public string? Value { get; }

    /// <summary>Creates a <see cref="StorageOperationKind.Set"/> operation.</summary>
    public static StorageOperation Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return new(StorageOperationKind.Set, key, value);
    }

    /// <summary>Creates a <see cref="StorageOperationKind.Remove"/> operation.</summary>
    public static StorageOperation Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(StorageOperationKind.Remove, key, null);
    }

    /// <summary>Creates a <see cref="StorageOperationKind.Clear"/> operation.</summary>
    public static StorageOperation Clear() => new(StorageOperationKind.Clear, null, null);

    internal void ApplyTo(IDictionary<string, string> items)
    {
        switch (Kind)
        {
            case StorageOperationKind.Set:
                items[Key!] = Value!;
                break;
            case StorageOperationKind.Remove:
                items.Remove(Key!);
                break;
            case StorageOperationKind.Clear:
                items.Clear();
                break;
        }
    }
}
