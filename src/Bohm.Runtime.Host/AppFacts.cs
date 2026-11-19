namespace Bohm.Runtime.Host;

/// <summary>
/// What is true of every application the runtime serves, given to a model that writes one or changes
/// one, so its work stays inside it. Facts about the runtime rather than advice about style: they hold
/// whichever model reads them, and change only when the runtime does. Two kinds: what holds here (the
/// lines a proposal must keep — the runtime refuses one that breaks those it can check), and what is here
/// to use (what this place offers or does differently, which a model cannot know otherwise). How the
/// application should look and work beyond that is the model's own judgment — a built-in skill offers one
/// way when it wants one.
/// </summary>
internal static class AppFacts
{
    /// <summary>How an application runs — the lines a model's work must keep true, then what it can use.</summary>
    internal const string HowItRuns = """
        What holds here (a proposal that breaks one the runtime can check is refused, with the reason):
        - It is one HTML file, served from its own origin with a strict content security policy.
        - Its inline scripts must parse as JavaScript.
        - Its data is kept only in localStorage; sessionStorage, IndexedDB and cookies are not kept.
        - There is no server behind it: do not add calls to /api or any other server, and do not add
          sign-in screens or passwords, which protect nothing here.
        - Other hosts cannot be reached for data. Scripts, styles and fonts it loads from a CDN are
          kept from when it was added, but new ones may not load; prefer code written in the file.
        - For AI, it calls the provider's API as written (OpenAI, Anthropic or Gemini) and never holds
          a real key: the runtime supplies it. Never put a key in the source.
        - Every label, message and button is in the person's language.

        What is here to use, and what works differently here:
        - AI calls can be answered when this computer has no internet connection — the AI may run on this
          computer or on the local network. Do not hold them back on navigator.onLine; handle a failed
          call instead.
        - Speech recognition in the page (SpeechRecognition, webkitSpeechRecognition) does not work here: it
          fails with a network error. To take speech, record it with getUserMedia and MediaRecorder; the
          person is asked before the microphone opens.
        - An application does not ask the person to send it the web page they are reading, and declares no
          share target. What it needs from a web page reaches it as a source the browser reads: a table it
          keeps reading, or a page whose rows an AI reads out — read again when the person presses the
          browser's own «read again» button beside it, signed in as the person. An application that already
          received pages can still read them with fetch('/__bohm/pages'); none arrive any more.
        - Its errors are collected and shown to the person, who can ask for them to be fixed: an uncaught
          error, a rejected promise, and anything passed to console.error. Report a failure with
          console.error instead of swallowing it silently or keeping a log of its own.
        - Elements written in the markup can be pointed at and changed later; prefer them to elements
          built by script.
        - localStorage holds a few megabytes for the application: pictures and recordings fill it fast.
        - An element with the hidden attribute must stay hidden: a style rule that gives it a display
          (a dialog's display: flex) shows it anyway, and a full-window layer then takes every click.
          Keep [hidden] { display: none !important; } in the stylesheet.
        - Its address is an internal one (a long id and a port that changes). When it is printed, the browser
          writes that address, the date and the title in the page margins unless the stylesheet sets
          @page { margin: 0 }; anything meant to be printed should set it and give the printed content its
          own margins.
        """;
}
