using System.Text.RegularExpressions;

namespace Bohm.Runtime.Adoption;

/// <summary>
/// What in an application's page will not work as written under the runtime, read from the page
/// before it is taken in — so the person hears it before adding the application, not after their
/// entries are gone. Only <c>localStorage</c> stays (it is kept by the runtime); the browser's other
/// stores belong to an origin that changes between runs, and outside servers are not reached.
/// </summary>
public static partial class PageCompatibility
{
    /// <summary>Keeps its data in an online database and nowhere local (see <see cref="OnlineStorage"/>).</summary>
    public const string OnlineDatabase = "online-database";

    /// <summary>Keeps its data in IndexedDB and never writes <c>localStorage</c>.</summary>
    public const string IndexedDb = "indexeddb";

    /// <summary>Keeps its data in <c>sessionStorage</c> and never writes <c>localStorage</c>.</summary>
    public const string SessionStorage = "session-storage";

    /// <summary>Keeps its data in cookies and never writes <c>localStorage</c>.</summary>
    public const string Cookies = "cookies";

    /// <summary>Requests data from an outside server by its address — other than an AI service the runtime relays.</summary>
    public const string OutsideData = "outside-data";

    /// <summary>
    /// The findings for <paramref name="html"/>, in a fixed order; empty when nothing is found. A store
    /// that does not stay is reported only for a page that writes no <c>localStorage</c>: one that does
    /// keeps its data there, and uses the other store for something passing.
    /// </summary>
    /// <param name="relayedHosts">Hosts of the AI services an application may call: requests to them are relayed, not outside data.</param>
    public static IReadOnlyList<string> Read(string html, IEnumerable<string> relayedHosts)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(relayedHosts);
        var relayed = relayedHosts.ToList();
        var findings = new List<string>();
        if (OnlineStorage.OnlyOnline(html) is not null) findings.Add(OnlineDatabase);
        if (!LocalStorageWrite().IsMatch(html))
        {
            if (IndexedDbOpen().IsMatch(html)) findings.Add(IndexedDb);
            if (SessionStorageWrite().IsMatch(html)) findings.Add(SessionStorage);
            if (CookieWrite().IsMatch(html)) findings.Add(Cookies);
        }

        if (OutsideRequest().Matches(html).Any(m => !relayed.Any(r => IsHostOf(m.Groups["host"].Value, r)))) findings.Add(OutsideData);
        return findings;
    }

    private static bool IsHostOf(string host, string relayed) =>
        host.Equals(relayed, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + relayed, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\blocalStorage\s*(?:\.\s*setItem\b|\[)")]
    private static partial Regex LocalStorageWrite();

    [GeneratedRegex(@"\bindexedDB\s*\.\s*open\b")]
    private static partial Regex IndexedDbOpen();

    [GeneratedRegex(@"\bsessionStorage\s*(?:\.\s*setItem\b|\[)")]
    private static partial Regex SessionStorageWrite();

    // An assignment, not a comparison.
    [GeneratedRegex(@"\bdocument\s*\.\s*cookie\s*=(?!=)")]
    private static partial Regex CookieWrite();

    // fetch, XMLHttpRequest.open or a WebSocket aimed at an absolute address. Script and style tags
    // from a CDN are not requests for data — those are fetched once when the application is added.
    [GeneratedRegex(@"(?:\bfetch\s*\(\s*[""'`]https?|\.open\s*\(\s*[""'][A-Za-z]+[""']\s*,\s*[""'`]https?|\bnew\s+WebSocket\s*\(\s*[""'`]wss?)://(?<host>[A-Za-z0-9.-]+)")]
    private static partial Regex OutsideRequest();
}
