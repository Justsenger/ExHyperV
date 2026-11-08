using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Application;

// 固定当前桌面 Explorer 的身份；不能用已提升的 WPF 身份代替。
internal sealed class ExplorerAccessCheck : IDisposable
{
    private readonly SafeAccessTokenHandle token;

    private ExplorerAccessCheck(SafeAccessTokenHandle token) => this.token = token;

    internal static ExplorerAccessCheck Capture(SecurityIdentifier expectedUser)
    {
        nint window = GetShellWindow();
        if (window == nint.Zero || GetWindowThreadProcessId(window, out uint processId) == 0)
            throw new InvalidOperationException("无法确认资源管理器进程，未修改目录权限。");

        using var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, processId);
        if (process.IsInvalid) throw NativeError("无法读取资源管理器进程");
        var imagePath = new StringBuilder(32768);
        uint length = (uint)imagePath.Capacity;
        if (!QueryFullProcessImageName(process, 0, imagePath, ref length))
            throw NativeError("无法确认资源管理器程序路径");
        string expectedImage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (!string.Equals(imagePath.ToString(), expectedImage, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前桌面程序不是 Windows 资源管理器，未修改目录权限。");

        if (!OpenProcessToken(process, 0x0008 | 0x0002 /* TOKEN_QUERY | TOKEN_DUPLICATE */, out var primary))
        {
            var error = NativeError("无法读取资源管理器身份");
            primary.Dispose();
            throw error;
        }
        using (primary)
        {
            using var identity = new WindowsIdentity(primary.DangerousGetHandle());
            SecurityIdentifier? explorerUser = identity.User;
            if (explorerUser == null || !expectedUser.Equals(explorerUser))
                throw new InvalidOperationException("资源管理器与当前程序不是同一用户，未修改目录权限。");

            if (GetShellWindow() != window || GetWindowThreadProcessId(window, out uint currentId) == 0 || currentId != processId)
                throw new InvalidOperationException("资源管理器正在重新启动，请稍后重试。");

            if (!DuplicateToken(primary, 2 /* SecurityImpersonation */, out var duplicate))
            {
                var error = NativeError("无法建立资源管理器访问检查身份");
                duplicate.Dispose();
                throw error;
            }
            return new ExplorerAccessCheck(duplicate);
        }
    }

    internal bool CanReadAndExecute(DirectorySecurity security)
    {
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        var mapping = new GenericMapping
        {
            Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF
        };
        uint length = 256;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            nint privileges = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                if (AccessCheck(descriptor, token, (uint)FileSystemRights.ReadAndExecute,
                    ref mapping, privileges, ref length, out _, out bool allowed))
                    return allowed;
                int error = Marshal.GetLastPInvokeError();
                if (error != 122 /* ERROR_INSUFFICIENT_BUFFER */ || length == 0 || length > 65536)
                    throw new Win32Exception(error,
                        $"无法核验资源管理器的目录权限：{new Win32Exception(error).Message}（{error}）");
            }
            finally { Marshal.FreeHGlobal(privileges); }
        }
        throw new IOException("目录权限检查所需的缓冲区发生变化，请重试。");
    }

    public void Dispose() => token.Dispose();

    private static Win32Exception NativeError(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation}：{new Win32Exception(error).Message}（{error}）");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping { public uint Read, Write, Execute, All; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetShellWindow();
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateToken(SafeAccessTokenHandle existing, int level, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AccessCheck(byte[] descriptor, SafeAccessTokenHandle clientToken, uint desiredAccess,
        ref GenericMapping mapping, nint privilegeSet, ref uint privilegeSetLength,
        out uint grantedAccess, [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);
}
