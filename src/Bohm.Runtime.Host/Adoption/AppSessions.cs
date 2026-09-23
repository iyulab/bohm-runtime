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

    /// <summary>One loaded page. Tracks the highest operation sequence applied from it.</summary>
    public sealed class Tab(string appId)
    {
        public string AppId { get; } = appId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long LastSequence { get; set; }
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

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
