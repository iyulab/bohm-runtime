using System.Text.Json;
using Bohm.Runtime.Host.Llm;
using Microsoft.Extensions.AI;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A tool whose schema says a parameter may be null by a list of types goes out with its one type and <c>nullable</c> — at every depth —
/// and a tool with no type list goes out as it came.
/// </summary>
public sealed class NoTypeListsTests
{
    private sealed record Field(string Key, string? Name = null);

    [Fact]
    public void A_type_list_anywhere_in_a_schema_goes_out_as_one_type_that_is_nullable()
    {
        var tool = AIFunctionFactory.Create((string title, string? summary = null, Field[]? fields = null) => title, "propose", "Proposes.");
        Assert.Contains("\"null\"", tool.JsonSchema.GetRawText(), StringComparison.Ordinal);   // what the factory writes

        var sent = Assert.IsAssignableFrom<AIFunctionDeclaration>(Assert.Single(NoTypeListsChatClient.Rewrite(new ChatOptions { Tools = [tool] })!.Tools!));
        var schema = sent.JsonSchema.GetRawText();

        Assert.DoesNotContain("\"null\"", schema, StringComparison.Ordinal);
        var properties = sent.JsonSchema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("summary").GetProperty("type").GetString());
        Assert.True(properties.GetProperty("summary").GetProperty("nullable").GetBoolean());
        Assert.Equal("array", properties.GetProperty("fields").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, properties.GetProperty("fields").GetProperty("items").GetProperty("properties").GetProperty("name").GetProperty("type").ValueKind);
        Assert.Equal(["title"], sent.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));   // optional stays optional
        Assert.Equal(("propose", "Proposes."), (sent.Name, sent.Description));
    }

    [Fact]
    public void Options_whose_tools_have_no_type_list_are_handed_on_as_they_came()
    {
        var tool = AIFunctionFactory.CreateDeclaration("click", "Clicks.", JsonElement.Parse("""{"type":"object","properties":{"ref":{"type":"string"},"kind":{"enum":["a","b"]}},"required":["ref"]}"""));
        var options = new ChatOptions { Tools = [tool] };

        Assert.Same(options, NoTypeListsChatClient.Rewrite(options));
        Assert.Null(NoTypeListsChatClient.Rewrite(null));
    }
}
