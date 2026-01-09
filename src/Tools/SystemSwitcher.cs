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

        public static bool EnablePrivilege(string privilegeName)
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out IntPtr hToken)) return false;
            try
            {
                if (!LookupPrivilegeValue(null, privilegeName, out LUID luid)) return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Privileges = new LUID_AND_ATTRIBUTES[1] };
                tp.Privileges[0].Luid = luid;
                tp.Privileges[0].Attributes = 0x00000002;
                return AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            finally { CloseHandle(hToken); }
        }

        public static string ExecutePatch(int mode)
        {
            string tempDir = @"C:\temp";
            // 必须使用固定文件名，确保多次点击是“覆盖”而非“累加”
            string hiveFile = Path.Combine(tempDir, "sys_mod_exec.hiv");
            string backupFile = Path.Combine(tempDir, "sys_bak_exec.hiv");

            try
            {
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                // 如果文件已被内核锁定，说明已经有任务在排队，直接返回 SUCCESS 提示重启即可
                try { if (File.Exists(hiveFile)) File.Delete(hiveFile); } catch { return "SUCCESS"; }
                try { if (File.Exists(backupFile)) File.Delete(backupFile); } catch { }

                if (!EnablePrivilege("SeBackupPrivilege") || !EnablePrivilege("SeRestorePrivilege")) return "权限不足";

                if (RegOpenKeyEx(new IntPtr(unchecked((int)0x80000002)), "SYSTEM", 0, 0x20019, out IntPtr hKey) != 0) return "打不开键";
                int ret = RegSaveKey(hKey, hiveFile, IntPtr.Zero);
                RegCloseKey(hKey);
                if (ret != 0) return $"导出失败:{ret}";

                byte[] buffer = File.ReadAllBytes(hiveFile);
                if (!PatchAllInstances(ref buffer, mode)) return "未找到特征";

                File.WriteAllBytes(hiveFile, buffer);
                ret = RegReplaceKey(new IntPtr(unchecked((int)0x80000002)), "SYSTEM", hiveFile, backupFile);

                return ret == 0 || ret == 5 ? "SUCCESS" : $"替换失败:{ret}";
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static bool PatchAllInstances(ref byte[] buffer, int mode)
        {
            byte[] dataWinNT = { 0x00, 0x00, 0x00, 0x00, 0xE8, 0xFF, 0xFF, 0xFF, 0x57, 0x00, 0x69, 0x00, 0x6E, 0x00, 0x4E, 0x00, 0x54, 0x00, 0x00, 0x00, 0x4E, 0x00, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00, 0xD0, 0xFF, 0xFF, 0xFF };
            byte[] dataServerNT = { 0x00, 0x00, 0x00, 0x00, 0xE8, 0xFF, 0xFF, 0xFF, 0x53, 0x00, 0x65, 0x00, 0x72, 0x00, 0x76, 0x00, 0x65, 0x00, 0x72, 0x00, 0x4E, 0x00, 0x54, 0x00, 0x00, 0x00, 0x00, 0x00, 0xD0, 0xFF, 0xFF, 0xFF };
            byte[] target = (mode == 1) ? dataServerNT : dataWinNT;
            byte[] key = Encoding.ASCII.GetBytes("ProductType\0");
            byte[] sig = { 0xE8, 0xFF, 0xFF, 0xFF };

            bool foundAny = false;
            for (int i = 0; i < buffer.Length - key.Length; i++)
            {
                if (IsMatch(buffer, i, key))
                {
                    for (int j = i; j < i + 256 && j < buffer.Length - 4; j++)
                    {
                        if (IsMatch(buffer, j, sig))
                        {
                            Array.Copy(target, 0, buffer, j - 4, 32);
                            foundAny = true;
                            i = j + 32; // 跳过已修改区域，继续向后扫描其他配置集
                            break;
                        }
                    }
                }
            }
            return foundAny;
        }

        private static bool IsMatch(byte[] b, int o, byte[] p)
        {
            if (o + p.Length > b.Length) return false;
            for (int k = 0; k < p.Length; k++) if (b[o + k] != p[k]) return false;
            return true;
        }
    }
}