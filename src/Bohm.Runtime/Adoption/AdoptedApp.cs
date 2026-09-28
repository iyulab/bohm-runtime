namespace Bohm.Runtime.Adoption;

/// <summary>
/// An HTML application taken in as-is. Its identity is issued at adoption and never derived from
/// the file: editing the file or moving it does not turn it into a different application, and a
/// revised file the person takes in as a new revision stays the same application with the same data.
/// </summary>
/// <param name="Id">Issued identifier: 32 lowercase hexadecimal characters, usable as a DNS label.</param>
/// <param name="AdoptedAt">When the application was first adopted. A new revision does not change it.</param>
/// <param name="Source">Where the bytes of the current revision came from.</param>
/// <param name="Protection">
/// How the application's stored data is protected at rest. Only <c>"none"</c> exists today; the
/// field is present so that adding protection does not change the record's shape.
/// </param>
/// <param name="Revision">The revision in use: 1 for the adopted file, higher for each revision taken in since.</param>
/// <param name="RevisedAt">When the revision in use was taken in; <see langword="null"/> for the first.</param>
/// <param name="ArchivedAt">
/// When the person put the application away; <see langword="null"/> while it is in use. Archiving
/// changes nothing but this mark: the code, the data, the revisions and the usage record stay where
/// they are, so restoring it brings back exactly what was there.
/// </param>
/// <param name="Unsaved">
/// A result made for the person (a generated page) that they have not kept yet. It stays out of the
/// person's applications until kept; once left, it goes after <see cref="AdoptionCatalog.UnsavedRetention"/>.
/// An application the person adopted themselves is never unsaved.
/// </param>
/// <param name="LeftAt">
/// When the person last left an unsaved application (closed its tab); <see langword="null"/> while it
/// is open or for a saved one. Its retention counts from here.
/// </param>
public sealed record AdoptedApp(string Id, DateTimeOffset AdoptedAt, AdoptionSource Source, string Protection, int Revision = 1, DateTimeOffset? RevisedAt = null,
    DateTimeOffset? ArchivedAt = null, bool Unsaved = false, DateTimeOffset? LeftAt = null);

/// <summary>The file a revision of an application was taken in from.</summary>
/// <param name="Sha256">Lowercase hexadecimal SHA-256 of the adopted bytes.</param>
/// <param name="OriginalPath">
/// Path the file was adopted from, if known. Informational: it is used to tell the person that a
/// file at the same path was adopted before, never to decide that two applications are the same.
/// </param>
/// <param name="Size">Length of the adopted bytes.</param>
public sealed record AdoptionSource(string Sha256, string? OriginalPath, long Size);

/// <summary>How an earlier adoption matches a file about to be adopted.</summary>
public enum AdoptionMatchKind
{
    /// <summary>The same bytes were adopted before.</summary>
    SameBytes,

    /// <summary>Different bytes were adopted before from a file at the same original path.</summary>
    SameOriginalPath,
}

/// <summary>An earlier adoption reported by <see cref="AdoptionCatalog.FindEarlierAdoptionsAsync"/>.</summary>
public sealed record AdoptionMatch(AdoptedApp App, AdoptionMatchKind Kind);

/// <summary>What <see cref="AdoptionCatalog.ReadListingAsync"/> found under the data root.</summary>
/// <param name="Apps">The applications whose record was read, oldest first.</param>
/// <param name="Unreadable">Application folders whose record could not be read, left exactly as they are.</param>
public sealed record CatalogListing(IReadOnlyList<AdoptedApp> Apps, IReadOnlyList<UnreadableApp> Unreadable);

/// <summary>An application folder whose record could not be read. Nothing in it was changed.</summary>
/// <param name="Id">The folder's identifier.</param>
/// <param name="Kind"><see cref="CannotOpen"/> or <see cref="Damaged"/>.</param>
/// <param name="Detail">What the operating system or the reader said, for a person helping out — not for display as is.</param>
public sealed record UnreadableApp(string Id, string Kind, string Detail)
{
    /// <summary>
    /// The operating system could not open the record right now — for example a file kept only in
    /// the cloud while there is no connection, or one another program holds. It may open later.
    /// </summary>
    public const string CannotOpen = "cannotOpen";

    /// <summary>The record is missing or is not in a known format.</summary>
    public const string Damaged = "damaged";

    /// <summary>
    /// A removal for good stopped before the folder reached the recycle bin (the runtime ended in
    /// between). The folder, data included, is still under the data root; it is not in the list.
    /// </summary>
    public const string InterruptedRemoval = "interruptedRemoval";
}

/// <summary>An application removed for good, as far as it is remembered: when it came, how far it was revised, and when it was put away and removed.</summary>
public sealed record RemovedApp(string Id, DateTimeOffset AdoptedAt, int Revision, DateTimeOffset? ArchivedAt, DateTimeOffset RemovedAt);
