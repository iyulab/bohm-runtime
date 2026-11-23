using System.Text;
using Bohm.Runtime.Adoption;

namespace Bohm.Runtime.Tests.Adoption;

/// <summary>
/// Code a model wrote carries a machine-readable mark in its head, put there by the runtime when the version is kept — whatever
/// the model wrote — and code a person brought in, or a version brought back from the history, is kept byte for byte.
/// </summary>
public sealed class AiWrittenMarkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bohm-mark-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Text(ReadOnlyMemory<byte> html) => Encoding.UTF8.GetString(html.Span);

    [Theory]
    [InlineData("<!doctype html><html lang=\"ko\"><head><title>t</title></head><body>b</body></html>", "<head>" + AiWrittenMark.Elements + "<title>")]
    [InlineData("<!DOCTYPE html><HTML><HEAD data-x=\"1\"><title>t</title></HEAD></HTML>", "<HEAD data-x=\"1\">" + AiWrittenMark.Elements + "<title>")]
    [InlineData("<!doctype html><html><body>b</body></html>", "<html>" + AiWrittenMark.Elements + "<body>")]
    [InlineData("<!doctype html><title>t</title><p>b</p>", "<!doctype html>" + AiWrittenMark.Elements + "<title>")]
    [InlineData("<p>b</p>", AiWrittenMark.Elements + "<p>b</p>")]
    public void The_mark_goes_at_the_start_of_the_head_or_where_a_parser_puts_it(string html, string expected)
    {
        var marked = Text(AiWrittenMark.Into(Encoding.UTF8.GetBytes(html)));

        Assert.Contains(expected, marked);
        Assert.True(AiWrittenMark.Carries(marked));
        Assert.Equal(marked, Text(AiWrittenMark.Into(Encoding.UTF8.GetBytes(marked))));   // once only
    }

    [Fact]
    public void Headless_parts_are_not_mistaken_for_the_head()
    {
        var marked = Text(AiWrittenMark.Into(Encoding.UTF8.GetBytes("<!doctype html><html><header>h</header></html>")));

        Assert.StartsWith("<!doctype html><html>" + AiWrittenMark.Elements + "<header>", marked);
    }

    [Fact]
    public async Task An_application_a_model_made_carries_the_mark_and_a_page_brought_in_does_not()
    {
        var catalog = new AdoptionCatalog(_root);
        var page = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>t</title></head></html>");

        var made = await catalog.AdoptAsync(page, generatedBy: "local/model.gguf", request: "a list", via: "made");
        var brought = await catalog.AdoptAsync(page, "page.html");

        var stored = await catalog.ReadHtmlAsync(made.Id);
        Assert.True(AiWrittenMark.Carries(Encoding.UTF8.GetString(stored!)));
        Assert.Equal(stored!.Length, made.Source.Size);   // the record describes the bytes kept
        Assert.Equal(page, await catalog.ReadHtmlAsync(brought.Id));
    }

    [Fact]
    public async Task A_version_a_model_wrote_is_marked_even_when_it_dropped_the_mark_and_a_restored_one_is_kept_as_it_was()
    {
        var catalog = new AdoptionCatalog(_root);
        var first = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>v1</title></head></html>");
        var app = await catalog.AdoptAsync(first, "page.html");
        await using var storage = await catalog.OpenStorageAsync(app.Id);

        // A model's edit of a page the person brought in — written without the mark.
        await catalog.ReviseAsync(app.Id, Encoding.UTF8.GetBytes("<!doctype html><html><head><title>v2</title></head></html>"), null, storage, generatedBy: "m");
        Assert.True(AiWrittenMark.Carries(Encoding.UTF8.GetString((await catalog.ReadHtmlAsync(app.Id))!)));

        // The person's own new file, and going back to the first version, are kept as they are.
        var own = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>v3</title></head></html>");
        await catalog.ReviseAsync(app.Id, own, "page.html", storage);
        Assert.Equal(own, await catalog.ReadHtmlAsync(app.Id));
        await catalog.RestoreAsync(app.Id, 1, storage);
        Assert.Equal(first, await catalog.ReadHtmlAsync(app.Id));
    }
}
