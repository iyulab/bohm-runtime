using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Promotion;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Questions before an application is made from what was asked: the model that writes applications says what it would ask —
/// or nothing — and the host keeps only the shape it bounds (four questions, two to four answers each). An answer that is not
/// that list is no questions, so making goes on from the request as it stands.
/// </summary>
public sealed class AppInterviewTests : IAsyncLifetime
{
    /// <summary>Words of the interview's instructions — what the fake answers.</summary>
    private const string Asking = "Before it is written";

    private FakeProvider _server = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync();
        using var set = await _host.ControlClient().PutAsync("/__control/llm/company-model",
            new StringContent($$"""{"endpoint":"{{new Uri(_server.Address, "v1/")}}","model":"m"}""", Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.OK, set);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _server.DisposeAsync();
    }

    private async Task<JsonElement> InterviewAsync(string body)
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps/interview", new StringContent(body, Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.OK, response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task The_models_questions_come_back_with_their_answers_and_the_request_goes_to_it()
    {
        _server.TextsWhen[Asking] = """Sure. {"questions":[{"question":"무엇을 보여 줄까요?","options":["매출","방문자","둘 다"]}]}""";

        var answer = await InterviewAsync("""{"question":"대시보드 사이트 만들어줘","lang":"ko"}""");

        var question = Assert.Single(answer.GetProperty("questions").EnumerateArray());
        Assert.Equal("무엇을 보여 줄까요?", question.GetProperty("question").GetString());
        Assert.Equal(["매출", "방문자", "둘 다"], question.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        var asked = Assert.Single(_server.Asked);
        Assert.Contains("대시보드 사이트 만들어줘", asked.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_that_is_not_the_list_is_no_questions()
    {
        _server.TextsWhen[Asking] = "I would ask about the colors.";

        var answer = await InterviewAsync("""{"question":"A tracker for my runs"}""");

        Assert.Empty(answer.GetProperty("questions").EnumerateArray());
    }

    [Fact]
    public async Task A_refusing_provider_is_no_questions_not_a_failure()
    {
        _server.Refusal = (500, """{"error":{"message":"down"}}""");

        var answer = await InterviewAsync("""{"question":"A tracker for my runs"}""");

        Assert.Empty(answer.GetProperty("questions").EnumerateArray());
    }

    [Fact]
    public async Task No_question_is_a_bad_request()
    {
        using var response = await _host.ControlClient().PostAsync("/__control/apps/interview", new StringContent("""{"question":"  "}""", Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.BadRequest, response);
    }

    [Fact]
    public void Only_the_bounded_shape_is_kept()
    {
        var many = string.Join(",", Enumerable.Range(1, 6).Select(i => $$"""{"question":"Q{{i}}","options":["a","b","c","d","e"]}"""));
        var read = AppInterview.Read($$"""{"questions":[{"question":"one answer","options":["only"]},{"question":"","options":["a","b"]},{{many}}]}""");

        Assert.Equal(AppInterview.MaxQuestions, read.Count);
        Assert.Equal("Q1", read[0].Question);
        Assert.All(read, q => Assert.Equal(AppInterview.MaxOptions, q.Options.Count));
        Assert.Empty(AppInterview.Read("""{"questions":[]}"""));
        Assert.Empty(AppInterview.Read("{ not json"));
        Assert.Single(AppInterview.Read("""Sure. {"questions":[{"question":"무엇을 보여 줄까요?","options":["매출","방문자","둘 다"]}]}"""));
    }

    [Fact]
    public void An_answer_cut_off_at_its_length_limit_keeps_the_questions_written_before_the_cut()
    {
        const string two = """{"note":{"a":[1,2]},"questions":[{"question":"누가 쓰나요?","options":["나","팀"]},{"question":"어디서?","options":["사무실","현장"]},""";
        Assert.Equal(["누가 쓰나요?", "어디서?"], AppInterview.Read(two + """{"question":"얼마나 자주","opt""").Select(q => q.Question));
        Assert.Equal(2, AppInterview.Read(two).Count);                                           // cut right after a comma
        Assert.Single(AppInterview.Read("""{"questions":[{"question":"Q","options":["a","b"]}"""));   // the array never closed
        Assert.Empty(AppInterview.Read("""{"questions":[{"question":"Q","options":["a","""));      // the only question unfinished
        Assert.Empty(AppInterview.Read("""{"other":"x","quest"""));                              // cut before the list
    }
}
