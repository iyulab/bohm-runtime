using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Host.Control;

namespace Bohm.Runtime.Tests.Host;

/// <summary>
/// What wrote an application's code is kept with each revision when an AI model did, and travels with the
/// application — so whoever receives it can tell it was made with AI, and with which model.
/// </summary>
public sealed class GeneratedByTests : IAsyncLifetime
{
    private RunningHost _host = null!;
    private string _out = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await RunningHost.StartAsync(configure: o => o with { FetchAssetsOnAdoption = false });
        _out = Directory.CreateTempSubdirectory("bohm-generated-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        Directory.Delete(_out, recursive: true);
    }



    [Fact]
    public async Task A_file_a_person_brought_in_says_nothing_about_a_model()
    {
        using var adopted = await _host.ControlClient().PostAsync("/__control/apps", new StringContent("<!doctype html><title>Mine</title>", Encoding.UTF8, "text/html"));
        HttpAssert.Status(HttpStatusCode.Created, adopted);
        var view = JsonDocument.Parse(await adopted.Content.ReadAsStringAsync()).RootElement;
        Assert.True(!view.TryGetProperty("generatedBy", out var by) || by.ValueKind == JsonValueKind.Null);
    }


    [Fact]
    public async Task A_model_name_is_kept_trimmed_and_bounded_and_a_blank_one_is_none()
    {
        var root = Directory.CreateTempSubdirectory("bohm-generated-catalog-").FullName;
        try
        {
            var catalog = new AdoptionCatalog(root);
            var named = await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>a</p>"), generatedBy: "  local/model.gguf  ");
            Assert.Equal("local/model.gguf", (await new AdoptionCatalog(root).GetAsync(named.Id))!.Source.GeneratedBy); // survives a restart
            var blank = await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>b</p>"), generatedBy: "   ");
            Assert.Null(blank.Source.GeneratedBy);
            var longName = await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>c</p>"), generatedBy: new string('m', 500));
            Assert.Equal(AdoptionCatalog.MaxModelNameLength, longName.Source.GeneratedBy!.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<JsonElement> PromoteAsync(string html, string model)
    {
        var body = JsonSerializer.Serialize(new { html, title = "Loans", generatedBy = model });
        using var promoted = await _host.ControlClient().PostAsync("/__control/apps/promotions", new StringContent(body, Encoding.UTF8, "application/json"));
        HttpAssert.Status(HttpStatusCode.Created, promoted);
        return JsonDocument.Parse(await promoted.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task<HttpResponseMessage> ReviseAsync(string id, string html, string? model)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/__control/apps/{id}/revisions") { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        if (model is not null) request.Headers.Add(ControlPlane.ModelHeader, Uri.EscapeDataString(model));
        return _host.ControlClient().SendAsync(request);
    }

    private async Task<List<JsonElement>> RevisionsAsync(string id)
    {
        using var listed = await _host.ControlClient().GetAsync($"/__control/apps/{id}/revisions");
        HttpAssert.Status(HttpStatusCode.OK, listed);
        return [.. JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.Clone().EnumerateArray()];
    }

    private async Task<JsonElement> ManifestAsync(string id)
    {
        var target = Path.Combine(_out, id + ".bohm");
        using (var packed = await _host.ControlClient().PostAsync($"/__control/apps/{id}/package?data=none", new StringContent(target, Encoding.UTF8))) HttpAssert.Status(HttpStatusCode.OK, packed);
        using var zip = ZipFile.OpenRead(target);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        return JsonDocument.Parse(stream).RootElement.Clone();
    }
}
