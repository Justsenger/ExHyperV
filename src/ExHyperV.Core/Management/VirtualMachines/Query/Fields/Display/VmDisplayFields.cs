using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>显示的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmDisplayFields
{
    /// <summary>分辨率策略：虚拟显示控制器使用的分辨率策略。</summary>
    /// <fielddoc>
    /// <meaning>虚拟显示控制器使用的分辨率策略。</meaning>
    /// <unit>策略编码</unit>
    /// <values kind="options">2：最大分辨率；3：固定分辨率；4：默认策略。</values>
    /// <source>Msvm_SyntheticDisplayControllerSettingData，读取或派生 ResolutionType。</source>
    /// <references><see cref="VmDisplaySettingsService"/>。</references>
    /// <modification kind="supported" input="resolutionType" submission="分辨率策略与尺寸一起提交">虚拟机未运行时可修改。仅固定分辨率策略会更新宽度和高度。<see cref="M:ExHyperV.Services.VmDisplaySettingsService.SetResolutionAsync(System.Guid,System.Int32,System.Int32,System.Int32)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<int> ResolutionType = new("Display.ResolutionType", "分辨率策略", "显示", VmQueryCollection.VirtualMachine, "Display", "ResolutionType");

    /// <summary>显示宽度：虚拟显示控制器配置的水平分辨率，单位为像素。</summary>
    /// <fielddoc>
    /// <meaning>虚拟显示控制器配置的水平分辨率，单位为像素。</meaning>
    /// <source>Msvm_SyntheticDisplayControllerSettingData，读取或派生 HorizontalResolution。</source>
    /// <references><see cref="VmDisplaySettingsService"/>。</references>
    /// <modification kind="supported" input="width" submission="分辨率策略与尺寸一起提交">虚拟机未运行时可修改。固定分辨率策略下，宽度和高度向下取偶数，用于下次引导显示。<see cref="M:ExHyperV.Services.VmDisplaySettingsService.SetResolutionAsync(System.Guid,System.Int32,System.Int32,System.Int32)"/></modification>
    /// <values kind="format">整数，单位为像素；例如 <c>1920</c> 表示 1920 像素。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> Width = new("Display.Width", "显示宽度", "显示", VmQueryCollection.VirtualMachine, "Display", "HorizontalResolution");

    /// <summary>显示高度：虚拟显示控制器配置的垂直分辨率，单位为像素。</summary>
    /// <fielddoc>
    /// <meaning>虚拟显示控制器配置的垂直分辨率，单位为像素。</meaning>
    /// <source>Msvm_SyntheticDisplayControllerSettingData，读取或派生 VerticalResolution。</source>
    /// <references><see cref="VmDisplaySettingsService"/>。</references>
    /// <modification kind="supported" input="height" submission="分辨率策略与尺寸一起提交">虚拟机未运行时可修改。固定分辨率策略下，宽度和高度向下取偶数，用于下次引导显示。<see cref="M:ExHyperV.Services.VmDisplaySettingsService.SetResolutionAsync(System.Guid,System.Int32,System.Int32,System.Int32)"/></modification>
    /// <values kind="format">整数，单位为像素；例如 <c>1920</c> 表示 1920 像素。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> Height = new("Display.Height", "显示高度", "显示", VmQueryCollection.VirtualMachine, "Display", "VerticalResolution");

    internal static readonly VmQueryField[] Items = [ResolutionType, Width, Height];
}
