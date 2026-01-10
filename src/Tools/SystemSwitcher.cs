using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern int RegOpenKeyEx(IntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern int RegSaveKey(IntPtr hKey, string lpFile, IntPtr lpSecurityAttributes);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern int RegReplaceKey(IntPtr hKey, string lpSubKey, string lpNewFile, string lpOldFile);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegCloseKey(IntPtr hKey);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern int RegFlushKey(IntPtr hKey);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern int RegSetValueEx(IntPtr hKey, string lpValueName, int Reserved, int dwType, byte[] lpData, int cbData);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern int RegQueryValueEx(IntPtr hKey, string lpValueName, IntPtr lpReserved, ref int lpType, ref int lpData, ref int lpcbData);

        [StructLayout(LayoutKind.Sequential)]
        struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)]
        struct TOKEN_PRIVILEGES { public uint PrivilegeCount; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public LUID_AND_ATTRIBUTES[] Privileges; }
        [StructLayout(LayoutKind.Sequential)]
        struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

        const uint KEY_READ = 0x20019;
        const uint KEY_SET_VALUE = 0x0002;
        const int REG_SZ = 1;
        static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));

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
            string hiveFile = Path.Combine(tempDir, "sys_mod_exec.hiv");
            string backupFile = Path.Combine(tempDir, "sys_bak_exec.hiv");

            try
            {
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                try { if (File.Exists(hiveFile)) File.Delete(hiveFile); } catch { return "SUCCESS"; }
                try { if (File.Exists(backupFile)) File.Delete(backupFile); } catch { }

                if (!EnablePrivilege("SeBackupPrivilege") || !EnablePrivilege("SeRestorePrivilege")) return "权限不足";

                if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, "SYSTEM", 0, (int)KEY_READ, out IntPtr hKey) != 0) return "打不开键";
                int ret = RegSaveKey(hKey, hiveFile, IntPtr.Zero);
                RegCloseKey(hKey);
                if (ret != 0) return $"导出失败:{ret}";

                string targetType = (mode == 1) ? "ServerNT" : "WinNT";
                if (!PatchHiveOffline(hiveFile, targetType)) return "离线修改失败";

                ret = RegReplaceKey(HKEY_LOCAL_MACHINE, "SYSTEM", hiveFile, backupFile);

                return ret == 0 || ret == 5 ? "SUCCESS" : $"替换失败:{ret}";
            }
            catch (Exception ex) { return ex.Message; }
        }

        private static bool PatchHiveOffline(string hivePath, string targetType)
        {
            string tempKeyName = "TEMP_OFFLINE_SYS_MOD";

            if (RegLoadKey(HKEY_LOCAL_MACHINE, tempKeyName, hivePath) != 0) return false;

            try
            {
                int currentSet = 1;
                string selectPath = tempKeyName + "\\Select";
                if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, selectPath, 0, (int)KEY_READ, out IntPtr hKeySelect) == 0)
                {
                    int type = 0;
                    int data = 0;
                    int size = 4;
                    if (RegQueryValueEx(hKeySelect, "Current", IntPtr.Zero, ref type, ref data, ref size) == 0)
                    {
                        currentSet = data;
                    }
                    RegCloseKey(hKeySelect);
                }

                string setPath = $"{tempKeyName}\\ControlSet{currentSet:D3}\\Control\\ProductOptions";
                if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, setPath, 0, (int)KEY_SET_VALUE, out IntPtr hKey) != 0)
                {
                    setPath = $"{tempKeyName}\\ControlSet001\\Control\\ProductOptions";
                    if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, setPath, 0, (int)KEY_SET_VALUE, out hKey) != 0) return false;
                }

                byte[] dataBytes = Encoding.ASCII.GetBytes(targetType + "\0");
                int writeRet = RegSetValueEx(hKey, "ProductType", 0, REG_SZ, dataBytes, dataBytes.Length);

                RegCloseKey(hKey);
                RegFlushKey(HKEY_LOCAL_MACHINE);

                return writeRet == 0;
            }
            finally
            {
                RegUnLoadKey(HKEY_LOCAL_MACHINE, tempKeyName);
            }
        }
    }
}