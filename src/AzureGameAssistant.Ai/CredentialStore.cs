using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AzureGameAssistant.Ai;

public interface IAiCredentialStore
{
    bool TryRead(string providerId, out string key);

    bool Save(string providerId, string key);

    bool Delete(string providerId);

    bool Exists(string providerId);
}

/// <summary>
/// Windows Credential Manager storage for provider API keys. Keys never touch
/// settings.json, logs or session files.
/// </summary>
public sealed class CredentialStore : IAiCredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxBlobBytes = 2560;

    public static CredentialStore Shared { get; } = new();

    public static string TargetName(string providerId) =>
        "AzureGameAssistant/Ai/" + SanitizeId(providerId);

    public bool TryRead(string providerId, out string key)
    {
        key = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (!CredReadW(TargetName(providerId), CredTypeGeneric, 0, out IntPtr handle))
        {
            return false;
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
            {
                return false;
            }

            key = Marshal.PtrToStringUni(credential.CredentialBlob, credential.CredentialBlobSize / 2) ?? string.Empty;
            return key.Length > 0;
        }
        finally
        {
            CredFree(handle);
        }
    }

    public bool Exists(string providerId) =>
        TryRead(providerId, out _);

    public bool Save(string providerId, string key)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        byte[] blob = Encoding.Unicode.GetBytes(key.Trim());
        if (blob.Length == 0 || blob.Length > MaxBlobBytes)
        {
            return false;
        }

        IntPtr target = Marshal.StringToCoTaskMemUni(TargetName(providerId));
        IntPtr user = Marshal.StringToCoTaskMemUni(Environment.UserName);
        IntPtr data = Marshal.AllocCoTaskMem(blob.Length);

        try
        {
            Marshal.Copy(blob, 0, data, blob.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlob = data,
                CredentialBlobSize = blob.Length,
                Persist = CredPersistLocalMachine,
                Comment = IntPtr.Zero,
                TargetAlias = IntPtr.Zero,
            };

            return CredWriteW(ref credential, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
            Marshal.FreeCoTaskMem(data);
        }
    }

    public bool Delete(string providerId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (CredDeleteW(TargetName(providerId), CredTypeGeneric, 0))
        {
            return true;
        }

        return Marshal.GetLastWin32Error() == ErrorNotFound;
    }

    private static string SanitizeId(string providerId)
    {
        string trimmed = (providerId ?? string.Empty).Trim().ToLowerInvariant();
        var builder = new StringBuilder(trimmed.Length);
        foreach (char ch in trimmed)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        return builder.Length == 0 ? "custom" : builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
