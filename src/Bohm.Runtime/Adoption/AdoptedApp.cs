namespace Bohm.Runtime.Adoption;

/// <summary>
/// An HTML application taken in as-is. Its identity is issued at adoption and never derived from
/// the file: editing the file or moving it does not turn it into a different application.
/// </summary>
/// <param name="Id">Issued identifier: 32 lowercase hexadecimal characters, usable as a DNS label.</param>
/// <param name="AdoptedAt">When the application was adopted.</param>
/// <param name="Source">Where the adopted bytes came from.</param>
/// <param name="Protection">
/// How the application's stored data is protected at rest. Only <c>"none"</c> exists today; the
/// field is present so that adding protection does not change the record's shape.
/// </param>
public sealed record AdoptedApp(string Id, DateTimeOffset AdoptedAt, AdoptionSource Source, string Protection);

/// <summary>The original file an application was adopted from.</summary>
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
