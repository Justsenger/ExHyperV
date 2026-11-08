using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>控制台的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmConsoleFields
{
    /// <summary>基本控制台开关：虚拟机是否配置了基本控制台所需的合成显示控制器。</summary>
    /// <fielddoc>
    /// <meaning>根据虚拟机配置中是否存在合成显示控制器判断基本控制台配置状态。实际连接状态由控制台连接接口提供。</meaning>
    /// <source>当前配置是否存在合成显示控制器，读取或派生 BasicEnabled。</source>
    /// <modification kind="supported" input="enable" submission="单项提交">仅支持第二代虚拟机，且要求虚拟机未运行。<see cref="VmConsoleSupportService.SetConsoleSupportAsync"/></modification>
    /// <values kind="options"><c>true</c>：配置了合成显示控制器；<c>false</c>：未配置。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> BasicEnabled = new("Console.BasicEnabled", "基本控制台开关", "控制台", VmQueryCollection.VirtualMachine, "DisplayPresence", "BasicEnabled");

    /// <summary>增强会话可用状态：Hyper-V 当前是否报告增强会话已可用。</summary>
    /// <fielddoc>
    /// <meaning>Hyper-V 当前是否报告增强会话已可用。</meaning>
    /// <values>EnhancedSessionModeState 为 2（允许且可用）时返回 true；3（不允许）、6（允许但尚不可用）及其他非空编码返回 false，无法由此 bool 区分具体原因。</values>
    /// <source>Msvm_ComputerSystem，读取或派生 EnhancedSessionModeState。</source>
    /// <modification kind="readonly">当前来宾是否提供增强会话的检测结果，不是可直接开启的虚拟机开关。</modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnhancedAvailable = new("Console.EnhancedAvailable", "增强会话可用状态", "控制台", VmQueryCollection.VirtualMachine, "Computer", "EnhancedSessionModeState", o => Convert.ToUInt16(o) == 2);

    /// <summary>控制台断开时锁定：是否配置在控制台断开连接时锁定来宾系统。</summary>
    /// <fielddoc>
    /// <meaning>是否配置在控制台断开连接时锁定来宾系统。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 LockOnDisconnect。</source>
    /// <modification kind="supported" input="enabled" submission="单项提交"><see cref="VmConsoleSupportService.SetLockOnDisconnectAsync"/></modification>
    /// <values kind="options"><c>true</c>：断开时锁定；<c>false</c>：不自动锁定。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> LockOnDisconnect = new("Console.LockOnDisconnect", "控制台断开时锁定", "控制台", VmQueryCollection.VirtualMachine, "Settings", "LockOnDisconnect");

    internal static readonly VmQueryField[] Items = [BasicEnabled, EnhancedAvailable, LockOnDisconnect];
}
