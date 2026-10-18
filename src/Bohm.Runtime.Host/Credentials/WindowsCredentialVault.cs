using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bohm.Runtime.Credentials;
using TauriKit.Credentials;

namespace Bohm.Runtime.Host.Credentials;

/// <summary>
/// The Windows Credential Manager, scoped to the signed-in user, through TauriKit.Credentials: one generic credential
/// per secret, named <c>&lt;name&gt;.&lt;service&gt;</c> and visible to the person in the Credential Manager control
/// panel — the entries a Tauri shell built on tauri-kit-credentials reads and writes too. A verification run passes
/// its own <paramref name="service"/> so it never touches the person's keys.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialVault(string service = "Bohm") : ICredentialVault
{
    private readonly TauriKit.Credentials.Credentials _entries = new(service, BuildKind.Release);

    /// <inheritdoc />
    public string? Read(string name)
    {
        if (_entries.Get(name) is { } secret) return secret;
        // TODO(remove after 2027-01-01): versions up to 0.13.9 kept a secret as «<service>/<name>». Moved the first time
        // it is read, so a person's keys survive the update; removing this reader early loses keys of anyone who skips it.
        if (Legacy.Read($"{service}/{name}") is not { } old) return null;
        _entries.Set(name, old);
        Legacy.Delete($"{service}/{name}");
        return old;
    }

    /// <inheritdoc />
    public void Write(string name, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        _entries.Set(name, secret);
        Legacy.Delete($"{service}/{name}");
    }

    /// <inheritdoc />
    public void Delete(string name)
    {
        _entries.Delete(name);
        Legacy.Delete($"{service}/{name}");
    }

    /// <summary>The entries earlier versions wrote — read once and removed, never written.</summary>
    internal static class Legacy
    {
        private const int GenericCredential = 1;
        private const int NotFound = 1168;

        public static string? Read(string target)
        {
            if (!CredRead(target, GenericCredential, 0, out var handle))
            {
                var error = Marshal.GetLastWin32Error();
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

        public static void Delete(string target)
        {
            if (CredDelete(target, GenericCredential, 0)) return;
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw new Win32Exception(error);
        }

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

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);
    }
}
