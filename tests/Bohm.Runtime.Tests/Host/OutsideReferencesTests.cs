using Bohm.Runtime.Host;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// What an application's source loads from another computer — the same refusal for making and changing an
/// application, since such a version opens broken without a network.
/// </summary>
public sealed class OutsideReferencesTests
{
    [Theory]
    [InlineData("<script src=\"https://cdn.example/x.js\"></script>")]
    [InlineData("<link rel=\"stylesheet\" href=\"//cdn.example/x.css\">")]
    [InlineData("<style>@import url(\"https://cdn.example/x.css\");</style>")]
    [InlineData("<style>@import 'https://cdn.example/x.css';</style>")]
    [InlineData("<script type=\"module\">import { a } from 'https://cdn.example/m.js';</script>")]
    [InlineData("<script>import('https://cdn.example/m.js').then(m => m.go());</script>")]
    [InlineData("<script type=\"module\">import \"https://cdn.example/side.js\";</script>")]
    public void Code_from_another_computer_is_told_by_its_host(string html)
    {
        var problem = Assert.Single(OutsideReferences.Problems(html));

        Assert.Equal("The application loads code from cdn.example, which cannot be reached; write it in the file.", problem);
    }

    [Theory]
    [InlineData("<img src=\"https://img.example/a.png\">")]
    [InlineData("<style>@font-face { font-family: X; src: url(https://img.example/x.woff2); }</style>")]
    [InlineData("<div style=\"background: url('https://img.example/b.jpg')\"></div>")]
    [InlineData("<iframe src=\"https://img.example/\"></iframe>")]
    public void A_file_from_another_computer_is_told_apart_from_code(string html)
    {
        var problem = Assert.Single(OutsideReferences.Problems(html));

        Assert.StartsWith("The application loads a file (an image, a font or a frame) from img.example", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<a href=\"https://example.com\">a link is not loaded</a>")]
    [InlineData("<p>Visit https://example.com or import from https://example.com</p>")]
    [InlineData("<script src=\"app.js\"></script><img src=\"data:image/png;base64,AAAA\"><style>div { background: url(local.png) }</style>")]
    [InlineData("<script>fetch('/__bohm/sources/x'); const u = 'https://example.com';</script>")]
    public void Links_text_and_files_of_its_own_are_not_refused(string html)
    {
        Assert.Empty(OutsideReferences.Problems(html));
    }

    [Fact]
    public void One_host_both_imported_and_used_as_a_file_is_told_once_as_code()
    {
        var problem = Assert.Single(OutsideReferences.Problems("<style>@import url(https://cdn.example/a.css); body { background: url(https://CDN.example/b.png) }</style>"));

        Assert.Contains("loads code from cdn.example", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void What_a_change_adds_is_only_what_the_source_did_not_load_before()
    {
        const string before = "<script src=\"https://old.example/a.js\"></script><p>hi</p>";
        const string after = "<script src=\"https://old.example/a.js\"></script><p>hi</p><img src=\"https://new.example/i.png\">";

        var added = Assert.Single(OutsideReferences.Added(before, after));

        Assert.Contains("new.example", added, StringComparison.Ordinal);
        Assert.Empty(OutsideReferences.Added(after, before));
    }
}
