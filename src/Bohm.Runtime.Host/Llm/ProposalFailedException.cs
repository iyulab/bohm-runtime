using IronHive.Agent.Loop;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// A proposal — a change to an application or a new one — could not be made: the model proposed none
/// the runtime could keep, or none at all.
/// </summary>
/// <param name="stopped">What stopped the model first — <c>output-limit</c> or <c>step-limit</c> — otherwise <see langword="null"/>.</param>
/// <param name="rounds">Where the model's rounds went, when it stopped at a limit.</param>
internal sealed class ProposalFailedException(string message, string? stopped = null, ProposalRounds? rounds = null) : Exception(message)
{
    /// <summary>Where the model's rounds went, when it stopped at a limit.</summary>
    public ProposalRounds? Rounds { get; } = rounds;

    /// <summary>The answer's length limit stopped the model: <see cref="OutputLimit"/>.</summary>
    public const string OutputLimit = "output-limit";

    /// <summary>The model used every round of tool calls the task allows: <see cref="StepLimit"/>.</summary>
    public const string StepLimit = "step-limit";

    /// <summary><see cref="OutputLimit"/> or <see cref="StepLimit"/> when that stopped the model first, otherwise <see langword="null"/>.</summary>
    public string? Stopped { get; } = stopped;

    /// <summary>What a turn's stop reason says to the person, when it is a limit: <see cref="OutputLimit"/>, <see cref="StepLimit"/>, otherwise <see langword="null"/>.</summary>
    public static string? StoppedBy(TurnStopReason reason) => reason switch
    {
        TurnStopReason.OutputLimit => OutputLimit,
        TurnStopReason.StepLimit => StepLimit,
        _ => null,
    };
}
