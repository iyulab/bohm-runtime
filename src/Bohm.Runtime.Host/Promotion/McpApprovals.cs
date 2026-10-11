using System.Collections.Concurrent;
using System.Text.Json;
using Bohm.Runtime.Host.Edit;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Bohm.Runtime.Host.Promotion;

/// <summary>
/// The tools of the MCP servers the person connected, for one request of the AI that makes or changes an application, with
/// whom to ask before each call: there are no MCP tools without someone to ask.
/// </summary>
internal sealed record McpUse(IReadOnlyList<McpTool> Tools, IHumanApprovalService Approver)
{
    public IEnumerable<AITool> Functions => Tools.Select(t => (AITool)t.Function);

    /// <summary>The policy calls are judged by: the MCP tools asked, the rest as <paramref name="inner"/> says.</summary>
    public static IToolCallPolicy Policy(IToolCallPolicy inner) => new McpAsks(inner);

    /// <summary>
    /// The MCP tools for one request that answers in lines (<paramref name="onProgress"/>) — the questions go on those lines; a plain
    /// answer has nobody to ask, so it gets none. None either when no server is connected.
    /// </summary>
    public static async Task<McpUse?> ForAsync(IServiceProvider services, Func<ProposalProgress, CancellationToken, Task>? onProgress, CancellationToken cancellationToken)
    {
        if (onProgress is null) return null;
        var tools = await services.GetRequiredService<AppMcp>().ToolsAsync(cancellationToken).ConfigureAwait(false);
        return tools.Count == 0 ? null : new McpUse(tools, new ProgressApprover(services.GetRequiredService<ApprovalBook>(), tools, onProgress));
    }
}

/// <summary>One MCP tool as the model sees it: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, with where it comes from.</summary>
internal sealed record McpTool(string Server, string Tool, bool Remote, AIFunction Function)
{
    public const string Prefix = "mcp__";

    public static string NameOf(string server, string tool) => Prefix + server + "__" + tool;
}

/// <summary>
/// The rules the AI's tool calls are judged by while it makes or changes an application: an MCP server's tool is always asked
/// of the person (<see cref="PermissionAction.Ask"/>) — it reaches outside the runtime, to a program or an address the person
/// gave; everything else follows <paramref name="inner"/>.
/// </summary>
internal sealed class McpAsks(IToolCallPolicy inner) : IToolCallPolicy
{
    public RiskAssessment Evaluate(string toolName, IDictionary<string, object?>? arguments) =>
        toolName.StartsWith(McpTool.Prefix, StringComparison.Ordinal)
            ? RiskAssessment.Risky(RiskLevel.Medium, "An MCP server's tool reaches outside the runtime.", "Use " + toolName + "?", PermissionAction.Ask)
            : inner.Evaluate(toolName, arguments);
}

/// <summary>
/// The questions waiting for the person: each one asked on a proposal's lines (<see cref="ProposalProgress.Approval"/>) and
/// answered through <c>POST /__control/approvals/{id}</c>. An answer nobody gives within <see cref="Wait"/> is a no.
/// </summary>
internal sealed class ApprovalBook
{
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiting = new(StringComparer.Ordinal);

    /// <summary>Gives the answer to question <paramref name="id"/>; false when no question of that id is waiting.</summary>
    public bool Answer(string id, bool approved) => _waiting.TryRemove(id, out var waiting) && waiting.TrySetResult(approved);

    /// <summary>Asks the person: writes the question with <paramref name="ask"/>, then waits for the answer.</summary>
    public async Task<bool> AskAsync(Func<string, CancellationToken, Task> ask, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = waiting;
        try
        {
            await ask(id, cancellationToken).ConfigureAwait(false);
            using var within = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            within.CancelAfter(Wait);
            return await waiting.Task.WaitAsync(within.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;   // nobody answered in time
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// Asks the person on a proposal's lines whether the AI may call an MCP tool: the server, the tool and the arguments as the model
/// wrote them, and whether the server is at an address (its answer then goes to the AI from outside this computer).
/// </summary>
internal sealed class ProgressApprover(ApprovalBook book, IReadOnlyList<McpTool> tools, Func<ProposalProgress, CancellationToken, Task> onProgress)
    : IHumanApprovalService
{
    public async Task<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var tool = tools.FirstOrDefault(t => t.Function.Name == request.ToolName);
        if (tool is null) return ApprovalResult.Reject("Only an MCP server's tool is asked about.");
        // As the model wrote them: M.E.AI's own options read whatever the arguments hold (JSON elements, text, numbers).
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>(request.Arguments ?? new Dictionary<string, object?>()), AIJsonUtilities.DefaultOptions);
        var approved = await book.AskAsync(
            (id, token) => onProgress(new ProposalProgress(Approval: new ApprovalAsk(id, tool.Server, tool.Tool, arguments, tool.Remote)), token),
            cancellationToken).ConfigureAwait(false);
        return approved
            ? ApprovalResult.Approve()
            : ApprovalResult.Reject("The person did not allow this. Do without it, or another way.");
    }
}

/// <summary>A question for the person on a proposal's lines: may the AI use <paramref name="Tool"/> of MCP server <paramref name="Server"/> with <paramref name="Arguments"/> (JSON)?</summary>
internal sealed record ApprovalAsk(string Id, string Server, string Tool, string Arguments, bool Remote);
