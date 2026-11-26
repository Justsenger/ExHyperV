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
    /// <modification kind="readonly" />
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

    /// <summary>增强会话剪贴板共享：是否允许在增强会话中同步剪贴板。</summary>
    /// <fielddoc>
    /// <meaning>是否在增强会话中启用双向剪贴板共享。修改将在下次连接或重连增强会话时生效。</meaning>
    /// <source>当前配置 KVP 组件的 HostOnlyItems 键 ExHyperV.ConsoleFeatures，未配置时默认为 true。</source>
    /// <modification kind="supported" input="clipboard, audioOutput, audioInput" submission="整组提交"><see cref="VmConsoleSupportService.SetEnhancedFeaturesAsync"/></modification>
    /// <values kind="options"><c>true</c>（默认）：允许共享剪贴板，<c>false</c>：禁用增强会话剪贴板通道。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnhancedClipboard = new("Console.EnhancedClipboard", "增强会话剪贴板共享", "控制台", VmQueryCollection.VirtualMachine, "ConsoleFeatures", "EnhancedClipboard");

    /// <summary>增强会话声音输出：是否允许在增强会话中输出虚拟机声音。</summary>
    /// <fielddoc>
    /// <meaning>是否在增强会话中将虚拟机声音输出转发到宿主机。修改将在下次连接或重连增强会话时生效。</meaning>
    /// <source>当前配置 KVP 组件的 HostOnlyItems 键 ExHyperV.ConsoleFeatures，未配置时默认为 true。</source>
    /// <modification kind="supported" input="clipboard, audioOutput, audioInput" submission="整组提交"><see cref="VmConsoleSupportService.SetEnhancedFeaturesAsync"/></modification>
    /// <values kind="options"><c>true</c>（默认）：允许接收来宾声音，<c>false</c>：禁用增强会话声音输出通道。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnhancedAudioOutput = new("Console.EnhancedAudioOutput", "增强会话声音输出", "控制台", VmQueryCollection.VirtualMachine, "ConsoleFeatures", "EnhancedAudioOutput");

    /// <summary>增强会话麦克风输入：是否允许在增强会话中传入本机麦克风。</summary>
    /// <fielddoc>
    /// <meaning>是否允许在增强会话中向虚拟机传入本机麦克风。工具栏默认仍保持静音直到手动开启，修改将在下次连接或重连时生效。</meaning>
    /// <source>当前配置 KVP 组件的 HostOnlyItems 键 ExHyperV.ConsoleFeatures，未配置时默认为 true。</source>
    /// <modification kind="supported" input="clipboard, audioOutput, audioInput" submission="整组提交"><see cref="VmConsoleSupportService.SetEnhancedFeaturesAsync"/></modification>
    /// <values kind="options"><c>true</c>（默认）：允许传入麦克风，<c>false</c>：禁用增强会话麦克风通道。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnhancedAudioInput = new("Console.EnhancedAudioInput", "增强会话麦克风输入", "控制台", VmQueryCollection.VirtualMachine, "ConsoleFeatures", "EnhancedAudioInput");

    internal static readonly VmQueryField[] Items = [BasicEnabled, EnhancedAvailable, LockOnDisconnect, EnhancedClipboard, EnhancedAudioOutput, EnhancedAudioInput];
}
