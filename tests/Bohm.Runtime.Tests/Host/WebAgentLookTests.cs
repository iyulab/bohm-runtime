using System.Net;
using System.Text.Json;
using Bohm.Runtime.Host.Agent;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The web agent's <c>look</c>: declared only for a model that takes pictures in, whose answer to one small picture is learned
/// when nobody said; the picture a tool returned reaches the model after the results it came with, marked as material — the
/// latest one only.
/// </summary>
public sealed class WebAgentLookTests : IDisposable
{
    private readonly FakeChatModel _model = new();

    public void Dispose() => _model.Dispose();

    private static readonly string Shot = Convert.ToBase64String(ImageInput.RedSquare);

    [Fact]
    public async Task A_model_that_sees_is_offered_look_and_one_that_does_not_is_not()
    {
        await TurnAsync("""{"messages":[{"role":"user","text":"What does the chart show?"}]}""", sees: true);
        await TurnAsync("""{"messages":[{"role":"user","text":"What does the chart show?"}]}""", sees: false);

        var (seeing, seeingOptions) = _model.Calls[0];
        Assert.Contains(WebAgent.LookNote, seeing[0].Text, StringComparison.Ordinal);
        Assert.Contains(seeingOptions!.Tools!, t => t.Name == WebAgent.LookToolName);
        var (blind, blindOptions) = _model.Calls[1];
        Assert.DoesNotContain(WebAgent.LookNote, blind[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(blindOptions!.Tools!, t => t.Name == WebAgent.LookToolName);
    }

    [Fact]
    public void Only_the_latest_picture_goes_in_after_the_results_it_came_with_and_marked_as_material()
    {
        var conversation = WebAgent.ParseConversation(JsonDocument.Parse($$$"""
            {"messages":[
              {"role":"user","text":"Did the filter apply?"},
              {"role":"assistant","toolCalls":[{"id":"l1","name":"look","arguments":{"tab":"web-1"}}]},
              {"role":"tool","toolCallId":"l1","text":"Title: Shop\nAddress: https://s.example/","image":"{{{Shot}}}"},
              {"role":"assistant","toolCalls":[{"id":"l2","name":"look","arguments":{"tab":"web-1"}},{"id":"r1","name":"read_page","arguments":{"tab":"web-1"}}]},
              {"role":"tool","toolCallId":"l2","text":"Title: Shop\nAddress: https://s.example/?f=1","image":"{{{Shot}}}","imageType":"image/png"},
              {"role":"tool","toolCallId":"r1","text":"Title: Shop\nAddress: https://s.example/?f=1\nText:\n2 results"}
            ]}
            """).RootElement);

        // One picture in the whole conversation: after both results of the second step (a tool message may not be
        // followed by anything but the other results of its step), with the words that mark it as page material.
        var pictures = conversation.SelectMany(m => m.Contents).OfType<DataContent>().ToList();
        var picture = Assert.Single(pictures);
        Assert.Equal("image/png", picture.MediaType);
        Assert.Equal(ImageInput.RedSquare, picture.Data.ToArray());
        var carrier = conversation.Single(m => m.Contents.Contains(picture));
        Assert.Equal(ChatRole.User, carrier.Role);
        Assert.Equal(conversation.Count - 1, conversation.IndexOf(carrier));
        Assert.Equal(ChatRole.Tool, conversation[^2].Role);
        Assert.Equal(WebAgent.PictureLead("l2"), carrier.Contents.OfType<TextContent>().Single().Text);
        Assert.StartsWith("<tab-material>", WebAgent.PictureLead("l2"), StringComparison.Ordinal);
        // The results' text stays as sent.
        Assert.Equal(3, conversation.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count());
    }

    [Fact]
    public void A_picture_that_is_not_base64_or_not_an_image_is_refused()
    {
        Assert.Throws<FormatException>(() => WebAgent.ParseConversation(JsonDocument.Parse("""
            {"messages":[{"role":"user","text":"q"},{"role":"assistant","toolCalls":[{"id":"l1","name":"look","arguments":{}}]},{"role":"tool","toolCallId":"l1","text":"t","image":"not base64!"}]}
            """).RootElement));
        Assert.Throws<FormatException>(() => WebAgent.ParseConversation(JsonDocument.Parse($$$"""
            {"messages":[{"role":"user","text":"q"},{"role":"assistant","toolCalls":[{"id":"l1","name":"look","arguments":{}}]},{"role":"tool","toolCallId":"l1","text":"t","image":"{{{Shot}}}","imageType":"text/html"}]}
            """).RootElement));
    }

    [Fact]
    public async Task A_call_to_look_stays_declared_when_the_answering_model_no_longer_sees()
    {
        await TurnAsync($$$"""
            {"messages":[
              {"role":"user","text":"What colour is the button?"},
              {"role":"assistant","toolCalls":[{"id":"l1","name":"look","arguments":{"tab":"web-1"}}]},
              {"role":"tool","toolCallId":"l1","text":"Title: T\nAddress: https://t.example/","image":"{{{Shot}}}"}
            ]}
            """, sees: false);

        var look = Assert.Single(_model.Calls).Options!.Tools!.Single(t => t.Name == WebAgent.LookToolName);
        Assert.Equal("No longer available.", look.Description);
    }

    [Fact]
    public async Task What_the_model_list_says_stands_and_an_undescribed_model_is_asked_once_with_a_picture()
    {
        var input = new ImageInput();
        var cancel = TestContext.Current.CancellationToken;
        Assert.True(await input.SeesAsync(new ChosenEditModel(_model, "company/listed", false, ModelLimits.Unknown, Sees: true), cancel));
        Assert.False(await input.SeesAsync(new ChosenEditModel(_model, "local", true, ModelLimits.Unknown, Sees: false), cancel));
        Assert.Empty(_model.Calls);

        _model.Reply = "Red.";
        var undescribed = new ChosenEditModel(_model, "company/qwen", false, ModelLimits.Unknown);
        Assert.Null(input.Known(undescribed));   // nothing is spent until a picture is about to go
        Assert.True(await input.SeesAsync(undescribed, cancel));
        Assert.True(await input.SeesAsync(undescribed, cancel));
        Assert.True(input.Known(undescribed));
        var (asked, _) = Assert.Single(_model.Calls);
        Assert.Equal(ImageInput.RedSquare, Assert.Single(asked).Contents.OfType<DataContent>().Single().Data.ToArray());
    }

    [Fact]
    public async Task A_refused_picture_is_kept_as_not_seeing_and_a_failure_that_says_nothing_is_asked_again()
    {
        var cancel = TestContext.Current.CancellationToken;
        var refusing = new ImageInput();
        _model.Failure = new HttpRequestException("image input is not supported", null, HttpStatusCode.BadRequest);
        var model = new ChosenEditModel(_model, "openrouter/text-only", false, ModelLimits.Unknown);
        Assert.False(await refusing.SeesAsync(model, cancel));
        Assert.False(await refusing.SeesAsync(model, cancel));
        Assert.Single(_model.Calls);

        var unreachable = new ImageInput();
        _model.Failure = new HttpRequestException("no route", null, HttpStatusCode.BadGateway);
        Assert.Null(await unreachable.SeesAsync(model, cancel));   // told nothing — not «does not see»
        _model.Failure = null;
        _model.Reply = "red";
        Assert.True(await unreachable.SeesAsync(model, cancel));
        Assert.Equal(3, _model.Calls.Count);
    }

    [Fact]
    public void A_picture_that_cannot_go_is_replaced_by_the_runtimes_word()
    {
        var conversation = WebAgent.ParseConversation(JsonDocument.Parse($$$"""
            {"messages":[
              {"role":"user","text":"What colour is the button?"},
              {"role":"assistant","toolCalls":[{"id":"l1","name":"look","arguments":{"tab":"web-1"}}]},
              {"role":"tool","toolCallId":"l1","text":"Title: T\nAddress: https://t.example/","image":"{{{Shot}}}"}
            ]}
            """).RootElement);
        Assert.True(WebAgent.HasPicture(conversation));

        var without = WebAgent.WithoutPicture(conversation, WebAgent.BlindNote);

        Assert.False(WebAgent.HasPicture(without));
        Assert.Equal(conversation.Count, without.Count);
        Assert.Equal(WebAgent.BlindNote, without[^1].Text);
        Assert.Equal(ChatRole.User, without[^1].Role);
    }

    private Task<TurnResult> TurnAsync(string conversation, bool sees) =>
        WebAgent.RunTurnAsync(_model, "test", onThisComputer: false, ModelLimits.Unknown, WebAgent.ParseConversation(JsonDocument.Parse(conversation).RootElement),
            DateTimeOffset.UnixEpoch, null, TestContext.Current.CancellationToken, sees: sees);
}
