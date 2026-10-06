using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FRPMonitor.Cloud;

public static class CloudCredential
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type; public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist, AttributeCount;
        public IntPtr Attributes; public string TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr pointer);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
    public static string? Read(CloudProfile profile)
    {
        if (!CredRead(profile.CredentialTarget, 1, 0, out var pointer)) return null;
        try { var c = Marshal.PtrToStructure<Credential>(pointer); return Marshal.PtrToStringUni(c.CredentialBlob, (int)c.CredentialBlobSize / 2); }
        finally { CredFree(pointer); }
    }
    public static void Save(CloudProfile profile, string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var c = new Credential { Type = 1, TargetName = profile.CredentialTarget, UserName = profile.User, Comment = "FRPMonitor cloud SSH", CredentialBlob = pointer, CredentialBlobSize = (uint)bytes.Length, Persist = 2, TargetAlias = "" };
            if (!CredWrite(ref c, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 SSH 凭据");
        }
        finally { for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(pointer, i, 0); Marshal.FreeHGlobal(pointer); Array.Clear(bytes); }
    }
}
