using Bohm.Runtime.Host.Edit;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// A model shown only the part around an element is told where the rest is — the lines that use what the element names and
/// an outline of scripts, styles and functions — so it reads what it needs instead of the file a range at a time.
/// </summary>
public sealed class SourceMapTests
{
    private static readonly string[] Lines =
    [
        "<!doctype html><title>Tasks</title>",                       // 1
        "<style>",                                                   // 2
        "  #save { color: navy; }",                                  // 3
        "</style>",                                                  // 4
        "<button id=\"save\" class=\"primary big\" onclick=\"saveAll(event)\">Save</button>",   // 5
        "<p>filler</p>",                                             // 6
        "<script>",                                                  // 7
        "  const list = [];",                                        // 8
        "  function saveAll(e) { localStorage.setItem('k', '1'); }", // 9
        "  const render = () => {};",                                // 10
        "  document.getElementById('save').addEventListener('click', render);",   // 11
        "</script>",                                                 // 12
    ];

    [Fact]
    public void The_element_names_its_id_classes_and_handler_functions()
    {
        Assert.Equal(["save", "primary", "big", "saveAll"], SourceMap.NamesIn("""<button id="save" class="primary big" onclick="saveAll(event)">Save</button>"""));
    }

    [Fact]
    public void Lines_elsewhere_that_use_those_names_are_found_outside_the_part_shown()
    {
        Assert.Equal([3, 9, 11], SourceMap.Related(Lines, ["save", "saveAll"], from: 5, to: 6));
    }

    [Fact]
    public void The_outline_names_script_and_style_blocks_and_functions()
    {
        Assert.Equal([2, 4, 7, 9, 10, 11, 12], SourceMap.Outline(Lines, from: 5, to: 6));
    }

    [Fact]
    public void Nothing_is_told_when_the_whole_source_is_shown()
    {
        Assert.Equal("", SourceMap.Describe(Lines, Lines[4], from: 1, to: Lines.Length));
    }

    [Fact]
    public void Ranges_are_read_from_text()
    {
        Assert.Equal([(120, 160), (300, 340), (7, 7)], SourceMap.Ranges("120-160, 340–300 and 7"));
        Assert.Empty(SourceMap.Ranges(null));
    }
}
