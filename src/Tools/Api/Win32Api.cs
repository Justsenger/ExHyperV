using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ExHyperV.Tools.Api;

// ══════════════════════════════════════════════════════════════════
//  Win32Api — 公开封装层
//  服务层只调用这里，不直接碰 NativeMethods
// ══════════════════════════════════════════════════════════════════
public static class Win32Api
{
    // ── PnP 设备控制 ──────────────────────────────────────────────
    // 替换 DDAService / VmGPUService 里的 Enable-PnpDevice / Disable-PnpDevice

    /// <summary>
    /// 启用指定 InstanceId 的 PnP 设备。
    /// 等效于 PowerShell: Enable-PnpDevice -InstanceId '...' -Confirm:$false
    /// </summary>
    public static ApiResponse EnablePnpDevice(string instanceId)
        => SetPnpDeviceState(instanceId, enable: true);

    /// <summary>
    /// 禁用指定 InstanceId 的 PnP 设备。
    /// 等效于 PowerShell: Disable-PnpDevice -InstanceId '...' -Confirm:$false
    /// </summary>
    public static ApiResponse DisablePnpDevice(string instanceId)
        => SetPnpDeviceState(instanceId, enable: false);

    private static ApiResponse SetPnpDeviceState(string instanceId, bool enable)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return ApiResponse.Fail("InstanceId cannot be empty");

        // 用 CM_Locate_DevNode 直接按 InstanceId 定位设备
        // Disable 时设备在线，用 NORMAL；Enable 时设备可能是 phantom（从 VM 移回），用 PHANTOM
        uint locateFlag = enable
            ? NativeMethods.CM_LOCATE_DEVNODE_PHANTOM
            : NativeMethods.CM_LOCATE_DEVNODE_NORMAL;

        int cr = NativeMethods.CM_Locate_DevNode(out uint devInst, instanceId, locateFlag);
        if (cr != NativeMethods.CR_SUCCESS)
            return ApiResponse.Fail(
                $"CM_Locate_DevNode failed for '{instanceId}'",
                cr, ApiErrorSource.Win32);

        cr = enable
            ? NativeMethods.CM_Enable_DevNode(devInst, 0)
            : NativeMethods.CM_Disable_DevNode(devInst, NativeMethods.CM_DISABLE_UI_NOT_OK);

        return cr == NativeMethods.CR_SUCCESS
            ? ApiResponse.Ok()
            : ApiResponse.Fail(
                $"{(enable ? "CM_Enable_DevNode" : "CM_Disable_DevNode")} failed for '{instanceId}'",
                cr, ApiErrorSource.Win32);
    }

    // ── 权限提升 ──────────────────────────────────────────────────
    // 替换 SystemSwitcher 里的 EnablePrivilege

    /// <summary>
    /// 为当前进程启用指定的 Windows 特权。
    /// 常用值：
    ///   "SeBackupPrivilege"  — 读取受保护文件/注册表
    ///   "SeRestorePrivilege" — 写入受保护文件/注册表
    ///   "SeDebugPrivilege"   — 调试其他进程
    /// </summary>
    public static ApiResponse EnablePrivilege(string privilegeName)
    {
        if (!NativeMethods.OpenProcessToken(
                NativeMethods.GetCurrentProcess(),
                NativeMethods.TOKEN_ADJUST_PRIVILEGES | NativeMethods.TOKEN_QUERY,
                out nint hToken))
        {
            int err = Marshal.GetLastWin32Error();
            return ApiResponse.Fail("OpenProcessToken failed", err, ApiErrorSource.Win32);
        }

        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                int err = Marshal.GetLastWin32Error();
                return ApiResponse.Fail(
                    $"LookupPrivilegeValue failed for '{privilegeName}'",
                    err, ApiErrorSource.Win32);
            }

            var tp = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LUID_AND_ATTRIBUTES[1]
            };
            tp.Privileges[0].Luid = luid;
            tp.Privileges[0].Attributes = NativeMethods.SE_PRIVILEGE_ENABLED;

            if (!NativeMethods.AdjustTokenPrivileges(
                    hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                return ApiResponse.Fail("AdjustTokenPrivileges failed", err, ApiErrorSource.Win32);
            }

            // AdjustTokenPrivileges 即使返回 true，也可能没完全生效
            // ERROR_NOT_ALL_ASSIGNED = 1300
            int lastErr = Marshal.GetLastWin32Error();
            if (lastErr == 1300)
                return ApiResponse.Fail(
                    $"Privilege '{privilegeName}' not assigned (insufficient rights?)",
                    1300, ApiErrorSource.Win32);

            return ApiResponse.Ok();
        }
        finally
        {
            NativeMethods.CloseHandle(hToken);
        }
    }

    // ── 离线注册表操作 ────────────────────────────────────────────
    // 替换 SystemSwitcher 里的 RegLoadKey / RegSaveKey / RegReplaceKey 等

    /// <summary>
    /// 将离线注册表文件挂载到指定的临时键名下。
    /// 调用前必须先 EnablePrivilege("SeBackupPrivilege") 和 EnablePrivilege("SeRestorePrivilege")。
    /// </summary>
    public static ApiResponse LoadHive(string subKeyName, string hivePath)
    {
        int ret = NativeMethods.RegLoadKey(
            NativeMethods.HKEY_LOCAL_MACHINE, subKeyName, hivePath);

        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail($"RegLoadKey failed: subKey={subKeyName}", ret, ApiErrorSource.Win32);
    }

    /// <summary>
    /// 卸载已挂载的离线注册表。
    /// </summary>
    public static ApiResponse UnloadHive(string subKeyName)
    {
        int ret = NativeMethods.RegUnLoadKey(NativeMethods.HKEY_LOCAL_MACHINE, subKeyName);
        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail($"RegUnLoadKey failed: subKey={subKeyName}", ret, ApiErrorSource.Win32);
    }

    /// <summary>
    /// 保存当前注册表键到文件（用于离线修改前的备份）。
    /// </summary>
    public static ApiResponse SaveHive(string subKeyName, string filePath)
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }

        int openRet = NativeMethods.RegOpenKeyEx(
            NativeMethods.HKEY_LOCAL_MACHINE,
            subKeyName, 0,
            (int)NativeMethods.KEY_READ,
            out nint hKey);

        if (openRet != 0)
            return ApiResponse.Fail($"RegOpenKeyEx failed: {subKeyName}", openRet, ApiErrorSource.Win32);

        try
        {
            int saveRet = NativeMethods.RegSaveKey(hKey, filePath, IntPtr.Zero);
            return saveRet == 0
                ? ApiResponse.Ok()
                : ApiResponse.Fail($"RegSaveKey failed", saveRet, ApiErrorSource.Win32);
        }
        finally
        {
            NativeMethods.RegCloseKey(hKey);
        }
    }

    /// <summary>
    /// 原子替换注册表键内容（SystemSwitcher 用于切换系统类型）。
    /// </summary>
    public static ApiResponse ReplaceHive(string subKeyName, string newHivePath, string backupPath)
    {
        int ret = NativeMethods.RegReplaceKey(
            NativeMethods.HKEY_LOCAL_MACHINE,
            subKeyName,
            newHivePath,
            backupPath);

        // 0 = 成功，5 = ACCESS_DENIED（已知在某些场景下会返回5但实际生效）
        return ret == 0 || ret == 5
            ? ApiResponse.Ok()
            : ApiResponse.Fail($"RegReplaceKey failed", ret, ApiErrorSource.Win32);
    }

    /// <summary>
    /// 打开注册表键并写入字符串值（离线 hive 修改用）。
    /// </summary>
    public static ApiResponse SetHiveStringValue(string subKeyName, string valueName, string value)
    {
        int openRet = NativeMethods.RegOpenKeyEx(
            NativeMethods.HKEY_LOCAL_MACHINE,
            subKeyName, 0,
            (int)NativeMethods.KEY_SET_VALUE,
            out nint hKey);

        if (openRet != 0)
            return ApiResponse.Fail($"RegOpenKeyEx failed: {subKeyName}", openRet, ApiErrorSource.Win32);

        try
        {
            byte[] data = Encoding.ASCII.GetBytes(value + "\0");
            int setRet = NativeMethods.RegSetValueEx(
                hKey, valueName, 0,
                NativeMethods.REG_SZ,
                data, data.Length);

            if (setRet != 0)
                return ApiResponse.Fail($"RegSetValueEx failed: {valueName}", setRet, ApiErrorSource.Win32);

            NativeMethods.RegFlushKey(hKey);
            return ApiResponse.Ok();
        }
        finally
        {
            NativeMethods.RegCloseKey(hKey);
        }
    }

    /// <summary>
    /// 读取注册表 DWORD 值（离线 hive 读取用）。
    /// </summary>
    public static ApiResponse<int> GetHiveDwordValue(string subKeyName, string valueName)
    {
        int openRet = NativeMethods.RegOpenKeyEx(
            NativeMethods.HKEY_LOCAL_MACHINE,
            subKeyName, 0,
            (int)NativeMethods.KEY_READ,
            out nint hKey);

        if (openRet != 0)
            return ApiResponse<int>.Fail(
                $"RegOpenKeyEx failed: {subKeyName}", openRet, ApiErrorSource.Win32);

        try
        {
            int type = 0, data = 0, size = 4;
            int queryRet = NativeMethods.RegQueryValueEx(
                hKey, valueName, IntPtr.Zero,
                ref type, ref data, ref size);

            return queryRet == 0
                ? ApiResponse<int>.Ok(data)
                : ApiResponse<int>.Fail(
                    $"RegQueryValueEx failed: {valueName}", queryRet, ApiErrorSource.Win32);
        }
        finally
        {
            NativeMethods.RegCloseKey(hKey);
        }
    }

    // ── 进程工具 ──────────────────────────────────────────────────

    /// <summary>
    /// 关闭 Win32 句柄。
    /// </summary>
    public static bool CloseHandle(nint handle)
        => NativeMethods.CloseHandle(handle);
}

// ══════════════════════════════════════════════════════════════════
//  NativeMethods — 内部 P/Invoke 声明
//  按 DLL 分 region，Win32Api 以外的代码不应直接引用
// ══════════════════════════════════════════════════════════════════
internal static class NativeMethods
{
    public static readonly nint INVALID_HANDLE_VALUE = new(-1);
    public static readonly nint HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));

    // ── region: setupapi.dll ─────────────────────────────────────
    // 保留声明（其他地方可能还用得到），但 SetPnpDeviceState 已改用 cfgmgr32

    #region setupapi

    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_ALLCLASSES = 0x00000004;
    public const int DIF_PROPERTYCHANGE = 0x12;
    public const uint DICS_ENABLE = 1;
    public const uint DICS_DISABLE = 2;
    public const uint DICS_FLAG_GLOBAL = 1;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern nint SetupDiGetClassDevs(
        IntPtr classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(
        nint deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiSetClassInstallParams(
        nint deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams,
        uint classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(
        int installFunction,
        nint deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public int InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    #endregion

    // ── region: cfgmgr32.dll ─────────────────────────────────────

    #region cfgmgr32

    public const int CR_SUCCESS = 0;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    public const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
    public const uint CM_DISABLE_UI_NOT_OK = 0x00000002;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNode(
        out uint pdnDevInst,
        string pDeviceID,
        uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(
        uint dnDevInst,
        uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(
        uint dnDevInst,
        uint ulFlags);

    #endregion

    // ── region: advapi32.dll ─────────────────────────────────────

    #region advapi32

    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    public const uint KEY_READ = 0x20019;
    public const uint KEY_SET_VALUE = 0x0002;
    public const int REG_SZ = 1;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(
        IntPtr processHandle,
        uint desiredAccess,
        out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool LookupPrivilegeValue(
        string? lpSystemName,
        string lpName,
        out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(
        nint tokenHandle,
        bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegOpenKeyEx(
        nint hKey,
        string lpSubKey,
        uint ulOptions,
        int samDesired,
        out nint phkResult);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegSaveKey(
        nint hKey,
        string lpFile,
        IntPtr lpSecurityAttributes);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegReplaceKey(
        nint hKey,
        string lpSubKey,
        string lpNewFile,
        string lpOldFile);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegCloseKey(nint hKey);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegLoadKey(
        nint hKey,
        string lpSubKey,
        string lpFile);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegUnLoadKey(
        nint hKey,
        string lpSubKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegFlushKey(nint hKey);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegSetValueEx(
        nint hKey,
        string lpValueName,
        int reserved,
        int dwType,
        byte[] lpData,
        int cbData);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegQueryValueEx(
        nint hKey,
        string lpValueName,
        IntPtr lpReserved,
        ref int lpType,
        ref int lpData,
        ref int lpcbData);

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public LUID_AND_ATTRIBUTES[] Privileges;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    #endregion

    // ── region: kernel32.dll ─────────────────────────────────────

    #region kernel32

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint hObject);

    #endregion
}