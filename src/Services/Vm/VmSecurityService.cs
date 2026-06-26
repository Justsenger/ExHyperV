using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>
    /// 现有 VM 的安全设置读写（仅第 2 代有效、改动需 VM 关机）：
    ///   安全启动 = Msvm_VirtualSystemSettingData.SecureBootEnabled / SecureBootTemplateId → ModifySystemSettings
    ///   vTPM    = Msvm_SecuritySettingData.TpmEnabled → Msvm_SecurityService.ModifySecuritySettings
    ///   加密迁移 = Msvm_SecuritySettingData.EncryptStateAndVmMigrationTraffic → 同上
    /// 改动统一走 WmiApi.WithObjectAsync(与 VmBoot/VmMemory/VmEdit 等服务一致)；仅 TPM 启用的 HGS
    /// guardian/keyprotector 因需跨 scope + ManagementClass 特例，忠实照抄 VmCreateService.EnableTpmAsync 的裸 WMI。
    /// </summary>
    public static class VmSecurityService
    {
        // Msvm_SecuritySettingData 改单个标志：走 WithObjectAsync，serviceWql 指向 Msvm_SecurityService.ModifySecuritySettings
        private const string SecuritySettingWql = "SELECT * FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{0}%'";
        private const string SecurityServiceWql = "SELECT * FROM Msvm_SecurityService";

        /// <summary>现有 VM 的安全设置快照。Ok=false 表示查询失败。</summary>
        public readonly record struct VmSecurityInfo(bool Ok, bool SecureBootEnabled, string SecureBootTemplateId, bool TpmEnabled, bool EncryptEnabled, bool Shielded);

        /// <summary>读取当前安全启动(+模板) / TPM / 加密迁移 状态。</summary>
        public static async Task<VmSecurityInfo> GetSecuritySettingsAsync(string vmName)
        {
            try
            {
                var sb = await WmiApi.QueryFirstAsync(
                    $"SELECT SecureBootEnabled, SecureBootTemplateId FROM Msvm_VirtualSystemSettingData " +
                    $"WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => new { Sb = obj["SecureBootEnabled"] is bool b && b, Tpl = obj["SecureBootTemplateId"]?.ToString() ?? string.Empty },
                    WmiScope.HyperV);

                bool tpm = false, enc = false, shielded = false;
                string? guid = await GetVmGuidAsync(vmName);
                if (!string.IsNullOrEmpty(guid))
                {
                    var t = await WmiApi.QueryFirstAsync(
                        $"SELECT TpmEnabled, EncryptStateAndVmMigrationTraffic, ShieldingRequested FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{guid}%'",
                        obj => new { Tpm = obj["TpmEnabled"] is bool b && b, Enc = obj["EncryptStateAndVmMigrationTraffic"] is bool e && e, Shield = obj["ShieldingRequested"] is bool s && s },
                        WmiScope.HyperV);
                    if (t.Success && t.Data != null) { tpm = t.Data.Tpm; enc = t.Data.Enc; shielded = t.Data.Shield; }
                }
                bool ok = sb.Success;
                return new VmSecurityInfo(ok, ok && (sb.Data?.Sb ?? false), sb.Data?.Tpl ?? string.Empty, tpm, enc, shielded);
            }
            catch { return new VmSecurityInfo(false, false, string.Empty, false, false, false); }
        }

        /// <summary>
        /// 向主机实拿"支持的安全启动模板"(显示名 + GUID)——不硬编码，因模板随主机功能而变
        /// (如"受防护的开源 VM"需装防护功能才在列；GUID 各主机一致但应以主机为准)。
        /// 路径与微软库 IVMService.GetSecureBootTemplates 一致：
        ///   Msvm_VirtualSystemManagementCapabilities --(SettingsDefineCapabilities)--> Msvm_VirtualSystemSettingData
        /// 取其中带 SecureBootTemplateId 的项；Description 是主机本地化友好名。已在本机实测命中 3 个。
        /// </summary>
        public static async Task<List<(string Name, string Guid)>> GetSecureBootTemplatesAsync()
        {
            const string wql =
                "ASSOCIATORS OF {Msvm_VirtualSystemManagementCapabilities.InstanceID=\"Microsoft:VirtualSystemManagementCapabilities\"} " +
                "WHERE AssocClass=Msvm_SettingsDefineCapabilities ResultClass=Msvm_VirtualSystemSettingData";

            var resp = await WmiApi.QueryAsync(wql,
                obj => (
                    Name: obj["Description"]?.ToString() ?? obj["ElementName"]?.ToString() ?? string.Empty,
                    Guid: obj["SecureBootTemplateId"]?.ToString() ?? string.Empty),
                WmiScope.HyperV);

            var list = new List<(string Name, string Guid)>();
            if (resp.Success && resp.Data != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in resp.Data)
                    if (!string.IsNullOrEmpty(t.Guid) && seen.Add(t.Guid))
                        list.Add(t);
            }
            return list;
        }

        /// <summary>设置安全启动开关。需 VM 关机，否则引擎拒绝并返回错误。</summary>
        public static async Task<(bool Success, string Message)> SetSecureBootAsync(string vmName, bool enabled)
            => Wrap(await WmiApi.WithObjectAsync(SystemSettingWql(vmName),
                obj => { if (obj.HasProperty("SecureBootEnabled")) obj["SecureBootEnabled"] = enabled; },
                submitMethod: "ModifySystemSettings", submitParamName: "SystemSettings", wrapInArray: false));

        /// <summary>设置安全启动模板(GUID)。需 VM 关机、安全启动开启。vTPM 初始化后引擎会锁死此属性。</summary>
        public static async Task<(bool Success, string Message)> SetSecureBootTemplateAsync(string vmName, string templateGuid)
            => Wrap(await WmiApi.WithObjectAsync(SystemSettingWql(vmName),
                obj => { if (obj.HasProperty("SecureBootTemplateId")) obj["SecureBootTemplateId"] = templateGuid; },
                submitMethod: "ModifySystemSettings", submitParamName: "SystemSettings", wrapInArray: false));

        /// <summary>设置"加密状态和虚拟机迁移流量"。前置 TPM 已开(KeyProtector 已就绪)。需 VM 关机。</summary>
        public static async Task<(bool Success, string Message)> SetEncryptionAsync(string vmName, bool enabled)
        {
            string? guid = await GetVmGuidAsync(vmName);
            if (string.IsNullOrEmpty(guid)) return (false, Properties.Resources.Error_Net_VmNotFound);
            return await ModifySecurityFlagAsync(guid, s => s["EncryptStateAndVmMigrationTraffic"] = enabled);
        }

        /// <summary>启用/关闭"防护(Shielding)"。需 VM 关机。
        /// 启用走级联(仿微软 Set-VMSecurityPolicy -Shielded $true)：先确保 vTPM(建/复用 KP)，再开 加密 + ShieldingRequested。
        /// 不再要求"先开 TPM"前置。关闭仅置 ShieldingRequested=false(不动 TPM/加密)。</summary>
        public static async Task<(bool Success, string Message)> SetShieldingAsync(string vmName, bool enabled)
        {
            string? guid = await GetVmGuidAsync(vmName);
            if (string.IsNullOrEmpty(guid)) return (false, Properties.Resources.Error_Net_VmNotFound);

            if (!enabled)
                return await ModifySecurityFlagAsync(guid, s => s["ShieldingRequested"] = false);

            // 级联：先确保 vTPM(建/复用 KP + TpmEnabled)，再开 加密 + 防护
            var (ok, msg) = await EnsureVtpmEnabledAsync(guid);
            if (!ok) return (ok, msg);
            return await ModifySecurityFlagAsync(guid, s => { s["EncryptStateAndVmMigrationTraffic"] = true; s["ShieldingRequested"] = true; });
        }

        /// <summary>启用/禁用 vTPM。需 VM 关机。
        /// 启用：已有真 KP 则复用、只翻 TpmEnabled；仅首次(无 KP)才铸钥。只设 TpmEnabled、不连带加密(加密是独立开关)。
        /// 绝不在已有 KP 时重铸：NewByGuardians 每次生成全新数据加密密钥，重铸换钥会使已加密状态解不开 → 启动 0xC000A002。
        /// 微软 Enable-VMTPM 同理：只翻标志、从不铸 KP、也不碰加密。禁用：仅置 TpmEnabled=false。</summary>
        public static async Task<(bool Success, string Message)> SetTpmAsync(string vmName, bool enabled)
        {
            string? guid = await GetVmGuidAsync(vmName);
            if (string.IsNullOrEmpty(guid)) return (false, Properties.Resources.Error_Net_VmNotFound);

            if (!enabled)
                return await ModifySecurityFlagAsync(guid, s => s["TpmEnabled"] = false);

            return await EnsureVtpmEnabledAsync(guid);
        }

        // 确保 vTPM 开启(只翻 TpmEnabled、不连带加密)：已有真 KP(>4 字节占位)就复用、仅翻标志；仅首次(无 KP)才铸钥。
        // 供 SetTpmAsync 与 SetShieldingAsync(级联)共用。
        private static async Task<(bool Success, string Message)> EnsureVtpmEnabledAsync(string guid)
        {
            if (await GetKeyProtectorLenAsync(guid) > 4)
                return await ModifySecurityFlagAsync(guid, s => s["TpmEnabled"] = true);

            try { await EnableTpmAsync(guid); return (true, string.Empty); }
            catch (Exception ex) { return (false, FriendlyError.CleanLines(ex.Message)); }
        }

        // ── 内部 ────────────────────────────────────────────────────

        private static string SystemSettingWql(string vmName) =>
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

        private static (bool, string) Wrap(ApiResponse r) => (r.Success, r.Success ? string.Empty : FriendlyError.CleanLines(r.Error));

        // 改 Msvm_SecuritySettingData 某标志 → Msvm_SecurityService.ModifySecuritySettings（走 app 统一封装）
        private static async Task<(bool Success, string Message)> ModifySecurityFlagAsync(string vmGuid, Action<ManagementObject> setFlag)
            => Wrap(await WmiApi.WithObjectAsync(
                string.Format(SecuritySettingWql, vmGuid),
                setFlag,
                submitMethod: "ModifySecuritySettings",
                submitParamName: "SecuritySettingData",
                wrapInArray: false,
                serviceWql: SecurityServiceWql));

        private static async Task<string?> GetVmGuidAsync(string vmName)
        {
            var resp = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
                obj => obj["Name"]?.ToString() ?? string.Empty,
                WmiScope.HyperV);
            return resp.Success ? resp.Data : null;
        }

        // 读当前 KeyProtector 字节数：4 字节=占位(无真 KP)，数千字节=已有真 KP。
        // GetKeyProtector 返回 byte[]、不适配标准封装，故用只读裸 WMI(Task.Run)。
        private static Task<int> GetKeyProtectorLenAsync(string vmGuid) => Task.Run(() =>
        {
            try
            {
                var scope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var ssSearcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'"));
                using var ss = ssSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (ss == null) return 0;
                using var svcSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Msvm_SecurityService"));
                using var svc = svcSearcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (svc == null) return 0;
                using var p = svc.GetMethodParameters("GetKeyProtector");
                p["SecuritySettingData"] = ss.GetText(TextFormat.CimDtd20);
                using var o = svc.InvokeMethod("GetKeyProtector", p, null);
                return (o["KeyProtector"] as byte[])?.Length ?? 0;
            }
            catch { return 0; }
        });

        // 启用 TPM：HGS guardian + 本地 KeyProtector + SetKeyProtector + TpmEnabled=true。
        // 忠实照抄 VmCreateService.EnableTpmAsync —— 跨 scope(hgs) + ManagementClass.InvokeMethod 取 cmdletOutput +
        // kpParams.Properties["Owner"].Value 这些特例 WmiApi 未抽象，必须裸 WMI。
        private static Task EnableTpmAsync(string vmGuid) => Task.Run(() =>
        {
            var hyperVScope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            const string hgsScope = @"root\microsoft\windows\hgs";
            var hgsMs = WmiConnectionCache.GetManagementScope(hgsScope, WmiContext.Local);

            void WaitJob(string? jobPath)
            {
                if (string.IsNullOrEmpty(jobPath)) return;
                using var job = new ManagementObject(hyperVScope, new ManagementPath(jobPath), null);
                for (int i = 0; i < 400; i++)
                {
                    job.Get();
                    ushort state = (ushort)job["JobState"];
                    if (state == 7) return;
                    if (state > 7)
                        throw new InvalidOperationException(string.Format(Properties.Resources.Error_VmCreate_TpmJobFail, state));
                    System.Threading.Thread.Sleep(300);
                }
                throw new InvalidOperationException(string.Format(Properties.Resources.Error_VmCreate_TpmJobFail, 0));
            }

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
                using var createResult = guardianClass.InvokeMethod("NewByGenerateCertificates", createParams, null);
                guardian = createResult["cmdletOutput"] as ManagementObject;
            }
            if (guardian == null)
                throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoGuardian);

            // Step 2: 生成本地 KeyProtector
            using var kpClass = new ManagementClass(hgsMs, new ManagementPath("MSFT_HgsKeyProtector"), null);
            using var kpParams = kpClass.GetMethodParameters("NewByGuardians");
            kpParams["AllowUntrustedRoot"] = true;
            kpParams.Properties["Owner"].Value = guardian;   // 实测确认必须用 Properties[].Value
            using var kpResult = kpClass.InvokeMethod("NewByGuardians", kpParams, null);
            var kpInstance = kpResult["cmdletOutput"] as ManagementBaseObject;
            byte[]? rawData = kpInstance?["RawData"] as byte[];
            if (rawData == null || rawData.Length == 0)
                throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoKeyProtector);

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

            using var kpInParams = secSvc.GetMethodParameters("SetKeyProtector");
            kpInParams["SecuritySettingData"] = secXml;
            kpInParams["KeyProtector"] = rawData;
            using var kpOut = secSvc.InvokeMethod("SetKeyProtector", kpInParams, null);
            int kpRet = Convert.ToInt32(kpOut["ReturnValue"]);
            if (kpRet == 4096) WaitJob(kpOut["Job"]?.ToString());
            else if (kpRet != 0)
                throw new InvalidOperationException(string.Format(Properties.Resources.Error_VmCreate_SetKeyProtectorFail, kpRet));

            // Step 5: 仅 TpmEnabled=true(加密是独立开关，开 TPM 不连带开加密)
            secSetting["TpmEnabled"] = true;
            string updatedXml = secSetting.GetText(TextFormat.CimDtd20);

            using var modInParams = secSvc.GetMethodParameters("ModifySecuritySettings");
            modInParams["SecuritySettingData"] = updatedXml;
            using var modOut = secSvc.InvokeMethod("ModifySecuritySettings", modInParams, null);
            int modRet = Convert.ToInt32(modOut["ReturnValue"]);
            if (modRet == 4096) WaitJob(modOut["Job"]?.ToString());
            else if (modRet != 0)
                throw new InvalidOperationException(string.Format(Properties.Resources.Error_VmCreate_ModifySecuritySettingsFail, modRet));
        });
    }
}
