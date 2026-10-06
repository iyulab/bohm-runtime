using Bohm.Runtime.Host.Edit;
using Bohm.Runtime.Host.Llm;
using Bohm.Runtime.Host.Promotion;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An application made here is told which AI answers its calls here — the order the relay answers in — so it
/// does not call a provider that has no key and is refused.
/// </summary>
public sealed class AppAiTests
{
    private static Func<LlmProvider, bool> Keys(params string[] ids) => p => ids.Contains(p.Id);

    [Fact]
    public void Without_an_OpenAI_key_a_model_that_answers_without_a_key_is_called_in_the_OpenAI_shape()
    {
        var line = AppAi.Line(Keys("anthropic"), keyless: true, new EditModelChoice("anthropic", "claude-x"));

        Assert.Contains("https://api.openai.com/v1/chat/completions", line, StringComparison.Ordinal);
        Assert.Contains("any model name", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chosen_provider_with_its_key_is_called_with_the_chosen_model()
    {
        var line = AppAi.Line(Keys("anthropic"), keyless: false, new EditModelChoice("anthropic", "claude-x"));

        Assert.Contains("https://api.anthropic.com/v1/messages", line, StringComparison.Ordinal);
        Assert.Contains("\"claude-x\"", line, StringComparison.Ordinal);
        Assert.Contains("Anthropic is the AI connected", line, StringComparison.Ordinal);
    }

    [Fact]
    public void With_an_OpenAI_key_a_keyless_model_is_not_promised_for_OpenAI_calls()
    {
        var line = AppAi.Line(Keys("openai"), keyless: true, new EditModelChoice("openai", "gpt-x"));

        Assert.Contains("\"gpt-x\"", line, StringComparison.Ordinal);   // an OpenAI call goes to OpenAI, so it needs a real model
        Assert.DoesNotContain("any model name", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Speech_to_text_is_offered_where_it_answers_and_said_to_be_missing_where_it_does_not()
    {
        var bridged = AppAi.Line(Keys(), keyless: true, chosen: null, speechWithoutKey: true);
        Assert.Contains("https://api.openai.com/v1/audio/transcriptions with any model name", bridged, StringComparison.Ordinal);

        var openai = AppAi.Line(Keys("openai"), keyless: false, new EditModelChoice("openai", "gpt-x"));
        Assert.Contains("audio/transcriptions with the model \"whisper-1\"", openai, StringComparison.Ordinal);

        foreach (var none in new[] { AppAi.Line(Keys(), keyless: true, chosen: null), AppAi.Line(Keys("anthropic"), keyless: false, chosen: null) })
        {
            Assert.Contains("Nothing here turns speech into text", none, StringComparison.Ordinal);
            Assert.DoesNotContain("audio/transcriptions", none, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_Gemini_model_is_named_in_the_address()
    {
        var line = AppAi.Line(Keys("google"), keyless: false, new EditModelChoice("google", "gemini-x"));

        Assert.Contains("v1beta/models/gemini-x:generateContent", line, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_chosen_provider_the_first_with_a_key_is_named_and_with_none_nothing_is_said()
    {
        Assert.Contains("api.groq.com/openai/v1/chat/completions", AppAi.Line(Keys("groq"), keyless: false, chosen: null), StringComparison.Ordinal);
        Assert.Null(AppAi.Line(Keys(), keyless: false, chosen: null));
    }

    [Fact]
    public async Task The_line_reaches_the_model_writing_an_application_from_an_instruction_only()
    {
        var made = new FakeChatModel();
        made.Script.Enqueue(new FunctionCallContent("c1", "propose_app", new Dictionary<string, object?>
        {
            ["title"] = "Log", ["sources"] = System.Text.Json.JsonSerializer.SerializeToElement(Array.Empty<object>()), ["html"] = "<!doctype html><title>Log</title>",
        }));

        await AppProposals.ProposeAsync(made, ModelLimits.Unknown, new AppRequest("A log", null, "en", null), TestContext.Current.CancellationToken, "AI LINE");

        var system = string.Join('\n', made.Calls[0].Messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        Assert.Contains("- AI LINE", system, StringComparison.Ordinal);
    }
}
