namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// A proposal — a change to an application or a new one — could not be made: the model proposed none
/// the runtime could keep, or none at all.
/// </summary>
/// <param name="stopped"><c>output-limit</c> when the model's answer reached its length limit first, otherwise <see langword="null"/>.</param>
internal sealed class ProposalFailedException(string message, string? stopped = null) : Exception(message)
{
    /// <summary>The answer's length limit stopped the model: <see cref="OutputLimit"/>.</summary>
    public const string OutputLimit = "output-limit";

    /// <summary><see cref="OutputLimit"/> when the model's answer reached its length limit first, otherwise <see langword="null"/>.</summary>
    public string? Stopped { get; } = stopped;
}
