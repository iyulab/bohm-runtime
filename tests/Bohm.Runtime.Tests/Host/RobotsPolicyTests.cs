using System.Net;
using System.Text;
using System.Text.Json;

namespace Bohm.Runtime.Tests.Host;

public sealed class RobotsPolicyTests : IAsyncLifetime
{
    private readonly Sites _sites = new();
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await RunningHost.StartAsync(configure: o => o with { RobotsHttpHandler = () => _sites });

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task A_disallowed_address_is_refused_with_the_rule_that_decided_it()
    {
        _sites.Answer("https://board.example", HttpStatusCode.OK, "User-agent: *\nDisallow: /board\n");

        var refused = await CheckAsync("https://board.example/board?page=2");
        var open = await CheckAsync("https://board.example/notice");

        Assert.False(refused.GetProperty("allowed").GetBoolean());
        Assert.Equal("disallowed", refused.GetProperty("reason").GetString());
        Assert.Equal("Disallow: /board", refused.GetProperty("rule").GetString());
        Assert.Equal("https://board.example/robots.txt", refused.GetProperty("robotsUrl").GetString());
        Assert.True(open.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task A_missing_file_allows_everything_and_a_server_error_or_no_answer_allows_nothing()
    {
        _sites.Answer("http://intranet.example:8080", HttpStatusCode.NotFound, "");
        _sites.Answer("https://down.example", HttpStatusCode.ServiceUnavailable, "");

        Assert.True((await CheckAsync("http://intranet.example:8080/approvals")).GetProperty("allowed").GetBoolean());
        var down = await CheckAsync("https://down.example/");
        var silent = await CheckAsync("https://silent.example/");

        Assert.False(down.GetProperty("allowed").GetBoolean());
        Assert.Equal("server-error", down.GetProperty("reason").GetString());
        Assert.False(silent.GetProperty("allowed").GetBoolean());
        Assert.Equal("unreachable", silent.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_file_is_read_once_per_origin_without_cookies_and_counted_as_fetched()
    {
        _sites.Answer("https://papers.example", HttpStatusCode.OK, "User-agent: *\nDisallow: /search\n");

        await CheckAsync("https://papers.example/search?q=a");
        await CheckAsync("https://papers.example/abs/1");
        await CheckAsync("https://papers.example/search?q=b");

        Assert.Equal(1, _sites.Requests("https://papers.example/robots.txt"));
        Assert.False(_sites.SentCookie);
        var fetched = JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/egress")).RootElement.GetProperty("fetched");
        Assert.Contains(fetched.EnumerateArray(), h => h.GetProperty("host").GetString() == "papers.example" && h.GetProperty("count").GetInt32() == 1);
    }

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("not an address")]
    [InlineData("")]
    public async Task Only_web_addresses_are_asked_about(string url)
    {
        using var response = await _host.ControlClient().GetAsync($"/__control/web/robots?url={Uri.EscapeDataString(url)}");

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
    }

    private async Task<JsonElement> CheckAsync(string url) =>
        JsonDocument.Parse(await _host.ControlClient().GetStringAsync($"/__control/web/robots?url={Uri.EscapeDataString(url)}")).RootElement;

    /// <summary>Sites by origin: each answers its robots.txt with a status and a body; an origin not set does not answer.</summary>
    private sealed class Sites : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _requests = new(StringComparer.OrdinalIgnoreCase);

        public bool SentCookie { get; private set; }

        public void Answer(string origin, HttpStatusCode status, string body) => _answers[origin] = (status, body);

        public int Requests(string url)
        {
            lock (_requests) return _requests.GetValueOrDefault(url);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            lock (_requests) _requests[url.AbsoluteUri] = _requests.GetValueOrDefault(url.AbsoluteUri) + 1;
            SentCookie |= request.Headers.Contains("Cookie");
            if (!_answers.TryGetValue(url.GetLeftPart(UriPartial.Authority), out var answer))
                throw new HttpRequestException("No answer.");
            return Task.FromResult(new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "text/plain") });
        }
    }
}
