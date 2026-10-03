using Bohm.Runtime.Pages;

namespace Bohm.Runtime.Tests.Pages;

/// <summary>
/// Whether an application receives shared pages, read from the web app manifest it carries inside:
/// a <c>&lt;link rel="manifest"&gt;</c> with a <c>data:</c> address whose <c>share_target</c> is the GET form.
/// </summary>
public sealed class ShareTargetTests
{
    private const string Manifest = """{"name":"Reading list","share_target":{"action":"/","params":{"title":"name","text":"description","url":"link"}}}""";

    private static string Page(string link) => $"<!doctype html><html><head><title>Reading list</title>{link}</head><body></body></html>";

    [Fact]
    public void A_percent_encoded_manifest_names_the_query_parameters()
    {
        var target = ShareTarget.Find(Page($"""<link rel="manifest" href="data:application/manifest+json,{Uri.EscapeDataString(Manifest)}">"""));

        Assert.Equal(new ShareTarget("name", "description", "link"), target);
    }

    [Fact]
    public void A_manifest_written_plainly_in_single_quotes_or_with_html_entities_is_read_too()
    {
        Assert.Equal(new ShareTarget("name", "description", "link"), ShareTarget.Find(Page($"<link rel='manifest' href='data:application/manifest+json,{Manifest}'>")));
        var entities = Manifest.Replace("\"", "&quot;", StringComparison.Ordinal);
        Assert.Equal(new ShareTarget("name", "description", "link"), ShareTarget.Find(Page($"""<LINK href="data:application/manifest+json,{entities}" REL="Manifest">""")));
    }

    [Fact]
    public void A_base64_manifest_is_read()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Manifest));

        Assert.Equal(new ShareTarget("name", "description", "link"), ShareTarget.Find(Page($"""<link rel="manifest" href="data:application/manifest+json;base64,{encoded}">""")));
    }

    [Fact]
    public void A_target_naming_only_some_parameters_leaves_the_rest_null()
    {
        var manifest = Uri.EscapeDataString("""{"share_target":{"action":"/share","method":"get","params":{"url":"u"}}}""");

        Assert.Equal(new ShareTarget(null, null, "u"), ShareTarget.Find(Page($"""<link rel="manifest" href="data:application/manifest+json,{manifest}">""")));
    }

    [Theory]
    [InlineData("""<link rel="stylesheet" href="data:text/css,body{}">""")]
    [InlineData("""<link rel="manifest" href="manifest.webmanifest">""")]
    [InlineData("""<link rel="manifest" href="data:application/manifest+json,%7B%22name%22%3A%22x%22%7D">""")]
    [InlineData("""<link rel="manifest" href="data:application/manifest+json,not json">""")]
    [InlineData("""<link rel="manifest" href="data:application/manifest+json;base64,@@@">""")]
    [InlineData("""<link rel="manifest" href="data:application/manifest+json,%7B%22share_target%22%3A%7B%22method%22%3A%22POST%22%2C%22params%22%3A%7B%22files%22%3A%5B%5D%7D%7D%7D">""")]
    [InlineData("")]
    public void Anything_else_declares_no_target(string link)
    {
        Assert.Null(ShareTarget.Find(Page(link)));
    }
}
