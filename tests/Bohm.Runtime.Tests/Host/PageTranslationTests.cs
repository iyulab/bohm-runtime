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
    public void An_answer_is_read_by_the_numbers_asked()
    {
        Assert.Equal([("1", "가"), ("2", "나")], PageTranslation.Read("""{"1":"가","2":"나"}""", ["1", "2"]));
        Assert.Equal([("2", "나")], PageTranslation.Read("""{"2":"나","9":"엉뚱","1":3}""", ["1", "2"]));
        Assert.Equal([("1", "가")], PageTranslation.Read("```json\n{\"1\":\"가\"}\n```", ["1", "2"]));
        Assert.Empty(PageTranslation.Read("""["가","나"]""", ["1", "2"]));
        Assert.Empty(PageTranslation.Read("가, 나", ["1", "2"]));
        Assert.Empty(PageTranslation.Read(null, ["1"]));
        // Cut short at the model's length bound: the pairs that arrived whole still count.
        Assert.Equal([("1", "가 \"인용\""), ("2", "나")], PageTranslation.Read("{\n\"1\": \"가 \\\"인용\\\"\",\n\"2\": \"나\",\n\"3\": \"다는 끝나지", ["1", "2", "3"]));
    }

    [Fact]
    public void The_model_is_told_the_language_by_name() => Assert.Contains("into Korean", PageTranslation.Instruction(PageTranslation.LanguageName("ko")));

    [Fact]
    public async Task The_pieces_come_back_translated_in_order_from_the_chosen_model()
    {
        await using var server = await FakeProvider.StartAsync();
        server.TextsWhen[Instruction] = """{"1":"안녕하세요","2":"세계"}""";
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
    public async Task A_piece_the_model_left_out_is_asked_alone_once_more_and_otherwise_stays_unanswered()
    {
        await using var server = await FakeProvider.StartAsync();
        server.TextsWhen[Instruction] = """{"1":"안녕하세요 세계"}""";
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = CompanyModelList.Of(new CompanyModelOptions(new Uri(server.Address, "v1/"), "org-model")) });

        using var response = await TranslateAsync(host, """{"texts":["Hello"," world"],"to":"ko"}""");

        HttpAssert.Status(HttpStatusCode.OK, response);
        var texts = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("texts");
        Assert.Equal("안녕하세요 세계", texts[0].GetString());
        Assert.Equal(JsonValueKind.Null, texts[1].ValueKind);
        var asked = server.Asked.Where(r => r.Body.Contains(Instruction, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, asked.Count);
        Assert.DoesNotContain("Hello", asked[1].Body, StringComparison.Ordinal);   // the second asks only for what was missing
    }

    [Fact]
    public async Task An_answer_with_no_piece_is_refused_after_one_more_try()
    {
        await using var server = await FakeProvider.StartAsync();
        server.TextsWhen[Instruction] = "안녕하세요, 세계";
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

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var texts = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("texts").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(4, texts.Count);
        Assert.All(texts.Take(3), text => Assert.Matches("[가-힣]", text ?? ""));
        Assert.Contains("21", texts[1] ?? "", StringComparison.Ordinal);

        // A long batch — the size a page sends: most pieces come back translated, each on its own number.
        var many = Enumerable.Range(1, 60).Select(i => $"Section {i}: Members may reserve a study room for up to three hours.").ToList();
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var batch = await TranslateAsync(host, JsonSerializer.Serialize(new { texts = many, to = "ko" }));
        Assert.True(batch.IsSuccessStatusCode, await batch.Content.ReadAsStringAsync());
        var back = JsonDocument.Parse(await batch.Content.ReadAsStringAsync()).RootElement.GetProperty("texts").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(60, back.Count);
        var korean = back.Count(text => text is not null && System.Text.RegularExpressions.Regex.IsMatch(text, "[가-힣]"));
        TestContext.Current.SendDiagnosticMessage($"60 pieces: {korean} translated in {started.Elapsed.TotalSeconds:0} s");
        Assert.True(korean >= 45, $"{korean} of 60 translated");
        // Each translation stays on its own piece: the section number it carries is its own.
        Assert.All(back.Select((text, i) => (text, i)).Where(p => p.text is not null), p => Assert.Contains((p.i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), p.text!, StringComparison.Ordinal));
    }

    private static Task<HttpResponseMessage> TranslateAsync(RunningHost host, string body) =>
        host.ControlClient().PostAsync("/__control/agent/translate", new StringContent(body, Encoding.UTF8, "application/json"));
}
