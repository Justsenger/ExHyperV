using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ExHyperV.Api;

public static class Win32Api
{
    // ── PnP 设备控制 ──────────────────────────────────────────────

    public static ApiResponse EnablePnpDevice(string instanceId)
        => SetPnpDeviceState(instanceId, enable: true);

    public static ApiResponse DisablePnpDevice(string instanceId)
        => SetPnpDeviceState(instanceId, enable: false);

    private static ApiResponse SetPnpDeviceState(string instanceId, bool enable)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return ApiResponse.Fail("InstanceId cannot be empty");

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

    // ── PnP 设备枚举 ─────────────────────────────────────────────

    /// <summary>
    /// 枚举系统中所有 PnP 设备的 InstanceId、Status、LocationPaths。
    /// 调用方负责过滤（PCI\*、PCIP\*、service 等）。
    /// </summary>
    public static List<PciDeviceInfo> GetAllDevices()
    {
        var sw = Stopwatch.StartNew();

        // 1. Win32_PnPEntity：拿在线设备的 Name/PNPClass/Service
        var pnpEntityMap = new Dictionary<string, (string Name, string PnpClass, string Service)>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                @"root\cimv2",
                "SELECT DeviceID, Name, PNPClass, Service FROM Win32_PnPEntity");
            foreach (System.Management.ManagementObject obj in searcher.Get())
            {
                string devId = obj["DeviceID"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(devId)) continue;
                pnpEntityMap[devId] = (
                    obj["Name"]?.ToString() ?? "",
                    obj["PNPClass"]?.ToString() ?? "",
                    obj["Service"]?.ToString() ?? "");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Win32Api.GetAllDevices] Win32_PnPEntity failed: {ex.Message}");
        }
        Debug.WriteLine($"[Win32Api.GetAllDevices] Win32_PnPEntity: {pnpEntityMap.Count} ({sw.ElapsedMilliseconds}ms)");

        // 2. CM_Get_Device_ID_List：FILTER_NONE 枚举所有设备（含分配给VM的Unknown设备）
        var allIds = new List<string>();
        uint bufferLen = 0;
        int cr = NativeMethods.CM_Get_Device_ID_List_Size(
            out bufferLen, null, NativeMethods.CM_GETIDLIST_FILTER_NONE);
        if (cr == NativeMethods.CR_SUCCESS && bufferLen > 0)
        {
            char[] buf = new char[bufferLen];
            cr = NativeMethods.CM_Get_Device_ID_List(null, buf, bufferLen, NativeMethods.CM_GETIDLIST_FILTER_NONE);
            if (cr == NativeMethods.CR_SUCCESS)
                allIds = ParseMultiString(buf);
        }
        Debug.WriteLine($"[Win32Api.GetAllDevices] CM all devices: {allIds.Count} ({sw.ElapsedMilliseconds}ms)");

        // 3. 并行查每个设备属性
        var results = allIds.AsParallel().Select(instanceId =>
        {
            // PHANTOM flag 对在线和离线设备都有效
            int lcr = NativeMethods.CM_Locate_DevNode(out uint devInst, instanceId, NativeMethods.CM_LOCATE_DEVNODE_PHANTOM);
            if (lcr != NativeMethods.CR_SUCCESS)
            {
                Debug.WriteLine($"[Win32Api.GetAllDevices] CM_Locate_DevNode failed '{instanceId}' cr={lcr}");
                return null;
            }

            // Status
            string status;
            lcr = NativeMethods.CM_Get_DevNode_Status(out uint dnStatus, out _, devInst, 0);
            if (lcr != NativeMethods.CR_SUCCESS)
                status = "Unknown";
            else if ((dnStatus & NativeMethods.DN_HAS_PROBLEM) != 0)
                status = "Error";
            else
                status = "OK";

            // Name/Class/Service：优先 cfgmgr32 DEVPKEY（支持 Unknown 状态设备）
            // Win32_PnPEntity 仅作为 fallback，Unknown 状态时该条目不存在
            string friendlyName = GetDevNodeStringProperty(devInst, instanceId,
                new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14); // DEVPKEY_Device_FriendlyName
            if (string.IsNullOrEmpty(friendlyName))
                friendlyName = GetDevNodeStringProperty(devInst, instanceId,
                    new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 2);  // DEVPKEY_Device_DeviceDesc
            string pnpClass = GetDevNodeStringProperty(devInst, instanceId,
                new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 9);  // DEVPKEY_Device_Class
            string service = GetDevNodeStringProperty(devInst, instanceId,
                new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 6);  // DEVPKEY_Device_Service
            // cfgmgr32 拿不到时 fallback Win32_PnPEntity
            if (string.IsNullOrEmpty(friendlyName) && pnpEntityMap.TryGetValue(instanceId, out var entityInfo))
            {
                friendlyName = entityInfo.Name;
                pnpClass = string.IsNullOrEmpty(pnpClass) ? entityInfo.PnpClass : pnpClass;
                service = string.IsNullOrEmpty(service) ? entityInfo.Service : service;
            }

            // LocationPaths
            var locationPaths = GetDevNodeStringListProperty(devInst, instanceId,
                new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 37);

            return new PciDeviceInfo
            {
                InstanceId = instanceId,
                FriendlyName = friendlyName,
                Class = pnpClass,
                Service = service,
                Status = status,
                LocationPaths = locationPaths
            };
        }).Where(x => x != null).Cast<PciDeviceInfo>().ToList();

        Debug.WriteLine($"[Win32Api.GetAllDevices] Done. {results.Count} ({sw.ElapsedMilliseconds}ms)");
        return results;
    }

    // ── cfgmgr32 属性查询 ─────────────────────────────────────────

    private static string GetDevNodeStringProperty(uint devInst, string instanceId, Guid fmtid, uint pid)
    {
        var key = new NativeMethods.DEVPROPKEY { fmtid = fmtid, pid = pid };
        uint propType = 0, bufSize = 0;
        int cr = NativeMethods.CM_Get_DevNode_Property(devInst, ref key, out propType, null, ref bufSize, 0);
        if (cr != NativeMethods.CR_BUFFER_SMALL && cr != NativeMethods.CR_SUCCESS) return "";
        if (bufSize == 0) return "";
        byte[] buf = new byte[bufSize];
        cr = NativeMethods.CM_Get_DevNode_Property(devInst, ref key, out propType, buf, ref bufSize, 0);
        if (cr != NativeMethods.CR_SUCCESS) return "";
        return Encoding.Unicode.GetString(buf).TrimEnd('\0');
    }

    private static List<string> GetDevNodeStringListProperty(uint devInst, string instanceId, Guid fmtid, uint pid)
    {
        var key = new NativeMethods.DEVPROPKEY { fmtid = fmtid, pid = pid };
        uint propType = 0, bufSize = 0;
        int cr = NativeMethods.CM_Get_DevNode_Property(devInst, ref key, out propType, null, ref bufSize, 0);
        if (cr != NativeMethods.CR_BUFFER_SMALL && cr != NativeMethods.CR_SUCCESS) return new List<string>();
        if (bufSize == 0) return new List<string>();
        byte[] buf = new byte[bufSize];
        cr = NativeMethods.CM_Get_DevNode_Property(devInst, ref key, out propType, buf, ref bufSize, 0);
        if (cr != NativeMethods.CR_SUCCESS) return new List<string>();
        return ParseMultiString(Encoding.Unicode.GetString(buf).ToCharArray());
    }

    private static List<string> ParseMultiString(char[] buffer)
    {
        var result = new List<string>();
        int start = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '\0')
            {
                if (i > start) result.Add(new string(buffer, start, i - start));
                else break;
                start = i + 1;
            }
        }
        return result;
    }

    // ── 权限提升 ──────────────────────────────────────────────────

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
                return ApiResponse.Fail($"LookupPrivilegeValue failed for '{privilegeName}'", err, ApiErrorSource.Win32);
            }
            var tp = new NativeMethods.TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new NativeMethods.LUID_AND_ATTRIBUTES[1]
            };
            tp.Privileges[0].Luid = luid;
            tp.Privileges[0].Attributes = NativeMethods.SE_PRIVILEGE_ENABLED;
            if (!NativeMethods.AdjustTokenPrivileges(hToken, false, ref tp, 0, nint.Zero, nint.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                return ApiResponse.Fail("AdjustTokenPrivileges failed", err, ApiErrorSource.Win32);
            }
            int lastErr = Marshal.GetLastWin32Error();
            if (lastErr == 1300)
                return ApiResponse.Fail($"Privilege '{privilegeName}' not assigned", 1300, ApiErrorSource.Win32);
            return ApiResponse.Ok();
        }
        finally { NativeMethods.CloseHandle(hToken); }
    }

    // ── 离线注册表操作 ────────────────────────────────────────────

    public static ApiResponse LoadHive(string subKeyName, string hivePath)
    {
        int ret = NativeMethods.RegLoadKey(NativeMethods.HKEY_LOCAL_MACHINE, subKeyName, hivePath);
        return ret == 0 ? ApiResponse.Ok() : ApiResponse.Fail($"RegLoadKey failed: {subKeyName}", ret, ApiErrorSource.Win32);
    }

    public static ApiResponse UnloadHive(string subKeyName)
    {
        int ret = NativeMethods.RegUnLoadKey(NativeMethods.HKEY_LOCAL_MACHINE, subKeyName);
        return ret == 0 ? ApiResponse.Ok() : ApiResponse.Fail($"RegUnLoadKey failed: {subKeyName}", ret, ApiErrorSource.Win32);
    }

    public static ApiResponse SaveHive(string subKeyName, string filePath)
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
        int openRet = NativeMethods.RegOpenKeyEx(NativeMethods.HKEY_LOCAL_MACHINE, subKeyName, 0, (int)NativeMethods.KEY_READ, out nint hKey);
        if (openRet != 0) return ApiResponse.Fail($"RegOpenKeyEx failed: {subKeyName}", openRet, ApiErrorSource.Win32);
        try
        {
            int saveRet = NativeMethods.RegSaveKey(hKey, filePath, nint.Zero);
            return saveRet == 0 ? ApiResponse.Ok() : ApiResponse.Fail("RegSaveKey failed", saveRet, ApiErrorSource.Win32);
        }
        finally { NativeMethods.RegCloseKey(hKey); }
    }

    public static ApiResponse ReplaceHive(string subKeyName, string newHivePath, string backupPath)
    {
        int ret = NativeMethods.RegReplaceKey(NativeMethods.HKEY_LOCAL_MACHINE, subKeyName, newHivePath, backupPath);
        return ret == 0 || ret == 5 ? ApiResponse.Ok() : ApiResponse.Fail("RegReplaceKey failed", ret, ApiErrorSource.Win32);
    }

    public static ApiResponse SetHiveStringValue(string subKeyName, string valueName, string value)
    {
        int openRet = NativeMethods.RegOpenKeyEx(NativeMethods.HKEY_LOCAL_MACHINE, subKeyName, 0, (int)NativeMethods.KEY_SET_VALUE, out nint hKey);
        if (openRet != 0) return ApiResponse.Fail($"RegOpenKeyEx failed: {subKeyName}", openRet, ApiErrorSource.Win32);
        try
        {
            byte[] data = Encoding.ASCII.GetBytes(value + "\0");
            int setRet = NativeMethods.RegSetValueEx(hKey, valueName, 0, NativeMethods.REG_SZ, data, data.Length);
            if (setRet != 0) return ApiResponse.Fail($"RegSetValueEx failed: {valueName}", setRet, ApiErrorSource.Win32);
            NativeMethods.RegFlushKey(hKey);
            return ApiResponse.Ok();
        }
        finally { NativeMethods.RegCloseKey(hKey); }
    }

    public static ApiResponse<int> GetHiveDwordValue(string subKeyName, string valueName)
    {
        int openRet = NativeMethods.RegOpenKeyEx(NativeMethods.HKEY_LOCAL_MACHINE, subKeyName, 0, (int)NativeMethods.KEY_READ, out nint hKey);
        if (openRet != 0) return ApiResponse<int>.Fail($"RegOpenKeyEx failed: {subKeyName}", openRet, ApiErrorSource.Win32);
        try
        {
            int type = 0, data = 0, size = 4;
            int queryRet = NativeMethods.RegQueryValueEx(hKey, valueName, nint.Zero, ref type, ref data, ref size);
            return queryRet == 0 ? ApiResponse<int>.Ok(data) : ApiResponse<int>.Fail($"RegQueryValueEx failed: {valueName}", queryRet, ApiErrorSource.Win32);
        }
        finally { NativeMethods.RegCloseKey(hKey); }
    }

    public static bool CloseHandle(nint handle) => NativeMethods.CloseHandle(handle);
}

public class PciDeviceInfo
{
    public string InstanceId { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Class { get; set; } = "";
    public string Service { get; set; } = "";
    public string Status { get; set; } = "";
    public List<string> LocationPaths { get; set; } = new();

    public string? FirstLocationPath =>
        LocationPaths.FirstOrDefault(p =>
            p.StartsWith("PCIROOT", StringComparison.OrdinalIgnoreCase))
        ?? LocationPaths.FirstOrDefault();
}

internal static class NativeMethods
{
    public static readonly nint INVALID_HANDLE_VALUE = new(-1);
    public static readonly nint HKEY_LOCAL_MACHINE = new(unchecked((int)0x80000002));

    #region setupapi
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_ALLCLASSES = 0x00000004;
    public const int DIF_PROPERTYCHANGE = 0x12;
    public const uint DICS_ENABLE = 1;
    public const uint DICS_DISABLE = 2;
    public const uint DICS_FLAG_GLOBAL = 1;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern nint SetupDiGetClassDevs(nint classGuid, string? enumerator, nint hwndParent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiSetClassInstallParams(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_PROPCHANGE_PARAMS classInstallParams, uint classInstallParamsSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiCallClassInstaller(int installFunction, nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);
    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_CLASSINSTALL_HEADER { public uint cbSize; public int InstallFunction; }
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_PROPCHANGE_PARAMS { public SP_CLASSINSTALL_HEADER ClassInstallHeader; public uint StateChange; public uint Scope; public uint HwProfile; }
    #endregion

    #region cfgmgr32
    public const int CR_SUCCESS = 0x00000000;
    public const int CR_BUFFER_SMALL = 0x0000001A;
    public const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    public const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
    public const uint CM_DISABLE_UI_NOT_OK = 0x00000002;
    public const uint CM_GETIDLIST_FILTER_NONE = 0x00000000;
    public const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000002;
    public const uint DN_HAS_PROBLEM = 0x00000400;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Enable_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Disable_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_List_Size(out uint pulLen, string? pszFilter, uint ulFlags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_ID_List(string? pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);
    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_DevNode_Property(uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType, byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    #endregion

    #region advapi32
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    public const uint KEY_READ = 0x20019;
    public const uint KEY_SET_VALUE = 0x0002;
    public const int REG_SZ = 1;

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(nint tokenHandle, bool disableAllPrivileges, ref TOKEN_PRIVILEGES newState, uint bufferLength, nint previousState, nint returnLength);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegOpenKeyEx(nint hKey, string lpSubKey, uint ulOptions, int samDesired, out nint phkResult);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegSaveKey(nint hKey, string lpFile, nint lpSecurityAttributes);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegReplaceKey(nint hKey, string lpSubKey, string lpNewFile, string lpOldFile);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegCloseKey(nint hKey);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegLoadKey(nint hKey, string lpSubKey, string lpFile);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegUnLoadKey(nint hKey, string lpSubKey);
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern int RegFlushKey(nint hKey);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegSetValueEx(nint hKey, string lpValueName, int reserved, int dwType, byte[] lpData, int cbData);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern int RegQueryValueEx(nint hKey, string lpValueName, nint lpReserved, ref int lpType, ref int lpData, ref int lpcbData);

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public LUID_AND_ATTRIBUTES[] Privileges;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }
    #endregion

    #region kernel32
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint hObject);
    #endregion
}