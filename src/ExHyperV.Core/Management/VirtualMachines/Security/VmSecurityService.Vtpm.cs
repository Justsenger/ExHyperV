using System.Collections.Concurrent;
using System.Management;
using System.Security.Cryptography;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>由 HGS 解锁密钥的所有者和授权列表解析出的宿主身份。</summary>
/// <param name="Name">Guardian 的宿主显示名称。</param>
/// <param name="SigningThumbprint">签名公钥证书指纹。</param>
/// <param name="EncryptionThumbprint">加密公钥证书指纹。</param>
/// <param name="IsLocal">本机是否拥有可用的签名与解密私钥。</param>
/// <param name="IsOwner">该身份是否为密钥保护器的所有者。</param>
public sealed record VtpmHost(string Name, string SigningThumbprint, string EncryptionThumbprint, bool IsLocal, bool IsOwner);

/// <summary>虚拟 TPM 的即时策略。</summary>
public enum VtpmPolicy
{
    /// <summary>绑定宿主物理 TPM。</summary>
    BindToHostTpm,
    /// <summary>禁止向来宾提供 VBS。</summary>
    VirtualizationBasedSecurityOptOut,
    /// <summary>禁止跨宿主迁移。</summary>
    DisableMigration
}

/// <summary>当前配置与宿主身份快照。可空的策略或密钥状态表示宿主没有提供可识别的值。</summary>
/// <param name="TpmEnabled">当前虚拟 TPM 开关。</param>
/// <param name="EncryptEnabled">当前保存状态及迁移流量加密开关。</param>
/// <param name="Shielded">当前请求的屏蔽保护标志。</param>
/// <param name="KeyProtectorConfigured">当前是否配置密钥保护器，读取失败时可为 null。</param>
/// <param name="LocalGuardianName">本机 Guardian 名称，未识别时为空。</param>
/// <param name="LocalSigningThumbprint">已识别本机 Guardian 的签名证书指纹。</param>
/// <param name="AuthorizedHosts">从密钥保护器解码得到的所有者及授权宿主。</param>
/// <param name="HasPreviousKey">本进程是否保存了与当前密钥匹配的恢复点。</param>
/// <param name="KeyReadError">读取或解码密钥失败时的诊断。</param>
/// <param name="GuestStateFile">已核实的来宾状态文件路径，缺失时为空。</param>
/// <param name="GuestStateDescription">来宾状态文件的路径或状态说明。</param>
/// <param name="BindToHostTpm">宿主报告的物理 TPM 绑定设置，缺失时为 null。</param>
/// <param name="VirtualizationBasedSecurityOptOut">宿主报告的来宾 VBS 禁用标志，缺失时为 null。</param>
/// <param name="MigratablePolicy">宿主报告的原始迁移策略值，缺失时为 null。</param>
public sealed record VmVtpmInfo(bool TpmEnabled, bool EncryptEnabled, bool Shielded, bool? KeyProtectorConfigured,
    string? LocalGuardianName, string? LocalSigningThumbprint, IReadOnlyList<VtpmHost> AuthorizedHosts,
    bool HasPreviousKey, string? KeyReadError, string? GuestStateFile, string GuestStateDescription,
    bool? BindToHostTpm, bool? VirtualizationBasedSecurityOptOut, byte? MigratablePolicy)
{
    /// <summary>解锁密钥是否包含当前宿主可使用的身份。</summary>
    public bool HasLocalKey => AuthorizedHosts.Any(g => g.IsLocal);

    /// <summary>仅解释宿主 MOF 定义的枚举值，保留未知值。</summary>
    public bool? MigrationDisabled => MigratablePolicy switch { 0 => false, 1 => true, _ => null };
}

public static partial class VmSecurityService
{
    // VMMS 没有查询“上次可用密钥”的接口。只保存本进程成功提交前的封装数据，并校验现版摘要。
    internal sealed class RecoveryPoint(byte[] previous, byte[] current)
    {
        internal byte[] Previous { get; } = (byte[])previous.Clone();
        private readonly byte[] expectedDigest = SHA256.HashData(current);

        internal bool Matches(byte[] actual) => expectedDigest.AsSpan().SequenceEqual(SHA256.HashData(actual));
    }
    private static readonly ConcurrentDictionary<Guid, RecoveryPoint> recovery = new();
    private const string GuardianStoreLock = "vtpm:guardian-store";

    private static ManagementObject ReadInstance(string wql)
    {
        var scope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var rows = searcher.Get();
        return rows.Cast<ManagementObject>().SingleOrDefault() ??
            throw new CoreOperationException(ApiResponse.Fail("未找到所需的 Hyper-V 配置或服务。", kind: ApiErrorKind.NotFound));
    }

    // VMMS 可仅返回四字节头部。实际宿主身份由 HGS 解码后确认。
    internal static bool ContainsProtector(byte[] raw) => raw.Length > sizeof(uint);

    private static byte[] ReadKey(string securityXml)
    {
        using var service = ReadInstance(SecurityServiceWql);
        using var input = service.GetMethodParameters("GetKeyProtector");
        input["SecuritySettingData"] = securityXml;
        using var output = service.InvokeMethod("GetKeyProtector", input, null);
        if (output?["ReturnValue"] is not uint code) throw new InvalidDataException("读取解锁密钥时未返回执行状态。");
        if (code != 0) throw new CoreOperationException(ApiResponse.Fail($"读取解锁密钥失败（{code}）。",
            unchecked((int)code), ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed));
        return DecodeKeyOutput(output["KeyProtector"]);
    }

    internal static byte[] DecodeKeyOutput(object? value)
    {
        if (value is byte[] raw && (raw.Length == 0 || raw.Length > sizeof(uint) ||
            raw.AsSpan().SequenceEqual(new byte[4]) || raw.AsSpan().SequenceEqual(new byte[] { 1, 0, 0, 0 })))
            return raw;
        throw new InvalidDataException("宿主未返回可识别的密钥保护器数据。");
    }

    /// <summary>读取真实安全配置、宿主授权、来宾状态文件和各策略的可用性。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>当前安全开关、授权宿主与状态文件信息。密钥读取诊断见 KeyReadError。</returns>
    public static Task<ApiResponse<VmVtpmInfo>> GetVtpmStateAsync(Guid vmId) => Task.Run(() =>
    {
        try
        {
            if (vmId == Guid.Empty) throw new ArgumentException("虚拟机 GUID 不能为空。");
            using var system = ReadInstance(SystemSettingWql(vmId));
            using var security = ReadInstance(string.Format(SecuritySettingWql, vmId));
            bool? configured = null;
            bool previous = false;
            string? name = null, thumb = null, keyError = null;
            IReadOnlyList<VtpmHost> hosts = [];
            try
            {
                var raw = ReadKey(security.GetText(TextFormat.CimDtd20));
                configured = ContainsProtector(raw);
                previous = FindRecovery(vmId, raw) != null;
                using var hgs = new HgsGuardians();
                using var key = configured.Value ? hgs.Decode(raw) : null;
                if (key != null) hosts = hgs.Describe(key);
                var local = hgs.LocalGuardian(key);
                if (local != null)
                {
                    name = (string)local["Name"];
                    thumb = HgsGuardians.Thumbprint(local);
                }
            }
            catch (Exception e) { keyError = $"无法读取当前宿主密钥信息。{e.Message}"; }

            var (file, description) = GuestStateLocation(system);
            return ApiResponse<VmVtpmInfo>.Ok(new((bool)security["TpmEnabled"], (bool)security["EncryptStateAndVmMigrationTraffic"],
                (bool)security["ShieldingRequested"], configured, name, thumb, hosts, previous, keyError, file, description,
                security.TryGet<bool>("BindToHostTpm"), security.TryGet<bool>("VirtualizationBasedSecurityOptOut"),
                security.TryGet<byte>("MigratablePolicy")));
        }
        catch (Exception e)
        {
            var failure = ApiFailure.From(e);
            return ApiResponse<VmVtpmInfo>.Fail(failure.Message, failure.Code, failure.Source, e, failure.Kind);
        }
    });

    private static (string? Path, string Description) GuestStateLocation(ManagementObject settings)
    {
        string? Text(string property) => settings.HasProperty(property) ? settings[property] as string : null;
        string? root = Text("GuestStateDataRoot"), file = Text("GuestStateFile");
        if (!settings.HasProperty("GuestStateFile")) return (null, "当前宿主未提供 GuestStateFile 属性。");
        string? directory = string.IsNullOrWhiteSpace(root) ? Text("ConfigurationDataRoot") : root;
        if (!string.IsNullOrWhiteSpace(file))
        {
            try
            {
                string path = VmConfigurationPaths.Resolve(file, root);
                directory = Path.GetDirectoryName(path);
                if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory)) return (path, path);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception e) { return (null, $"无法核实状态文件。{e.Message}"); }
        }
        return (null, string.IsNullOrWhiteSpace(directory) ? "尚未生成，宿主未报告虚拟机目录。" : $"尚未生成，虚拟机目录为 {directory}");
    }

    private static RecoveryPoint? FindRecovery(Guid vmId, byte[] raw) =>
        recovery.TryGetValue(vmId, out var point) && point.Matches(raw) ? point : null;

    private static async Task<ApiResponse> ReplaceKeyAsync(Guid vmId, string xml, byte[] before, byte[] after, bool rememberPrevious = true)
    {
        if (before.AsSpan().SequenceEqual(after)) return ApiResponse.Ok();
        var result = await WmiApi.InvokeAsync(SecurityServiceWql, "SetKeyProtector", input =>
        {
            input["SecuritySettingData"] = xml;
            input["KeyProtector"] = after;
        });
        if (result.Success)
        {
            result = VerifyKeyWrite(after, () =>
            {
                using var current = ReadInstance(string.Format(SecuritySettingWql, vmId));
                return ReadKey(current.GetText(TextFormat.CimDtd20));
            });
            if (!result.Success) return result;
            if (rememberPrevious && ContainsProtector(before)) recovery[vmId] = new(before, after);
            else recovery.TryRemove(vmId, out _);
        }
        return result;
    }

    internal static ApiResponse VerifyKeyWrite(byte[] expected, Func<byte[]> read)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(expected), SHA256.HashData(read()))
                ? ApiResponse.Ok() : ApiResponse.Fail("回读的密钥保护器与提交值不符。", kind: ApiErrorKind.Conflict);
        }
        catch (Exception error)
        {
            var failure = ApiFailure.From(error);
            return ApiResponse.Fail(failure.Message, failure.Code, failure.Source, error, ApiErrorKind.OutcomeUnknown);
        }
    }

    private static Task<ApiResponse> PrepareLocalKeyAsync(Guid vmId, bool reissue) => HostResourceLock.RunAsync(GuardianStoreLock, async () =>
    {
        using var security = ReadInstance(string.Format(SecuritySettingWql, vmId));
        string xml = security.GetText(TextFormat.CimDtd20);
        byte[] before = ReadKey(xml);
        using var hgs = new HgsGuardians();
        // 自动准备密钥时保留已有授权。主动重新签发可替换无法解析的旧密钥。
        ManagementBaseObject? decoded = null;
        if (ContainsProtector(before))
        {
            try { decoded = hgs.Decode(before); }
            catch (Exception e) when (reissue && e is CoreOperationException or ManagementException or InvalidDataException) { }
        }
        using var key = decoded;
        if (!reissue && key != null && hgs.Describe(key).Any(host => host.IsLocal)) return ApiResponse.Ok();
        var steps = new List<OperationStepResult>();
        string step = "Guardian";
        try
        {
            var owner = hgs.EnsureLocal(key);
            steps.Add(new(step, OperationStepState.Succeeded));
            step = "GenerateKeyProtector";
            byte[] issued = hgs.Issue(owner, reissue ? null : key);
            steps.Add(new(step, OperationStepState.Succeeded));
            step = "SetKeyProtector";
            return await RecordSecurityStepAsync(step,
                ReplaceKeyAsync(vmId, xml, before, issued, rememberPrevious: key != null), steps);
        }
        catch (Exception error)
        {
            var failure = ApiFailure.From(error);
            var result = ApiResponse.Fail(failure.Message, failure.Code, failure.Source, error, failure.Kind);
            return result.WithSteps(steps.Append(OperationStepResult.From(step, result)));
        }
    });

    internal static VmModificationCondition GetKeyChangeCondition(int generation, VmState state) =>
        generation == 2 ? VmModificationCondition.PoweredOff(state) : VmModificationCondition.Unsupported("仅第 2 代虚拟机支持 vTPM。");

    private static Task<ApiResponse> ChangeKeyAsync(Guid vmId, Func<Task<ApiResponse>> action) =>
        VmSettingsWriteLock.RunAsync(vmId, async () =>
        {
            var state = await VmConfigurationState.ReadAsync(vmId);
            if (!state.HasData) return Failure(state);
            var condition = GetKeyChangeCondition(state.Data.Generation, state.Data.State);
            return condition.CanModify ? await action() : condition.ToResponse();
        });

    /// <summary>用当前宿主重新签发解锁密钥。关机的第 2 代虚拟机在受防护模式下也可修复。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>重新签发、提交及密钥回读的结果。</returns>
    public static Task<ApiResponse> ReissueLocalKeyProtectorAsync(Guid vmId) =>
        ChangeKeyAsync(vmId, () => PrepareLocalKeyAsync(vmId, reissue: true));

    /// <summary>恢复本进程上一次成功修改前的封装密钥。外部更改或进程重启后没有可确认的恢复点。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>原密钥的恢复结果。没有匹配的进程内恢复点时返回 InvalidState。</returns>
    public static Task<ApiResponse> RestorePreviousKeyProtectorAsync(Guid vmId) => ChangeKeyAsync(vmId, async () =>
    {
        using var security = ReadInstance(string.Format(SecuritySettingWql, vmId));
        string xml = security.GetText(TextFormat.CimDtd20);
        var raw = ReadKey(xml);
        var point = FindRecovery(vmId, raw);
        if (point == null) return ApiResponse.Fail("本次会话没有与当前配置匹配的上一版解锁密钥。", kind: ApiErrorKind.InvalidState);
        var result = await ReplaceKeyAsync(vmId, xml, raw, point.Previous);
        if (result.Success) recovery.TryRemove(vmId, out _);
        return result;
    });

    /// <summary>将当前宿主的公共元数据导出为 XML，或将其签名公钥证书导出为 CER、CRT。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="path">.xml 元数据或证书文件的路径。</param>
    /// <returns>公共证书或元数据文件的导出结果。</returns>
    public static Task<ApiResponse> ExportLocalGuardianAsync(Guid vmId, string path) =>
        VmSettingsWriteLock.RunAsync(vmId, () => HostResourceLock.RunAsync(GuardianStoreLock, async () =>
    {
        var state = await VmConfigurationState.ReadAsync(vmId);
        if (!state.HasData) return Failure(state);
        if (state.Data.Generation != 2) return VmModificationCondition.Unsupported().ToResponse();
        using var security = ReadInstance(string.Format(SecuritySettingWql, vmId));
        var raw = ReadKey(security.GetText(TextFormat.CimDtd20));
        using var hgs = new HgsGuardians();
        using var key = ContainsProtector(raw) ? hgs.Decode(raw) : null;
        hgs.Export(hgs.EnsureLocal(key), Path.GetFullPath(path));
        return ApiResponse.Ok();
    }));

    /// <summary>通过完整 Guardian XML 或已登记宿主的证书向现有密钥追加授权。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="path">.xml 元数据或证书文件的路径。</param>
    /// <param name="password">读取受密码保护的 PFX 时使用的密码。</param>
    /// <param name="metadataPath">单张证书不能唯一匹配已登记宿主时，用于核对证书对的 Guardian XML 路径。</param>
    /// <returns>追加指定 Guardian 并核验新密钥保护器的结果。</returns>
    public static Task<ApiResponse> GrantHostAccessAsync(Guid vmId, string path, string? password = null, string? metadataPath = null) =>
        EditHostsAsync(vmId, (hgs, key) => hgs.Grant(key, Path.GetFullPath(path), password, metadataPath));

    /// <summary>撤销指定外部宿主的授权，保留所有者和其余宿主。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="signingThumbprint">待撤销宿主的签名证书指纹。</param>
    /// <param name="encryptionThumbprint">同一宿主的加密证书指纹。</param>
    /// <returns>撤销指定证书对授权并核验密钥保护器的结果。</returns>
    public static Task<ApiResponse> RevokeHostAccessAsync(Guid vmId, string signingThumbprint, string encryptionThumbprint) =>
        EditHostsAsync(vmId, (hgs, key) => hgs.Revoke(key, signingThumbprint, encryptionThumbprint));

    private static Task<ApiResponse> EditHostsAsync(Guid vmId, Func<HgsGuardians, ManagementBaseObject, byte[]> edit) =>
        ChangeKeyAsync(vmId, () => HostResourceLock.RunAsync(GuardianStoreLock, async () =>
        {
            using var security = ReadInstance(string.Format(SecuritySettingWql, vmId));
            string xml = security.GetText(TextFormat.CimDtd20);
            var raw = ReadKey(xml);
            if (!ContainsProtector(raw)) return ApiResponse.Fail("请先签发当前宿主密钥。", kind: ApiErrorKind.InvalidState);
            using var hgs = new HgsGuardians();
            using var key = hgs.Decode(raw);
            return await ReplaceKeyAsync(vmId, xml, raw, edit(hgs, key));
        }));

    /// <summary>写入宿主实际支持的安全属性。未知迁移枚举值保留原样。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="policy">要修改的 vTPM 策略项。</param>
    /// <param name="enabled">请求的开关值。</param>
    /// <returns>策略写入结果，包含电源状态、屏蔽保护或提供者能力限制。</returns>
    public static Task<ApiResponse> SetVtpmPolicyAsync(Guid vmId, VtpmPolicy policy, bool enabled) =>
        ChangeKeyAsync(vmId, async () =>
        {
            using var security = ReadInstance(string.Format(SecuritySettingWql, vmId));
            if (security["ShieldingRequested"] is true)
                return ApiResponse.Fail("受防护模式已锁定此策略。", kind: ApiErrorKind.InvalidState);
            string field = policy switch
            {
                VtpmPolicy.BindToHostTpm => "BindToHostTpm",
                VtpmPolicy.VirtualizationBasedSecurityOptOut => "VirtualizationBasedSecurityOptOut",
                VtpmPolicy.DisableMigration => "MigratablePolicy",
                _ => throw new ArgumentException("未知的 vTPM 策略。")
            };
            if (!security.HasProperty(field) || (policy == VtpmPolicy.DisableMigration
                    ? security[field] is not byte migration || migration > 1 : security[field] is not bool))
                return ApiResponse.Fail($"当前宿主未提供可识别的 {field} 策略。", kind: ApiErrorKind.Unsupported);
            if (policy == VtpmPolicy.BindToHostTpm && security["TpmEnabled"] is not true)
                return ApiResponse.Fail("请先开启 vTPM。", kind: ApiErrorKind.InvalidState);
            security[field] = policy == VtpmPolicy.DisableMigration ? (byte)(enabled ? 1 : 0) : enabled;
            string xml = security.GetText(TextFormat.CimDtd20);
            return await WmiApi.InvokeAsync(SecurityServiceWql, "ModifySecuritySettings", p => p["SecuritySettingData"] = xml);
        });
}
