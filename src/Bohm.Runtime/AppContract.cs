namespace Bohm.Runtime;

/// <summary>
/// What an application written for the runtime may rely on — one HTML file, data in localStorage, no
/// server, AI through the providers' own APIs, and (from edition 2) web pages the person sends it at
/// <c>/__bohm/pages</c>, and (from edition 3) AI calls that are answered without an internet connection — is published as a short text with an edition number.
/// This is that number, so the text and the runtime that serves the applications can be checked
/// against each other.
/// </summary>
/// <remarks>
/// Adding something applications may now rely on raises the edition. Taking something away is a
/// change to what running applications depend on, and is not made by raising a number.
/// </remarks>
public static class AppContract
{
    /// <summary>The edition of the application contract this runtime serves.</summary>
    public const int Edition = 3;
}
