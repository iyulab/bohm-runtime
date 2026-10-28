using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Host.Llm;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// Which model proposes changes: the model on this computer by default, or a connected provider's
/// model the person chose — reached at the provider's OpenAI-compatible base with its key, and
/// counted as the application's data sent.
/// </summary>
public sealed class EditModelTests : IAsyncLifetime
{
    private const string App = """<!doctype html><title>Tasks</title><button onclick="add()">Add Task</button>""";
    private const string Key = "sk-real-secret-0123456789";

    private FakeProvider _provider = null!;
    private RunningHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _provider = await FakeProvider.StartAsync();
        _host = await RunningHost.StartAsync(configure: o => o with
        {
            LlmEndpoints = LlmProviders.All.ToDictionary(p => p.Host, _ => _provider.Address),
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _provider.DisposeAsync();
    }











    [Theory]
    [InlineData("""{"provider":"nowhere","model":"m"}""")]
    [InlineData("""{"provider":"openai","model":"  "}""")]
    [InlineData("""{"provider":"openai"}""")]
    [InlineData("not json")]
    public async Task A_choice_that_is_not_a_known_provider_and_a_model_name_is_refused(string body)
    {
        using var response = await _host.ControlClient().PutAsync("/__control/edit/model", new StringContent(body, Encoding.UTF8, "application/json"));

        HttpAssert.Status(HttpStatusCode.BadRequest, response);
        Assert.Equal(JsonValueKind.Null, (await GetModelAsync()).GetProperty("provider").ValueKind);
    }

    [Fact]
    public async Task A_choice_is_remembered_across_launches_and_can_be_taken_back()
    {
        var dataRoot = Directory.CreateTempSubdirectory("bohm-edit-model-").FullName;
        try
        {
            var first = await RunningHost.StartAsync(dataRoot);
            using (var chose = await first.ControlClient().PutAsync("/__control/edit/model",
                new StringContent("""{"provider":"groq","model":"llama-x"}""", Encoding.UTF8, "application/json"))) HttpAssert.Status(HttpStatusCode.OK, chose);
            await first.StopKeepingDataAsync();

            await using var second = await RunningHost.StartAsync(dataRoot);
            var remembered = JsonDocument.Parse(await second.ControlClient().GetStringAsync("/__control/edit/model")).RootElement;
            Assert.Equal("groq", remembered.GetProperty("provider").GetString());
            Assert.Equal("llama-x", remembered.GetProperty("model").GetString());

            using var back = await second.ControlClient().DeleteAsync("/__control/edit/model");
            HttpAssert.Status(HttpStatusCode.OK, back);
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(await back.Content.ReadAsStringAsync()).RootElement.GetProperty("provider").ValueKind);
            Assert.False(File.Exists(Path.Combine(dataRoot, "edit-model.json")));
        }
        finally
        {
            if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
        }
    }



    private async Task<JsonElement> GetModelAsync() => JsonDocument.Parse(await _host.ControlClient().GetStringAsync("/__control/edit/model")).RootElement;

    private Task<HttpResponseMessage> ChooseAsync(string provider, string model) =>
        _host.ControlClient().PutAsync("/__control/edit/model", new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, string> { ["provider"] = provider, ["model"] = model }), Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> ProposeAsync(string id) =>
        _host.ControlClient().PostAsync($"/__control/apps/{id}/proposals", new StringContent(
            """{"instruction":"Change the text to Save","target":{"html":"<button onclick=\"add()\">Add Task</button>","text":"Add Task"}}""",
            Encoding.UTF8, "application/json"));
}
