namespace Bohm.Runtime.Host;

/// <summary>
/// What is true of every application the runtime serves, given to a model that writes one or changes
/// one, so its work stays inside it. Facts about the runtime rather than advice about style: they hold
/// whichever model reads them, and change only when the runtime does.
/// </summary>
internal static class AppFacts
{
    /// <summary>How an application runs — the lines a model's work must keep true.</summary>
    internal const string HowItRuns = """
        - It is one HTML file, served from its own origin with a strict content security policy.
        - Its data is kept only in localStorage; sessionStorage, IndexedDB and cookies are not kept.
        - There is no server behind it: do not add calls to /api or any other server, and do not add
          sign-in screens or passwords, which protect nothing here.
        - Other hosts cannot be reached for data. Scripts, styles and fonts it loads from a CDN are
          kept from when it was added, but new ones may not load; prefer code written in the file.
        - For AI, it calls the provider's API as written (OpenAI, Anthropic or Gemini) and never holds
          a real key: the runtime supplies it. Never put a key in the source.
        - AI calls can be answered when this computer has no internet connection — the AI may run on this
          computer or on the local network. Do not hold them back on navigator.onLine; handle a failed
          call instead.
        - Elements written in the markup can be pointed at and changed later; prefer them to elements
          built by script.
        - An element with the hidden attribute must stay hidden: a style rule that gives it a display
          (a dialog's display: flex) shows it anyway, and a full-window layer then takes every click.
          Keep [hidden] { display: none !important; } in the stylesheet.
        """;
}
