using System.Net;
using System.Text;

namespace Bohm.Runtime.Tests.Assets;

/// <summary>
/// A stand-in for the network: canned answers by URL, redirects included (reported the way a real
/// handler does — the final request URL on the response), and a log of what was asked.
/// </summary>
public sealed class FakeCdn : HttpMessageHandler
{
    private readonly Dictionary<string, (string ContentType, byte[] Body)> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _redirects = new(StringComparer.Ordinal);

    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inFlightByHost = new(StringComparer.OrdinalIgnoreCase);
    private int _inFlight;

    public List<string> Requested { get; } = [];

    /// <summary>The most requests that were ever being answered at once.</summary>
    public int MostAtOnce { get; private set; }

    /// <summary>The most requests to one host that were ever being answered at once.</summary>
    public int MostAtOncePerHost { get; private set; }

    public FakeCdn File(string url, string contentType, string body)
    {
        _files[url] = (contentType, Encoding.UTF8.GetBytes(body));
        return this;
    }

    public FakeCdn File(string url, string contentType, byte[] body)
    {
        _files[url] = (contentType, body);
        return this;
    }

    /// <summary>Every answer from <paramref name="host"/> takes <paramref name="delay"/>.</summary>
    public FakeCdn Slow(string host, TimeSpan delay)
    {
        _delays[host] = delay;
        return this;
    }

    public FakeCdn Redirect(string from, string to)
    {
        _redirects[from] = to;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri!.Host;
        lock (Requested)
        {
            Requested.Add(request.RequestUri.AbsoluteUri);
            MostAtOnce = Math.Max(MostAtOnce, ++_inFlight);
            _inFlightByHost[host] = _inFlightByHost.GetValueOrDefault(host) + 1;
            MostAtOncePerHost = Math.Max(MostAtOncePerHost, _inFlightByHost[host]);
        }

        try
        {
            if (_delays.TryGetValue(host, out var delay)) await Task.Delay(delay, cancellationToken);
            return Answer(request);
        }
        finally
        {
            lock (Requested)
            {
                _inFlight--;
                _inFlightByHost[host]--;
            }
        }
    }

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        var url = request.RequestUri!.AbsoluteUri;
        for (var hops = 0; _redirects.TryGetValue(url, out var next) && hops < 5; hops++) url = next;
        if (!_files.TryGetValue(url, out var file))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(file.Body),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, url),
        };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", file.ContentType);
        return response;
    }
}
