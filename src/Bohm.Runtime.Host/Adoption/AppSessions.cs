using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Who may write to which application's storage. Loading an application's document issues a
/// session token (sent as an HTTP-only cookie scoped to that application's origin) and a tab
/// identifier (given to the injected script). A write must present both, and both must belong to
/// the application whose origin received the request.
/// </summary>
internal sealed class AppSessions
{
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Tab> _tabs = new(StringComparer.Ordinal);
    // Tabs ended by RevokeApp, kept so a write that arrives from one afterwards is recognised as
    // this application's own page (refused, and counted as a possible loss) rather than a stranger.
    private readonly ConcurrentDictionary<string, Tab> _retired = new(StringComparer.Ordinal);

    /// <summary>
    /// One loaded page. Tracks the highest operation sequence applied from it, and the highest
    /// sequence the page said it had issued — more than applied means a write has not arrived.
    /// </summary>
    public sealed class Tab(string appId)
    {
        public string AppId { get; } = appId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long LastSequence { get; set; }
        public long Issued { get; set; }
        /// <summary>Whether the page's report sent after it left has arrived — then <see cref="Issued"/> is final.</summary>
        public bool Left { get; set; }
    }

    public string IssueSession(string appId)
    {
        var token = NewToken();
        _sessions[token] = appId;
        return token;
    }

    public string IssueTab(string appId)
    {
        var id = NewToken();
        _tabs[id] = new Tab(appId);
        return id;
    }

    /// <summary>Whether <paramref name="sessionToken"/> is a session of <paramref name="appId"/>.</summary>
    public bool IsSession(string appId, string? sessionToken) =>
        sessionToken is not null && _sessions.TryGetValue(sessionToken, out var sessionApp) && sessionApp == appId;

    /// <summary>The tab, if both it and the session belong to <paramref name="appId"/>.</summary>
    public Tab? Authorize(string appId, string? sessionToken, string? tabId)
    {
        if (sessionToken is null || tabId is null) return null;
        if (!_sessions.TryGetValue(sessionToken, out var sessionApp) || sessionApp != appId) return null;
        return _tabs.TryGetValue(tabId, out var tab) && tab.AppId == appId ? tab : null;
    }

    /// <summary>The tab, if it exists and belongs to <paramref name="appId"/> — for the host, which holds no session.</summary>
    public Tab? Find(string appId, string tabId) =>
        _tabs.TryGetValue(tabId, out var tab) && tab.AppId == appId ? tab : null;

    /// <summary>A tab of <paramref name="appId"/> that <see cref="RevokeApp"/> ended, if <paramref name="tabId"/> names one.</summary>
    public Tab? Retired(string appId, string? tabId) =>
        tabId is not null && _retired.TryGetValue(tabId, out var tab) && tab.AppId == appId ? tab : null;

    /// <summary>
    /// Ends every session and tab of <paramref name="appId"/>. A page loaded before this can no longer
    /// write — used when the application's code is replaced, so a page still running the old code
    /// cannot write into data the new code now owns.
    /// </summary>
    public void RevokeApp(string appId)
    {
        foreach (var (token, app) in _sessions)
            if (app == appId) _sessions.TryRemove(token, out _);
        foreach (var (id, tab) in _tabs)
            if (tab.AppId == appId && _tabs.TryRemove(id, out _)) _retired[id] = tab;
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
