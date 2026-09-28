using System.Security.Cryptography;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Proposed documents held for a look before they are taken in. Each is served from its own
/// throwaway origin, <c>http://pv-&lt;token&gt;.localhost:&lt;port&gt;/</c> — never the application's own —
/// with the application's data to read and nowhere to write it, and collects what went wrong while
/// it loaded. Held in memory only, for a short while.
/// </summary>
internal sealed class AppPreviews(TimeProvider time)
{
    public const string HostPrefix = "pv-";

    /// <summary>How long a preview stays servable after it was made.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private const int MaxPreviews = 8;
    private const int MaxReports = 20;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Preview> _previews = new(StringComparer.Ordinal);

    public sealed class Preview(string appId, byte[] html, DateTimeOffset created)
    {
        private readonly List<string> _errors = [];
        private readonly List<string> _blocked = [];

        public string AppId { get; } = appId;
        public byte[] Html { get; } = html;
        public DateTimeOffset Created { get; } = created;

        /// <summary>Errors thrown while the document loaded, with lines counted as in the document.</summary>
        public IReadOnlyList<string> Errors { get { lock (_errors) return [.. _errors]; } }

        /// <summary>What the content security policy refused, as <c>category host</c>.</summary>
        public IReadOnlyList<string> Blocked { get { lock (_errors) return [.. _blocked]; } }

        public void AddError(string message)
        {
            lock (_errors) if (_errors.Count + _blocked.Count < MaxReports && !_errors.Contains(message)) _errors.Add(message);
        }

        public void AddBlocked(string category, string host)
        {
            var entry = $"{category} {host}";
            lock (_errors) if (_errors.Count + _blocked.Count < MaxReports && !_blocked.Contains(entry)) _blocked.Add(entry);
        }
    }

    /// <summary>Holds <paramref name="html"/> as a preview of <paramref name="appId"/> and returns its token.</summary>
    public string Create(string appId, byte[] html)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            Sweep();
            // The oldest goes first; a caller that never removes its previews cannot grow this.
            while (_previews.Count >= MaxPreviews)
                _previews.Remove(_previews.MinBy(p => p.Value.Created).Key);
            _previews[token] = new Preview(appId, html, time.GetUtcNow());
        }
        return token;
    }

    public Preview? Find(string token)
    {
        lock (_lock)
        {
            Sweep();
            return _previews.GetValueOrDefault(token);
        }
    }

    /// <summary>The preview of <paramref name="appId"/> named by <paramref name="token"/>, if there is one.</summary>
    public Preview? Find(string appId, string token) => Find(token) is { } preview && preview.AppId == appId ? preview : null;

    public bool Remove(string appId, string token)
    {
        lock (_lock)
            return _previews.TryGetValue(token, out var preview) && preview.AppId == appId && _previews.Remove(token);
    }

    /// <summary>The preview a request is addressed to, from its <c>Host</c> header.</summary>
    public static string? TokenOf(HttpRequest request)
    {
        var host = request.Host.Host;
        const string suffix = ".localhost";
        if (!host.StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase) || !host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = host[HostPrefix.Length..^suffix.Length].ToLowerInvariant();
        return token.Length == 32 && token.All(char.IsAsciiHexDigitLower) ? token : null;
    }

    public static Uri Origin(string token, int port) => new($"http://{HostPrefix}{token}.localhost:{port}/");

    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var expired in _previews.Where(p => now - p.Value.Created > Lifetime).Select(p => p.Key).ToList())
            _previews.Remove(expired);
    }
}
