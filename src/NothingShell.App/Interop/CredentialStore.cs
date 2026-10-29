using System.Runtime.InteropServices;
using System.Text;

namespace NothingShell.App.Interop;

/// <summary>
/// Password nel Gestore credenziali di Windows (cifrate per l'utente, visibili in Pannello di controllo ›
/// Gestione credenziali › Credenziali di Windows). Mai su disco in chiaro, mai in <c>settings.json</c>.
/// </summary>
internal static class CredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    public static bool Write(string target, string userName, string password, string comment = "Nothing Shell: casella di posta del notch")
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var pointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, pointer, blob.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = pointer,
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };
            return CredWrite(ref credential, 0);
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(pointer);
        }
    }

    public static string? Read(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
            return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.CredentialBlobSize == 0
                ? string.Empty
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public static void Delete(string target) => CredDelete(target, CredTypeGeneric, 0);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
