using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>引导的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmBootFields
{
    /// <summary>启动顺序位置：启动项在固件启动顺序中的位置，从 0 开始。</summary>
    /// <fielddoc>
    /// <meaning>启动项在固件启动顺序中的位置。</meaning>
    /// <source>VmBootService.GetBootOrderAsync 的固件启动条目，读取或派生 Order。</source>
    /// <references><see cref="VmBootService"/>。</references>
    /// <modification kind="supported" input="items" submission="完整顺序列表">顺序针对完整启动项列表。第二代通过稳定设备路径识别启动项；第一代虚拟机运行时不能修改启动顺序。<see cref="M:ExHyperV.Services.VmBootService.SetBootOrderAsync(System.Guid,System.Collections.Generic.List{ExHyperV.Models.BootOrderItem})"/></modification>
    /// <values kind="format">从 <c>0</c> 开始的整数序号。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> Order = new("Boot.Order", "启动顺序位置", "引导", VmQueryCollection.BootEntries, "Boot", "Order");

    /// <summary>启动项名称：固件启动项的显示名称。</summary>
    /// <fielddoc>
    /// <meaning>固件启动项的显示名称。</meaning>
    /// <source>VmBootService.GetBootOrderAsync 的固件启动条目，读取或派生 Name。</source>
    /// <references><see cref="VmBootService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("Boot.Name", "启动项名称", "引导", VmQueryCollection.BootEntries, "Boot", "Name");

    /// <summary>启动项类型：固件启动项对应的设备类型。</summary>
    /// <fielddoc>
    /// <meaning>固件启动项对应的设备类型。</meaning>
    /// <source>VmBootService.GetBootOrderAsync 的固件启动条目，读取或派生 Kind。</source>
    /// <references><see cref="VmBootService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<BootDeviceKind> Kind = new("Boot.Kind", "启动项类型", "引导", VmQueryCollection.BootEntries, "Boot", "Kind");

    /// <summary>启动项描述：启动模块提供的启动项说明。</summary>
    /// <fielddoc>
    /// <meaning>启动模块提供的启动项说明。</meaning>
    /// <source>VmBootService.GetBootOrderAsync 的固件启动条目，读取或派生 Description。</source>
    /// <references><see cref="VmBootService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> Description = new("Boot.Description", "启动项描述", "引导", VmQueryCollection.BootEntries, "Boot", "Description");

    internal static readonly VmQueryField[] Items = [Order, Name, Kind, Description];
}
