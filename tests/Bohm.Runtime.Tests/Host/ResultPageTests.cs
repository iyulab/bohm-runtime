using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An answer the person was given becomes a page of its own: the text as they read it, escaped, with
/// where it came from — an unsaved result served like any application, with no script of its own.
/// </summary>
public sealed class ResultPageTests : IAsyncLifetime
{
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await RunningHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_answer_becomes_an_unsaved_page_with_its_text_escaped_and_its_sources()
    {
        var body = """
            {"title":"Lunch <this week>","text":"Monday: rice\nTuesday: <script>alert(1)</script> noodles\n\n| a | b |","lang":"ko",
             "sourcesHeading":"출처","sources":[{"name":"School menu","url":"https://school.example/menu?w=1&d=2"},{"name":"Notice"}]}
            """;
        using var made = await _host.ControlClient().PostAsync("/__control/results", new StringContent(body, Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.Created, made);
        var app = JsonDocument.Parse(await made.Content.ReadAsStringAsync()).RootElement;
        Assert.True(app.GetProperty("unsaved").GetBoolean());
        var id = app.GetProperty("id").GetString()!;

        var html = Encoding.UTF8.GetString(await _host.Catalog.ReadHtmlAsync(id));
        Assert.StartsWith("<!doctype html>\n<html lang=\"ko\">", html);
        Assert.Contains("<title>Lunch &lt;this week&gt;</title>", html);
        Assert.Contains("Tuesday: &lt;script&gt;alert(1)&lt;/script&gt; noodles", html);
        Assert.Contains("Monday: rice\nTuesday:", html); // the lines as read
        Assert.DoesNotContain("<script", html);
        Assert.Contains("<h2>출처</h2>", html);
        Assert.Contains("<li>School menu<br><span class=\"url\">https://school.example/menu?w=1&amp;d=2</span></li>", html);
        Assert.Contains("<li>Notice</li>", html);

        using var served = await _host.ClientForApp(id).GetAsync("/");
        HttpAssert.Status(HttpStatusCode.OK, served);
        Assert.Contains("Monday: rice", await served.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_sources_the_page_has_no_sources_heading()
    {
        using var made = await _host.ControlClient().PostAsync("/__control/results",
            new StringContent("""{"title":"Note","text":"Just this."}""", Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.Created, made);
        var id = JsonDocument.Parse(await made.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var html = Encoding.UTF8.GetString(await _host.Catalog.ReadHtmlAsync(id));
        Assert.DoesNotContain("<h2>", html);
        Assert.StartsWith("<!doctype html>\n<html>", html);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"title":"","text":"x"}""")]
    [InlineData("""{"title":"T","text":"  "}""")]
    [InlineData("""{"title":"T"}""")]
    [InlineData("""{"title":"T","text":"x","sources":"a"}""")]
    [InlineData("""{"title":"T","text":"x","sources":[{"url":"https://a.example/"}]}""")]
    public async Task A_body_in_another_shape_is_refused_and_nothing_is_added(string body)
    {
        using var made = await _host.ControlClient().PostAsync("/__control/results", new StringContent(body, Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.BadRequest, made);
        Assert.Empty(await _host.Catalog.ListAsync());
    }
}
