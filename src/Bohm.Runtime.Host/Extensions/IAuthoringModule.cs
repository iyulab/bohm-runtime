using Bohm.Runtime.Adoption;

namespace Bohm.Runtime.Host.Extensions;

/// <summary>
/// The authoring layer: writing a new application from a request, and changing one the person has — proposals the person
/// reviews, never silent replacements. The runtime serves, stores and runs applications without it; the desktop application
/// brings it. Each member answers one control route, which is why they take the request as it is.
/// </summary>
/// <remarks>
/// Internal and not a stable contract: it changes without notice while the runtime is 0.x.
/// A runtime with no module answers these routes <c>501 authoring-not-available</c>.
/// </remarks>
internal interface IAuthoringModule
{
    /// <summary><c>POST /__control/apps/proposals</c> — a new application proposed from what was asked, or from pages read.</summary>
    Task ProposeNewAsync(HttpContext context, CancellationToken cancel);

    /// <summary><c>POST /__control/apps/interview</c> — what to ask first, before a new application is proposed.</summary>
    Task InterviewAsync(HttpContext context, CancellationToken cancel);

    /// <summary><c>POST /__control/apps/promotions</c> — taking a proposed application in; the application, or <see langword="null"/> when the route already answered.</summary>
    Task<AdoptedApp?> PromoteAsync(HttpContext context, CancellationToken cancel);

    /// <summary><c>POST /__control/previews</c> — a proposed new application shown before it is taken in.</summary>
    Task PreviewNewAsync(HttpContext context, int port, CancellationToken cancel);

    /// <summary><c>POST /__control/apps/{id}/proposals</c> — a change to an application proposed.</summary>
    Task ProposeChangeAsync(HttpContext context, string appId, CancellationToken cancel);

    /// <summary><c>POST /__control/apps/{id}/proposals/from-html</c> — a change made elsewhere and pasted back, taken as a proposal.</summary>
    Task ProposeFromHtmlAsync(HttpContext context, string appId, CancellationToken cancel);

    /// <summary>Folders of skills the layer brings, listed as built-in beside the runtime's own.</summary>
    IReadOnlyList<string> BuiltinSkillRoots { get; }
}
