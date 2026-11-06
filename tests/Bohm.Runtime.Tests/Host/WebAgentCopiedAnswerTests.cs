using System.Text.Json;
using Bohm.Runtime.Host.Agent;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An answer that writes back out the page it read is asked for once more: the text shown so far is taken back, the model
/// is told what it did, and its second answer stands. A summary that quotes a line, and an answer that read nothing, are not.
/// </summary>
public sealed class WebAgentCopiedAnswerTests : IDisposable
{
    private readonly FakeChatModel _model = new();

    public void Dispose() => _model.Dispose();

    private static readonly string[] Paragraphs =
    [
        "The city council voted on Tuesday to extend the riverside walking path by another two kilometres.",
        "Work is expected to begin in the spring and to take around eighteen months, the council said in a statement.",
        "Residents who spoke at the meeting mostly welcomed the plan, though some raised concerns about parking near the park.",
        "The project will be paid for from the regional transport fund, with a smaller share coming from the city budget.",
        "A second public meeting on the design of the new bridge over the canal is planned for next month.",
    ];

    private static readonly string Page = "Title: Path extended\nAddress: https://news.example/path\nText:\n" + string.Join("\n\n", Paragraphs);

    private static string Conversation(string question) => JsonSerializer.Serialize(new
    {
        messages = new object[]
        {
            new { role = "user", text = question },
            new { role = "assistant", toolCalls = new[] { new { id = "r1", name = "read_page", arguments = new { tab = "web-1" } } } },
            new { role = "tool", toolCallId = "r1", text = Page },
        },
    });

    private static readonly string Summary = "시의회가 강변 산책로를 2 km 더 늘리기로 했습니다. 봄에 공사를 시작해 약 18 개월이 걸리고, 비용은 주로 지역 교통 기금에서 나옵니다.";

    [Fact]
    public void An_answer_made_of_the_pages_lines_is_a_copy_and_a_summary_that_quotes_one_is_not()
    {
        var conversation = WebAgent.ParseConversation(JsonDocument.Parse(Conversation("Summarize this page")).RootElement);

        Assert.True(CopiedAnswer.Copies("Path extended\n\n" + string.Join("\n\n", Paragraphs), conversation));
        var quoting = string.Join("\n\n", Enumerable.Repeat(Summary, 4)) + "\n\n> " + Paragraphs[2];
        Assert.True(quoting.Length >= CopiedAnswer.ShortestCopy);
        Assert.False(CopiedAnswer.Copies(quoting, conversation));
        // Nothing read, nothing to copy from.
        Assert.False(CopiedAnswer.Copies(string.Join("\n\n", Paragraphs), WebAgent.ParseConversation(JsonDocument.Parse("""{"messages":[{"role":"user","text":"q"}]}""").RootElement)));
    }

    [Fact]
    public async Task A_copied_answer_is_taken_back_and_asked_for_once_more()
    {
        _model.Script.Enqueue(new TextContent(string.Join("\n\n", Paragraphs)));
        _model.Script.Enqueue(new TextContent(Summary));
        var pieces = new List<string>();
        var restarts = 0;

        var turn = await WebAgent.RunTurnAsync(_model, "test", onThisComputer: false, ModelLimits.Unknown,
            WebAgent.ParseConversation(JsonDocument.Parse(Conversation("이 페이지 요약해 줘")).RootElement), DateTimeOffset.UnixEpoch,
            (text, _) => { pieces.Add(text); return Task.CompletedTask; }, TestContext.Current.CancellationToken,
            onRestart: _ => { restarts++; pieces.Clear(); return Task.CompletedTask; });

        Assert.Equal("done", turn.Status);
        Assert.Equal(Summary, turn.Text);
        Assert.Equal(1, restarts);
        Assert.Equal(Summary, string.Concat(pieces));
        // The second ask carries the copied answer and the note after it.
        Assert.Equal(2, _model.Calls.Count);
        var second = _model.Calls[1].Messages;
        Assert.Equal(ChatRole.User, second[^1].Role);
        Assert.Equal(CopiedAnswer.Note, second[^1].Text);
        Assert.Equal(ChatRole.Assistant, second[^2].Role);
    }

    [Fact]
    public async Task The_second_answer_stands_even_when_it_copies_again()
    {
        var copy = string.Join("\n\n", Paragraphs);
        _model.Script.Enqueue(new TextContent(copy));
        _model.Script.Enqueue(new TextContent(copy));

        var turn = await WebAgent.RunTurnAsync(_model, "test", onThisComputer: false, ModelLimits.Unknown,
            WebAgent.ParseConversation(JsonDocument.Parse(Conversation("Show me the article text")).RootElement), DateTimeOffset.UnixEpoch,
            null, TestContext.Current.CancellationToken);

        Assert.Equal(copy, turn.Text);
        Assert.Equal(2, _model.Calls.Count);
    }

    [Fact]
    public async Task An_answer_in_its_own_words_is_not_asked_again()
    {
        _model.Script.Enqueue(new TextContent(Summary));

        var turn = await WebAgent.RunTurnAsync(_model, "test", onThisComputer: false, ModelLimits.Unknown,
            WebAgent.ParseConversation(JsonDocument.Parse(Conversation("이 페이지 요약해 줘")).RootElement), DateTimeOffset.UnixEpoch,
            null, TestContext.Current.CancellationToken, onRestart: _ => throw new InvalidOperationException("not restarted"));

        Assert.Equal(Summary, turn.Text);
        Assert.Single(_model.Calls);
    }
}
