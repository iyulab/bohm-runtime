using Bohm.Runtime.Host.Edit;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The text of one field of a tool call's arguments, read from the pieces of JSON a provider sends while the
/// model writes the call: whatever the pieces' boundaries, what comes out is the field's text, unescaped.
/// </summary>
public sealed class ToolArgumentTextTests
{
    private const string Arguments = """
        {"title":"Log \"one\"","sources":[{"name":"html","html":"not this"}],"html":"<p class=\"a\">café 😀\n\ttab \\ slash \/ end</p>","summary":"html"}
        """;

    private const string Html = "<p class=\"a\">café 😀\n\ttab \\ slash / end</p>";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(1000)]
    public void The_fields_text_comes_out_whole_whatever_the_pieces(int size)
    {
        var text = new ToolArgumentText("html");

        var written = string.Concat(Arguments.Trim().Chunk(size).Select(piece => text.Add(new string(piece))));

        Assert.Equal(Html, written);
    }

    [Fact]
    public void No_piece_ends_in_half_a_character()
    {
        var text = new ToolArgumentText("html");
        var pieces = Arguments.Trim().Chunk(1).Select(piece => text.Add(new string(piece))).Where(p => p.Length > 0).ToList();

        Assert.All(pieces, piece => Assert.False(char.IsHighSurrogate(piece[^1])));
        Assert.Contains("😀", pieces);
    }

    [Fact]
    public void A_field_of_the_same_name_deeper_in_or_a_value_that_is_not_text_gives_nothing()
    {
        var text = new ToolArgumentText("html");

        Assert.Equal("", text.Add("""{"sources":[{"html":"no"}],"html":null,"other":{"html":"no"}}"""));
    }

    [Fact]
    public void A_key_that_only_shares_the_fields_start_is_not_it()
    {
        var text = new ToolArgumentText("html");

        Assert.Equal("yes", text.Add("""{"html_note":"no","htm":"no","html":"yes"}"""));
    }
}
