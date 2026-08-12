using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WAFlow.Core.Infrastructure;

public interface ISecretStore
{
    void Save(string secret);
    string? Read();
}

public sealed class WindowsCredentialStore : ISecretStore
{
    public const string ActiveAiProviderTarget = "WAFlow/AiProvider/active";
    public const string LegacyAiProviderTarget = "WAFlow/DeepSeekApiKey";
    private const uint CredTypeGeneric = 1;
    private const uint PersistLocalMachine = 2;
    private readonly string _target;

    public WindowsCredentialStore(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("凭据目标不能为空。", nameof(target));
        _target = target.Trim();
    }

    public void Save(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return;
        var bytes = Encoding.Unicode.GetBytes(secret);
        var pointer = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric, TargetName = _target, CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = pointer, Persist = PersistLocalMachine, UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入 Windows 凭据管理器。");
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(pointer, index, 0);
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    public string? Read()
    {
        if (!CredRead(_target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error, "无法读取 Windows 凭据管理器。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return credential.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally { CredFree(pointer); }
    }

    public bool Exists()
    {
        if (!CredRead(_target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return false;
            throw new Win32Exception(error, "无法检查 Windows 凭据管理器。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return credential.CredentialBlobSize > 0;
        }
        finally { CredFree(pointer); }
    }

    public void Delete()
    {
        if (CredDelete(_target, CredTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != 1168) throw new Win32Exception(error, "无法删除 Windows 凭据管理器中的密钥。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags; public uint Type; public string TargetName; public string? Comment; public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize; public IntPtr CredentialBlob; public uint Persist; public uint AttributeCount; public IntPtr Attributes;
        public string? TargetAlias; public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);
}
