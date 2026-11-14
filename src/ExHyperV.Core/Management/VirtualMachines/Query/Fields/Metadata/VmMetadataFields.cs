using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>随虚拟机配置保存的自定义数据，以键和值组成一项。</summary>
public static class VmMetadataFields
{
    /// <summary>元数据键：虚拟机 HostOnlyItems 元数据项的键名。</summary>
    /// <fielddoc>
    /// <values>非空字符串，区分大小写，例如 <c>MyApp.Environment</c>。</values>
    /// <meaning>每项自定义数据的名称，用于读取、修改或删除该项。数据由 VMMS 保存到虚拟机的 VMCX 配置文件（.vmcx），通过 HostOnlyItems 提供宿主侧读写，随虚拟机导出携带。</meaning>
    /// <source>当前配置 KVP 组件的 HostOnlyItems 键值，读取或派生 Key。</source>
    /// <references><see cref="VmMetadataService"/>。</references>
    /// <modification kind="unavailable">键名不能重命名。<see cref="VmMetadataService.SetAsync"/>、<see cref="VmMetadataService.RemoveAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Key = new("Metadata.Key", "元数据键", "自定义数据", VmQueryCollection.Metadata, "Metadata", "Key");

    /// <summary>元数据值：同一元数据项的字符串值，通过键名关联。</summary>
    /// <fielddoc>
    /// <values>字符串，可以为空；可保存普通文本或 JSON 等结构化文本。</values>
    /// <meaning>指定键对应的字符串内容。每个键保存一个值，内容格式由使用该键的程序约定。</meaning>
    /// <source>当前配置 KVP 组件的 HostOnlyItems 键值，读取或派生 Value。</source>
    /// <references><see cref="VmMetadataService"/>。</references>
    /// <modification kind="supported" input="key、value" submission="单键提交">相同键对应一项内容；写入不存在的键会新增该项。<see cref="VmMetadataService.SetAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Value = new("Metadata.Value", "元数据值", "自定义数据", VmQueryCollection.Metadata, "Metadata", "Value");

    internal static readonly VmQueryField[] Items = [Key, Value];
}
