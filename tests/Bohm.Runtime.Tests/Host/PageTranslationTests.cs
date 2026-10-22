using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Agent;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A web page translated where it stands: its text pieces go out in order and come back translated in the same order — or not at
/// all, so a piece never lands under another one's element.
/// </summary>
public sealed class PageTranslationTests
{
    private const string Instruction = "You translate the text of a web page";

    [Fact]
    public void An_answer_counts_only_with_one_string_per_piece()
    {
        Assert.Equal(["가", "나"], PageTranslation.Read("""{"texts":["가","나"]}""", 2));
        Assert.Equal(["가", "나"], PageTranslation.Read("""["가","나"]""", 2));
        Assert.Equal(["가", "나"], PageTranslation.Read("```json\n{\"texts\":[\"가\",\"나\"]}\n```", 2));
        Assert.Null(PageTranslation.Read("""{"texts":["가나"]}""", 2));
        Assert.Null(PageTranslation.Read("""{"texts":["가",2]}""", 2));
        Assert.Null(PageTranslation.Read("가, 나", 2));
        Assert.Null(PageTranslation.Read(null, 2));
    }

    [Fact]
    public void The_model_is_told_the_language_by_name() => Assert.Contains("into Korean", PageTranslation.Instruction(PageTranslation.LanguageName("ko")));

    [Fact]
    public async Task The_pieces_come_back_translated_in_order_from_the_chosen_model()
    {
        await using var server = await FakeProvider.StartAsync();
        server.TextsWhen[Instruction] = """{"texts":["안녕하세요","세계"]}""";
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = CompanyModelList.Of(new CompanyModelOptions(new Uri(server.Address, "v1/"), "org-model")) });

        using var response = await TranslateAsync(host, """{"texts":["Hello"," world"],"to":"ko"}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["안녕하세요", "세계"], answer.GetProperty("texts").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal("company/org-model", answer.GetProperty("model").GetString());
        var asked = Assert.Single(server.Asked, r => r.Body.Contains(Instruction, StringComparison.Ordinal));
        Assert.Contains("json_object", asked.Body, StringComparison.Ordinal);
        Assert.Contains("Hello", asked.Body, StringComparison.Ordinal);
        // Counted as sent, like a web question.
        var sent = Assert.Single(JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("sent").EnumerateArray());
        Assert.Equal(server.Address.Authority, sent.GetProperty("host").GetString());
    }

    [Fact]
    public async Task An_answer_that_merges_pieces_is_asked_once_more_then_refused()
    {
        await using var server = await FakeProvider.StartAsync();
        server.TextsWhen[Instruction] = """{"texts":["안녕하세요 세계"]}""";
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = CompanyModelList.Of(new CompanyModelOptions(new Uri(server.Address, "v1/"), "org-model")) });

        using var response = await TranslateAsync(host, """{"texts":["Hello"," world"],"to":"ko"}""");

        HttpAssert.Status(HttpStatusCode.ServiceUnavailable, response);
        Assert.Equal("translate-failed", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        Assert.Equal(2, server.Asked.Count(r => r.Body.Contains(Instruction, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_batch_past_the_limits_or_without_a_language_is_refused_before_any_model()
    {
        await using var host = await RunningHost.StartAsync();

        using (var big = await TranslateAsync(host, JsonSerializer.Serialize(new { texts = Enumerable.Repeat("x", PageTranslation.MaxPieces + 1), to = "ko" })))
            HttpAssert.Status(HttpStatusCode.RequestEntityTooLarge, big);
        using (var longText = await TranslateAsync(host, JsonSerializer.Serialize(new { texts = new[] { new string('x', PageTranslation.MaxCharacters + 1) }, to = "ko" })))
            HttpAssert.Status(HttpStatusCode.RequestEntityTooLarge, longText);
        using (var noLanguage = await TranslateAsync(host, """{"texts":["Hello"]}"""))
            HttpAssert.Status(HttpStatusCode.BadRequest, noLanguage);
        using (var empty = await TranslateAsync(host, """{"texts":[],"to":"ko"}"""))
            HttpAssert.Status(HttpStatusCode.BadRequest, empty);
        // Within the limits but no model: what is missing, as for a web question.
        using var missing = await TranslateAsync(host, """{"texts":["Hello"],"to":"ko"}""");
        HttpAssert.Status(HttpStatusCode.Conflict, missing);
    }

    [Fact]
    public async Task A_real_model_on_this_computer_translates_a_page_piece_by_piece()
    {
        var gguf = Environment.GetEnvironmentVariable("BOHM_TEST_GGUF");
        var server = Environment.GetEnvironmentVariable("BOHM_TEST_LLAMA_SERVER");
        Assert.SkipWhen(string.IsNullOrEmpty(gguf) || string.IsNullOrEmpty(server), "BOHM_TEST_GGUF and BOHM_TEST_LLAMA_SERVER are not set.");
        await using var host = await RunningHost.StartAsync(configure: o => o with { LocalModel = new LocalModelOptions { ModelPath = gguf!, ServerPath = server } });

        using var response = await TranslateAsync(host, """{"texts":["Weather forecast","Tomorrow will be sunny with a high of 21 degrees.","Contact us","Seoul"],"to":"ko"}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        var texts = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("texts").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(4, texts.Count);
        Assert.All(texts.Take(3), text => Assert.Matches("[가-힣]", text));
        Assert.Contains("21", texts[1], StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> TranslateAsync(RunningHost host, string body) =>
        host.ControlClient().PostAsync("/__control/agent/translate", new StringContent(body, Encoding.UTF8, "application/json"));
}
