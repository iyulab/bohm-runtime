using System.Runtime.CompilerServices;
using Bohm.Runtime.Host.Llm;
using IronHive.Abstractions.Exceptions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A model's limits, as whoever set it said: an answer bound above the model's own is lowered to it,
/// no thinking setting reaches a model that has none, and a request refused because the answer's room
/// did not fit the context is tried once more with the room that is left.
/// </summary>
public sealed class ModelLimitsTests
{
    private static readonly ChatMessage[] Question = [new(ChatRole.User, "Hello?")];

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(32768, 4096, true)]
    [InlineData(4096, 4096, true)]
    [InlineData(0, null, false)]
    [InlineData(null, -1, false)]
    [InlineData(4096, 8192, false)] // an answer cannot be larger than the window it has to fit in
    public void Limits_that_do_not_make_sense_together_are_refused(int? window, int? answer, bool usable) =>
        Assert.Equal(usable, ModelLimits.TryCreate(window, answer, null, out _));

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, null, false)] // a server's model is only asked about thinking when it is known to think
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void Thinking_is_set_on_this_computer_unless_known_absent_and_elsewhere_only_when_known(bool onThisComputer, bool? reasoning, bool thinks) =>
        Assert.Equal(thinks, new ModelLimits(Reasoning: reasoning).ThinksOn(onThisComputer));

    [Theory]
    [InlineData(null, 1024)]
    [InlineData(4000, 1024)]
    [InlineData(512, 512)]
    public async Task An_answer_bound_above_the_models_own_is_lowered_to_it(int? asked, int sent)
    {
        var model = new FakeChatModel();
        var fitted = new ModelFitChatClient(model, new ModelLimits(MaxOutputTokens: 1024));
        var options = new ChatOptions { MaxOutputTokens = asked };

        await fitted.GetResponseAsync(Question, options, TestContext.Current.CancellationToken);

        Assert.Equal(sent, Assert.Single(model.Calls).Options!.MaxOutputTokens);
        Assert.Equal(asked, options.MaxOutputTokens); // the caller's options are left as they were
    }

    [Fact]
    public async Task No_thinking_setting_reaches_a_model_known_not_to_think()
    {
        var model = new FakeChatModel();
        var fitted = new ModelFitChatClient(model, new ModelLimits(Reasoning: false));

        await fitted.GetResponseAsync(Question, new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } }, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(model.Calls).Options!.Reasoning);
    }

    [Fact]
    public async Task With_nothing_known_the_request_goes_as_it_is()
    {
        var model = new FakeChatModel();
        var options = new ChatOptions { MaxOutputTokens = 4000, Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } };

        await new ModelFitChatClient(model, ModelLimits.Unknown).GetResponseAsync(Question, options, TestContext.Current.CancellationToken);

        Assert.Same(options, Assert.Single(model.Calls).Options);
    }

    [Fact]
    public async Task A_refusal_for_the_answers_room_is_tried_once_more_with_the_room_left_and_the_window_is_kept()
    {
        // 6000 tokens in, 4000 asked for, 8192 in all: 2192 are left for the answer.
        var model = new RefusesOnce(new ContextOverflowException("too long", null!) { ContextWindow = 8192, RequestTokens = 10000 });
        var fitted = new ModelFitChatClient(model, ModelLimits.Unknown);

        var response = await fitted.GetResponseAsync(Question, new ChatOptions { MaxOutputTokens = 4000 }, TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
        Assert.Equal([4000, 2192], model.Asked);
        Assert.Equal(8192, fitted.Limits.ContextWindow);
    }

    [Fact]
    public async Task A_streamed_request_refused_before_its_first_piece_is_tried_once_more()
    {
        var model = new RefusesOnce(new ContextOverflowException("too long", null!) { ContextWindow = 8192, RequestTokens = 10000 });
        var fitted = new ModelFitChatClient(model, ModelLimits.Unknown);

        var text = string.Concat(await fitted.GetStreamingResponseAsync(Question, new ChatOptions { MaxOutputTokens = 4000 }, TestContext.Current.CancellationToken)
            .Select(u => u.Text).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("ok", text);
        Assert.Equal([4000, 2192], model.Asked);
    }

    [Theory]
    [InlineData(8192, 13000, 4000)] // the question alone is past the window: a smaller answer would not help
    [InlineData(8192, 12100, 4000)] // what is left is too small to be an answer
    [InlineData(null, 10000, 4000)] // the window is not known
    [InlineData(8192, null, 4000)] // nor how much was asked for
    [InlineData(8192, 10000, null)] // no answer bound was asked for, so there is none to lower
    public async Task A_refusal_a_smaller_answer_would_not_cure_stands(int? window, int? requested, int? asked)
    {
        var refusal = new ContextOverflowException("too long", null!) { ContextWindow = window, RequestTokens = requested };
        var model = new RefusesOnce(refusal);
        var fitted = new ModelFitChatClient(model, ModelLimits.Unknown);

        var thrown = await Assert.ThrowsAsync<ContextOverflowException>(() =>
            fitted.GetResponseAsync(Question, new ChatOptions { MaxOutputTokens = asked }, TestContext.Current.CancellationToken));

        Assert.Same(refusal, thrown);
        Assert.Single(model.Asked);
    }

    [Fact]
    public async Task A_second_refusal_is_not_tried_again()
    {
        var model = new RefusesOnce(new ContextOverflowException("too long", null!) { ContextWindow = 8192, RequestTokens = 10000 }, times: 2);
        var fitted = new ModelFitChatClient(model, ModelLimits.Unknown);

        await Assert.ThrowsAsync<ContextOverflowException>(() =>
            fitted.GetResponseAsync(Question, new ChatOptions { MaxOutputTokens = 4000 }, TestContext.Current.CancellationToken));

        Assert.Equal([4000, 2192], model.Asked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Thinking_in_an_answer_marks_a_model_nobody_described_as_one_that_thinks(bool streamed)
    {
        var fitted = new ModelFitChatClient(new Thinks(), ModelLimits.Unknown);
        Assert.Null(fitted.Limits.Reasoning);

        if (streamed) await fitted.GetStreamingResponseAsync(Question, null, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        else await fitted.GetResponseAsync(Question, null, TestContext.Current.CancellationToken);

        Assert.True(fitted.Limits.Reasoning);
        Assert.True(fitted.Limits.ThinksOn(onThisComputer: false)); // so the next task asks it to think briefly
    }

    [Fact]
    public async Task A_model_said_not_to_think_stays_so_whatever_its_answers_carry()
    {
        var fitted = new ModelFitChatClient(new Thinks(), new ModelLimits(Reasoning: false));

        await fitted.GetResponseAsync(Question, null, TestContext.Current.CancellationToken);

        Assert.False(fitted.Limits.Reasoning);
    }

    [Fact]
    public async Task An_answer_without_thinking_leaves_it_unknown()
    {
        var fitted = new ModelFitChatClient(new FakeChatModel(), ModelLimits.Unknown);

        await fitted.GetResponseAsync(Question, null, TestContext.Current.CancellationToken);

        Assert.Null(fitted.Limits.Reasoning);
    }

    /// <summary>A model that thinks before every answer, as a server sends it (the thinking as its own content).</summary>
    private sealed class Thinks : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextReasoningContent("The person wants OK."), new TextContent("OK")])));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("The person wants OK.")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "OK");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>A model that refuses the first <c>times</c> requests and then answers "ok".</summary>
    private sealed class RefusesOnce(Exception refusal, int times = 1) : IChatClient
    {
        public List<int?> Asked { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Asked.Add(options?.MaxOutputTokens);
            return Asked.Count <= times ? Task.FromException<ChatResponse>(refusal) : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Asked.Add(options?.MaxOutputTokens);
            await Task.Yield();
            if (Asked.Count <= times) throw refusal;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
