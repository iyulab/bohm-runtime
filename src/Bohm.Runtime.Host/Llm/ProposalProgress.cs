using System.Text.Json.Serialization;
using Bohm.Runtime.Host.Extensions;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Something a proposal's model did while it worked, told as it happens: a piece of what it thinks before it
/// writes (a model that thinks), a piece of the text a tool call is writing — <see cref="Start"/> on the first
/// piece of a call — a proposal the runtime refused, and why (the model is told and writes again) — or a question for the
/// person: may the AI use an MCP server's tool (<see cref="Approval"/> — answered through <c>POST /__control/approvals/{id}</c>) — or a
/// tool of a server the person allowed for this request, used without asking (<see cref="Used"/>) — or a skill the model read
/// (<see cref="Skill"/>, its name).
/// </summary>
/// <remarks>Sent as one line of an answer made as it goes, so what a line does not carry is left out of it.</remarks>
internal sealed record ProposalProgress(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Thinking = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Writing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Start = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Refused = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApprovalAsk? Approval = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] McpUsed? Used = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Skill = null);
