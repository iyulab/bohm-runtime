using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Host.Llm;

/// <summary>
/// Tool schemas go out with no type list in them: a parameter that may be null, which a schema writes as a list of types
/// (<c>"type": ["string", "null"]</c> — what an optional parameter of a C# tool, or an MCP server's, is written as), goes out as its one
/// type with <c>"nullable": true</c>.
/// </summary>
/// <remarks>
/// Some OpenAI-compatible servers fail on the list form: a GPUStack server ends a streamed tool call with
/// <c>{"error": "unhashable type: 'list'"}</c> when the model fills such a parameter — and whether a server is one cannot be told from the
/// request side once a gateway or relay stands in front of it, so every tool goes out this way, whoever serves it. Nothing is lost: an
/// optional parameter stays out of <c>required</c>, and <c>nullable</c> still says it may be null. Tools without a type list go out as they came.
/// </remarks>
internal sealed class NoTypeListsChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private static readonly AIJsonSchemaTransformOptions Transform = new() { UseNullableKeyword = true };
    private static readonly ConditionalWeakTable<AITool, AITool> Rewritten = [];

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Rewrite(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Rewrite(options), cancellationToken);

    /// <summary>The options with each tool whose schema holds a type list replaced by a declaration without one — the same options when none does.</summary>
    internal static ChatOptions? Rewrite(ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 } tools) return options;
        var outgoing = tools.Select(Outgoing).ToList();
        if (outgoing.SequenceEqual(tools, ReferenceEqualityComparer.Instance)) return options;
        var copy = options.Clone();
        copy.Tools = outgoing;
        return copy;
    }

    private static AITool Outgoing(AITool tool) =>
        tool is AIFunctionDeclaration declaration
            ? Rewritten.GetValue(tool, _ => HasTypeList(declaration.JsonSchema)
                ? AIFunctionFactory.CreateDeclaration(declaration.Name, declaration.Description, AIJsonUtilities.TransformSchema(declaration.JsonSchema, Transform),
                    declaration.ReturnJsonSchema)
                : tool)
            : tool;

    private static bool HasTypeList(JsonElement schema) => schema.ValueKind switch
    {
        JsonValueKind.Object => schema.EnumerateObject().Any(p => p.NameEquals("type") && p.Value.ValueKind == JsonValueKind.Array || HasTypeList(p.Value)),
        JsonValueKind.Array => schema.EnumerateArray().Any(HasTypeList),
        _ => false,
    };
}
