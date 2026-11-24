using System.Runtime.InteropServices;
using System.Text;

namespace VsAgentic.VSExtension.Options;

/// <summary>
/// Keeps a secret in Windows Credential Manager, under the current user. Used for
/// speech-service keys, which must not sit in the Options registry hive.
/// </summary>
internal static class CredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    public static string Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return "";
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return "";
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally { CredFree(ptr); }
    }

    public static void Write(string target, string secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            CredDelete(target, CRED_TYPE_GENERIC, 0);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(secret);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "VsAgentic",
            };
            if (!CredWrite(ref cred, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
