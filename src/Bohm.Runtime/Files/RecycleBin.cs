using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bohm.Runtime.Files;

/// <summary>
/// Sends a folder to the Windows recycle bin, where the person can still restore it with the
/// operating system's own tools.
/// </summary>
/// <remarks>
/// On a drive that has no recycle bin (a network share, for instance) Windows deletes instead —
/// the same thing Explorer does after its warning, which this cannot show from a background process.
/// </remarks>
public static partial class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>Whether this operating system has a recycle bin to send to.</summary>
    public static bool Available => OperatingSystem.IsWindows();

    /// <exception cref="PlatformNotSupportedException">Not on Windows.</exception>
    /// <exception cref="IOException">The folder could not be sent.</exception>
    public static void Send(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The recycle bin exists only on Windows.");

        // The list of paths ends with two null characters.
        var from = Marshal.StringToHGlobalUni(Path.GetFullPath(folder) + "\0\0");
        try
        {
            var operation = new FileOperation
            {
                Function = FoDelete,
                From = from,
                Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi),
            };
            var result = SHFileOperation(ref operation);
            if (result != 0) throw new IOException($"The folder could not be sent to the recycle bin (shell error 0x{result:x}).", new Win32Exception(result));
            if (operation.AnyOperationsAborted != 0) throw new IOException("Sending the folder to the recycle bin was aborted.");
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref FileOperation operation);
}
