using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// An administrator who allows no AI provider but the servers they listed (<c>--no-remote-providers</c>): the built-in services and
/// the person's own providers drop out of the list, cannot be added or asked for models, and an application's call to one is not
/// sent with a key — a listed server answers it, or it is refused.
/// </summary>
public sealed class RemoteProvidersPolicyTests
{
    private const string List = """
        {"providers":{"GPU Server":{"baseUrl":"http://models.example:8000/v1","models":[{"id":"qwen3"}]}}}
        """;

    private const string Mine = """{ "id": "mine", "baseUrl": "http://mine.example/v1", "api": "openai-chat", "auth": { "style": "none" }, "models": [{ "id": "m" }] }""";

    private static CompanyModelList Listed()
    {
        Assert.True(CompanyModelList.TryParse(List, out var list));
        return list!;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Only_the_listed_servers_are_providers_and_one_the_person_added_before_is_neither_listed_nor_kept_as_the_answer()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-rpp-").FullName;
        var before = await RunningHost.StartAsync(dataRoot);
        using (var added = await before.ControlClient().PutAsync("/__control/llm/providers", Json(Mine)))
            HttpAssert.Status(HttpStatusCode.OK, added);
        using (var chosen = await before.ControlClient().PutAsync("/__control/llm/answer-model", Json("""{ "provider": "mine", "model": "m" }""")))
            HttpAssert.Status(HttpStatusCode.OK, chosen);
        Assert.Contains("mine", (await before.ControlClient().GetStringAsync("/__control/llm")), StringComparison.Ordinal);
        await before.StopKeepingDataAsync();

        await using var host = await RunningHost.StartAsync(dataRoot, configure: o => o with { CompanyModels = Listed(), RemoteProvidersAllowed = false });

        var providers = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm")).RootElement;
        Assert.Equal(["gpu-server"], providers.EnumerateArray().Select(p => p.GetProperty("id").GetString()));

        var answer = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/answer-model")).RootElement;
        Assert.Equal(["GPU Server"], answer.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("providerName").GetString()));

        // The person's provider is not removed from their file — it comes back if the policy is lifted.
        Assert.Contains("\"mine\"", await File.ReadAllTextAsync(Path.Combine(dataRoot, "providers.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_a_provider_asking_an_outside_address_for_models_or_connecting_a_built_in_key_is_refused()
    {
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = Listed(), RemoteProvidersAllowed = false });

        using var added = await host.ControlClient().PutAsync("/__control/llm/providers", Json(Mine));
        HttpAssert.Status(HttpStatusCode.Forbidden, added);
        Assert.Contains("remote-providers-off", await added.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var models = await host.ControlClient().PostAsync("/__control/llm/providers/models", Json("""{ "endpoint": "https://api.example.com/v1", "key": "sk-0123456789" }"""));
        HttpAssert.Status(HttpStatusCode.Forbidden, models);

        using var key = await host.ControlClient().PutAsync("/__control/llm/openai/key", new StringContent("sk-0123456789"));
        HttpAssert.Status(HttpStatusCode.NotFound, key);

    }

    [Fact]
    public async Task An_application_calling_a_provider_whose_key_is_in_the_vault_is_refused_and_nothing_is_sent()
    {
        await using var provider = await FakeProvider.StartAsync();
        var vault = new global::Bohm.Runtime.Credentials.MemoryCredentialVault();
        vault.Write(ProviderDeclaration.VaultNameOf("openai"), "sk-real-secret-0123456789");
        await using var host = await RunningHost.StartAsync(configure: o => o with
        {
            Vault = vault,
            RemoteProvidersAllowed = false,
            LlmEndpoints = new Dictionary<string, Uri> { ["api.openai.com"] = provider.Address },
        });
        var app = await host.AdoptAsync("<!doctype html><title>AI</title>");
        using var load = await host.ClientForApp(app).GetAsync("/");
        var setCookie = Assert.Single(load.Headers.GetValues("Set-Cookie"));

        var request = new HttpRequestMessage(HttpMethod.Post, "/__bohm/llm/api.openai.com/v1/chat/completions") { Content = Json("""{"model":"m"}""") };
        request.Headers.Authorization = new("Bearer", $"bohm-key-{app}");
        request.Headers.Add("Cookie", setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)]);
        request.Headers.Add("Origin", $"http://{app}.localhost:{host.Port}");
        using var response = await host.ClientForApp(app).SendAsync(request);

        HttpAssert.Status(HttpStatusCode.Forbidden, response);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal("bohm_remote_providers_off", error.GetProperty("type").GetString());
        Assert.Empty(provider.Received);

        // Not a key to connect: the application is not reported as needing one.
        var status = JsonDocument.Parse(await host.ControlClient().GetStringAsync($"/__control/apps/{app}/status")).RootElement;
        Assert.Empty(status.GetProperty("needsKey").EnumerateArray());
    }
}
