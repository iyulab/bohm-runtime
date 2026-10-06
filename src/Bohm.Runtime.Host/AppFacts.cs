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
        - Speech recognition in the page (SpeechRecognition, webkitSpeechRecognition) does not work here: it
          fails with a network error. To take speech, record it with getUserMedia and MediaRecorder; the
          person is asked before the microphone opens.
        - The person can send the web page they are reading in the browser to an application that declares
          a share target, with the send button at the end of the address bar (there is no share menu). Whatever
          the person reads in the browser is a web page: an article, a mail open in their webmail, a post, a product
          or an order page. So one that works on such text should take it this way rather than ask them to copy and
          paste (pasting can stay as a second way). Declare it in
          the head: <link rel="manifest" href='data:application/manifest+json,{"name":"Name","share_target":{"action":"/","params":{"title":"title","url":"url"}}}'>.
          A sent page opens the application with bohm_page=<id> in its address: read it with
          fetch('/__bohm/pages/' + id), which answers { url, title, text, html, lang, byline } (html is the
          article body, already cleaned), and every page it has received with fetch('/__bohm/pages'). The
          pages are kept for it; keep in localStorage only what it adds, keyed by the page id.
        - Its errors are collected and shown to the person, who can ask for them to be fixed: an uncaught
          error, a rejected promise, and anything passed to console.error. Report a failure with
          console.error instead of swallowing it silently or keeping a log of its own.
        - Elements written in the markup can be pointed at and changed later; prefer them to elements
          built by script.
        - An element with the hidden attribute must stay hidden: a style rule that gives it a display
          (a dialog's display: flex) shows it anyway, and a full-window layer then takes every click.
          Keep [hidden] { display: none !important; } in the stylesheet.
        - Its address is an internal one (a long id and a port that changes). When it is printed, the browser
          writes that address, the date and the title in the page margins unless the stylesheet sets
          @page { margin: 0 }; anything meant to be printed should set it and give the printed content its
          own margins.
        """;
}
