using System.Text.Json;
using Bohm.Runtime.Host.Agent;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An answer in another script than the question is asked for once more, in the person's language: a Korean question
/// answered wholly in English, about a page in English. A Korean answer with English names in it, an English question, and a
/// short Latin answer are not.
/// </summary>
public sealed class WebAgentOtherLanguageAnswerTests : IDisposable
{
    private readonly FakeChatModel _model = new();

    public void Dispose() => _model.Dispose();

    private const string Page = "Title: Path extended\nAddress: https://news.example/path\nText:\nThe city council voted on Tuesday to extend the riverside walking path by another two kilometres.";

    private static List<ChatMessage> Conversation(string question) => WebAgent.ParseConversation(JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        messages = new object[]
        {
            new { role = "user", text = question },
            new { role = "assistant", toolCalls = new[] { new { id = "r1", name = "read_page", arguments = new { tab = "web-1" } } } },
            new { role = "tool", toolCallId = "r1", text = Page },
        },
    })).RootElement);

    private const string English = "The city council voted on Tuesday to extend the riverside walking path by another two kilometres, with work starting in spring.";
    private const string Korean = "시의회가 화요일에 강변 산책로를 2 km 더 늘리기로 했습니다(Riverside Path · City Council). 공사는 봄에 시작합니다.";

    [Fact]
    public void An_english_answer_to_a_korean_question_differs_and_a_korean_one_with_names_does_not()
    {
        Assert.True(OtherLanguageAnswer.Differs(English, Conversation("이 페이지 요약해 줘")));
        Assert.False(OtherLanguageAnswer.Differs(Korean, Conversation("이 페이지 요약해 줘")));
        Assert.False(OtherLanguageAnswer.Differs(English, Conversation("Summarize this page")));
        // A short answer (a name, a code) is not looked at.
        Assert.False(OtherLanguageAnswer.Differs("Eva Geller", Conversation("이름이 뭐야?")));
        Assert.True(OtherLanguageAnswer.Differs(English, Conversation("Кратко перескажи страницу")));
    }

    [Fact]
    public async Task An_answer_in_another_language_is_taken_back_and_asked_for_once_more()
    {
        _model.Script.Enqueue(new TextContent(English));
        _model.Script.Enqueue(new TextContent(Korean));
        var pieces = new List<string>();
        var restarts = 0;

        var turn = await WebAgent.RunTurnAsync(_model, "test", onThisComputer: false, ModelLimits.Unknown, Conversation("이 페이지 요약해 줘"), DateTimeOffset.UnixEpoch,
            (text, _) => { pieces.Add(text); return Task.CompletedTask; }, TestContext.Current.CancellationToken,
            onRestart: _ => { restarts++; pieces.Clear(); return Task.CompletedTask; });

        Assert.Equal(Korean, turn.Text);
        Assert.Equal(1, restarts);
        Assert.Equal(Korean, string.Concat(pieces));
        Assert.Equal(2, _model.Calls.Count);
        Assert.Equal(OtherLanguageAnswer.Note, _model.Calls[1].Messages[^1].Text);
    }

    [Fact]
    public async Task The_second_answer_stands_even_in_another_language()
    {
        _model.Script.Enqueue(new TextContent(English));
        _model.Script.Enqueue(new TextContent(English));

        var turn = await WebAgent.RunTurnAsync(_model, "test", onThisComputer: false, ModelLimits.Unknown, Conversation("이 페이지를 영어로 번역해 줘"), DateTimeOffset.UnixEpoch,
            null, TestContext.Current.CancellationToken);

        Assert.Equal(English, turn.Text);
        Assert.Equal(2, _model.Calls.Count);
    }
}
