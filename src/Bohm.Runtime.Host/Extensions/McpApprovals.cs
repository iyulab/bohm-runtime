using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using IronHive.Agent.Mode;
using IronHive.Agent.Permissions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Host.Extensions;

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

/// <summary>The person's answer to a question: no, yes for this call, or yes for every call to that server in this request.</summary>
internal enum ApprovalAnswer
{
    No,
    Yes,
    YesForServer,
}

/// <summary>
/// The questions waiting for the person: each one asked on a proposal's lines (<see cref="ProposalProgress.Approval"/>) and
/// answered through <c>POST /__control/approvals/{id}</c> with <c>true</c> (this call), <c>false</c>, or <c>"server"</c> (every call
/// to that server for the rest of the request). An answer nobody gives within <see cref="Wait"/> is a no.
/// </summary>
internal sealed class ApprovalBook
{
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalAnswer>> _waiting = new(StringComparer.Ordinal);

    /// <summary>Gives the answer to question <paramref name="id"/>; false when no question of that id is waiting.</summary>
    public bool Answer(string id, ApprovalAnswer answer) => _waiting.TryRemove(id, out var waiting) && waiting.TrySetResult(answer);

    /// <summary>Gives a yes or a no to question <paramref name="id"/>.</summary>
    public bool Answer(string id, bool approved) => Answer(id, approved ? ApprovalAnswer.Yes : ApprovalAnswer.No);

    /// <summary>Asks the person: writes the question with <paramref name="ask"/>, then waits for the answer.</summary>
    public async Task<ApprovalAnswer> AskAsync(Func<string, CancellationToken, Task> ask, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var waiting = new TaskCompletionSource<ApprovalAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            return ApprovalAnswer.No;   // nobody answered in time
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// Asks the person on a proposal's lines whether the AI may call an MCP tool: the server, the tool, what the server says the tool
/// does, the arguments as the model wrote them, and whether the server is at an address (its answer then goes to the AI from
/// outside this computer). One approver serves one request: a server the person allowed for this request is not asked about
/// again in it — each such call is still told on the lines (<see cref="ProposalProgress.Used"/>), and the next request asks again.
/// </summary>
internal sealed class ProgressApprover(ApprovalBook book, IReadOnlyList<McpTool> tools, Func<ProposalProgress, CancellationToken, Task> onProgress)
    : IHumanApprovalService
{
    private readonly HashSet<string> _allowedServers = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>The most characters of a tool's own description a question carries.</summary>
    internal const int DescriptionLength = 240;

    public async Task<ApprovalResult> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var tool = tools.FirstOrDefault(t => t.Function.Name == request.ToolName);
        if (tool is null) return ApprovalResult.Reject("Only an MCP server's tool is asked about.");
        // As the model wrote them: M.E.AI's own options read whatever the arguments hold (JSON elements, text, numbers).
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>(request.Arguments ?? new Dictionary<string, object?>()), AIJsonUtilities.DefaultOptions);
        bool allowed;
        lock (_sync) allowed = _allowedServers.Contains(tool.Server);
        if (allowed)
        {
            await onProgress(new ProposalProgress(Used: new McpUsed(tool.Server, tool.Tool, arguments)), cancellationToken).ConfigureAwait(false);
            return ApprovalResult.Approve();
        }

        var answer = await book.AskAsync(
            (id, token) => onProgress(new ProposalProgress(Approval: new ApprovalAsk(id, tool.Server, tool.Tool, arguments, tool.Remote, DescriptionOf(tool))), token),
            cancellationToken).ConfigureAwait(false);
        if (answer == ApprovalAnswer.YesForServer)
        {
            lock (_sync) _allowedServers.Add(tool.Server);
        }

        return answer == ApprovalAnswer.No
            ? ApprovalResult.Reject("The person did not allow this. Do without it, or another way.")
            : ApprovalResult.Approve();
    }

    /// <summary>The first line of what the server says the tool does, cut to <see cref="DescriptionLength"/>; null when it says nothing.</summary>
    internal static string? DescriptionOf(McpTool tool)
    {
        var first = tool.Function.Description?.Split('\n', 2)[0].Trim();
        if (string.IsNullOrEmpty(first)) return null;
        return first.Length <= DescriptionLength ? first : first[..(DescriptionLength - 1)].TrimEnd() + "…";
    }
}

/// <summary>
/// A question for the person on a proposal's lines: may the AI use <paramref name="Tool"/> of MCP server <paramref name="Server"/>
/// with <paramref name="Arguments"/> (JSON)? <paramref name="Description"/> is what the server says the tool does, when it says.
/// </summary>
internal sealed record ApprovalAsk(string Id, string Server, string Tool, string Arguments, bool Remote,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null);

/// <summary>An MCP tool the AI used without asking, because the person allowed its server for this request.</summary>
internal sealed record McpUsed(string Server, string Tool, string Arguments);
