using System.Text;
using Bohm.Runtime.Adoption;
using Bohm.Runtime.Storage;
using LocalOrigin.Storage;

namespace Bohm.Runtime.Tests.Storage;

/// <summary>
/// An application's data on disk keeps the format earlier versions of the runtime wrote and read, so
/// neither an update nor going back to an earlier version leaves data unreadable.
/// </summary>
public sealed class AppStorageFormatTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bohm-format-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Snapshots_of_application_data_carry_the_runtimes_own_format()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>x</p>"), "x.html");
        await using (var storage = await catalog.OpenStorageAsync(app.Id, new KeyValueStoreOptions { CheckpointEvery = 1 }))
            await storage.ApplyAsync([KeyValueOperation.Set("k", "v")]);

        var snapshot = Directory.GetFiles(_root, "local.json", SearchOption.AllDirectories).Single();
        Assert.Contains("\"format\": \"bohm.storage/0\"", await File.ReadAllTextAsync(snapshot), StringComparison.Ordinal);
        Assert.Equal(AppStorageFormat.Format, AppStorageFormat.Options(new KeyValueStoreOptions { Format = "other/1" }).Format);
    }

    [Fact]
    public async Task Data_an_earlier_runtime_wrote_is_read()
    {
        var catalog = new AdoptionCatalog(_root);
        var app = await catalog.AdoptAsync(Encoding.UTF8.GetBytes("<p>x</p>"), "x.html");
        await using (await catalog.OpenStorageAsync(app.Id)) { }
        var directory = Path.GetDirectoryName(Directory.GetFiles(_root, "journal.ndjson", SearchOption.AllDirectories).Single())!;
        await File.WriteAllTextAsync(Path.Combine(directory, "local.json"), "{\n  \"format\": \"bohm.storage/0\",\n  \"seq\": 4,\n  \"items\": {\n    \"loans\": \"[1]\"\n  }\n}\n");

        await using var storage = await catalog.OpenStorageAsync(app.Id);

        Assert.Equal("[1]", storage.GetItems()["loans"]);
        Assert.Equal(4, storage.Sequence);
        Assert.Empty(storage.Recovery);
    }
}
