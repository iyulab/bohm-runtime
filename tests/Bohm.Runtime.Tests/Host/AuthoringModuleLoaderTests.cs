using Bohm.Runtime.Adoption;
using Bohm.Runtime.Host.Extensions;
using Microsoft.AspNetCore.Http;

namespace Bohm.Runtime.Tests.Host;

/// <summary>Finding the authoring layer beside the executable: found by one fixed name, left out when it cannot be used.</summary>
public sealed class AuthoringModuleLoaderTests
{
    [Fact]
    public void No_file_means_no_layer()
    {
        Assert.Null(AuthoringModuleLoader.Load(Path.Combine(Path.GetTempPath(), "bohm-no-such-layer", AuthoringModuleLoader.FileName)));
    }

    [Fact]
    public void A_file_that_is_not_an_assembly_is_left_out_and_said_so()
    {
        var dir = Directory.CreateTempSubdirectory("bohm-layer-").FullName;
        var path = Path.Combine(dir, AuthoringModuleLoader.FileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var said = new List<string>();

        Assert.Null(AuthoringModuleLoader.Load(path, said.Add));
        Assert.Single(said);
        Assert.Contains("left out", said[0]);
    }

    [Fact]
    public void An_assembly_with_no_layer_in_it_gives_none()
    {
        Assert.Null(AuthoringModuleLoader.FromAssembly(typeof(AdoptedApp).Assembly));
    }

    [Fact]
    public void The_one_class_that_implements_the_layer_and_takes_no_arguments_is_made()
    {
        var found = AuthoringModuleLoader.FromAssembly(typeof(AuthoringModuleLoaderTests).Assembly);
        Assert.IsType<TheOneLayer>(found);
    }

    /// <summary>The only layer in this assembly that can be made with no arguments (the loader takes exactly one).</summary>
    public sealed class TheOneLayer : IAuthoringModule
    {
        public IReadOnlyList<string> BuiltinSkillRoots => [];

        public Task ProposeNewAsync(HttpContext context, CancellationToken cancel) => Task.CompletedTask;

        public Task InterviewAsync(HttpContext context, CancellationToken cancel) => Task.CompletedTask;

        public Task<AdoptedApp?> PromoteAsync(HttpContext context, CancellationToken cancel) => Task.FromResult<AdoptedApp?>(null);

        public Task PreviewNewAsync(HttpContext context, int port, CancellationToken cancel) => Task.CompletedTask;

        public Task ProposeChangeAsync(HttpContext context, string appId, CancellationToken cancel) => Task.CompletedTask;

        public Task ProposeFromHtmlAsync(HttpContext context, string appId, CancellationToken cancel) => Task.CompletedTask;
    }
}
