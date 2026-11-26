using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.ConsoleIpc;

internal static unsafe class ConsoleIpcTrust
{
    internal static void Verify(SafePipeHandle pipe, ConsoleIpcEndpoint endpoint, long openedAfter)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipe, out uint pid)) throw Denied();
            using var process = OpenProcess(0x1000, false, pid);
            if (process.IsInvalid || !GetProcessTimes(process, out long created, out _, out _, out _)
                || created > openedAfter || !OpenProcessToken(process, 8, out var token)) throw Denied();
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                // WindowsIdentity queries TokenUser and the enabled, non deny-only TokenGroups.
                if (identity.User != endpoint.Sid && identity.Groups?.Contains(endpoint.Sid) != true) throw Denied();
            }

            char* buffer = stackalloc char[32768];
            uint length = 32768;
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref length)) throw Denied();
            string image = new(buffer, 0, (int)length);
            VerifyImage(image, endpoint);
            if (!GetNamedPipeServerProcessId(pipe, out uint again) || again != pid
                || !GetProcessTimes(process, out long same, out long exited, out _, out _) || same != created || exited != 0)
                throw Denied();
        }
        catch (Exception) { throw Denied(); }
    }

    internal static void VerifyImage(string image, ConsoleIpcEndpoint endpoint)
    {
        using var file = File.OpenHandle(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        char* buffer = stackalloc char[32768];
        uint length = GetFinalPathNameByHandleW(file, buffer, 32768, 0);
        if (length is 0 or >= 32768) throw Denied();
        string resolved = new(buffer, 0, (int)length);
        if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            !image.Equals(resolved[4..], StringComparison.OrdinalIgnoreCase) ||
            endpoint.ProtectedPath && !IsProtected(image) || !VerifySignature(file, image, endpoint.Thumbprint)) throw Denied();
    }

    private static readonly Lazy<string[]> Roots = new(() =>
    {
        // The native Program Files known folder also covers ProgramW6432 under emulation.
        Guid nativePrograms = new("6D809377-6AF0-444B-8957-A3773F02200E");
        int result = SHGetKnownFolderPath(in nativePrograms, 0, 0, out nint path);
        try
        {
            return [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.SystemDirectory,
                result >= 0 ? Marshal.PtrToStringUni(path) ?? "" : ""];
        }
        finally { Marshal.FreeCoTaskMem(path); }
    });

    internal static bool IsProtected(string image)
    {
        string full = Path.GetFullPath(image);
        return Roots.Value.Any(root => root.Length > 0 && full.StartsWith(Path.TrimEndingDirectorySeparator(root) + "\\", StringComparison.OrdinalIgnoreCase));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", ExactSpelling = true)] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    private static UnauthorizedAccessException Denied() => new("EXT-TRUST-02");

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile { internal uint Size; internal char* Path; internal nint Handle, Subject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        internal uint Size;
        internal nint Policy, Sip;
        internal uint UI, Revocation, Choice;
        internal TrustFile* File;
        internal uint Action;
        internal nint State, Url;
        internal uint Flags, Context;
        internal nint Settings;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCert { internal uint Size; internal nint Certificate; }

    private static bool VerifySignature(SafeFileHandle file, string image, string thumbprint)
    {
        Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        fixed (char* path = image)
        {
            var info = new TrustFile { Size = (uint)sizeof(TrustFile), Path = path, Handle = file.DangerousGetHandle() };
            var trust = new TrustData { Size = (uint)sizeof(TrustData), UI = 2, Revocation = 1, Choice = 1, File = &info,
                Action = 1, Flags = 0x1080 };
            try
            {
                if (WinVerifyTrust(-1, &action, &trust) != 0) return false;
                nint data = WTHelperProvDataFromStateData(trust.State);
                nint signer = data == 0 ? 0 : WTHelperGetProvSignerFromChain(data, 0, 0, 0);
                var leaf = signer == 0 ? null : WTHelperGetProvCertFromChain(signer, 0);
                byte* hash = stackalloc byte[32];
                uint size = 32;
                return leaf != null && leaf->Size >= sizeof(ProviderCert) && leaf->Certificate != 0 &&
                    CertGetCertificateContextProperty(leaf->Certificate, 107, hash, &size) && size == 32 &&
                    Convert.ToHexString(new ReadOnlySpan<byte>(hash, 32)).Equals(thumbprint, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                trust.Action = 2;
                WinVerifyTrust(-1, &action, &trust);
                GC.KeepAlive(file);
            }
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char* buffer, ref uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char* buffer, uint length, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll")] private static extern int WinVerifyTrust(nint window, Guid* action, TrustData* data);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll")] private static extern nint WTHelperProvDataFromStateData(nint state);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll")] private static extern nint WTHelperGetProvSignerFromChain(nint data, uint index, int counterSigner, uint counterIndex);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wintrust.dll")] private static extern ProviderCert* WTHelperGetProvCertFromChain(nint signer, uint index);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("crypt32.dll")] private static extern bool CertGetCertificateContextProperty(nint context, uint id, byte* bytes, uint* size);
}
