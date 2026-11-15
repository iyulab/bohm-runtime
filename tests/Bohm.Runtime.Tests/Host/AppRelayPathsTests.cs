using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>An application relays only AI requests with the person's key — not requests that act on their account.</summary>
public sealed class AppRelayPathsTests
{
    private static LlmProvider P(string id) => LlmProviders.ById(id)!;

    [Theory]
    [InlineData("openai", "POST", "v1/chat/completions")]
    [InlineData("openai", "POST", "/v1/chat/completions/")]
    [InlineData("openai", "POST", "v1/responses")]
    [InlineData("openai", "POST", "v1/embeddings")]
    [InlineData("openai", "POST", "v1/audio/transcriptions")]
    [InlineData("openai", "POST", "v1/audio/speech")]
    [InlineData("openai", "POST", "v1/images/generations")]
    [InlineData("openai", "GET", "v1/models")]
    [InlineData("openai", "GET", "v1/models/gpt-4o-mini")]
    [InlineData("anthropic", "POST", "v1/messages")]
    [InlineData("anthropic", "POST", "v1/messages/count_tokens")]
    [InlineData("anthropic", "GET", "v1/models")]
    [InlineData("google", "POST", "v1beta/models/gemini-2.0-flash:generateContent")]
    [InlineData("google", "POST", "v1beta/models/gemini-2.0-flash:streamGenerateContent")]
    [InlineData("google", "POST", "v1/models/text-embedding-004:embedContent")]
    [InlineData("google", "GET", "v1beta/models")]
    [InlineData("google", "POST", "v1beta/openai/chat/completions")]
    [InlineData("groq", "POST", "openai/v1/chat/completions")]
    [InlineData("openrouter", "POST", "api/v1/chat/completions")]
    [InlineData("openrouter", "GET", "api/v1/models")]
    [InlineData("mistral", "POST", "v1/embeddings")]
    public void Using_ai_is_relayed(string provider, string method, string path) =>
        Assert.True(AppRelayPaths.Allows(P(provider), method, path));

    [Theory]
    [InlineData("openai", "GET", "v1/files")]
    [InlineData("openai", "POST", "v1/files")]
    [InlineData("openai", "DELETE", "v1/files/file-abc")]
    [InlineData("openai", "POST", "v1/batches")]
    [InlineData("openai", "POST", "v1/fine_tuning/jobs")]
    [InlineData("openai", "POST", "v1/assistants")]
    [InlineData("openai", "POST", "v1/vector_stores")]
    [InlineData("openai", "DELETE", "v1/models/ft:gpt-4o:me")]
    [InlineData("openai", "GET", "v1/organization/usage/completions")]
    [InlineData("openai", "POST", "v1/chat/../files")]
    [InlineData("openai", "POST", "v1/chat%2Fcompletions")]
    [InlineData("openai", "GET", "v1/chat/completions")]
    [InlineData("anthropic", "POST", "v1/messages/batches")]
    [InlineData("anthropic", "POST", "v1/files")]
    [InlineData("anthropic", "GET", "v")]
    [InlineData("google", "POST", "v1beta/files")]
    [InlineData("google", "POST", "v1beta/cachedContents")]
    [InlineData("google", "POST", "v1beta/tunedModels")]
    [InlineData("groq", "POST", "openai/v1/files")]
    [InlineData("groq", "POST", "openai/v1/audio/speech")]
    [InlineData("mistral", "POST", "v1/fine_tuning/jobs")]
    [InlineData("mistral", "POST", "")]
    public void Acting_on_the_account_is_not(string provider, string method, string path) =>
        Assert.False(AppRelayPaths.Allows(P(provider), method, path));

    [Fact]
    public async Task A_refused_request_is_answered_in_the_providers_shape_and_never_sent()
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

        var request = new HttpRequestMessage(HttpMethod.Delete, "/__bohm/llm/api.openai.com/v1/files/file-abc");
        request.Headers.Authorization = new("Bearer", $"bohm-key-{app}");
        request.Headers.Add("Cookie", setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)]);
        request.Headers.Add("Origin", $"http://{app}.localhost:{host.Port}");
        using var response = await host.ClientForApp(app).SendAsync(request);

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
        Assert.Equal("bohm_path_not_allowed", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Empty(provider.Received);
    }
}
