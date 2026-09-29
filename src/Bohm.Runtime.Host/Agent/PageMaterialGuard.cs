using IronHive.Agent.Mode;

namespace Bohm.Runtime.Host.Agent;

/// <summary>
/// Marks what a page tool returned as material, not instructions, before the model reads it. The text
/// goes inside one <c>&lt;tab-material&gt;</c> block with <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c>
/// escaped, so nothing a page says can close the block and speak outside it.
/// </summary>
internal sealed class PageMaterialGuard : IToolResultGuard
{
    public static readonly PageMaterialGuard Instance = new();

    public ValueTask<ToolResultVerdict> InspectAsync(ToolResultInspection inspection, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ToolResultVerdict.Replace(Wrap(inspection.Result)));

    /// <summary>The block the model reads for <paramref name="text"/>.</summary>
    public static string Wrap(string text) => $"<tab-material>\n{Escape(text)}\n</tab-material>";

    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
