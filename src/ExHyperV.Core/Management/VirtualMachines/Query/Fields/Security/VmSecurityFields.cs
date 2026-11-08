using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>安全的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmSecurityFields
{
    /// <summary>安全启动：是否启用安全启动，在启动过程中验证引导组件的签名。</summary>
    /// <fielddoc>
    /// <meaning>是否启用安全启动，在启动过程中验证引导组件的签名。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 SecureBootEnabled。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="VmSecurityService.SetSecureBootAsync"/></modification>
    /// <values kind="options"><c>true</c>：启用安全启动；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> SecureBootEnabled = new("Security.SecureBootEnabled", "安全启动", "安全", VmQueryCollection.VirtualMachine, "Settings", "SecureBootEnabled");

    /// <summary>安全启动模板：安全启动所使用的信任模板标识。</summary>
    /// <fielddoc>
    /// <meaning>安全启动所使用的信任模板标识。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 SecureBootTemplateId。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="templateId" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="VmSecurityService.SetSecureBootTemplateAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> SecureBootTemplateId = new("Security.SecureBootTemplateId", "安全启动模板", "安全", VmQueryCollection.VirtualMachine, "Settings", "SecureBootTemplateId");

    /// <summary>虚拟 TPM：是否为虚拟机启用虚拟 TPM。</summary>
    /// <fielddoc>
    /// <meaning>是否为虚拟机启用虚拟 TPM。</meaning>
    /// <source>Msvm_SecuritySettingData，读取或派生 TpmEnabled。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="VmSecurityService.SetTpmAsync"/></modification>
    /// <values kind="options"><c>true</c>：启用虚拟 TPM；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> TpmEnabled = new("Security.TpmEnabled", "虚拟 TPM", "安全", VmQueryCollection.VirtualMachine, "Security", "TpmEnabled");

    /// <summary>保存状态及迁移流量加密：是否对虚拟机的保存状态和迁移流量启用加密。</summary>
    /// <fielddoc>
    /// <meaning>是否对虚拟机的保存状态和迁移流量启用加密。</meaning>
    /// <source>Msvm_SecuritySettingData，读取或派生 EncryptStateAndVmMigrationTraffic。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="VmSecurityService.SetEncryptionAsync"/></modification>
    /// <values kind="options"><c>true</c>：启用保存状态和迁移流量加密；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EncryptEnabled = new("Security.EncryptEnabled", "保存状态及迁移流量加密", "安全", VmQueryCollection.VirtualMachine, "Security", "EncryptStateAndVmMigrationTraffic");

    /// <summary>屏蔽保护请求：虚拟机配置是否请求屏蔽保护。</summary>
    /// <fielddoc>
    /// <meaning>ShieldingRequested 配置标记；不证明来宾已通过证明或保护已生效。</meaning>
    /// <source>Msvm_SecuritySettingData，读取或派生 ShieldingRequested。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="VmSecurityService.SetShieldingAsync"/></modification>
    /// <values kind="options"><c>true</c>：请求屏蔽保护；<c>false</c>：未请求。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> Shielded = new("Security.Shielded", "屏蔽虚拟机", "安全", VmQueryCollection.VirtualMachine, "Security", "ShieldingRequested");

    internal static readonly VmQueryField[] Items = [SecureBootEnabled, SecureBootTemplateId, TpmEnabled, EncryptEnabled, Shielded];
}
