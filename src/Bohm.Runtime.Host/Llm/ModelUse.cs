using IronHive.Agent.Loop;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// The tokens a provider reported for the model calls behind one answer — a proposal, a change, a web question's turn —
/// so what an answer costs can be seen and measured. Absent where the provider reported nothing (a model on this
/// computer may not).
/// </summary>
/// <param name="Input">Tokens sent to the model, across every call the answer took.</param>
/// <param name="Output">Tokens the model wrote, thinking included where the provider counts it as output.</param>
/// <param name="CachedInput">How many of <paramref name="Input"/> the provider read from its prompt cache; 0 when it did not say.</param>
internal sealed record ModelUse(long Input, long Output, long CachedInput)
{
    /// <summary>
    /// A turn's reported usage, or <see langword="null"/> when there is none — a turn whose provider reported nothing
    /// comes with zero counts, and no call the model answered takes in nothing.
    /// </summary>
    public static ModelUse? Of(TurnRecord turn) =>
        turn.Usage is { } usage && (usage.InputTokens > 0 || usage.OutputTokens > 0)
            ? new(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens)
            : null;

    /// <summary>Two answers' usage together — an answer that took more than one turn (asked once more, a second pass).</summary>
    public static ModelUse? Add(ModelUse? first, ModelUse? second) =>
        first is null ? second
        : second is null ? first
        : new(first.Input + second.Input, first.Output + second.Output, first.CachedInput + second.CachedInput);
}
