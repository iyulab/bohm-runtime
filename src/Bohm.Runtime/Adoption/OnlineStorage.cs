using System.Text.RegularExpressions;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// Recognizes an application that keeps its data only in an online database, by reading its
/// document. Such an application loads and accepts input under the runtime, but its writes go to a
/// server the runtime does not reach, so whatever the person enters is lost without an error. A
/// document that also writes to the browser's own storage is not reported: those applications fall
/// back to it when the database is unavailable.
/// </summary>
public static partial class OnlineStorage
{
    /// <summary>
    /// The online database the document keeps its data in when it has no local storage of its own —
    /// <c>"firestore"</c> — or <c>null</c>.
    /// </summary>
    public static string? OnlyOnline(string html) =>
        Firestore().IsMatch(html) && !LocalStorage().IsMatch(html) ? "firestore" : null;

    // The Firestore module of the Firebase SDK (any version, any CDN path) or its entry point.
    [GeneratedRegex(@"firebase-firestore|\bgetFirestore\s*\(")]
    private static partial Regex Firestore();

    // A write to the browser's own storage, or Firestore's own cache kept in it.
    [GeneratedRegex(@"\blocalStorage\s*(?:\.\s*setItem\b|\[)|\bindexedDB\s*\.\s*open\b|\bpersistentLocalCache\b|\benable(?:MultiTab)?IndexedDbPersistence\b")]
    private static partial Regex LocalStorage();
}
