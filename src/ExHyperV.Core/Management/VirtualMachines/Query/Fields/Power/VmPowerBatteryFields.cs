using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>电源的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmPowerBatteryFields
{
    /// <summary>来宾重启时关机：是否将来宾的重启请求处理为关闭虚拟机。</summary>
    /// <fielddoc>
    /// <meaning>是否将来宾的重启请求处理为关闭虚拟机。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 TurnOffOnGuestRestart。</source>
    /// <references><see cref="VmPowerService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交"><see cref="VmPowerService.SetTurnOffOnGuestRestartAsync"/></modification>
    /// <values kind="options"><c>true</c>：来宾重启时关闭虚拟机；<c>false</c>：执行重启。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> TurnOffOnGuestRestart = new("PowerBattery.TurnOffOnGuestRestart", "来宾重启时关机", "电源", VmQueryCollection.VirtualMachine, "Settings", "TurnOffOnGuestRestart");

    /// <summary>来宾休眠支持：是否为虚拟机启用来宾休眠支持。</summary>
    /// <fielddoc>
    /// <meaning>是否为虚拟机启用来宾休眠支持。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 EnableHibernation。</source>
    /// <applicability>Windows 来宾使用休眠需要支持 S4，并具有完整型休眠文件。</applicability>
    /// <verification>研究规则 released-domain-153:1：17763、28000 来宾组件先回复接受请求，再执行休眠 API。许可已保存、请求已接受、实际已休眠须分别判断；当前设置接口只负责第一项。</verification>
    /// <references><see cref="VmPowerService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">虚拟机未运行时可修改。<see cref="VmPowerService.SetHibernationEnabledAsync"/></modification>
    /// <values kind="options"><c>true</c>：允许来宾休眠；<c>false</c>：不允许。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EnableHibernation = new("PowerBattery.EnableHibernation", "来宾休眠支持", "电源", VmQueryCollection.VirtualMachine, "Settings", "EnableHibernation");

    /// <summary>虚拟电池开关：虚拟机是否配置了虚拟电池组件。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机是否配置了虚拟电池组件。</meaning>
    /// <source>虚拟电池默认模板与当前配置的电池组件，读取或派生 BatteryEnabled。</source>
    /// <absence>宿主未提供虚拟电池默认模板时返回 Unsupported。</absence>
    /// <references><see cref="VmPowerService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交"><see cref="VmPowerService.SetBatteryEnabledAsync"/></modification>
    /// <values kind="options"><c>true</c>：配置了虚拟电池；<c>false</c>：未配置。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> BatteryEnabled = new("PowerBattery.BatteryEnabled", "虚拟电池开关", "电源", VmQueryCollection.VirtualMachine, "Battery", "BatteryEnabled");

    internal static readonly VmQueryField[] Items = [TurnOffOnGuestRestart, EnableHibernation, BatteryEnabled];
}
