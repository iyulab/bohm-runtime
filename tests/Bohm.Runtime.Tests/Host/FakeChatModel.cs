using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>A model that records what it was asked and answers as told.</summary>
internal sealed class FakeChatModel : IChatClient
{
    public List<(List<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public string Reply { get; set; } = "ok";

    public IReadOnlyList<string> Chunks { get; set; } = ["ok"];

    public FunctionCallContent? Call { get; set; }

    /// <summary>
    /// Answers to give in order, one per request, before falling back to <see cref="Call"/> or
    /// <see cref="Reply"/> — a tool call, then the text that follows it.
    /// </summary>
    public Queue<AIContent> Script { get; } = new();

    /// <summary>What the model throws instead of answering, or nothing.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Token counts to report, or none.</summary>
    public UsageDetails? Usage { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(([.. messages], options));
        if (Failure is not null) return Task.FromException<ChatResponse>(Failure);
        if (Script.TryDequeue(out var next))
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [next]))
            {
                ModelId = "local-test",
                FinishReason = next is FunctionCallContent ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            });
        }

        var message = Call is { } call ? new ChatMessage(ChatRole.Assistant, [call]) : new ChatMessage(ChatRole.Assistant, Reply);
        return Task.FromResult(new ChatResponse(message)
        {
            ModelId = "local-test",
            FinishReason = Call is null ? ChatFinishReason.Stop : ChatFinishReason.ToolCalls,
            Usage = Usage,
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add(([.. messages], options));
        foreach (var chunk in Chunks)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk) { ModelId = "local-test", ResponseId = "r-1" };
        }

        yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, ModelId = "local-test", ResponseId = "r-1", Contents = Usage is null ? [] : [new UsageContent(Usage)] };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
