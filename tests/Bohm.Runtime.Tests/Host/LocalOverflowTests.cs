using System.Runtime.CompilerServices;
using Bohm.Runtime.Host.Llm;
using IronHive.Abstractions.Exceptions;
using LMSupply.Exceptions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The model on this computer refuses a request too long for its context in the model library's own type; the runtime's
/// recovery from such a request (a smaller answer, the pages read cut down) and the reason it reports go by the type every
/// other model's refusal takes. A refusal the recovery could not undo is a conversation too long, not a model that gave no
/// answer.
/// </summary>
public sealed class LocalOverflowTests
{
    private static readonly ChatMessage[] Question = [new(ChatRole.User, "Hello?")];

    [Fact]
    public async Task A_local_refusal_for_the_answers_room_is_tried_once_more_with_the_room_left()
    {
        // 6000 tokens in, 4000 asked for, 8192 in all: the library says 10000 did not fit 8192.
        var model = new LibraryRefusesOnce(new ContextLengthExceededException(10000, 8192));
        var fitted = new ModelFitChatClient(new LocalOverflowChatClient(model), ModelLimits.Unknown);

        var response = await fitted.GetResponseAsync(Question, new ChatOptions { MaxOutputTokens = 4000 }, TestContext.Current.CancellationToken);

        Assert.Equal("ok", response.Text);
        Assert.Equal([4000, 2192], model.Asked);
    }

    [Fact]
    public async Task A_local_refusal_inside_the_stream_is_tried_once_more_with_the_room_left()
    {
        var model = new LibraryRefusesOnce(new ContextLengthExceededException(10000, 8192));
        var fitted = new ModelFitChatClient(new LocalOverflowChatClient(model), ModelLimits.Unknown);

        var text = string.Concat(await fitted.GetStreamingResponseAsync(Question, new ChatOptions { MaxOutputTokens = 4000 }, TestContext.Current.CancellationToken)
            .Select(u => u.Text).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("ok", text);
        Assert.Equal([4000, 2192], model.Asked);
    }

    [Fact]
    public void The_local_refusal_carries_the_window_and_the_length_asked_for()
    {
        var overflow = LocalOverflowChatClient.Overflow(new ContextLengthExceededException(40000, 32768));

        Assert.Equal(32768, overflow.ContextWindow);
        Assert.Equal(40000, overflow.RequestTokens);
    }

    [Fact]
    public void A_conversation_too_long_for_the_context_is_reported_as_that_not_as_a_model_that_gave_no_answer()
    {
        var failure = LocalModelFailure.Answering(new ContextOverflowException("too long", null!) { ContextWindow = 32768, RequestTokens = 40000 });

        Assert.Equal(LocalModelFailure.ContextFull, failure.Reason);
    }

    [Fact]
    public void A_model_that_wrote_nothing_is_reported_as_that_not_as_a_server_that_failed()
    {
        var failure = LocalModelFailure.Answering(new Bohm.Runtime.Host.Agent.EmptyAnswerException());

        Assert.Equal(LocalModelFailure.EmptyAnswer, failure.Reason);
        Assert.Null(failure.Detail);
    }

    [Fact]
    public void A_server_that_reports_a_failure_and_keeps_running_is_a_model_that_gave_no_answer_in_the_servers_words()
    {
        var failure = LocalModelFailure.Answering(new InvalidOperationException("Compute error."));

        Assert.Equal(LocalModelFailure.AnswerFailed, failure.Reason);
        Assert.Equal("Compute error.", failure.Detail);
    }

    [Fact]
    public void A_server_that_exits_while_answering_is_reported_with_its_exit_code_and_last_line_through_the_failures_that_wrap_it()
    {
        var exited = new InferenceBackendExitedException(-9, "load_tensors: done\nggml_metal: out of memory\n", new HttpRequestException("The response ended prematurely."));

        var failure = LocalModelFailure.Answering(new InvalidOperationException("The chat request failed.", exited));

        Assert.Equal(LocalModelFailure.ServerStopped, failure.Reason);
        Assert.Equal("llama-server stopped (exit code -9): ggml_metal: out of memory", failure.Detail);
    }

    [Fact]
    public void A_server_that_exits_without_a_word_is_still_reported_as_stopped()
    {
        var failure = LocalModelFailure.Answering(new InferenceBackendExitedException(null, "", new HttpRequestException("Connection refused.")));

        Assert.Equal(LocalModelFailure.ServerStopped, failure.Reason);
        Assert.Equal("llama-server stopped (no exit code).", failure.Detail);
    }

    /// <summary>A model on this computer that refuses the first request in the model library's type, then answers "ok".</summary>
    private sealed class LibraryRefusesOnce(Exception refusal) : IChatClient
    {
        public List<int?> Asked { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Asked.Add(options?.MaxOutputTokens);
            return Asked.Count == 1 ? Task.FromException<ChatResponse>(refusal) : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Asked.Add(options?.MaxOutputTokens);
            await Task.Yield();
            if (Asked.Count == 1) throw refusal;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
