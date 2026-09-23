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

    public List<string> Requested { get; } = [];

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

    public FakeCdn Redirect(string from, string to)
    {
        _redirects[from] = to;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        lock (Requested) Requested.Add(url);
        for (var hops = 0; _redirects.TryGetValue(url, out var next) && hops < 5; hops++) url = next;
        if (!_files.TryGetValue(url, out var file))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(file.Body),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, url),
        };
        response.Content.Headers.TryAddWithoutValidation("Content-Type", file.ContentType);
        return Task.FromResult(response);
    }
}
