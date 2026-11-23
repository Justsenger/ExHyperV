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
    /// <modification kind="supported" input="enabled" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="M:ExHyperV.Services.VmSecurityService.SetSecureBootAsync(System.Guid,System.Boolean)"/></modification>
    /// <values kind="options"><c>true</c>：启用安全启动；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> SecureBootEnabled = new("Security.SecureBootEnabled", "安全启动", "安全", VmQueryCollection.VirtualMachine, "Settings", "SecureBootEnabled");

    /// <summary>安全启动模板：安全启动所使用的信任模板标识。</summary>
    /// <fielddoc>
    /// <meaning>安全启动所使用的信任模板标识。</meaning>
    /// <source>当前配置的 Msvm_VirtualSystemSettingData，读取或派生 SecureBootTemplateId。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="templateId" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="M:ExHyperV.Services.VmSecurityService.SetSecureBootTemplateAsync(System.Guid,System.Guid)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> SecureBootTemplateId = new("Security.SecureBootTemplateId", "安全启动模板", "安全", VmQueryCollection.VirtualMachine, "Settings", "SecureBootTemplateId");

    /// <summary>虚拟 TPM：是否为虚拟机启用虚拟 TPM。</summary>
    /// <fielddoc>
    /// <meaning>是否为虚拟机启用虚拟 TPM。</meaning>
    /// <source>Msvm_SecuritySettingData，读取或派生 TpmEnabled。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="M:ExHyperV.Services.VmSecurityService.SetTpmAsync(System.Guid,System.Boolean)"/></modification>
    /// <values kind="options"><c>true</c>：启用虚拟 TPM；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> TpmEnabled = new("Security.TpmEnabled", "虚拟 TPM", "安全", VmQueryCollection.VirtualMachine, "Security", "TpmEnabled");

    /// <summary>保存状态及迁移流量加密：是否对虚拟机的保存状态和迁移流量启用加密。</summary>
    /// <fielddoc>
    /// <meaning>是否对虚拟机的保存状态和迁移流量启用加密。</meaning>
    /// <source>Msvm_SecuritySettingData，读取或派生 EncryptStateAndVmMigrationTraffic。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="M:ExHyperV.Services.VmSecurityService.SetEncryptionAsync(System.Guid,System.Boolean)"/></modification>
    /// <values kind="options"><c>true</c>：启用保存状态和迁移流量加密；<c>false</c>：关闭。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> EncryptEnabled = new("Security.EncryptEnabled", "保存状态及迁移流量加密", "安全", VmQueryCollection.VirtualMachine, "Security", "EncryptStateAndVmMigrationTraffic");

    /// <summary>屏蔽保护请求：虚拟机配置是否请求屏蔽保护。</summary>
    /// <fielddoc>
    /// <meaning>ShieldingRequested 配置标记；不证明来宾已通过证明或保护已生效。</meaning>
    /// <source>Msvm_SecuritySettingData，读取或派生 ShieldingRequested。</source>
    /// <references><see cref="VmSecurityService"/>。</references>
    /// <modification kind="supported" input="enabled" submission="单项提交">仅支持第二代虚拟机，且要求完全关机。<see cref="M:ExHyperV.Services.VmSecurityService.SetShieldingAsync(System.Guid,System.Boolean)"/></modification>
    /// <values kind="options"><c>true</c>：请求屏蔽保护；<c>false</c>：未请求。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> Shielded = new("Security.Shielded", "屏蔽虚拟机", "安全", VmQueryCollection.VirtualMachine, "Security", "ShieldingRequested");

    /// <summary>宿主支持时返回虚拟 TPM 是否绑定到物理 TPM。</summary>
    /// <fielddoc>
    /// <meaning>绑定到宿主物理 TPM。启用绑定前须开启虚拟 TPM。</meaning>
    /// <modification kind="supported" input="VtpmPolicy.BindToHostTpm、enabled" submission="单项提交">要求第二代虚拟机完全关机且未开启屏蔽保护。<see cref="M:ExHyperV.Services.VmSecurityService.SetVtpmPolicyAsync(System.Guid,ExHyperV.Services.VtpmPolicy,System.Boolean)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> BindToHostTpm = new("Security.BindToHostTpm", "绑定宿主物理 TPM", "安全", VmQueryCollection.VirtualMachine, "Security", "BindToHostTpm");

    /// <summary>是否禁止向来宾提供基于虚拟化的安全功能。</summary>
    /// <fielddoc>
    /// <meaning>是否禁止向来宾提供基于虚拟化的安全功能。</meaning>
    /// <modification kind="supported" input="VtpmPolicy.VirtualizationBasedSecurityOptOut、enabled" submission="单项提交">要求第二代虚拟机完全关机且未开启屏蔽保护。<see cref="M:ExHyperV.Services.VmSecurityService.SetVtpmPolicyAsync(System.Guid,ExHyperV.Services.VtpmPolicy,System.Boolean)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<bool> VirtualizationBasedSecurityOptOut = new("Security.VirtualizationBasedSecurityOptOut", "禁用 VBS", "安全", VmQueryCollection.VirtualMachine, "Security", "VirtualizationBasedSecurityOptOut");

    /// <summary>迁移策略的原始枚举值。0 为默认，1 为禁止迁移。</summary>
    /// <fielddoc>
    /// <meaning>迁移策略的原始值。0 为默认，1 为禁止迁移。未知枚举值只读。</meaning>
    /// <modification kind="supported" input="VtpmPolicy.DisableMigration、enabled" submission="单项提交">要求第二代虚拟机完全关机且未开启屏蔽保护。<see cref="M:ExHyperV.Services.VmSecurityService.SetVtpmPolicyAsync(System.Guid,ExHyperV.Services.VtpmPolicy,System.Boolean)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<byte> MigratablePolicy = new("Security.MigratablePolicy", "跨宿主迁移策略", "安全", VmQueryCollection.VirtualMachine, "Security", "MigratablePolicy");

    internal static readonly VmQueryField[] Items = [SecureBootEnabled, SecureBootTemplateId, TpmEnabled, EncryptEnabled, Shielded,
        BindToHostTpm, VirtualizationBasedSecurityOptOut, MigratablePolicy];
}
