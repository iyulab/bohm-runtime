using Bohm.Runtime.Pages;
using Bohm.Runtime.Sources;
using LocalOrigin.Previews;

namespace Bohm.Runtime.Host.Adoption;

/// <summary>
/// Proposed documents held for a look before they are taken in. Each is served from its own
/// throwaway origin, <c>http://pv-&lt;token&gt;.localhost:&lt;port&gt;/</c> — never the application's own —
/// with the application's data to read and nowhere to write it, and collects what went wrong while
/// it loaded. A proposed new application has a preview too, with no application behind it: no data, and
/// the rows just read for its sources in place of theirs, and the pages the person sends it to try it with.
/// Held in memory only, while it is used and a short while after (<see cref="PreviewOrigins{T}"/>); a
/// preview's token is its scope name without <see cref="HostPrefix"/>.
/// </summary>
internal sealed class AppPreviews(TimeProvider time)
{
    public const string HostPrefix = "pv-";

    /// <summary>
    /// How long a preview stays servable once it is left alone — every request it serves, and every look at its
    /// report, renews it: a person may try a proposal for as long as they like.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly PreviewOrigins<Content> _origins = new(new PreviewOptions { NamePrefix = HostPrefix, Lifetime = Lifetime, MaxPreviews = 8, RenewOnUse = true }, time);

    /// <summary>What is kept with each preview.</summary>
    internal sealed class Content(string? appId, byte[] html, IReadOnlyDictionary<string, SourceReading>? readings, HeldPages? pages)
    {
        public string? AppId { get; } = appId;
        public byte[] Html { get; } = html;
        public IReadOnlyDictionary<string, SourceReading> Readings { get; } = readings ?? new Dictionary<string, SourceReading>();
        public HeldPages? Pages { get; } = pages;

        /// <summary>How many errors the report held when the first call to a model was declined; <see langword="null"/> before.</summary>
        public int? ErrorsBeforeModel;
    }

    public sealed class Preview(Preview<Content> held)
    {
        /// <summary>The application previewed, or <see langword="null"/> for a proposed new one.</summary>
        public string? AppId => held.Content.AppId;

        /// <summary>For a proposed new application, each source's rows as just read — what it shows until taken in.</summary>
        public IReadOnlyDictionary<string, SourceReading> Readings => held.Content.Readings;

        public byte[] Html => held.Content.Html;

        /// <summary>The preview's scope name — its origin's, and the scope of the session it is served with.</summary>
        public string Scope => held.Scope;

        /// <summary>
        /// For a proposed new application, the pages the person sent it to try it with — held here, gone with the
        /// preview; <see langword="null"/> for a preview of an application, which reads the application's pages.
        /// </summary>
        public HeldPages? Pages => held.Content.Pages;

        /// <summary>What went wrong while the document loaded.</summary>
        public PreviewReport Report => held.Report;

        /// <summary>
        /// Errors thrown or reported while the document was served, with lines counted as in the document — those that
        /// arrived before its first call to a model was declined, which taking it in would show too.
        /// </summary>
        public IReadOnlyList<string> Errors => Split().Before;

        /// <summary>
        /// Errors that arrived after the document's first call to a model was declined: they may follow from that answer,
        /// which the application taken in would not get. Empty while no call was declined.
        /// </summary>
        public IReadOnlyList<string> AfterModel => Split().After;

        /// <summary>What the content security policy refused, as <c>category host</c>.</summary>
        public IReadOnlyList<string> Blocked =>
            [.. held.Report.Blocked.Select(b => $"{b.Category.ToString().ToLowerInvariant()} {b.Host}")];

        /// <summary>Whether the document called a model while it was served and was declined; see <see cref="PreviewServing"/>.</summary>
        public bool AskedModel
        {
            get { lock (held.Content) return held.Content.ErrorsBeforeModel is not null; }
        }

        /// <summary>
        /// Records that a call to a model was declined. The first one splits the errors: the report keeps them in the order
        /// they arrived, and the page reports each as it happens, so those already here came before the answer.
        /// </summary>
        public void MarkAskedModel()
        {
            lock (held.Content) held.Content.ErrorsBeforeModel ??= held.Report.Errors.Count;
        }

        private (IReadOnlyList<string> Before, IReadOnlyList<string> After) Split()
        {
            var errors = held.Report.Errors;
            int? before;
            lock (held.Content) before = held.Content.ErrorsBeforeModel;
            return before is not { } count || count >= errors.Count ? (errors, []) : ([.. errors.Take(count)], [.. errors.Skip(count)]);
        }
    }

    /// <summary>Holds <paramref name="html"/> as a preview of <paramref name="appId"/> and returns its token.</summary>
    public string Create(string appId, byte[] html) => Hold(new Content(appId, html, null, null));

    /// <summary>Holds <paramref name="html"/> as a preview of a proposed new application whose sources read <paramref name="readings"/>, and returns its token.</summary>
    public string CreateNew(byte[] html, IReadOnlyDictionary<string, SourceReading> readings) => Hold(new Content(null, html, readings, new HeldPages(time)));

    private string Hold(Content content) => _origins.Create(content).Scope[HostPrefix.Length..];

    public Preview? Find(string token) => _origins.Find(HostPrefix + token) is { } held ? new Preview(held) : null;

    /// <summary>The preview of <paramref name="appId"/> — or, for <see langword="null"/>, of a proposed new application — named by <paramref name="token"/>, if there is one.</summary>
    public Preview? Find(string? appId, string token) => Find(token) is { } preview && preview.AppId == appId ? preview : null;

    public bool Remove(string? appId, string token) => Find(appId, token) is not null && _origins.Remove(HostPrefix + token);

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
}
