using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>存储的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmStorageGlobalFields
{
    /// <summary>完整 SCSI 命令集：是否为整台虚拟机开放完整 SCSI 命令集。</summary>
    /// <fielddoc>
    /// <meaning>是否为整台虚拟机开放完整 SCSI 命令集。</meaning>
    /// <applicability>虚拟机级全局配置，通过虚拟机记录读取。</applicability>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 AllowFullSCSICommandSet。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">作用于整台虚拟机。<see cref="VmStorageService.SetFullScsiCommandSetEnabledAsync"/></modification>
    /// <values kind="options"><c>true</c>：开放完整 SCSI 命令集；<c>false</c>：不开放。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> AllowFullScsiCommandSet = new("StorageGlobal.AllowFullScsiCommandSet", "完整 SCSI 命令集", "存储", VmQueryCollection.VirtualMachine, "Settings", "AllowFullSCSICommandSet");

    internal static readonly VmQueryField[] Items = [AllowFullScsiCommandSet];
}
