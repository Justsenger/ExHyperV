using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class HostReadApiCases
{
    internal static bool Supports(string api) => api is "HyperVHostService.GetVmmsStatus()" or "HyperVHostService.IsHypervisorPresent()"
        or "HyperVHostService.IsHyperVWmiNamespaceAvailable()"
        or "HostNvmeService.IsNativeNvmeEnabled()" or "HostOpenHclService.IsFirmwareLoadFromFileEnabled()"
        or "HostProductTypeService.IsServerSystem()" or "HostPowerPlanService.GetPowerPlans()"
        or "HostPowerPlanService.GetActivePowerPlanId()" or "HostProcessorPlatformService.GetManufacturersAsync()"
        or "HostDiskService.GetHostOpticalDrivesAsync()" or "HyperVHostService.IsIommuEnabled()"
        or "HyperVHostService.IsVirtualizationEnabled()";

    internal static void Run(string api, ApiCaseContext context)
    {
        switch (api)
        {
            case "HostNvmeService.IsNativeNvmeEnabled()":
            {
                bool value = ReadRegistry(@"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides", "1176759950") is int number && number == 1;
                var response = context.Call(HostNvmeService.IsNativeNvmeEnabled);
                context.Require(response.HasData, "读取覆盖值成功：" + response.Error);
                context.Equal(value, response.Data, "NVMe 配置与原始注册表值对照");
                break;
            }
            case "HostOpenHclService.IsFirmwareLoadFromFileEnabled()":
            {
                bool value = ReadRegistry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization", "AllowFirmwareLoadFromFile") is int number && number == 1;
                context.Equal(value, context.Call(HostOpenHclService.IsFirmwareLoadFromFileEnabled), "固件加载策略与原始注册表值对照");
                break;
            }
            case "HostProductTypeService.IsServerSystem()":
            {
                var value = ReadRegistry(@"SYSTEM\CurrentControlSet\Control\ProductOptions", "ProductType") as string;
                if (value is null) throw new ApiCheckSkippedException("系统产品类型缺少独立基准。");
                context.Equal(!value.Equals("WinNT", StringComparison.OrdinalIgnoreCase), context.Call(HostProductTypeService.IsServerSystem), "系统产品类型 " + value);
                break;
            }
            case "HostPowerPlanService.GetPowerPlans()":
            {
                var baseline = ReadPowerPlans();
                var plans = context.Call(HostPowerPlanService.GetPowerPlans);
                if (!baseline.Keys.Order().SequenceEqual(ReadPowerPlans().Keys.Order()))
                    throw new ApiCheckSkippedException("电源计划在采样期间变化，留待复测。");
                context.Sequence(baseline.Keys.Order(), plans.Select(p => p.Id).Order(), "电源计划 GUID 完整性与唯一性");
                foreach (var plan in plans) context.Equal(baseline[plan.Id], plan.Name, "计划名称 " + plan.Id);
                break;
            }
            case "HostPowerPlanService.GetActivePowerPlanId()":
            {
                Guid Read() => Guid.Parse(Regex.Match(PowerCfg("/getactivescheme"), @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}").Value);
                var expectedId = Read(); var id = context.Call(HostPowerPlanService.GetActivePowerPlanId);
                if (expectedId != Read()) throw new ApiCheckSkippedException("活动电源计划在采样期间变化，留待复测。");
                context.Equal<Guid?>(expectedId, id, "与 powercfg 活动计划对照");
                break;
            }
            case "HostProcessorPlatformService.GetManufacturersAsync()":
            {
                var baseline = Query("root/cimv2", "SELECT Manufacturer FROM Win32_Processor", row => Convert.ToString(row["Manufacturer"])!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var result = context.Call(() => HostProcessorPlatformService.GetManufacturersAsync().GetAwaiter().GetResult());
                context.Require(result.Success, "厂商查询成功：" + result.Error);
                context.Sequence(baseline, result.Data ?? [], "厂商去重与排序");
                break;
            }
            case "HostDiskService.GetHostOpticalDrivesAsync()":
            {
                var baseline = Query("root/cimv2", "SELECT PNPDeviceID,Drive,Caption FROM Win32_CDROMDrive", row => (Id: Convert.ToString(row["PNPDeviceID"]) ?? "", Drive: Convert.ToString(row["Drive"]) ?? "", Name: Convert.ToString(row["Caption"]) ?? ""))
                    .Where(p => p.Id.Length > 0).OrderBy(p => p.Id).ToArray();
                var result = context.Call(() => HostDiskService.GetHostOpticalDrivesAsync().GetAwaiter().GetResult());
                context.Require(result.Success, "光驱查询成功：" + result.Error);
                context.Sequence(baseline, (result.Data ?? []).Select(p => (p.PnpDeviceId, p.Drive, p.Model)).OrderBy(p => p.PnpDeviceId), "光驱身份、盘符与名称；零设备也是有效清单");
                break;
            }
            case "HyperVHostService.IsIommuEnabled()":
            {
                var values = Query("root/Microsoft/Windows/DeviceGuard", "SELECT AvailableSecurityProperties FROM Win32_DeviceGuard", row =>
                    ((Array?)row["AvailableSecurityProperties"])?.Cast<object>().Select(Convert.ToUInt32).ToArray() ?? []);
                context.Equal(values.Any(v => v.Contains(3u)), context.Call(HyperVHostService.IsIommuEnabled), "独立 DeviceGuard 属性代码 3");
                break;
            }
            case "HyperVHostService.IsVirtualizationEnabled()":
            {
                bool expectedValue;
                if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
                    expectedValue = Query("root/cimv2", "SELECT VMMonitorModeExtensions FROM Win32_Processor", row => Convert.ToBoolean(row["VMMonitorModeExtensions"])).Any(v => v);
                else if (System.Runtime.Intrinsics.X86.X86Base.IsSupported)
                {
                    var leaf = System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0);
                    if ((leaf.Ecx & int.MinValue) == 0)
                        expectedValue = Query("root/cimv2", "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor", row => Convert.ToBoolean(row["VirtualizationFirmwareEnabled"])).Any(v => v);
                    else
                    {
                        var vendor = System.Runtime.Intrinsics.X86.X86Base.CpuId(0x40000000, 0);
                        bool root = vendor.Ebx == 0x7263694D && vendor.Ecx == 0x666F736F && vendor.Edx == 0x76482074
                            && (System.Runtime.Intrinsics.X86.X86Base.CpuId(0x40000003, 0).Ebx & 1) != 0;
                        bool svm = unchecked((uint)System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x80000000), 0).Eax) >= 0x80000001
                            && (System.Runtime.Intrinsics.X86.X86Base.CpuId(unchecked((int)0x80000001), 0).Ecx & 4) != 0;
                        expectedValue = root || (leaf.Ecx & 32) != 0 || svm;
                    }
                }
                else throw new ApiCheckSkippedException("当前进程架构缺少 CPUID 基准，请使用本机架构发布包。");
                context.Equal(expectedValue, context.Call(HyperVHostService.IsVirtualizationEnabled), "系统固件标志与处理器虚拟化权限对照");
                break;
            }
            case "HyperVHostService.GetVmmsStatus()":
            {
                int before = ReadVmms();
                int result = context.Call(HyperVHostService.GetVmmsStatus);
                int after = ReadVmms();
                if (before != after) throw new ApiCheckSkippedException("VMMS 在采样期间改变状态，本项留待复测。");
                context.Equal(before, result, "独立服务控制管理器：运行=1，其他已安装状态=2，缺席=0");
                break;
            }
            case "HyperVHostService.IsHypervisorPresent()":
            {
                bool expected = ReadHypervisor();
                bool actual = context.Call(HyperVHostService.IsHypervisorPresent);
                context.Equal(expected, actual, "系统 HypervisorPresent 与 Core 结果");
                break;
            }
            case "HyperVHostService.IsHyperVWmiNamespaceAvailable()":
            {
                bool baseline = ReadManagementService();
                bool availability = context.Call(HyperVHostService.IsHyperVWmiNamespaceAvailable);
                if (baseline != ReadManagementService()) throw new ApiCheckSkippedException("管理服务在采样期间变化，本项留待复测。");
                context.Equal(baseline, availability, "独立查询管理服务的可访问性");
                break;
            }
        }
    }

    private static object? ReadRegistry(string path, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name);
    }

    private static List<T> Query<T>(string scope, string sql, Func<ManagementObject, T> map)
    {
        using var searcher = new ManagementObjectSearcher(scope, sql);
        using var rows = searcher.Get();
        var result = new List<T>();
        foreach (ManagementObject row in rows) using (row) result.Add(map(row));
        return result;
    }

    private static Dictionary<Guid, string> ReadPowerPlans()
    {
        var result = new Dictionary<Guid, string>();
        foreach (Match match in Regex.Matches(PowerCfg("/list"), @"(?im)([0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12})\s+\((.*)\)"))
            result.Add(Guid.Parse(match.Groups[1].Value), match.Groups[2].Value);
        if (result.Count == 0) throw new ApiCheckSkippedException("powercfg 未提供可解析的计划基准。");
        return result;
    }

    private static string PowerCfg(string argument)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("powercfg 启动失败。");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(true); throw new TimeoutException("powercfg 查询超时。"); }
        if (process.ExitCode != 0) throw new IOException(error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }

    private static bool ReadHypervisor()
    {
        using var query = new ManagementObjectSearcher("root/cimv2", "SELECT HypervisorPresent FROM Win32_ComputerSystem");
        using var rows = query.Get();
        bool found = false, present = false;
        foreach (ManagementObject row in rows) using (row) { found = true; present |= Convert.ToBoolean(row["HypervisorPresent"]); }
        if (!found) throw new ApiCheckSkippedException("独立系统基准返回空清单，保留为待复测。");
        return present;
    }

    private static bool ReadManagementService()
    {
        try
        {
            using var query = new ManagementObjectSearcher("root/virtualization/v2", "SELECT Name FROM Msvm_VirtualSystemManagementService");
            using var rows = query.Get();
            bool found = false;
            foreach (ManagementObject row in rows) using (row) found = true;
            return found;
        }
        catch (ManagementException error) when (error.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.AccessDenied) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static int ReadVmms()
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, "vmms", 4);
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                return error == 1060 ? 0 : throw new Win32Exception(error);
            }
            try
            {
                if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return status.CurrentState == 4 ? 1 : 2;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, CurrentState, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
