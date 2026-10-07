using System.Globalization;
using System.Text;

namespace Bohm.Runtime.Host.Agent;

/// <summary>
/// A site's robots.txt, read as RFC 9309 says: the groups that name the product token apply, or else
/// the <c>*</c> groups, or else nothing; within them the rule with the longest matching path wins, and
/// an <c>allow</c> wins a tie. <c>*</c> matches any run of characters and a final <c>$</c> ends the path.
/// </summary>
internal sealed class RobotsTxt
{
    /// <summary>The longest file read — RFC 9309 asks for at least 500 kibibytes; what follows is ignored.</summary>
    public const int MaxBytes = 500 * 1024;

    private readonly List<Group> _groups;

    private RobotsTxt(List<Group> groups) => _groups = groups;

    /// <summary>A file with no rules: every path is allowed.</summary>
    public static RobotsTxt Empty { get; } = new([]);

    public static RobotsTxt Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var groups = new List<Group>();
        Group? current = null;
        var lastWasAgent = false;
        foreach (var raw in text.TrimStart('﻿').Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#', StringComparison.Ordinal);
            if (hash >= 0) line = line[..hash];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "user-agent":
                    if (current is null || !lastWasAgent)
                    {
                        current = new Group();
                        groups.Add(current);
                    }
                    current.Agents.Add(value.ToLowerInvariant());
                    lastWasAgent = true;
                    break;
                case "allow" or "disallow":
                    lastWasAgent = false;
                    // An empty disallow allows everything: it is no rule at all. A rule before any user-agent line belongs to no group.
                    if (current is null || value.Length == 0) break;
                    current.Rules.Add(new Rule(key == "allow", Normalize(value)));
                    break;
                case "crawl-delay":
                    lastWasAgent = false;
                    if (current is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                        current.CrawlDelay = seconds;
                    break;
                default:
                    // Sitemap and other lines neither belong to a group nor end one's user-agent lines.
                    break;
            }
        }
        return new RobotsTxt(groups);
    }

    /// <summary>What the file says about <paramref name="pathAndQuery"/> (as it is sent: the path, then the query) for <paramref name="productToken"/>.</summary>
    public RobotsVerdict Check(string productToken, string pathAndQuery)
    {
        ArgumentException.ThrowIfNullOrEmpty(productToken);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        var path = Normalize(pathAndQuery.Length == 0 ? "/" : pathAndQuery);
        if (path == "/robots.txt") return new RobotsVerdict(true, null, null);

        var token = productToken.ToLowerInvariant();
        var groups = _groups.Where(g => g.Agents.Contains(token)).ToList();
        if (groups.Count == 0) groups = _groups.Where(g => g.Agents.Contains("*")).ToList();

        Rule? best = null;
        double? delay = null;
        foreach (var group in groups)
        {
            delay ??= group.CrawlDelay;
            foreach (var rule in group.Rules)
            {
                if (!Matches(rule.Pattern, path)) continue;
                if (best is null || rule.Pattern.Length > best.Pattern.Length || (rule.Pattern.Length == best.Pattern.Length && rule.Allow && !best.Allow))
                    best = rule;
            }
        }
        return new RobotsVerdict(best?.Allow ?? true, best is null ? null : $"{(best.Allow ? "Allow" : "Disallow")}: {best.Pattern}", delay);
    }

    /// <summary>Whether <paramref name="pattern"/> matches the start of <paramref name="path"/> — or all of it, when it ends in <c>$</c>.</summary>
    internal static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored) pattern = pattern[..^1];
        return Match(pattern, 0, path, 0, anchored);
    }

    private static bool Match(string pattern, int p, string path, int s, bool anchored)
    {
        while (p < pattern.Length)
        {
            if (pattern[p] == '*')
            {
                while (p < pattern.Length && pattern[p] == '*') p++;
                if (p == pattern.Length) return true;
                for (var i = s; i <= path.Length; i++)
                    if (Match(pattern, p, path, i, anchored)) return true;
                return false;
            }
            if (s >= path.Length || pattern[p] != path[s]) return false;
            p++;
            s++;
        }
        return !anchored || s == path.Length;
    }

    /// <summary>
    /// The form paths and patterns are compared in: characters outside ASCII percent-encoded as UTF-8, and
    /// percent-escapes in upper case — so <c>/caf%c3%a9</c>, <c>/caf%C3%A9</c> and <c>/café</c> are one path.
    /// </summary>
    internal static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                builder.Append('%').Append(char.ToUpperInvariant(value[i + 1])).Append(char.ToUpperInvariant(value[i + 2]));
                i += 2;
            }
            else if (c > 0x7F)
            {
                var length = char.IsHighSurrogate(c) && i + 1 < value.Length ? 2 : 1;
                foreach (var b in Encoding.UTF8.GetBytes(value.Substring(i, length))) builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                i += length - 1;
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }

    private sealed class Group
    {
        public HashSet<string> Agents { get; } = new(StringComparer.Ordinal);
        public List<Rule> Rules { get; } = [];
        public double? CrawlDelay { get; set; }
    }

    private sealed record Rule(bool Allow, string Pattern);
}

/// <summary>What a robots.txt says about one path: allowed or not, the rule that decided it (null when none did) and the crawl delay asked for, if any.</summary>
internal sealed record RobotsVerdict(bool Allowed, string? Rule, double? CrawlDelay);
