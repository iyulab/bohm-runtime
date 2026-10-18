using System.ComponentModel;
using System.Runtime.InteropServices;
using Bohm.Runtime.Host.Credentials;

namespace Bohm.Runtime.Tests.Credentials;

public sealed class WindowsCredentialVaultTests
{
    [Fact]
    public void A_secret_round_trips_through_the_windows_credential_manager_and_can_be_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows Credential Manager exists only on Windows.");
            return;
        }

        // A unique service keeps the test away from any real entry.
        var vault = new WindowsCredentialVault("BohmTest-" + Guid.NewGuid().ToString("n"));
        try
        {
            Assert.Null(vault.Read("llm/openai"));

            vault.Write("llm/openai", "sk-테스트-🔑");
            Assert.Equal("sk-테스트-🔑", vault.Read("llm/openai"));

            vault.Write("llm/openai", "replaced");
            Assert.Equal("replaced", vault.Read("llm/openai"));
        }
        finally
        {
            vault.Delete("llm/openai");
        }

        Assert.Null(vault.Read("llm/openai"));
        vault.Delete("llm/openai"); // Deleting what is absent is not an error.
    }

    [Fact]
    public void A_key_kept_by_an_earlier_version_is_read_once_moved_to_the_shared_entry_and_its_old_entry_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows Credential Manager exists only on Windows.");
            return;
        }

        var service = "BohmTest-" + Guid.NewGuid().ToString("n");
        var vault = new WindowsCredentialVault(service);
        var shared = new TauriKit.Credentials.Credentials(service, TauriKit.Credentials.BuildKind.Release);
        WriteOldEntry($"{service}/llm/anthropic", "sk-ant-예전");
        try
        {
            Assert.Null(shared.Get("llm/anthropic"));
            Assert.Equal("sk-ant-예전", vault.Read("llm/anthropic"));
            Assert.Equal("sk-ant-예전", shared.Get("llm/anthropic")); // the entry a tauri-kit shell reads too
            Assert.Null(WindowsCredentialVault.Legacy.Read($"{service}/llm/anthropic"));
            Assert.Equal("sk-ant-예전", vault.Read("llm/anthropic"));

            // Deleting removes an old entry too, so a key the person removed never comes back from it.
            WriteOldEntry($"{service}/llm/openai", "sk-stale");
            vault.Delete("llm/openai");
            Assert.Null(vault.Read("llm/openai"));
        }
        finally
        {
            vault.Delete("llm/anthropic");
            vault.Delete("llm/openai");
        }
    }

    private static void WriteOldEntry(string target, string secret)
    {
        var blob = System.Text.Encoding.Unicode.GetBytes(secret);
        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        var targetPointer = Marshal.StringToHGlobalUni(target);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new OldCredential { Type = 1, TargetName = targetPointer, CredentialBlobSize = (uint)blob.Length, CredentialBlob = blobPointer, Persist = 2 };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(targetPointer);
            Marshal.FreeHGlobal(blobPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OldCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref OldCredential credential, int flags);
}
