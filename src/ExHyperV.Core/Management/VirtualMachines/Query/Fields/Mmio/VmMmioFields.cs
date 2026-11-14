using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>MMIO的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmMmioFields
{
    /// <summary>低位 MMIO 空间：为设备预留的低位 MMIO 地址空间大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>为设备预留的低位 MMIO 地址空间大小。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 LowMmioGapSize。</source>
    /// <references><see cref="VmMmioService"/>。</references>
    /// <modification kind="supported" input="settings.LowSizeMiB、propertyName" submission="指定属性提交">虚拟机未运行时可修改。<see cref="M:ExHyperV.Services.VmMmioService.SetSettingAsync(System.Guid,ExHyperV.Models.VmMmioSettings,System.String)"/></modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> LowSizeMiB = new("Mmio.LowSizeMiB", "低位 MMIO 空间", "MMIO", VmQueryCollection.VirtualMachine, "Settings", "LowMmioGapSize");

    /// <summary>高位 MMIO 空间：为设备预留的高位 MMIO 地址空间大小，单位为 MiB。</summary>
    /// <fielddoc>
    /// <meaning>为设备预留的高位 MMIO 地址空间大小。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 HighMmioGapSize。</source>
    /// <references><see cref="VmMmioService"/>。</references>
    /// <modification kind="supported" input="settings.HighSizeMiB、propertyName" submission="指定属性提交">虚拟机未运行时可修改。<see cref="M:ExHyperV.Services.VmMmioService.SetSettingAsync(System.Guid,ExHyperV.Models.VmMmioSettings,System.String)"/></modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> HighSizeMiB = new("Mmio.HighSizeMiB", "高位 MMIO 空间", "MMIO", VmQueryCollection.VirtualMachine, "Settings", "HighMmioGapSize");

    /// <summary>高位 MMIO 基址：高位 MMIO 地址空间的起始地址，以 MiB 表示。</summary>
    /// <fielddoc>
    /// <meaning>高位 MMIO 地址空间的起始地址，以 MiB 表示。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 HighMmioGapBase。</source>
    /// <references><see cref="VmMmioService"/>。</references>
    /// <modification kind="supported" input="settings.HighBaseMiB、propertyName" submission="指定属性提交">虚拟机未运行时可修改。<see cref="M:ExHyperV.Services.VmMmioService.SetSettingAsync(System.Guid,ExHyperV.Models.VmMmioSettings,System.String)"/></modification>
    /// <values kind="format">单位为 MiB；1 MiB = 1,048,576 字节。例如 <c>1024</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> HighBaseMiB = new("Mmio.HighBaseMiB", "高位 MMIO 基址", "MMIO", VmQueryCollection.VirtualMachine, "Settings", "HighMmioGapBase");

    internal static readonly VmQueryField[] Items = [LowSizeMiB, HighSizeMiB, HighBaseMiB];
}
