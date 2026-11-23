namespace ExHyperV.Models;

/// <summary>一代虚拟机直接升级为二代时采用的存储和固件设置。</summary>
public sealed class VmGenerationUpgradeOptions
{
    /// <summary>自动转换所有独立 VHD。默认开启，关闭时遇到 VHD 会在预检阶段拒绝升级。</summary>
    public bool ConvertVhdToVhdx { get; init; } = true;

    /// <summary>在二代虚拟机上启用安全启动。默认关闭。</summary>
    public bool EnableSecureBoot { get; init; }

    /// <summary>安全启动模板 GUID，须同时启用安全启动。启用时省略模板将采用 Microsoft Windows 模板。</summary>
    public Guid? SecureBootTemplateId { get; init; }
}
