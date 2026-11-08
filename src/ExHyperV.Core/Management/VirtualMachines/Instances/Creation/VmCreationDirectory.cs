using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace ExHyperV.Services;

// Directory.CreateDirectory 对已有目录也会成功，不能用它的返回值证明
// 此次事务创建了叶目录；这里只认领原生创建明确成功的目录。
internal static class VmCreationDirectory
{
    internal static bool TryCreateLeaf(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (parent != null)
            Directory.CreateDirectory(parent);

        // Core 的宿主未必声明 longPathAware，长路径须显式使用扩展格式，
        // 保留原先 .NET 目录创建支持长路径的能力；短路径仍沿用正常化规则。
        string nativePath = fullPath;
        if (fullPath.Length >= 248 && !fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            nativePath = fullPath.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;

        if (CreateDirectory(nativePath, IntPtr.Zero))
            return true;

        int error = Marshal.GetLastPInvokeError();
        if (error is 80 or 183) // ERROR_FILE_EXISTS / ERROR_ALREADY_EXISTS
            return false;

        throw new IOException($"无法创建虚拟机目录“{fullPath}”。", new Win32Exception(error));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
}
