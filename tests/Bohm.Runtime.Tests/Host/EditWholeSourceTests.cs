using Bohm.Runtime.Host.Edit;
using Bohm.Runtime.Host.Llm;
using IronHive.Abstractions.Exceptions;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A provider's model is shown the whole source when it fits, so it does not spend its rounds reading the file before it
/// changes anything; the model on this computer is shown the part around the element, as is a source too large.
/// </summary>
public sealed class EditWholeSourceTests
{
    private static readonly string[] Lines = [.. Enumerable.Range(1, 300).Select(i => i == 200 ? """<button id="go">Go</button>""" : $"<p>line {i}</p>")];
    private static readonly string Source = string.Join('\n', Lines);

    [Fact]
    public async Task A_providers_model_is_shown_the_whole_source_when_it_fits()
    {
        var model = new FakeChatModel { Reply = "Nothing to change." };

        await ProposeAsync(model, onThisComputer: false, ModelLimits.Unknown);

        var asked = Asked(model, 0);
        Assert.Contains("1: <p>line 1</p>", asked, StringComparison.Ordinal);
        Assert.Contains("300: <p>line 300</p>", asked, StringComparison.Ordinal);
        Assert.Contains("Source lines 1–300 of 300", asked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_on_this_computer_is_shown_the_part_around_the_element()
    {
        var model = new FakeChatModel { Reply = "Nothing to change." };

        await ProposeAsync(model, onThisComputer: true, ModelLimits.Unknown);

        var asked = Asked(model, 0);
        Assert.DoesNotContain("<p>line 1</p>", asked, StringComparison.Ordinal);
        Assert.Contains("200: <button id=\"go\">Go</button>", asked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_refuses_the_whole_source_as_too_long_is_asked_again_with_the_part_around_the_element()
    {
        var model = new FakeChatModel { Reply = "Nothing to change." };
        model.FirstFailures.Enqueue(new ContextOverflowException("too long", null!) { RequestTokens = 9_000 });

        await ProposeAsync(model, onThisComputer: false, ModelLimits.Unknown);

        Assert.Equal(2, model.Calls.Count);
        Assert.Contains("1: <p>line 1</p>", Asked(model, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("<p>line 1</p>", Asked(model, 1), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, null, 3_000, true)]    // a provider's model, window unknown, a small source
    [InlineData(false, null, 90_000, true)]   // window unknown, an 80 KB app — read whole anyway, a round at a time, when given only the part (cycle-586)
    [InlineData(false, null, 200_000, false)] // too large to show whole without knowing the window
    [InlineData(false, 8_192, 30_000, false)] // more than half of a known window
    [InlineData(false, 131_072, 100_000, true)]
    [InlineData(true, 131_072, 3_000, false)] // the model on this computer: never
    public void Whether_the_whole_source_is_shown(bool onThisComputer, int? window, int characters, bool whole) =>
        Assert.Equal(whole, EditProposals.ShowsWholeSource(new string('x', characters), characters / 40, onThisComputer, new ModelLimits(window)));

    private static Task<EditProposal> ProposeAsync(FakeChatModel model, bool onThisComputer, ModelLimits limits) =>
        EditProposals.ProposeAsync(model, onThisComputer, limits, Source, new EditTarget("""<button id="go">Go</button>""", "Go"), "Make it bigger", CancellationToken.None);

    private static string Asked(FakeChatModel model, int call) => model.Calls[call].Messages.Last(m => m.Role == ChatRole.User).Text;
}
