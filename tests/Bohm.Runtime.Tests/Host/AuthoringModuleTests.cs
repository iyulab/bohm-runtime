using System.Net;
using System.Text;
using System.Text.Json;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Host;
using Bohm.Runtime.Host.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bohm.Runtime.Tests.Host;

/// <summary>The runtime without the authoring layer: it serves and stores applications, and says plainly what it cannot do.</summary>
public sealed class AuthoringModuleTests
{
    [Fact]
    public async Task Without_an_authoring_layer_the_six_authoring_routes_answer_501_and_say_why()
    {
        await using var host = await RunningHost.StartAsync(build: b => b.Services.RemoveAll<IAuthoringModule>());
        var appId = await host.AdoptAsync("<!doctype html><title>t</title><p>a</p>");
        using var control = host.ControlClient();

        foreach (var route in new[]
                 {
                     "apps/proposals", "apps/interview", "apps/promotions", "previews",
                     $"apps/{appId}/proposals", $"apps/{appId}/proposals/from-html",
                 })
        {
            using var response = await control.PostAsync($"/__control/{route}", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("authoring-not-available", problem.RootElement.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task Without_an_authoring_layer_applications_are_still_served_listed_and_changed_by_hand()
    {
        await using var host = await RunningHost.StartAsync(build: b => b.Services.RemoveAll<IAuthoringModule>());
        var appId = await host.AdoptAsync("<!doctype html><title>t</title><p>a</p>");
        using var control = host.ControlClient();

        using var list = await control.GetAsync("/__control/apps");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(appId, await list.Content.ReadAsStringAsync());

        using var served = await host.ClientForApp(appId).GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public async Task A_plugged_in_layer_is_asked_for_each_route_and_its_skills_are_listed_as_built_in()
    {
        var skills = Directory.CreateTempSubdirectory("bohm-layer-skills-").FullName;
        Directory.CreateDirectory(Path.Combine(skills, "from-the-layer"));
        await File.WriteAllTextAsync(Path.Combine(skills, "from-the-layer", "SKILL.md"),
            "---\nname: from-the-layer\ndescription: A skill the authoring layer brings.\n---\nBody.\n");
        var layer = new RecordingLayer([skills]);

        await using var host = await RunningHost.StartAsync(build: b =>
        {
            b.Services.RemoveAll<IAuthoringModule>();
            b.Services.AddSingleton<IAuthoringModule>(layer);
        });
        using var control = host.ControlClient();
        var appId = await host.AdoptAsync("<!doctype html><title>t</title><p>a</p>");

        using (var response = await control.PostAsync("/__control/apps/interview", new StringContent("{}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using (var response = await control.PostAsync($"/__control/apps/{appId}/proposals", new StringContent("{}", Encoding.UTF8, "application/json")))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(["interview", $"change:{appId}"], layer.Asked);

        var entry = Assert.Single(host.Services.GetRequiredService<AppSkills>().List(), s => s.Name == "from-the-layer");
        Assert.True(entry.Builtin);
    }

    /// <summary>A layer that only records which route reached it.</summary>
    private sealed class RecordingLayer(IReadOnlyList<string> skillRoots) : IAuthoringModule
    {
        public List<string> Asked { get; } = [];

        public IReadOnlyList<string> BuiltinSkillRoots => skillRoots;

        public Task ProposeNewAsync(HttpContext context, CancellationToken cancel) => Answer("new", context);

        public Task InterviewAsync(HttpContext context, CancellationToken cancel) => Answer("interview", context);

        public Task<AdoptedApp?> PromoteAsync(HttpContext context, CancellationToken cancel)
        {
            Asked.Add("promote");
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.FromResult<AdoptedApp?>(null);
        }

        public Task PreviewNewAsync(HttpContext context, int port, CancellationToken cancel) => Answer("preview", context);

        public Task ProposeChangeAsync(HttpContext context, string appId, CancellationToken cancel) => Answer($"change:{appId}", context);

        public Task ProposeFromHtmlAsync(HttpContext context, string appId, CancellationToken cancel) => Answer($"paste:{appId}", context);

        private Task Answer(string route, HttpContext context)
        {
            Asked.Add(route);
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return Task.CompletedTask;
        }
    }
}
