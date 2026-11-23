using System.Net;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A model of a built-in provider is not described by anyone the runtime trusts to name its limits — so they are asked of the
/// provider itself, once, where its API says them (Anthropic's model information), never read from the model's name; and its client
/// is fitted and learns as an added provider's does.
/// </summary>
public sealed class BuiltInModelInfoTests : IAsyncLifetime
{
    private const string Model = "claude-test-1";
    private const string InfoPath = "/v1/models/" + Model;

    private FakeProvider _provider = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync(configure: o => o with
        {
            LlmEndpoints = new Dictionary<string, Uri> { ["api.anthropic.com"] = _provider.Address },
        });
        using (var key = await _host.ControlClient().PutAsync("/__control/llm/anthropic/key", new StringContent("sk-ant-test-0123456789")))
            HttpAssert.Status(HttpStatusCode.OK, key);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private async Task<ChosenEditModel> ChooseAsync()
    {
        using var content = new StringContent($$"""{"provider":"anthropic","model":"{{Model}}"}""", System.Text.Encoding.UTF8, "application/json");
        using (var response = await _host.ControlClient().PutAsync("/__control/agent/model", content))
            HttpAssert.Status(HttpStatusCode.OK, response);
        return (await _host.Services.GetRequiredService<AgentModel>().GetAsync(CancellationToken.None))!;
    }

    private int InfoRequests => _provider.Received.Count(r => r.Method == "GET" && r.PathAndQuery.StartsWith(InfoPath, StringComparison.Ordinal));

    [Fact]
    public async Task A_built_in_providers_model_is_asked_of_the_provider_once_for_its_window_and_its_largest_answer()
    {
        _provider.Answers[InfoPath] = $$"""
            {"type":"model","id":"{{Model}}","display_name":"Test","created_at":"2026-09-21T00:00:00Z","max_input_tokens":200000,"max_tokens":128000}
            """;

        var chosen = await ChooseAsync();

        Assert.NotNull(chosen.Client.GetService<ModelFitChatClient>());
        var known = ModelLimits.Of(chosen.Client, chosen.Limits);
        Assert.Equal(200000, known.ContextWindow);
        Assert.Equal(128000, known.MaxOutputTokens);
        Assert.Equal(1, InfoRequests);
        await _host.Services.GetRequiredService<AgentModel>().GetAsync(CancellationToken.None);
        Assert.Equal(1, InfoRequests);   // the same client is not asked again
    }

    [Fact]
    public async Task A_model_the_provider_says_thinks_is_known_to_think_before_its_first_answer()
    {
        _provider.Answers[InfoPath] = """
            {"type":"model","id":"MODEL","display_name":"Test","created_at":"2026-09-21T00:00:00Z","max_input_tokens":200000,"max_tokens":128000,
             "capabilities":{"batch":{"supported":true},"citations":{"supported":true},"code_execution":{"supported":false},
               "context_management":{"supported":true,"clear_thinking_20251015":{"supported":true},"clear_tool_uses_20250919":{"supported":true},"compact_20260112":{"supported":true}},
               "effort":{"supported":true,"low":{"supported":true},"medium":{"supported":true},"high":{"supported":true},"max":{"supported":true},"xhigh":{"supported":false}},
               "image_input":{"supported":true},"pdf_input":{"supported":true},"structured_outputs":{"supported":true},
               "server_tools":{"supported":true,"code_execution":{"supported":false},"web_search":{"supported":true}},
               "thinking":{"supported":true,"types":{"adaptive":{"supported":true},"enabled":{"supported":true},"disabled":{"supported":true},"between_tools":{"supported":false}}}}}
            """.Replace("MODEL", Model, StringComparison.Ordinal);

        var chosen = await ChooseAsync();

        Assert.True(ModelLimits.Of(chosen.Client, chosen.Limits).Reasoning);
        Assert.Equal(1, InfoRequests);
    }

    [Fact]
    public async Task An_answer_asked_larger_than_the_provider_said_its_model_gives_is_lowered_to_it()
    {
        _provider.Answers[InfoPath] = $$"""{"type":"model","id":"{{Model}}","display_name":"Test","created_at":"2026-09-21T00:00:00Z","max_input_tokens":200000,"max_tokens":64000}""";
        var chosen = await ChooseAsync();

        await chosen.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions { MaxOutputTokens = 128000 });

        var body = Assert.Single(_provider.Asked, r => r.PathAndQuery.EndsWith("/v1/messages", StringComparison.Ordinal)).Body;
        Assert.Contains("\"max_tokens\":64000", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_does_not_say_leaves_the_limits_unknown_and_the_request_goes_on()
    {
        var chosen = await ChooseAsync();   // no model information: the fake answers 404

        var known = ModelLimits.Of(chosen.Client, chosen.Limits);
        Assert.Null(known.ContextWindow);
        Assert.Null(known.MaxOutputTokens);
        Assert.Equal(FakeProvider.Reply, (await chosen.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")])).Text);
    }

    [Fact]
    public async Task Limits_that_do_not_make_sense_together_are_not_taken()
    {
        _provider.Answers[InfoPath] = $$"""{"type":"model","id":"{{Model}}","display_name":"Test","created_at":"2026-09-21T00:00:00Z","max_input_tokens":1000,"max_tokens":128000}""";

        var chosen = await ChooseAsync();

        Assert.Null(ModelLimits.Of(chosen.Client, chosen.Limits).MaxOutputTokens);
    }
}
