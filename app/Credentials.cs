using System.Runtime.InteropServices;
using System.Text;

namespace RemoteRooms;

internal static class Credentials
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags, Type;
        public IntPtr TargetName, Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes, TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    private static string Target(string id) => "RemoteRooms:" + id;
    public static void Save(string id, string username, string password)
    {
        var blob = Encoding.UTF8.GetBytes(password);
        var c = new Credential
        {
            Type = 1,
            Persist = 2,
            TargetName = Marshal.StringToHGlobalUni(Target(id)),
            UserName = Marshal.StringToHGlobalUni(username),
            CredentialBlobSize = blob.Length,
            CredentialBlob = Marshal.AllocHGlobal(blob.Length)
        };
        try
        {
            Marshal.Copy(blob, 0, c.CredentialBlob, blob.Length);
            if (!CredWrite(ref c, 0)) throw new InvalidOperationException("Не вдалося зберегти пароль у Windows Credential Manager.");
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(c.TargetName);
            Marshal.FreeHGlobal(c.UserName);
            Marshal.FreeHGlobal(c.CredentialBlob);
        }
    }
    public static string? Load(string id)
    {
        if (!CredRead(Target(id), 1, 0, out var ptr)) return null;
        try
        {
            var c = Marshal.PtrToStructure<Credential>(ptr);
            var bytes = new byte[c.CredentialBlobSize];
            Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(ptr); }
    }
    public static void Delete(string id) => CredDelete(Target(id), 1, 0);
}
