using System.Text.Json.Serialization;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// A packaged application: one zip file holding <see cref="ManifestFile"/> and the application's
/// folder as an export makes it (see <see cref="AdoptionCatalog.PackAsync"/>). The manifest says
/// what is inside, what the application asks to do and where it came from. Bohm writes it when it
/// packs an application — the application's own files never carry one. A reader ignores members it
/// does not know.
/// </summary>
public static class AppPackage
{
    /// <summary>The file name extension of a package.</summary>
    public const string Extension = ".bohm";

    /// <summary>Format identifier of the package, written into its manifest.</summary>
    public const string Format = "bohm.package/0";

    /// <summary>Version of the manifest's own schema.</summary>
    public const string ManifestVersion = "0";

    /// <summary>Name of the manifest, the package's first entry.</summary>
    public const string ManifestFile = "manifest.json";

    /// <summary>The contract a plain HTML application runs under.</summary>
    public const int PlainContract = 1;

    internal static string DataName(PackageData data) => data switch
    {
        PackageData.None => "none",
        PackageData.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(data)),
    };
}

/// <summary>What of the application's data a package carries.</summary>
public enum PackageData
{
    /// <summary>Only what makes the application: its record, its code in every revision, the code it loads from other hosts and the rules of its sources. Whoever takes it in starts with no data.</summary>
    None,

    /// <summary>The whole folder: also its data, its usage record, the data kept with its revisions, what it read and the tables put into it — <see cref="PackageManifest.Includes"/> lists which of these are there.</summary>
    All,
}

/// <summary>
/// The manifest of a package.
/// </summary>
/// <param name="ManifestVersion">See <see cref="AppPackage.ManifestVersion"/>.</param>
/// <param name="Format">See <see cref="AppPackage.Format"/>.</param>
/// <param name="Id">The application's id. Not a web application manifest's <c>id</c>: no origin rules apply to it.</param>
/// <param name="Version">
/// The published version: counted by the packing computer, one more each time the current code differs
/// from the last package's. Revision numbers are not used — they are counted per computer, so the same
/// application changed on two computers has two different "second revisions". Two packages hold the
/// same code when <see cref="PackageProvenance.ContentSha256"/> is the same.
/// </param>
/// <param name="Name">The name a person sees.</param>
/// <param name="Contract">The contract the application runs under (<see cref="AppPackage.PlainContract"/>).</param>
/// <param name="Contents">Each part's format and the version of it the package holds — read from the parts present, so a part not packed is not listed.</param>
/// <param name="Data"><c>none</c> or <c>all</c> (see <see cref="PackageData"/>).</param>
/// <param name="Includes">What of the application's data is actually inside: <c>storage</c>, <c>usage</c>, <c>revision-data</c>, <c>read-rows</c>, <c>import-data</c>. Empty for <c>none</c>.</param>
/// <param name="Permissions">What the application asks to do.</param>
/// <param name="Provenance">Where it came from and how to check it arrived whole.</param>
public sealed record PackageManifest(string ManifestVersion, string Format, string Id, string Version, string Name, int Contract,
    IReadOnlyDictionary<string, int> Contents, string Data, IReadOnlyList<string> Includes, PackagePermissions Permissions, PackageProvenance Provenance);

/// <summary>
/// What a packaged application asks to do.
/// </summary>
/// <param name="Storage">It keeps data in its own folder.</param>
/// <param name="Ai">
/// The AI services its current page calls, by id — found by looking for each service's address in the
/// page, so a call built from pieces at run time is missed. Best effort, for a person to read.
/// </param>
/// <param name="Tabs">The sites its sources read pages from.</param>
/// <param name="Network">Hosts it may reach on its own. Always empty: an application's requests do not leave the computer.</param>
/// <param name="Shared">Collections it shares with others. Empty until shared data exists.</param>
public sealed record PackagePermissions(bool Storage, IReadOnlyList<string> Ai, IReadOnlyList<string> Tabs, IReadOnlyList<string> Network, IReadOnlyList<string> Shared);

/// <summary>
/// Where a package came from.
/// </summary>
/// <param name="ContentSha256">Lowercase hexadecimal SHA-256 of the page of the revision in use — the identity of the code.</param>
/// <param name="Files">
/// Every file in the package but the manifest, by its path with <c>/</c> separators, as <c>sha256:</c>
/// and the lowercase hexadecimal hash. Checked when the package is opened — integrity only: who made
/// the package is not vouched for by anything inside it.
/// </param>
public sealed record PackageProvenance(string ContentSha256, IReadOnlyDictionary<string, string> Files);

/// <summary>An application written into a package.</summary>
public sealed record PackedApp(AdoptedApp App, PackageManifest Manifest, string Path);

/// <summary>What a package holds, checked, and whether its application is already here.</summary>
/// <param name="Manifest">The package's manifest.</param>
/// <param name="AlreadyHere">The application is already here.</param>
/// <param name="SameCode">It is, and the package's code is one of its revisions.</param>
/// <param name="SameCodeInUse">It is, and the package's code is the revision in use.</param>
/// <param name="Compatibility">What in the page the package would run will not work as written — <see cref="PageCompatibility"/>'s findings.</param>
public sealed record PackageInspection(PackageManifest Manifest, bool AlreadyHere, bool SameCode, bool SameCodeInUse, IReadOnlyList<string> Compatibility);

/// <summary>Why a file could not be taken in as a package.</summary>
public enum PackageProblem
{
    /// <summary>Not a zip archive with a readable manifest — or no file at all.</summary>
    NotAPackage,

    /// <summary>A manifest of a format this runtime does not know.</summary>
    UnknownFormat,

    /// <summary>The package did not arrive whole: an entry unlisted, changed, unreadable, missing or outside the package, or the application it names absent.</summary>
    Damaged,

    /// <summary>The package unpacks to more than the drive has room for, or holds too many entries.</summary>
    TooLarge,
}

/// <summary>A file that cannot be taken in as a package. Nothing was taken in.</summary>
public sealed class InvalidPackageException(PackageProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Why.</summary>
    public PackageProblem Problem { get; } = problem;
}

/// <summary>
/// The application a package holds is already here. Nothing was replaced.
/// </summary>
/// <param name="id">The application's id.</param>
/// <param name="sameCode">The package's code is one of the revisions here.</param>
/// <param name="sameCodeInUse">The package's code is the revision in use here.</param>
public sealed class AppAlreadyHereException(string id, bool sameCode, bool sameCodeInUse) : InvalidOperationException("This application is already here.")
{
    /// <summary>The application's id.</summary>
    public string Id { get; } = id;

    /// <summary>The package's code is one of the revisions here.</summary>
    public bool SameCode { get; } = sameCode;

    /// <summary>The package's code is the revision in use here.</summary>
    public bool SameCodeInUse { get; } = sameCodeInUse;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(PackageManifest))]
internal sealed partial class PackageJson : JsonSerializerContext;
