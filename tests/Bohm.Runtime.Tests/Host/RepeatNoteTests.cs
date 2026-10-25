using Bohm.Runtime.Host.Agent;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>The note a turn gets when it goes round the same few calls: a pair of steps that a page undoes, three rounds in a row.</summary>
public sealed class RepeatNoteTests
{
    private static string[] Calls(params string[] names) => names;

    [Fact]
    public void A_pair_of_calls_gone_through_three_times_in_a_row_is_noted()
    {
        var note = RepeatNote.Of(Calls("type(a)", "click(b)", "type(a)", "click(b)", "type(a)", "click(b)"));
        Assert.NotNull(note);
        Assert.Contains("same 2 calls 3 times", note);
    }

    [Fact]
    public void The_rounds_need_not_start_at_the_beginning_of_the_conversation()
    {
        Assert.NotNull(RepeatNote.Of(Calls("open(x)", "snapshot()", "type(a)", "click(b)", "type(a)", "click(b)", "type(a)", "click(b)")));
    }

    [Fact]
    public void Two_rounds_are_not_yet_a_loop()
    {
        Assert.Null(RepeatNote.Of(Calls("type(a)", "click(b)", "type(a)", "click(b)")));
        Assert.Null(RepeatNote.Of(Calls("open(x)", "type(a)", "click(b)", "type(a)", "click(b)")));
    }

    [Fact]
    public void A_different_value_is_a_different_call()
    {
        Assert.Null(RepeatNote.Of(Calls("type(a=1)", "click(b)", "type(a=2)", "click(b)", "type(a=3)", "click(b)")));
    }

    [Fact]
    public void One_call_alone_and_a_run_that_puts_no_text_in_a_field_are_never_noted()
    {
        Assert.Null(RepeatNote.Of(Calls("scroll()", "scroll()", "scroll()", "scroll()", "scroll()")));
        Assert.Null(RepeatNote.Of(Calls("scroll()", "read_page()", "scroll()", "read_page()", "scroll()", "read_page()")));
        Assert.Null(RepeatNote.Of(Calls("click(e12)", "read_page()", "click(e12)", "read_page()", "click(e12)", "read_page()")));
    }

    [Fact]
    public void A_run_of_three_calls_is_noted_after_three_rounds_and_a_broken_round_ends_it()
    {
        var round = new[] { "type(a)", "b()", "c()" };
        Assert.Contains("same 3 calls 3 times", RepeatNote.Of([.. round, .. round, .. round]));
        Assert.Null(RepeatNote.Of([.. round, .. round, "type(a)", "b()", "d()"]));
        Assert.Null(RepeatNote.Of([]));
    }

    [Fact]
    public void A_call_is_told_from_another_by_the_tool_and_what_it_was_given_not_by_the_order_of_the_arguments()
    {
        var one = new FunctionCallContent("1", "type", new Dictionary<string, object?> { ["ref"] = "e3", ["text"] = "x" });
        var same = new FunctionCallContent("2", "type", new Dictionary<string, object?> { ["text"] = "x", ["ref"] = "e3" });
        var other = new FunctionCallContent("3", "type", new Dictionary<string, object?> { ["ref"] = "e3", ["text"] = "y" });
        Assert.Equal(RepeatNote.Signature(one), RepeatNote.Signature(same));
        Assert.NotEqual(RepeatNote.Signature(one), RepeatNote.Signature(other));
    }
}
