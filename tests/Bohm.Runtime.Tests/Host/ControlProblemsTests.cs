using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Control;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The control surface answers every error with a body that says why and what to do next — an RFC 9457
/// problem with a <c>reason</c> — and takes in only HTML as an application.
/// </summary>
public sealed class ControlProblemsTests : IAsyncLifetime
{
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await RunningHost.StartAsync(configure: o => o with { FetchAssetsOnAdoption = false });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task An_error_a_route_answers_with_no_body_becomes_a_problem_with_a_reason_and_a_next_step()
    {
        using var missing = await _host.ControlClient().GetAsync("/__control/apps/0123456789abcdef0123456789abcdef/status");
        HttpAssert.Status(HttpStatusCode.NotFound, missing);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("not-found", problem.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.False(problem.TryGetProperty("instance", out _)); // the path carries ids; it is not echoed
    }

    [Fact]
    public async Task An_unknown_route_says_so_too()
    {
        using var unknown = await _host.ControlClient().GetAsync("/__control/no-such-route");
        Assert.True((int)unknown.StatusCode >= 400);
        Assert.Equal("application/problem+json", unknown.Content.Headers.ContentType?.MediaType);
        Assert.True(JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()).RootElement.TryGetProperty("reason", out _));
    }

    [Theory]
    [InlineData("Just a note, no markup at all.")]
    [InlineData("""{"title":"not an app","items":[1,2,3]}""")]
    [InlineData("a < b and c > d")]
    public async Task A_body_that_is_not_html_is_not_taken_in_as_an_application(string body)
    {
        using var refused = await _host.ControlClient().PostAsync("/__control/apps", new StringContent(body, Encoding.UTF8, "text/html"));
        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        Assert.Equal("not-html", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("reason").GetString());
        Assert.Empty(await _host.Catalog.ListAsync());
    }

    [Fact]
    public async Task A_binary_file_is_not_taken_in_either()
    {
        using var refused = await _host.ControlClient().PostAsync("/__control/apps", new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x00, 0x3C, 0x61, 0x3E]));
        HttpAssert.Status(HttpStatusCode.BadRequest, refused);
        Assert.Empty(await _host.Catalog.ListAsync());
    }

    [Theory]
    [InlineData("<p>Hello</p>")]
    [InlineData("Hello <b>there</b>")]
    [InlineData("<!doctype html><title>T</title>")]
    public async Task Any_document_with_a_tag_is_still_taken_in(string body)
    {
        using var taken = await _host.ControlClient().PostAsync("/__control/apps", new StringContent(body, Encoding.UTF8, "text/html"));
        HttpAssert.Status(HttpStatusCode.Created, taken);
    }

    [Theory]
    [InlineData(400, "bad-request")]
    [InlineData(409, "conflict")]
    [InlineData(503, "unavailable")]
    [InlineData(500, "internal")]
    public void Every_error_status_has_a_general_reason(int status, string reason) =>
        Assert.Equal(reason, ControlProblems.General(status).Reason);
}
