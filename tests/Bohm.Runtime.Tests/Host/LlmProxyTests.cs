using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

public sealed class LlmProxyTests : IAsyncLifetime
{
    private const string RealKey = "sk-real-secret-0123456789";

    private FakeProvider _provider = null!;
    private RunningHost _host = null!;
    private string _app = null!;
    private string _cookie = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync(configure: o => o with
        {
            LlmEndpoints = new Dictionary<string, Uri>
            {
                ["api.openai.com"] = _provider.Address,
                ["api.anthropic.com"] = _provider.Address,
                ["generativelanguage.googleapis.com"] = _provider.Address,
            },
        });
        _app = await _host.AdoptAsync("<!doctype html><title>AI</title>");
        using var load = await _host.ClientForApp(_app).GetAsync("/");
        var setCookie = Assert.Single(load.Headers.GetValues("Set-Cookie"));
        _cookie = setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private string Placeholder => $"bohm-key-{_app}";

    [Fact]
    public async Task Without_a_connected_key_the_app_gets_a_provider_shaped_error_and_the_need_is_reported()
    {
        using var response = await SendAsync(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions", """{"model":"m"}""", bearer: Placeholder);

        HttpAssert.Status(HttpStatusCode.Unauthorized, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bohm_no_key", error.GetProperty("type").GetString());
        Assert.Contains("OpenAI", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(_provider.Received);

        var status = JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/apps/{_app}/status")).RootElement;
        Assert.Equal(["openai"], status.GetProperty("needsKey").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_placeholder_is_replaced_by_the_real_key_and_nothing_else_changes()
    {
        await ConnectAsync("openai");

        using var response = await SendAsync(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions?x=1", """{"model":"m","messages":[]}""", bearer: Placeholder);

        HttpAssert.Status(HttpStatusCode.OK, response);
        Assert.Contains(FakeProvider.Reply, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("req-1", Assert.Single(response.Headers.GetValues("x-provider-request-id")));
        var received = Assert.Single(_provider.Received);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/v1/chat/completions?x=1", received.PathAndQuery);
        Assert.Equal($"Bearer {RealKey}", received.Headers["Authorization"]);
        Assert.Equal("""{"model":"m","messages":[]}""", received.Body);
        Assert.StartsWith("application/json", received.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.False(received.Headers.ContainsKey("Cookie"));
        Assert.False(received.Headers.ContainsKey("Origin"));
    }

    [Fact]
    public async Task A_relayed_request_is_recorded_as_sent_to_where_it_went_and_a_refused_one_is_not()
    {
        (await SendAsync(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions", "{}", bearer: Placeholder)).Dispose(); // no key yet
        await ConnectAsync("openai");
        (await SendAsync(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions", "{}", bearer: Placeholder)).Dispose();
        (await SendAsync(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions", "{}", bearer: Placeholder)).Dispose();

        var egress = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement;
        var sent = Assert.Single(egress.GetProperty("sent").EnumerateArray());
        Assert.Equal(_provider.Address.Authority, sent.GetProperty("host").GetString()); // the stand-in (with its port), not the name the app used
        Assert.Equal(2, sent.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Each_provider_gets_the_key_the_way_it_expects()
    {
        await ConnectAsync("anthropic");
        await ConnectAsync("google");

        using var anthropic = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.anthropic.com/v1/messages") { Content = Json("{}") };
        anthropic.Headers.Add("x-api-key", Placeholder);
        anthropic.Headers.Add("anthropic-version", "2023-06-01");
        (await SendAsync(anthropic)).Dispose();
        (await SendAsync(HttpMethod.Post, $"/__bohm/llm/generativelanguage.googleapis.com/v1beta/models/g:generateContent?key={Placeholder}", "{}")).Dispose();

        var received = _provider.Received.ToArray();
        Assert.Equal(RealKey, received[0].Headers["x-api-key"]);
        Assert.Equal("2023-06-01", received[0].Headers["anthropic-version"]);
        Assert.Equal($"/v1beta/models/g:generateContent?key={RealKey}", received[1].PathAndQuery);
        Assert.Equal(RealKey, received[1].Headers["x-goog-api-key"]);
    }

    [Fact]
    public async Task Streamed_answers_arrive_while_they_are_being_produced()
    {
        await ConnectAsync("openai");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions") { Content = Json("""{"stream":true}""") };
        request.Headers.Add("Cookie", _cookie);

        using var response = await _host.ClientForApp(_app).SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var first = await reader.ReadLineAsync();
        var rest = await reader.ReadToEndAsync();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("data: ", first, StringComparison.Ordinal);
        Assert.Contains("[DONE]", rest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_without_the_applications_session_is_refused()
    {
        await ConnectAsync("openai");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions") { Content = Json("{}") };

        using var response = await _host.ClientForApp(_app).SendAsync(request);

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
        Assert.Empty(_provider.Received);
    }

    [Fact]
    public async Task Hosts_that_are_not_known_providers_are_not_relayed()
    {
        using var response = await SendAsync(HttpMethod.Get, "/__bohm/llm/example.com/anything", null);

        HttpAssert.Status(HttpStatusCode.NotFound, response);
    }

    [Theory]
    [InlineData("/__bohm/llm/api.openai.com//attacker.example/steal")]
    [InlineData("/__bohm/llm/api.openai.com/https://attacker.example/steal")]
    [InlineData("/__bohm/llm/api.openai.com/v1/../../other")]
    [InlineData("/__bohm/llm/api.openai.com/%2F%2Fattacker.example/steal")]
    public async Task The_key_is_never_sent_anywhere_but_the_provider(string path)
    {
        await ConnectAsync("openai");

        using var response = await SendAsync(HttpMethod.Post, path, "{}", bearer: Placeholder);

        // Refused (400, or 404 when the server normalises the path before it reaches the proxy), or
        // delivered to the provider itself. An attempt to reach any other host would surface as 502
        // (the attacker host does not resolve here), which must never happen.
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.OK, $"got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task An_unreachable_provider_is_reported_in_the_providers_shape()
    {
        await using var host = await RunningHost.StartAsync(configure: o => o with
        {
            LlmEndpoints = new Dictionary<string, Uri> { ["api.anthropic.com"] = new("http://127.0.0.1:9/") },
        });
        var app = await host.AdoptAsync("<p>x</p>");
        using var load = await host.ClientForApp(app).GetAsync("/");
        var cookie = Assert.Single(load.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var put = await host.ControlClient().PutAsync("/__control/llm/anthropic/key", new StringContent(RealKey));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.anthropic.com/v1/messages") { Content = Json("{}") };
        request.Headers.Add("Cookie", cookie);

        using var response = await host.ClientForApp(app).SendAsync(request);

        HttpAssert.Status(HttpStatusCode.BadGateway, response);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("error", body.GetProperty("type").GetString());
        Assert.Equal("bohm_unreachable", body.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_control_api_reports_connection_without_revealing_keys()
    {
        await ConnectAsync("openai");

        var text = await _host.ControlClient().GetStringAsync("/__control/llm");

        Assert.DoesNotContain(RealKey, text, StringComparison.Ordinal);
        var openai = JsonDocument.Parse(text).RootElement.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "openai");
        Assert.True(openai.GetProperty("connected").GetBoolean());

        using var removed = await _host.ControlClient().DeleteAsync("/__control/llm/openai/key");
        Assert.DoesNotContain(JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/llm")).RootElement.EnumerateArray(),
            p => p.GetProperty("connected").GetBoolean());
    }

    private async Task ConnectAsync(string provider)
    {
        using var response = await _host.ControlClient().PutAsync($"/__control/llm/{provider}/key", new StringContent(RealKey));
        HttpAssert.Status(HttpStatusCode.OK, response);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = Json(body);
        if (bearer is not null) request.Headers.Authorization = new("Bearer", bearer);
        return SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        request.Headers.Add("Cookie", _cookie);
        request.Headers.Add("Origin", $"http://{_app}.localhost:{_host.Port}");
        return _host.ClientForApp(_app).SendAsync(request);
    }
}
