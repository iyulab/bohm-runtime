using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>A relayed request that would have the provider fetch an address itself is refused; data given inline passes.</summary>
public sealed class AppRelayFetchesTests
{
    private static string? Find(string json) => AppRelayFetches.Find(Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"https://evil.example/x.png?d=secret"}}]}]}""")]
    [InlineData("""{"model":"m","input":[{"role":"user","content":[{"type":"input_image","image_url":"http://evil.example/a"}]}]}""")]
    [InlineData("""{"model":"m","input":[{"role":"user","content":[{"type":"input_file","file_url":"https://evil.example/f.pdf"}]}]}""")]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":[{"type":"image","source":{"type":"url","url":"https://evil.example/i.png"}}]}]}""")]
    [InlineData("""{"contents":[{"parts":[{"file_data":{"mime_type":"image/png","file_uri":"https://evil.example/i.png"}}]}]}""")]
    [InlineData("""{"contents":[{"parts":[{"fileData":{"mimeType":"image/png","fileUri":"https://evil.example/i.png"}}]}]}""")]
    [InlineData("""{"model":"m","tools":[{"type":"web_search_preview"}],"input":"hi"}""")]
    [InlineData("""{"model":"m","tools":[{"type":"web_fetch_20250910","name":"web_fetch"}],"messages":[]}""")]
    [InlineData("""{"model":"m","tools":[{"type":"mcp","server_label":"x","server_url":"https://evil.example/mcp"}],"input":"hi"}""")]
    [InlineData("""{"contents":[],"tools":[{"google_search":{}}]}""")]
    [InlineData("""{"contents":[],"tools":[{"urlContext":{}}]}""")]
    public void A_request_the_provider_would_fetch_for_is_found(string json) => Assert.NotNull(Find(json));

    [Theory]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}]}]}""")]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":"look at https://example.com and tell me"}]}""")]
    [InlineData("""{"model":"m","messages":[],"tools":[{"type":"function","function":{"name":"f","parameters":{"type":"object","properties":{"url":{"type":"string"}}}}}]}""")]
    [InlineData("""{"contents":[{"parts":[{"inline_data":{"mime_type":"image/png","data":"AAAA"}}]}]}""")]
    [InlineData("""not json at all""")]
    public void Inline_data_text_and_function_tools_pass(string json) => Assert.Null(Find(json));

    [Fact]
    public async Task A_refused_request_is_never_sent_and_an_inline_one_is_sent_as_written()
    {
        await using var provider = await FakeProvider.StartAsync();
        var vault = new global::Bohm.Runtime.Credentials.MemoryCredentialVault();
        vault.Write(ProviderDeclaration.VaultNameOf("openai"), "sk-real-secret-0123456789");
        await using var host = await RunningHost.StartAsync(configure: o => o with
        {
            Vault = vault,
            LlmEndpoints = new Dictionary<string, Uri> { ["api.openai.com"] = provider.Address },
        });
        var app = await host.AdoptAsync("<!doctype html><title>AI</title>");
        using var load = await host.ClientForApp(app).GetAsync("/");
        var setCookie = Assert.Single(load.Headers.GetValues("Set-Cookie"));
        async Task<HttpResponseMessage> Send(string body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new("Bearer", $"bohm-key-{app}");
            request.Headers.Add("Cookie", setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)]);
            request.Headers.Add("Origin", $"http://{app}.localhost:{host.Port}");
            return await host.ClientForApp(app).SendAsync(request);
        }

        using (var refused = await Send("""{"model":"m","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"https://evil.example/x?d=1"}}]}]}"""))
        {
            HttpAssert.Status(HttpStatusCode.Forbidden, refused);
            Assert.Equal("bohm_fetch_not_allowed", JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("type").GetString());
        }

        Assert.Empty(provider.Received);

        const string inline = """{"model":"m","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}]}]}""";
        using (var sent = await Send(inline))
            HttpAssert.Status(HttpStatusCode.OK, sent);
        var received = Assert.Single(provider.Received);
        Assert.Equal(inline, received.Body);
        Assert.StartsWith("application/json", received.Headers["Content-Type"], StringComparison.Ordinal);
    }
}
