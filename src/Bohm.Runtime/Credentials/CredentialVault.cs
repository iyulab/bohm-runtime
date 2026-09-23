using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Bohm.Runtime.Credentials;

/// <summary>
/// Where the runtime keeps secrets such as AI provider keys. Secrets live here and nowhere else —
/// never in an application's folder — so handing someone an application's folder never hands
/// them a key. Applications only ever see a placeholder.
/// </summary>
public interface ICredentialVault
{
    /// <summary>The secret stored under <paramref name="name"/>, or <see langword="null"/>.</summary>
    string? Read(string name);

    /// <summary>Stores <paramref name="secret"/> under <paramref name="name"/>, replacing any previous one.</summary>
    void Write(string name, string secret);

    /// <summary>Removes the secret stored under <paramref name="name"/>, if any.</summary>
    void Delete(string name);
}

/// <summary>A vault held in memory only — for tests and for deployments that inject secrets at start.</summary>
public sealed class MemoryCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public string? Read(string name)
    {
        lock (_lock) return _secrets.GetValueOrDefault(name);
    }

    /// <inheritdoc />
    public void Write(string name, string secret)
    {
        lock (_lock) _secrets[name] = secret;
    }

    /// <inheritdoc />
    public void Delete(string name)
    {
        lock (_lock) _secrets.Remove(name);
    }
}

/// <summary>
/// The Windows Credential Manager, scoped to the signed-in user. Entries are generic credentials
/// named <c>&lt;prefix&gt;/&lt;name&gt;</c>, visible to the person in the Credential Manager control panel.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialVault(string prefix = "Bohm") : ICredentialVault
{
    private const int GenericCredential = 1;
    private const int PersistLocalMachine = 2;
    private const int NotFound = 1168;

    /// <inheritdoc />
    public string? Read(string name)
    {
        if (!CredRead(Target(name), GenericCredential, 0, out var handle))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);
            return credential.CredentialBlobSize == 0
                ? ""
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <inheritdoc />
    public void Write(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var blob = Encoding.Unicode.GetBytes(secret);
        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        var targetPointer = Marshal.StringToHGlobalUni(Target(name));
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Marshal.FreeHGlobal(targetPointer);
            // Overwrite the plain copy before releasing it.
            Marshal.Copy(new byte[blob.Length], 0, blobPointer, blob.Length);
            Marshal.FreeHGlobal(blobPointer);
            Array.Clear(blob);
        }
    }

    /// <inheritdoc />
    public void Delete(string name)
    {
        if (CredDelete(Target(name), GenericCredential, 0)) return;
        var error = Marshal.GetLastPInvokeError();
        if (error != NotFound) throw new Win32Exception(error);
    }

    private string Target(string name) => $"{prefix}/{name}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
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

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
