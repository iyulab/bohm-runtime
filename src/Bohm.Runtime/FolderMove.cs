namespace Bohm.Runtime;

/// <summary>
/// Moves a folder into place, waiting out a moment when another program holds it. On Windows a folder just written can
/// be held for a moment by a virus scanner or the search indexer, and moving it then fails with «access denied» though
/// nothing is wrong — seen twice in the runtime's own tests while taking an application in. Every move that puts a folder
/// the runtime has just written into place (taking an application in, a revision, a table import or export, setting one aside)
/// goes through here.
/// </summary>
public static class FolderMove
{
    /// <summary>Waits before the next try: 50 ms, then double, five tries in all (750 ms of waiting at most).</summary>
    private static readonly TimeSpan[] Waits = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)];

    /// <summary>
    /// <see cref="Directory.Move"/>, tried again after a short wait when the move is refused while both ends still look as
    /// they should — the source is there and nothing is at the destination yet. A missing source, or something already at
    /// the destination, fails at once: waiting would not change it.
    /// </summary>
    public static void Move(string from, string to) => Move(from, to, Thread.Sleep);

    internal static void Move(string from, string to, Action<TimeSpan> wait)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception e) when (attempt < Waits.Length && Held(e) && Directory.Exists(from) && !Directory.Exists(to) && !File.Exists(to))
            {
                wait(Waits[attempt]);
            }
        }
    }

    private static bool Held(Exception e) => e is UnauthorizedAccessException || e is IOException && e is not DirectoryNotFoundException and not PathTooLongException;
}
