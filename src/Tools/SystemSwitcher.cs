using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;

namespace ExHyperV.Tools
{
    public static class SystemSwitcher
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegSaveKey(IntPtr hKey, string lpFile, IntPtr lpSecurityAttributes);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegReplaceKey(IntPtr hKey, string lpSubKey, string lpNewFile, string lpOldFile);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegCloseKey(IntPtr hKey);

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public uint PrivilegeCount; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public LUID_AND_ATTRIBUTES[] Privileges; }
        [StructLayout(LayoutKind.Sequential)]
        struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));
        const int KEY_READ = 0x20019;
        const int ERROR_SUCCESS = 0;

        public static bool EnablePrivilege(string privilegeName)
        {
            IntPtr hToken;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out hToken)) return false;
            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, privilegeName, out luid)) return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Privileges = new LUID_AND_ATTRIBUTES[1] };
                tp.Privileges[0].Luid = luid;
                tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
                if (!AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == ERROR_SUCCESS;
            }
            finally { CloseHandle(hToken); }
        }

        private static void CleanupTempFiles(string tempDir)
        {
            try
            {
                var files = Directory.GetFiles(tempDir, "sys_*.hiv");
                foreach (var file in files)
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }

        public static string ExecutePatch(int mode)
        {
            string tempDir = @"C:\temp";
            string randomId = Guid.NewGuid().ToString("N").Substring(0, 8);
            string hiveFile = Path.Combine(tempDir, $"sys_mod_{randomId}.hiv");
            string backupFile = Path.Combine(tempDir, $"sys_bak_{randomId}.hiv");

            try
            {
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                CleanupTempFiles(tempDir);

                if (!EnablePrivilege("SeBackupPrivilege") || !EnablePrivilege("SeRestorePrivilege"))
                    return "错误：无法获取管理员权限。";

                IntPtr hKey;
                if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, "SYSTEM", 0, KEY_READ, out hKey) != ERROR_SUCCESS)
                    return "错误：无法打开注册表键。";

                int ret = RegSaveKey(hKey, hiveFile, IntPtr.Zero);
                RegCloseKey(hKey);

                if (ret != ERROR_SUCCESS) return $"错误：导出失败 (Code: {ret})。";

                byte[] buffer = File.ReadAllBytes(hiveFile);
                if (!PatchBlock(ref buffer, mode)) return "错误：未找到目标数据结构。";

                File.WriteAllBytes(hiveFile, buffer);
                ret = RegReplaceKey(HKEY_LOCAL_MACHINE, "SYSTEM", hiveFile, backupFile);

                if (ret == ERROR_SUCCESS) return "SUCCESS";
                if (ret == 5) return "PENDING";
                return $"错误：替换失败 (Code: {ret})。";
            }
            catch (Exception ex) { return "异常: " + ex.Message; }
        }

        private static bool PatchBlock(ref byte[] buffer, int mode)
        {
            byte[] dataWinNT = new byte[] { 0x00, 0x00, 0x00, 0x00, 0xE8, 0xFF, 0xFF, 0xFF, 0x57, 0x00, 0x69, 0x00, 0x6E, 0x00, 0x4E, 0x00, 0x54, 0x00, 0x00, 0x00, 0x4E, 0x00, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00, 0xD0, 0xFF, 0xFF, 0xFF };
            byte[] dataServerNT = new byte[] { 0x00, 0x00, 0x00, 0x00, 0xE8, 0xFF, 0xFF, 0xFF, 0x53, 0x00, 0x65, 0x00, 0x72, 0x00, 0x76, 0x00, 0x65, 0x00, 0x72, 0x00, 0x4E, 0x00, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00, 0xD0, 0xFF, 0xFF, 0xFF };
            byte[] targetData = (mode == 1) ? dataServerNT : dataWinNT;
            byte[] keyPattern = Encoding.ASCII.GetBytes("ProductType").Concat(new byte[] { 0x00 }).ToArray();
            byte[] cellSig = new byte[] { 0xE8, 0xFF, 0xFF, 0xFF };

            for (int i = 0; i < buffer.Length - keyPattern.Length; i++)
            {
                if (IsMatch(buffer, i, keyPattern))
                {
                    int searchLimit = Math.Min(buffer.Length, i + 128);
                    for (int j = i; j < searchLimit - 4; j++)
                    {
                        if (IsMatch(buffer, j, cellSig))
                        {
                            int startPos = j - 4;
                            if (startPos >= 0 && (startPos + 32) <= buffer.Length)
                            {
                                Array.Copy(targetData, 0, buffer, startPos, 32);
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsMatch(byte[] buffer, int offset, byte[] pattern)
        {
            for (int k = 0; k < pattern.Length; k++) if (buffer[offset + k] != pattern[k]) return false;
            return true;
        }
    }
}