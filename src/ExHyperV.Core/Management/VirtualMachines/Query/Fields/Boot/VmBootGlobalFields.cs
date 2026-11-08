using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>引导的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmBootGlobalFields
{
    /// <summary>启动数字锁定：虚拟机启动时是否启用数字键盘锁定。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机启动时是否启用数字键盘锁定。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 BIOSNumLock。</source>
    /// <references><see cref="VmBootService"/>。</references>
    /// <modification kind="supported" input="enable" submission="单项提交">虚拟机未运行时可修改。<see cref="VmBootService.SetBootNumLockAsync"/></modification>
    /// <values kind="options"><c>true</c>：启动时开启 Num Lock；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> NumLock = new("BootGlobal.NumLock", "启动数字锁定", "引导", VmQueryCollection.VirtualMachine, "Settings", "BIOSNumLock");

    internal static readonly VmQueryField[] Items = [NumLock];
}
