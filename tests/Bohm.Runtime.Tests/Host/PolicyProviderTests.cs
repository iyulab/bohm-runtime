using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// The model servers an administrator lists are providers of the same shape as any other
/// (<see cref="ProviderOrigin.Policy"/>): chosen, kept and checked the same way, their key where a listed
/// server's key has always been kept, and standing over a declaration under the same id.
/// </summary>
public sealed class PolicyProviderTests
{
    private const string List = """
        {"providers":{
          "GPU Server":{"baseUrl":"http://models.example:8000/v1","models":[
            {"id":"qwen3","name":"Qwen 3","contextWindow":32768,"maxTokens":8192,"reasoning":true,"input":["text","image"]},
            {"id":"small"}]},
          "gpu-server":{"baseUrl":"http://other.example/v1","models":[{"id":"phi"}]},
          "local":{"baseUrl":"http://third.example/v1","models":[{"id":"x"}]}
        }}
        """;

    private static CompanyModelList Listed(string json)
    {
        Assert.True(CompanyModelList.TryParse(json, out var list));
        return list!;
    }

    [Fact]
    public void A_listed_server_is_a_policy_declaration_of_an_openai_compatible_server_with_its_models_as_listed()
    {
        var gpu = Listed(List).ToDeclarations()[0];

        Assert.Equal(("gpu-server", "GPU Server", ProviderOrigin.Policy, ProviderApi.OpenAIChat), (gpu.Id, gpu.Name, gpu.Origin, gpu.Api));
        Assert.Equal(new Uri("http://models.example:8000/v1/"), gpu.BaseUrl);
        Assert.Equal(["qwen3", "small"], gpu.Models.Select(m => m.Id));
        var qwen = gpu.Find("qwen3")!;
        Assert.Equal(("Qwen 3", new ModelLimits(32768, 8192, true)), (qwen.Name, qwen.Limits));
        Assert.Equal(["text", "image"], qwen.Input!);
        Assert.Equal(ModelLimits.Unknown, gpu.Find("small")!.Limits);
        Assert.Null(qwen.Api);   // the provider's format, no format of its own
    }

    [Fact]
    public void A_listed_server_may_be_used_with_or_without_a_key_and_its_key_stays_where_a_listed_servers_key_is_kept()
    {
        var gpu = Listed(List).ToDeclarations()[0];

        Assert.True(gpu.Auth.TakesKey);
        Assert.False(gpu.Auth.KeyRequired);   // it is the server that asks for a key, not the list
        Assert.Equal(CompanyModel.VaultNameOf("GPU Server"), gpu.VaultName);   // under the name in the list, not the id made of it
        Assert.Equal("llm/ollama", ProviderDeclaration.VaultNameOf("ollama"));
    }

    [Fact]
    public void A_name_that_is_not_an_id_is_made_into_one_and_no_listed_server_is_left_without()
    {
        var ids = Listed(List).ToDeclarations().Select(d => d.Id).ToList();

        // "GPU Server" and "gpu-server" make the same id; "local" is reserved. Each listed server is still a provider.
        Assert.Equal(["gpu-server", "gpu-server-2", "local-2"], ids);
        Assert.All(ids, id => Assert.True(ProviderDeclaration.IsUsableId(id)));
    }

    [Theory]
    [InlineData("서버", "organization")]
    [InlineData("  Team.A / Lab  ", "team-a-lab")]
    [InlineData("a-very-long-name-that-goes-on-and-on-past-the-limit", "a-very-long-name-that-goes-on-an")]
    public void An_id_is_lowercase_letters_digits_and_dashes_within_the_limit(string name, string expected)
    {
        var list = new CompanyModelList([new CompanyServer(name, new Uri("http://models.example/v1/"), [new CompanyListedModel("m", null, ModelLimits.Unknown, null)])]);

        var id = list.ToDeclarations().Single().Id;

        Assert.Equal(expected, id);
        Assert.True(ProviderDeclaration.IsUsableId(id));
    }

    [Fact]
    public void A_server_given_the_older_way_is_one_provider_with_the_key_where_it_always_was()
    {
        var older = CompanyModelList.Of(new CompanyModelOptions(new Uri("http://models.example:8000/v1/"), "fixed-model")).ToDeclarations().Single();

        Assert.Equal(("organization", ProviderOrigin.Policy, CompanyModel.VaultName), (older.Id, older.Origin, older.VaultName));
        Assert.Equal(["fixed-model"], older.Models.Select(m => m.Id));
    }

    [Fact]
    public void The_catalog_lists_them_after_the_others_and_a_listed_server_stands_over_a_declaration_of_its_id()
    {
        var person = new ProviderDeclaration("gpu-server", "Mine", new Uri("http://mine.example/v1/"), ProviderApi.OpenAIChat, ProviderAuth.None, [], ProviderOrigin.Person);
        var listed = Listed(List).ToDeclarations();

        var merged = ProviderStore.Merge(ProviderDeclaration.BuiltIn, [person], listed);

        Assert.Equal(ProviderDeclaration.BuiltIn.Count + 3, merged.Count);
        var gpu = merged.Single(d => d.Id == "gpu-server");
        Assert.Equal((ProviderOrigin.Policy, "GPU Server"), (gpu.Origin, gpu.Name));
        Assert.All(merged.Take(ProviderDeclaration.BuiltIn.Count), d => Assert.Equal(ProviderOrigin.BuiltIn, d.Origin));
    }

    [Fact]
    public async Task A_listed_server_is_in_the_provider_list_with_its_key_connected_through_the_providers_own_route()
    {
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = Listed(List) });

        var providers = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm")).RootElement;
        var gpu = providers.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "gpu-server");
        Assert.Equal(("policy", "openai-chat", "models.example:8000"),
            (gpu.GetProperty("origin").GetString(), gpu.GetProperty("api").GetString(), gpu.GetProperty("host").GetString()));
        Assert.True(gpu.GetProperty("connected").GetBoolean());   // usable as it stands: the key is presented when connected
        Assert.True(gpu.GetProperty("keyRequired").GetBoolean());   // and there is a place for one
        Assert.True(gpu.GetProperty("keyOptional").GetBoolean());
        Assert.False(gpu.GetProperty("keyConnected").GetBoolean());
        Assert.Equal(["qwen3", "small"], gpu.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()));

        // The key connected here is the one the listed server has always used: the organization's model view sees it.
        using (var key = await host.ControlClient().PutAsync("/__control/llm/gpu-server/key", new StringContent("org-key-0123456789")))
        {
            HttpAssert.Status(HttpStatusCode.OK, key);
            Assert.True(JsonDocument.Parse(await key.Content.ReadAsStringAsync()).RootElement.GetProperty("keyConnected").GetBoolean());
        }

        var company = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/llm/company-model")).RootElement;
        var choice = company.GetProperty("choices").EnumerateArray().First(c => c.GetProperty("server").GetString() == "GPU Server");
        Assert.True(choice.GetProperty("keyConnected").GetBoolean());
    }

    [Fact]
    public async Task A_provider_declared_under_a_listed_servers_id_is_refused_and_a_listed_server_cannot_be_removed()
    {
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = Listed(List) });
        const string mine = """{ "id": "gpu-server", "baseUrl": "http://mine.example/v1", "api": "openai-chat", "auth": { "style": "none" } }""";

        using var declared = await host.ControlClient().PutAsync("/__control/llm/providers", new StringContent(mine, Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.Conflict, declared);

        using var removed = await host.ControlClient().DeleteAsync("/__control/llm/providers/gpu-server");
        HttpAssert.Status(HttpStatusCode.NotFound, removed);
    }

    [Fact]
    public async Task A_listed_server_can_be_chosen_for_proposals_and_needs_no_key_to_be()
    {
        await using var host = await RunningHost.StartAsync(configure: o => o with { CompanyModels = Listed(List) });

        using var chosen = await host.ControlClient().PutAsync("/__control/edit/model",
            new StringContent("""{ "provider": "gpu-server", "model": "qwen3" }""", Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.OK, chosen);

        var view = JsonDocument.Parse(await host.ControlClient().GetStringAsync("/__control/edit/model")).RootElement;
        Assert.Equal(("gpu-server", "qwen3"), (view.GetProperty("provider").GetString(), view.GetProperty("model").GetString()));
        Assert.True(view.GetProperty("missing").ValueKind == JsonValueKind.Null);   // a key is not what is missing
    }
}
