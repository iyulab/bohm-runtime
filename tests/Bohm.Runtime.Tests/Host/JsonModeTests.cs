using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An application that asks for a JSON answer gets JSON: a server that wraps the JSON it was asked for
/// in one Markdown code fence has the fence taken off, and nothing else about an answer is changed.
/// </summary>
public sealed class JsonModeTests
{
    private static readonly ChatMessage[] Question = [new(ChatRole.User, "Classify these shops.")];
    private static readonly ChatOptions AsksForJson = new() { ResponseFormat = ChatResponseFormat.Json };

    [Theory]
    [InlineData("```json\n{\"result\": {\"a\": \"b\"}}\n```", "{\"result\": {\"a\": \"b\"}}")]
    [InlineData("```\n[1, 2]\n```", "[1, 2]")]
    [InlineData("  ```JSON\r\n{\"a\": 1}\r\n```  \n", "{\"a\": 1}")]
    [InlineData("```json\n{\"a\": 1}```", "{\"a\": 1}")]
    public async Task Json_wrapped_in_one_fence_comes_back_without_it(string answer, string expected)
    {
        var json = new JsonModeChatClient(new FakeChatModel { Reply = answer });
        var response = await json.GetResponseAsync(Question, AsksForJson);
        Assert.Equal(expected, response.Text);
    }

    [Theory]
    [InlineData("{\"a\": 1}")] // already JSON
    [InlineData("Here it is:\n```json\n{\"a\": 1}\n```")] // words outside the fence — not the server's wrapping, the model's answer
    [InlineData("```json\n{\"a\": 1\n```")] // the fenced text is not JSON either
    [InlineData("```json\n{\"a\": 1}\n```\n```json\n{\"b\": 2}\n```")] // two fences
    public async Task Any_other_answer_is_left_as_it_came(string answer)
    {
        var json = new JsonModeChatClient(new FakeChatModel { Reply = answer });
        Assert.Equal(answer, (await json.GetResponseAsync(Question, AsksForJson)).Text);
    }

    [Fact]
    public async Task An_application_that_did_not_ask_for_JSON_gets_the_fence()
    {
        const string answer = "```json\n{\"a\": 1}\n```";
        var json = new JsonModeChatClient(new FakeChatModel { Reply = answer });
        Assert.Equal(answer, (await json.GetResponseAsync(Question, new ChatOptions())).Text);
        Assert.Equal(answer, (await json.GetResponseAsync(Question)).Text);
    }

    [Fact]
    public async Task A_schema_counts_as_asking_for_JSON()
    {
        var schema = System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement;
        var json = new JsonModeChatClient(new FakeChatModel { Reply = "```json\n{}\n```" });
        Assert.Equal("{}", (await json.GetResponseAsync(Question, new ChatOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema(schema) })).Text);
    }

    [Fact]
    public async Task Thinking_and_the_finish_are_kept()
    {
        var json = new JsonModeChatClient(new ThinksThenFences());
        var response = await json.GetResponseAsync(Question, AsksForJson);
        Assert.Equal("{\"a\": 1}", response.Text);
        Assert.Contains(response.Messages[^1].Contents, c => c is TextReasoningContent);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public async Task A_streamed_JSON_answer_arrives_without_the_fence()
    {
        var json = new JsonModeChatClient(new FakeChatModel { Chunks = ["```json\n{\"res", "ult\": ", "1}\n``", "`"] });
        var text = string.Concat((await ToListAsync(json.GetStreamingResponseAsync(Question, AsksForJson))).Select(u => u.Text));
        Assert.Equal("{\"result\": 1}", text);
    }

    [Fact]
    public async Task A_stream_that_did_not_ask_for_JSON_is_passed_through_piece_by_piece()
    {
        var json = new JsonModeChatClient(new FakeChatModel { Chunks = ["```json\n", "{}", "\n```"] });
        var updates = await ToListAsync(json.GetStreamingResponseAsync(Question, new ChatOptions()));
        Assert.Equal(["```json\n", "{}", "\n```"], updates.Select(u => u.Text).Where(t => t.Length > 0));
    }

    private static async Task<List<ChatResponseUpdate>> ToListAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var list = new List<ChatResponseUpdate>();
        await foreach (var update in updates) list.Add(update);
        return list;
    }

    /// <summary>A server that thinks, then wraps its JSON in a fence.</summary>
    private sealed class ThinksThenFences : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("They want JSON."), new TextContent("```json\n{\"a\": 1}\n```")]))
            {
                FinishReason = ChatFinishReason.Stop,
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
