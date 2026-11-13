using Bohm.Runtime;

namespace Bohm.Runtime.Tests.Adoption;

public sealed class FolderMoveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bohm-folder-move-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_folder_moves_into_place()
    {
        var from = Directory.CreateDirectory(Path.Combine(_root, "staging")).FullName;
        File.WriteAllText(Path.Combine(from, "app.html"), "<p>x</p>");
        var to = Path.Combine(_root, "app");

        FolderMove.Move(from, to);

        Assert.False(Directory.Exists(from));
        Assert.Equal("<p>x</p>", File.ReadAllText(Path.Combine(to, "app.html")));
    }

    [Fact]
    public void A_missing_source_or_a_taken_destination_fails_at_once_without_waiting()
    {
        var waited = new List<TimeSpan>();
        Assert.Throws<DirectoryNotFoundException>(() => FolderMove.Move(Path.Combine(_root, "none"), Path.Combine(_root, "x"), waited.Add));
        var from = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var taken = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        Assert.ThrowsAny<IOException>(() => FolderMove.Move(from, taken, waited.Add));
        Assert.Empty(waited);
    }
}
