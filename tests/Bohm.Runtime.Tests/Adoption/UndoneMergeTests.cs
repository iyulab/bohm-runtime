using System.Text.Json.Nodes;
using Bohm.Runtime.Adoption;

namespace Bohm.Runtime.Tests.Adoption;

public sealed class UndoneMergeTests
{
    private static Dictionary<string, string> Items(params (string Key, string Value)[] items) => items.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static void AssertJson(string expected, string actual) => Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), actual);

    [Fact]
    public void Records_both_sides_added_are_all_kept_in_the_order_now_then_the_kept_sides_new_ones()
    {
        var merged = UndoneMerge.Merge(
            Items(("loans", """[{"id":1,"book":"a"}]""")),
            Items(("loans", """[{"id":1,"book":"a"},{"id":2,"book":"b"}]""")),
            Items(("loans", """[{"id":1,"book":"a"},{"id":3,"book":"c"}]""")));

        Assert.NotNull(merged);
        AssertJson("""[{"id":1,"book":"a"},{"id":3,"book":"c"},{"id":2,"book":"b"}]""", merged.Items["loans"]);
        Assert.Equal(1, merged.Incoming);
    }

    [Fact]
    public void A_record_edited_on_one_side_and_one_added_on_the_other_are_both_kept()
    {
        var merged = UndoneMerge.Merge(
            Items(("t", """[{"id":"a","done":false}]""")),
            Items(("t", """[{"id":"a","done":true}]""")),
            Items(("t", """[{"id":"a","done":false},{"id":"b","done":false}]""")));

        AssertJson("""[{"id":"a","done":true},{"id":"b","done":false}]""", merged!.Items["t"]);
    }

    [Fact]
    public void A_record_removed_on_the_kept_side_is_removed()
    {
        var merged = UndoneMerge.Merge(
            Items(("t", """[{"id":1},{"id":2}]""")),
            Items(("t", """[{"id":1}]""")),
            Items(("t", """[{"id":1},{"id":2},{"id":3}]""")));

        AssertJson("""[{"id":1},{"id":3}]""", merged!.Items["t"]);
    }

    [Fact]
    public void Different_fields_of_one_record_merge_but_the_same_field_changed_both_ways_is_a_conflict()
    {
        var based = Items(("t", """[{"id":1,"title":"x","done":false}]"""));
        var merged = UndoneMerge.Merge(based, Items(("t", """[{"id":1,"title":"y","done":false}]""")), Items(("t", """[{"id":1,"title":"x","done":true}]""")));
        AssertJson("""[{"id":1,"title":"y","done":true}]""", merged!.Items["t"]);

        Assert.Null(UndoneMerge.Merge(based, Items(("t", """[{"id":1,"title":"y","done":false}]""")), Items(("t", """[{"id":1,"title":"z","done":false}]"""))));
    }

    [Fact]
    public void Lists_inside_an_object_merge_and_a_property_only_one_side_changed_is_taken()
    {
        var merged = UndoneMerge.Merge(
            Items(("lib", """{"books":[{"id":1}],"loans":[],"owner":"kim"}""")),
            Items(("lib", """{"books":[{"id":1}],"loans":[{"id":10}],"owner":"lee"}""")),
            Items(("lib", """{"books":[{"id":1},{"id":2}],"loans":[{"id":11}],"owner":"kim"}""")));

        AssertJson("""{"books":[{"id":1},{"id":2}],"loans":[{"id":11},{"id":10}],"owner":"lee"}""", merged!.Items["lib"]);
        Assert.Equal(2, merged.Incoming);
    }

    [Theory]
    // A list whose elements carry no id, a repeated id, a value that is not JSON — changed on both sides, nothing to align by.
    [InlineData("[1]", "[1,2]", "[1,3]")]
    [InlineData("""[{"id":1}]""", """[{"id":1},{"id":2}]""", """[{"id":1},{"id":1}]""")]
    [InlineData("""[{"n":1}]""", """[{"n":1},{"n":2}]""", """[{"n":1},{"n":3}]""")]
    [InlineData("plain", "kept", "now")]
    [InlineData("1", "2", "3")]
    public void Values_changed_on_both_sides_that_cannot_be_aligned_are_a_conflict(string based, string kept, string now) =>
        Assert.Null(UndoneMerge.Merge(Items(("k", based)), Items(("k", kept)), Items(("k", now))));

    [Fact]
    public void Keys_no_merge_was_needed_for_keep_their_text_and_merged_ones_keep_their_letters()
    {
        var merged = UndoneMerge.Merge(
            Items(("same", "{ \"a\" : 1 }"), ("t", """[{"id":1,"이름":"가"}]""")),
            Items(("same", "{ \"a\" : 1 }"), ("t", """[{"id":1,"이름":"가"},{"id":2,"이름":"나"}]"""), ("new", "그대로")),
            Items(("same", "{ \"a\" : 1 }"), ("t", """[{"id":1,"이름":"가"},{"id":3,"이름":"다"}]""")));

        Assert.Equal("{ \"a\" : 1 }", merged!.Items["same"]);
        Assert.Equal("그대로", merged.Items["new"]);
        Assert.Equal("""[{"id":1,"이름":"가"},{"id":3,"이름":"다"},{"id":2,"이름":"나"}]""", merged.Items["t"]);
    }

    [Fact]
    public void Values_equal_as_json_are_the_same_value()
    {
        var merged = UndoneMerge.Merge(Items(("k", """{"a":1,"b":2}""")), Items(("k", """{ "b": 2, "a": 1 }""")), Items(("k", """[1]""")));

        Assert.Equal("[1]", merged!.Items["k"]);
        Assert.Equal(0, merged.Incoming);
    }

    [Fact]
    public void A_key_the_kept_side_removed_is_removed_and_one_it_added_is_added()
    {
        var merged = UndoneMerge.Merge(Items(("gone", "1"), ("t", "[]")), Items(("t", "[]"), ("added", "2")), Items(("gone", "1"), ("t", "[]"), ("mine", "3")));

        Assert.Equal(["added", "mine", "t"], merged!.Items.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, merged.Incoming);
    }
}
