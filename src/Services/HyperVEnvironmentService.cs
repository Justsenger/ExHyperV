using ExHyperV.Tools.Api;
using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>
/// 提供 Hyper-V 环境、CPU 虚拟化及 IOMMU 状态的底层检测服务。
/// </summary>
public static class HyperVEnvironmentService
{
    /// <summary>
    /// 检测 CPU 虚拟化是否可用（BIOS 开启且 CPU 支持）。
    /// 逻辑：如果 Hypervisor 正在运行，则虚拟化必定开启；否则检查 CPU 固件标志。
    /// </summary>
    public static bool IsVirtualizationEnabled()
    {
        try
        {
            if (IsHypervisorPresent()) return true;

            var response = WmiApi.QueryAsync(
                "SELECT VirtualizationFirmwareEnabled FROM Win32_Processor",
                obj => obj["VirtualizationFirmwareEnabled"] is bool enabled && enabled,
                WmiScope.CimV2).GetAwaiter().GetResult();

            return response.Success && (response.Data?.Any(x => x) ?? false);
        }
        catch { return false; }
    }

    /// <summary>
    /// 仅检测 Hypervisor（Hyper-V）是否正在运行。
    /// </summary>
    public static bool IsHypervisorPresent()
    {
        try
        {
            var response = WmiApi.QueryAsync(
                "SELECT HypervisorPresent FROM Win32_ComputerSystem",
                obj => obj["HypervisorPresent"] is bool present && present,
                WmiScope.CimV2).GetAwaiter().GetResult();

            return response.Success && (response.Data?.Any(x => x) ?? false);
        }
        catch { return false; }
    }

    /// <summary>
    /// 检测 IOMMU（VT-d / AMD-Vi）状态。
    /// 通过 Win32_DeviceGuard 获取可用安全属性，属性值 3 表示 IOMMU 已启用。
    /// </summary>
    public static bool IsIommuEnabled()
    {
        try
        {
            var response = WmiApi.QueryAsync(
                "SELECT AvailableSecurityProperties FROM Win32_DeviceGuard",
                obj => obj["AvailableSecurityProperties"] as int[],
                WmiScope.DeviceGuard).GetAwaiter().GetResult();

            return response.Success &&
                   (response.Data?.Any(props => props?.Contains(3) ?? false) ?? false);
        }
        catch { return false; }
    }

    /// <summary>
    /// 检测 Hyper-V 虚拟机管理服务（vmms）的运行状态。
    /// 返回值：0 = 未安装，1 = 正在运行，2 = 已停止
    /// </summary>
    public static int GetVmmsStatus()
    {
        try
        {
            var response = WmiApi.QueryAsync(
                "SELECT State FROM Win32_Service WHERE Name = 'vmms'",
                obj => obj["State"]?.ToString() ?? string.Empty,
                WmiScope.CimV2).GetAwaiter().GetResult();

            if (!response.Success || response.Data == null || response.Data.Count == 0)
                return 0;

            return response.Data.Any(s => s.Equals("Running", StringComparison.OrdinalIgnoreCase))
                ? 1
                : 2;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 检查当前系统是否为 Server 系统。
    /// 只要不是 "WinNT"（工作站），即视为 Server。
    /// </summary>
    public static bool IsServerSystem()
    {
        try
        {
            using var key = Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            var type = key?.GetValue("ProductType")?.ToString();
            return type != null && !type.Equals("WinNT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}