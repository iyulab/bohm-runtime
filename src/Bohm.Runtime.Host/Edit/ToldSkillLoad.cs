using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Edit;

/// <summary>
/// The tool that reads a skill's instructions, telling each read on a request's lines (<see cref="ProposalProgress.Skill"/>):
/// which skill shaped the work is the person's to see, not only the model's.
/// </summary>
internal sealed class ToldSkillLoad(AIFunction inner, Func<ProposalProgress, CancellationToken, Task> onProgress) : DelegatingAIFunction(inner)
{
    /// <summary>The loader's tool as it is when nobody is told; told on <paramref name="onProgress"/> otherwise.</summary>
    public static AIFunction Of(AIFunction load, Func<ProposalProgress, CancellationToken, Task>? onProgress) =>
        onProgress is null ? load : new ToldSkillLoad(load, onProgress);

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (NameIn(arguments) is { Length: > 0 } name)
            await onProgress(new ProposalProgress(Skill: name), cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>The skill's name as the model wrote it — text, or a JSON string.</summary>
    private static string? NameIn(AIFunctionArguments arguments) =>
        arguments.TryGetValue("name", out var value)
            ? value switch
            {
                string text => text.Trim(),
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim(),
                _ => null,
            }
            : null;
}
