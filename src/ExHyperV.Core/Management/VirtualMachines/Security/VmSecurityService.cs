using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>读取和修改第 2 代虚拟机的安全启动、虚拟 TPM、保存状态及迁移流量加密设置。</summary>
    /// <remarks>修改前要求虚拟机完全关机；启用虚拟 TPM 时由 Core 准备所需的密钥保护配置。具体能力和互锁限制通过查询及校验接口读取。</remarks>
    public static class VmSecurityService
    {
        // Msvm_SecuritySettingData 改单个标志：走 WithObjectAsync，serviceWql 指向 Msvm_SecurityService.ModifySecuritySettings
        private const string SecuritySettingWql = "SELECT * FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{0}%'";
        private const string SecurityServiceWql = "SELECT * FROM Msvm_SecurityService";

        /// <summary>成功读取的安全设置快照；读取失败由外层 ApiResponse 表示。</summary>
        /// <param name="SecureBootEnabled">安全启动是否启用。</param>
        /// <param name="SecureBootTemplateId">当前安全启动模板 GUID 字符串。</param>
        /// <param name="TpmEnabled">虚拟 TPM 是否启用。</param>
        /// <param name="EncryptEnabled">保存状态及迁移流量加密是否启用。</param>
        /// <param name="Shielded">ShieldingRequested 配置标记，表示请求屏蔽保护；不证明来宾已通过证明或保护已生效。</param>
        public readonly record struct VmSecurityInfo(bool SecureBootEnabled, string SecureBootTemplateId, bool TpmEnabled, bool EncryptEnabled, bool Shielded);

        /// <summary>需要进行互锁及状态校验的安全设置项。</summary>
        public enum Setting {
            /// <summary>安全启动开关。</summary>
            SecureBoot,
            /// <summary>安全启动信任模板。</summary>
            SecureBootTemplate,
            /// <summary>虚拟可信平台模块开关。</summary>
            Tpm,
            /// <summary>保存状态和迁移流量加密开关。</summary>
            Encryption,
            /// <summary>虚拟机屏蔽开关。</summary>
            Shielding }
        /// <summary>当前上下文下整组及各项安全设置的编辑许可。</summary>
        /// <param name="CanEdit">整组安全配置是否允许编辑。</param>
        /// <param name="SecureBoot">是否允许编辑安全启动。</param>
        /// <param name="SecureBootTemplate">是否允许选择安全启动模板。</param>
        /// <param name="Tpm">是否允许切换 TPM。</param>
        /// <param name="Encryption">是否允许切换加密。</param>
        /// <param name="Shielding">是否允许切换屏蔽。</param>
        public readonly record struct Capabilities(bool CanEdit, bool SecureBoot, bool SecureBootTemplate, bool Tpm, bool Encryption, bool Shielding);

        /// <summary>判断虚拟机是否为第 2 代且已经完全关机。</summary>
        /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
        /// <param name="state">当前运行状态快照；只有 PoweredOff 满足关机要求。保存、挂起、休眠、过渡及未知状态均不满足。</param>
        /// <returns>第 2 代且 state 为 PoweredOff 时返回 true；各安全开关的互锁规则另由 GetCapabilities 检查。</returns>
        /// <remarks>此方法只判断传入的快照，不查询宿主。实际修改入口会重新读取状态；返回 true 不锁定后续状态。</remarks>
        public static bool CanEdit(int generation, VmState state) => generation == 2 && state == VmState.PoweredOff;

        /// <summary>仅按安全启动、TPM 和屏蔽状态计算各安全开关之间的互锁关系。</summary>
        /// <param name="secureBoot">当前安全启动状态。</param>
        /// <param name="tpm">当前虚拟 TPM 状态。</param>
        /// <param name="shielded">当前屏蔽状态。</param>
        /// <returns>每个安全设置是否允许编辑的标记；不包含运行状态及代数限制。</returns>
        public static Capabilities GetInterlocks(bool secureBoot, bool tpm, bool shielded)
        {
            bool unlocked = !shielded;
            // These depend only on the security selections. A caller can bind VM-state
            // availability separately without caching state inside each child control.
            return new(true, unlocked, unlocked && secureBoot, unlocked, unlocked && tpm, true);
        }

        /// <summary>合并代数、运行状态和安全开关互锁规则。</summary>
        /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
        /// <param name="state">当前运行状态快照；只有 PoweredOff 允许修改。此方法不自行刷新宿主状态。</param>
        /// <param name="secureBoot">当前安全启动状态。</param>
        /// <param name="tpm">当前虚拟 TPM 状态。</param>
        /// <param name="shielded">当前屏蔽状态。</param>
        /// <returns>当前上下文下各安全设置的编辑能力。</returns>
        public static Capabilities GetCapabilities(int generation, VmState state, bool secureBoot, bool tpm, bool shielded)
        {
            bool canEdit = CanEdit(generation, state);
            var interlocks = GetInterlocks(secureBoot, tpm, shielded);
            // vTPM 的初始化锁不由 TPM/KP 存在与否猜测；模板写入仍由原生引擎最终判断。
            return new(canEdit, canEdit && interlocks.SecureBoot, canEdit && interlocks.SecureBootTemplate,
                canEdit && interlocks.Tpm, canEdit && interlocks.Encryption, canEdit && interlocks.Shielding);
        }

        /// <summary>校验指定安全设置是否满足当前代数、运行状态和互锁规则。</summary>
        /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
        /// <param name="state">当前运行状态快照；保存、挂起、休眠、过渡及未知状态均返回需要完全关机。</param>
        /// <param name="current">当前成功读取的安全设置，含互锁所需开关。</param>
        /// <param name="setting">准备修改的安全设置项。</param>
        /// <returns>不允许修改的说明；允许继续时为 null。</returns>
        public static string? GetSettingError(int generation, VmState state, VmSecurityInfo current, Setting setting)
        {
            if (generation != 2) return Properties.Resources.Error_VerNotSupport;
            if (state != VmState.PoweredOff) return Properties.Resources.Warn_VmOffRequired;
            var caps = GetCapabilities(generation, state, current.SecureBootEnabled, current.TpmEnabled, current.Shielded);
            bool allowed = setting switch
            {
                Setting.SecureBoot => caps.SecureBoot,
                Setting.SecureBootTemplate => caps.SecureBootTemplate,
                Setting.Tpm => caps.Tpm,
                Setting.Encryption => caps.Encryption,
                Setting.Shielding => caps.Shielding,
                _ => false
            };
            return allowed ? null : Properties.Resources.Error_VerNotSupport;
        }

        private static ApiResponse Failure<T>(ApiResponse<T> response, string? missing = null) => ApiResponse.Fail(
            response.Success ? missing ?? Properties.Resources.Error_Net_VmNotFound : response.Error,
            response.Code, response.ErrorSource, response.Exception, response.Success ? ApiErrorKind.NotFound : response.ErrorKind);

        private static async Task<ApiResponse> ValidateSettingAsync(Guid vmId, Setting setting)
        {
            if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var state = await VmConfigurationState.ReadAsync(vmId);
            if (!state.HasData) return Failure(state);
            if (state.Data.Generation != 2)
                return ApiResponse.Fail(Properties.Resources.Error_VerNotSupport, kind: ApiErrorKind.Unsupported);
            if (!CanEdit(state.Data.Generation, state.Data.State))
                return ApiResponse.Fail(Properties.Resources.Warn_VmOffRequired, kind: ApiErrorKind.InvalidState);
            var current = await GetSecuritySettingsAsync(vmId);
            if (!current.HasData) return Failure(current);
            string? error = GetSettingError(state.Data.Generation, state.Data.State, current.Data, setting);
            return error == null ? ApiResponse.Ok() : ApiResponse.Fail(error,
                kind: ApiErrorKind.InvalidState);
        }

        /// <summary>读取安全启动模板、TPM、状态加密及屏蔽状态；任何必要子查询失败时返回失败。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>安全配置快照或查询失败。此模型把 null 或非 bool 的属性值折为 false；需要区分缺测与关闭时使用相应 VmSecurityFields 查询结果。</returns>
        public static async Task<ApiResponse<VmSecurityInfo>> GetSecuritySettingsAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return ApiResponse<VmSecurityInfo>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var sb = await WmiApi.QueryFirstAsync(
                $"SELECT SecureBootEnabled, SecureBootTemplateId FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
                obj => new { Sb = obj["SecureBootEnabled"] is bool b && b, Tpl = obj["SecureBootTemplateId"]?.ToString() ?? string.Empty }, WmiScope.HyperV);
            if (!sb.HasData)
            {
                var failure = Failure(sb);
                return ApiResponse<VmSecurityInfo>.Fail(failure.Error, failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind);
            }
            var security = await WmiApi.QueryFirstAsync(
                $"SELECT TpmEnabled, EncryptStateAndVmMigrationTraffic, ShieldingRequested FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{vmId:D}%'",
                obj => new { Tpm = obj["TpmEnabled"] is bool b && b, Enc = obj["EncryptStateAndVmMigrationTraffic"] is bool e && e, Shield = obj["ShieldingRequested"] is bool s && s }, WmiScope.HyperV);
            if (!security.HasData)
            {
                var failure = Failure(security, Properties.Resources.Error_VmCreate_NoSecuritySettings);
                return ApiResponse<VmSecurityInfo>.Fail(failure.Error, failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind);
            }
            return ApiResponse<VmSecurityInfo>.Ok(new(sb.Data.Sb, sb.Data.Tpl, security.Data.Tpm, security.Data.Enc, security.Data.Shield));
        }

        /// <summary>宿主提供的安全启动信任模板。</summary>
        /// <param name="Name">模板的显示名称。</param>
        /// <param name="Id">用于选择模板的稳定 GUID。</param>
        public sealed record SecureBootTemplate(string Name, Guid Id);

        /// <summary>读取宿主提供的安全启动模板，查询失败与成功空列表分别返回。</summary>
        /// <returns>模板名称及稳定 GUID 列表；不会把查询错误当作没有模板。</returns>
        public static async Task<ApiResponse<IReadOnlyList<SecureBootTemplate>>> GetSecureBootTemplatesAsync()
        {
            const string wql =
                "ASSOCIATORS OF {Msvm_VirtualSystemManagementCapabilities.InstanceID=\"Microsoft:VirtualSystemManagementCapabilities\"} " +
                "WHERE AssocClass=Msvm_SettingsDefineCapabilities ResultClass=Msvm_VirtualSystemSettingData";
            var response = await WmiApi.QueryAsync(wql, obj => new
            {
                Name = obj["Description"]?.ToString() ?? obj["ElementName"]?.ToString() ?? string.Empty,
                Id = obj["SecureBootTemplateId"]?.ToString() ?? string.Empty
            }, WmiScope.HyperV);
            if (!response.Success)
                return ApiResponse<IReadOnlyList<SecureBootTemplate>>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
            var templates = new List<SecureBootTemplate>();
            var seen = new HashSet<Guid>();
            foreach (var value in response.Data ?? [])
            {
                if (string.IsNullOrEmpty(value.Id)) continue;
                if (!Guid.TryParse(value.Id, out Guid id) || id == Guid.Empty)
                    return ApiResponse<IReadOnlyList<SecureBootTemplate>>.Fail("宿主返回的安全启动模板身份无效。", kind: ApiErrorKind.OperationFailed);
                if (seen.Add(id)) templates.Add(new(value.Name, id));
            }
            return ApiResponse<IReadOnlyList<SecureBootTemplate>>.Ok(templates.AsReadOnly());
        }

        /// <summary>校验当前状态后启用或关闭安全启动。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="enabled">是否启用安全启动。</param>
        /// <returns>写入结果或当前状态限制。</returns>
        public static async Task<ApiResponse> SetSecureBootAsync(Guid vmId, bool enabled)
        {
            var validation = await ValidateSettingAsync(vmId, Setting.SecureBoot);
            if (!validation.Success) return validation;
            return await WmiApi.WithObjectAsync(SystemSettingWql(vmId),
                obj => { if (!obj.HasProperty("SecureBootEnabled")) throw new NotSupportedException(Properties.Resources.Error_VerNotSupport); obj["SecureBootEnabled"] = enabled; },
                submitMethod: "ModifySystemSettings", submitParamName: "SystemSettings", wrapInArray: false);
        }

        /// <summary>设置安全启动信任模板。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="templateId">宿主安全启动模板查询结果中的 GUID。</param>
        /// <returns>写入结果；无效 GUID 在提交前拒绝。</returns>
        public static async Task<ApiResponse> SetSecureBootTemplateAsync(Guid vmId, Guid templateId)
        {
            if (templateId == Guid.Empty) return ApiResponse.Fail("安全启动模板 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var validation = await ValidateSettingAsync(vmId, Setting.SecureBootTemplate);
            if (!validation.Success) return validation;
            return await WmiApi.WithObjectAsync(SystemSettingWql(vmId),
                obj => { if (!obj.HasProperty("SecureBootTemplateId")) throw new NotSupportedException(Properties.Resources.Error_VerNotSupport); obj["SecureBootTemplateId"] = templateId.ToString("D"); },
                submitMethod: "ModifySystemSettings", submitParamName: "SystemSettings", wrapInArray: false);
        }

        /// <summary>修改保存状态和迁移流量加密，遵守 TPM 与屏蔽互锁规则。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="enabled">是否启用加密。</param>
        /// <returns>当前状态校验及底层写入结果。</returns>
        public static async Task<ApiResponse> SetEncryptionAsync(Guid vmId, bool enabled)
        {
            var validation = await ValidateSettingAsync(vmId, Setting.Encryption);
            return validation.Success ? await ModifySecurityFlagAsync(vmId, s => s["EncryptStateAndVmMigrationTraffic"] = enabled) : validation;
        }

        /// <summary>启用屏蔽时先确保 TPM，再启用加密与屏蔽；关闭时只清除屏蔽标记。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="enabled">是否启用屏蔽。</param>
        /// <returns>最终写入结果；Steps 记录按需执行的 Guardian、GenerateKeyProtector、SetKeyProtector、Tpm 和 Shielding。后续失败保留此前已完成的设置。</returns>
        public static async Task<ApiResponse> SetShieldingAsync(Guid vmId, bool enabled)
        {
            var validation = await ValidateSettingAsync(vmId, Setting.Shielding);
            if (!validation.Success) return validation;
            if (!enabled) return await RecordSecurityStepAsync("Shielding", ModifySecurityFlagAsync(vmId, s => s["ShieldingRequested"] = false));
            var tpm = await EnsureVtpmEnabledAsync(vmId);
            if (!tpm.Success) return tpm;
            return await RecordSecurityStepAsync("Shielding", ModifySecurityFlagAsync(vmId, s => { s["EncryptStateAndVmMigrationTraffic"] = true; s["ShieldingRequested"] = true; }), tpm.Steps);
        }

        /// <summary>配置虚拟 TPM，启用前按需建立本地密钥保护器。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="enabled">是否启用虚拟 TPM。</param>
        /// <returns>写入结果；Steps 记录按需执行的 Guardian、GenerateKeyProtector、SetKeyProtector 和 Tpm。失败保留已完成步骤；OutcomeUnknown 表示最终状态尚未确认。不需要或未开始的步骤不列出。</returns>
        public static async Task<ApiResponse> SetTpmAsync(Guid vmId, bool enabled)
        {
            var validation = await ValidateSettingAsync(vmId, Setting.Tpm);
            if (!validation.Success) return validation;
            return enabled ? await EnsureVtpmEnabledAsync(vmId) : await RecordSecurityStepAsync("Tpm", ModifySecurityFlagAsync(vmId, s => s["TpmEnabled"] = false));
        }

        private static async Task<ApiResponse> EnsureVtpmEnabledAsync(Guid vmId)
        {
            var length = await GetKeyProtectorLenAsync(vmId.ToString("D"));
            if (!length.HasData) return Failure(length);
            if (length.Data > 4) return await RecordSecurityStepAsync("Tpm", ModifySecurityFlagAsync(vmId, s => s["TpmEnabled"] = true));
            return await EnableTpmAsync(vmId.ToString("D"));
        }

        internal static async Task<ApiResponse> RecordSecurityStepAsync(string name, Task<ApiResponse> operation, IEnumerable<OperationStepResult>? previous = null)
        {
            var result = await operation;
            return result.WithSteps((previous ?? Array.Empty<OperationStepResult>()).Append(OperationStepResult.From(name, result)));
        }

        private static string SystemSettingWql(Guid vmId) =>
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}";

        private static Task<ApiResponse> ModifySecurityFlagAsync(Guid vmId, Action<ManagementObject> setFlag)
            => WmiApi.WithObjectAsync(string.Format(SecuritySettingWql, vmId.ToString("D")), setFlag,
                submitMethod: "ModifySecuritySettings", submitParamName: "SecuritySettingData", wrapInArray: false, serviceWql: SecurityServiceWql);

        private static async Task<(bool Success, string Message)> ByNameAsync(string vmName, Func<Guid, Task<ApiResponse>> execute)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return (false, identity.Error);
            var response = await execute(identity.Data);
            return (response.Success, response.Error);
        }
        internal static Task<(bool Success, string Message)> SetSecureBootAsync(string vmName, bool enabled) => ByNameAsync(vmName, id => SetSecureBootAsync(id, enabled));
        internal static Task<(bool Success, string Message)> SetEncryptionAsync(string vmName, bool enabled) => ByNameAsync(vmName, id => SetEncryptionAsync(id, enabled));
        internal static Task<(bool Success, string Message)> SetShieldingAsync(string vmName, bool enabled) => ByNameAsync(vmName, id => SetShieldingAsync(id, enabled));
        internal static Task<(bool Success, string Message)> SetTpmAsync(string vmName, bool enabled) => ByNameAsync(vmName, id => SetTpmAsync(id, enabled));
        internal static Task<(bool Success, string Message)> SetSecureBootTemplateAsync(string vmName, string templateGuid)
            => ByNameAsync(vmName, id => SetSecureBootTemplateAsync(id, Guid.TryParse(templateGuid, out Guid templateId) ? templateId : Guid.Empty));
        // 读当前 KeyProtector 字节数：4 字节=占位(无真 KP)，数千字节=已有真 KP。
        // GetKeyProtector 返回 byte[]、不适配标准封装，故用只读裸 WMI(Task.Run)。
        private static Task<ApiResponse<int>> GetKeyProtectorLenAsync(string vmGuid) => Task.Run(() =>
        {
            try
            {
                var scope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var ssSearcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'"));
                using var ss = ssSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (ss == null) return ApiResponse<int>.Fail(Properties.Resources.Error_VmCreate_NoSecuritySettings, kind: ApiErrorKind.NotFound);
                using var svcSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_SecurityService"));
                using var svc = svcSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (svc == null) return ApiResponse<int>.Fail(Properties.Resources.Error_VmCreate_NoSecurityService, kind: ApiErrorKind.NotFound);
                using var p = svc.GetMethodParameters("GetKeyProtector");
                p["SecuritySettingData"] = ss.GetText(TextFormat.CimDtd20);
                using var o = svc.InvokeMethod("GetKeyProtector", p, null);
                if (o["ReturnValue"] == null)
                    return ApiResponse<int>.Fail("密钥保护器查询未返回执行状态。", kind: ApiErrorKind.OperationFailed);
                int code = Convert.ToInt32(o["ReturnValue"]);
                return code == 0 ? ApiResponse<int>.Ok((o["KeyProtector"] as byte[])?.Length ?? 0)
                    : ApiResponse<int>.Fail("读取密钥保护器失败。", code, ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed);
            }
            catch (Exception error) { return ApiResponse<int>.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed); }
        });

        // 启用 TPM：HGS guardian + 本地 KeyProtector + SetKeyProtector + TpmEnabled=true。
        // HGS 使用嵌入的 cmdletOutput；Hyper-V 写入复用统一作业等待，不把停止等待当作作业失败。
        private static Task<ApiResponse> EnableTpmAsync(string vmGuid) => Task.Run(async () =>
        {
            bool submitted = false;
            var steps = new List<OperationStepResult>();
            string? step = null;
            try
            {
                var hyperVScope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                const string hgsScope = @"root\microsoft\windows\hgs";
                var hgsMs = WmiConnectionCache.GetManagementScope(hgsScope, WmiContext.Local);

                // Step 1: 取或创建 UntrustedGuardian
                using var guardianSearcher = new ManagementObjectSearcher(
                    hgsMs, new ObjectQuery("SELECT * FROM MSFT_HgsGuardian WHERE Name = 'UntrustedGuardian'"));
                using var guardianCol = guardianSearcher.Get();
                var guardian = guardianCol.Cast<ManagementObject>().FirstOrDefault();
                if (guardian == null)
                {
                    using var guardianClass = new ManagementClass(hgsMs, new ManagementPath("MSFT_HgsGuardian"), null);
                    using var createParams = guardianClass.GetMethodParameters("NewByGenerateCertificates");
                    createParams["Name"] = "UntrustedGuardian";
                    createParams["GenerateCertificates"] = true;
                    step = "Guardian";
                    submitted = true;
                    using var createResult = guardianClass.InvokeMethod("NewByGenerateCertificates", createParams, null);
                    guardian = createResult?["cmdletOutput"] as ManagementObject;
                    if (guardian is not null)
                    {
                        submitted = false;
                        steps.Add(new(step, OperationStepState.Succeeded));
                        step = null;
                    }
                }
                if (guardian == null)
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoGuardian);
                using var ownedGuardian = guardian;

                // Step 2: 生成本地 KeyProtector
                using var kpClass = new ManagementClass(hgsMs, new ManagementPath("MSFT_HgsKeyProtector"), null);
                using var kpParams = kpClass.GetMethodParameters("NewByGuardians");
                kpParams["AllowUntrustedRoot"] = true;
                kpParams.Properties["Owner"].Value = guardian;   // 通过 Properties[].Value 写入嵌入对象
                step = "GenerateKeyProtector";
                submitted = true;
                using var kpResult = kpClass.InvokeMethod("NewByGuardians", kpParams, null);
                using var kpInstance = kpResult?["cmdletOutput"] as ManagementBaseObject;
                byte[]? rawData = kpInstance?["RawData"] as byte[];
                if (rawData == null || rawData.Length == 0)
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoKeyProtector);
                submitted = false;
                steps.Add(new(step, OperationStepState.Succeeded));
                step = null;

                // Step 3: 取 Msvm_SecuritySettingData 序列化
                using var secSettingSearcher = new ManagementObjectSearcher(
                    hyperVScope, new ObjectQuery($"SELECT * FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'"));
                using var secSettingCol = secSettingSearcher.Get();
                using var secSetting = secSettingCol.Cast<ManagementObject>().FirstOrDefault();
                if (secSetting == null)
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoSecuritySettings);
                string secXml = secSetting.GetText(TextFormat.CimDtd20);

                // Step 4: Msvm_SecurityService.SetKeyProtector
                using var secSvcSearcher = new ManagementObjectSearcher(hyperVScope, new ObjectQuery("SELECT * FROM Msvm_SecurityService"));
                using var secSvcCol = secSvcSearcher.Get();
                using var secSvc = secSvcCol.Cast<ManagementObject>().FirstOrDefault();
                if (secSvc == null)
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoSecurityService);

                var setProtector = await WmiApi.InvokeOnObjectAsync(secSvc, "SetKeyProtector", parameters =>
                {
                    parameters["SecuritySettingData"] = secXml;
                    parameters["KeyProtector"] = rawData;
                }, timeout: TimeSpan.FromMinutes(2));
                steps.Add(OperationStepResult.From("SetKeyProtector", setProtector));
                if (!setProtector.Success) return setProtector.WithSteps(steps);

                // Step 5: 仅 TpmEnabled=true(加密是独立开关，开 TPM 不连带开加密)
                secSetting["TpmEnabled"] = true;
                string updatedXml = secSetting.GetText(TextFormat.CimDtd20);

                return await RecordSecurityStepAsync("Tpm", WmiApi.InvokeOnObjectAsync(secSvc, "ModifySecuritySettings",
                    parameters => parameters["SecuritySettingData"] = updatedXml, timeout: TimeSpan.FromMinutes(2)), steps);
            }
            catch (Exception error)
            {
                var failure = WmiJobWaiter.FromException(error, submitted);
                if (step != null) steps.Add(OperationStepResult.From(step, failure));
                return failure.WithSteps(steps);
            }
        });
    }
}
