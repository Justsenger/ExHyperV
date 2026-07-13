using System.IO;
using System.Management;
using ExHyperV.Tools;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public static class VmCreateService
    {
        private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

        public static async Task<List<string>> GetSupportedVersionsAsync()
        {
            var capsResp = await WmiApi.QueryFirstAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementCapabilities",
                obj => obj,
                WmiScope.HyperV);

            if (!capsResp.HasData)
                return new List<string>();

            var settingsResp = await WmiApi.QueryRelatedAsync(
                capsResp.Data!,
                "Msvm_VirtualSystemSettingData",
                obj => obj["Version"]?.ToString() ?? "",
                scope: WmiScope.HyperV);

            var versions = (settingsResp.Data ?? new List<string>())
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct()
                .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
                .ToList();

            return versions.Count > 0 ? versions : new List<string>();
        }

        private sealed record IsolationItem(string InstanceID, bool IsolationEnabled, int IsolationType);

        public static async Task<(bool Supported, List<string> Types)> GetIsolationSupportAsync()
        {
            var capsResp = await WmiApi.QueryFirstAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementCapabilities",
                obj => obj,
                WmiScope.HyperV);

            if (!capsResp.HasData)
                return (false, new List<string> { "Disabled" });

            var settingsResp = await WmiApi.QueryRelatedAsync(
                capsResp.Data!,
                "Msvm_VirtualSystemSettingData",
                obj => new IsolationItem(
                    obj["InstanceID"]?.ToString() ?? "",
                    obj["GuestStateIsolationEnabled"] is bool b && b,
                    Convert.ToInt32(obj["GuestStateIsolationType"] ?? -1)
                ),
                scope: WmiScope.HyperV);

            var isolationTypes = (settingsResp.Data ?? new List<IsolationItem>())
                .Where(s => s.InstanceID.IndexOf("GuestStateIsolationType",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(s => s.IsolationType switch
                {
                    0 => "TrustedLaunch",
                    1 => "VBS",
                    2 => "SNP",
                    3 => "TDX",
                    _ => "Disabled"
                })
                .Distinct()
                .ToList();

            if (isolationTypes.Count == 0)
                return (false, new List<string> { "Disabled" });

            if (!isolationTypes.Contains("Disabled"))
                isolationTypes.Add("Disabled");

            return (true, isolationTypes);
        }
        public static async Task<(string DefaultVmPath, string DefaultVhdPath)> GetHostDefaultPathsAsync()
        {
            // 26100 起 DefaultVirtualMachinePath 已空，改用 DefaultExternalDataRoot（实测 = Get-VMHost.VirtualMachinePath）；旧 build 回退老属性
            var resp = await WmiApi.QueryFirstAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData",
                obj => (
                    VmPath: obj.TryGetString("DefaultExternalDataRoot")
                            ?? obj.TryGetString("DefaultVirtualMachinePath")
                            ?? @"C:\ProgramData\Microsoft\Windows\Hyper-V",
                    VhdPath: obj.TryGetString("DefaultVirtualHardDiskPath") ?? ""
                ),
                WmiScope.HyperV);

            if (resp.HasData)
                return (resp.Data.VmPath, resp.Data.VhdPath);

            return (@"C:\ProgramData\Microsoft\Windows\Hyper-V", "");
        }

        public static async Task<(bool Success, string Message)> CreateVirtualMachineAsync(VmCreationParams p)
        {
            // 总是查重(含手动命名)：撞到已存在的文件夹 / 在册同名 VM 时自动改名 "test3 (2)"…，
            // 避开 DefineSystem/建 VHD 的 ERROR_FILE_EXISTS(0x80070050)。用户预期：同名也应自动改名而非报错。
            string finalVmName = await GetUniqueVmNameAsync(p.Name, p.Path);
            bool vmCreated = false;   // DefineSystem 成功后置 true;失败回滚的依据
            try
            {
                // ── Step 1: 创建目录 ──────────────────────────────
                string vmHomeFolder = Path.Combine(p.Path, finalVmName);
                if (!Directory.Exists(vmHomeFolder))
                    Directory.CreateDirectory(vmHomeFolder);

                // 新建磁盘：用户没手选保存位置时，默认放进 VM 目录、并跟随查重改名后的最终名；
                // 手选过(IsDiskPathManual)则尊重用户选的完整路径，不覆盖。
                if (p.DiskMode == 0 && !p.IsDiskPathManual)
                    p.VhdPath = Path.Combine(vmHomeFolder, $"{finalVmName}.vhdx");

                // ── Step 2: DefineSystem 创建 VM ──────────────────
                using var svcForScope = WmiApi.GetVirtualSystemManagementService();

                var vssdClass = new ManagementClass(
                    svcForScope.Scope,
                    new ManagementPath("Msvm_VirtualSystemSettingData"),
                    null);
                using var vssd = vssdClass.CreateInstance();

                vssd["ElementName"] = finalVmName;
                vssd["VirtualSystemSubType"] = p.Generation == 2
                    ? "Microsoft:Hyper-V:SubType:2"
                    : "Microsoft:Hyper-V:SubType:1";
                vssd["Version"] = p.Version;
                vssd["ConfigurationDataRoot"] = Path.Combine(p.Path, finalVmName);
                vssd["SnapshotDataRoot"] = Path.Combine(p.Path, finalVmName);
                vssd["SwapFileDataRoot"] = Path.Combine(p.Path, finalVmName);

                // 新建即默认启动时 NumLock 开（Gen2 默认关，会致控制台连上把宿主 NumLock 带掉）；TrySetAlways 内置 HasProperty 守卫，防个别 build 无此属性
                vssd.TrySetAlways("BIOSNumLock", true);

                if (p.Generation == 2 && p.IsolationType != "Disabled" &&
                    !string.IsNullOrEmpty(p.IsolationType))
                {
                    vssd.TrySet("GuestStateIsolationType", p.IsolationType);
                }

                string vssdXml = vssd.GetText(TextFormat.CimDtd20);

                var defineResp = await WmiApi.InvokeWithResultAsync(
                    ServiceWql,
                    "DefineSystem",
                    p2 =>
                    {
                        p2["SystemSettings"] = vssdXml;
                        p2["ResourceSettings"] = Array.Empty<string>();
                        p2["ReferenceConfiguration"] = null;
                    },
                    resultField: "ResultingSystem");

                if (!defineResp.Success)
                    return (false, defineResp.Error);

                // DefineSystem 已成功，VM 此刻可能/已在 Hyper-V 中创建——此后任一步骤(含下面取路径/GUID)失败都
                // 必须走 catch 回滚删除，避免留孤儿半成品。故标志位提前到这里、后续失败一律 throw 而非 return。
                vmCreated = true;

                string? vmPath = defineResp.Data?.FirstOrDefault();
                if (string.IsNullOrEmpty(vmPath))
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoSystemPath);

                // ── Step 3: 取新 VM 的 Name（GUID）───────────────
                string vmGuid;
                using (var vmObj = new ManagementObject(svcForScope.Scope, new ManagementPath(vmPath), null))
                {
                    vmObj.Get();
                    vmGuid = vmObj["Name"]?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(vmGuid))
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoGuid);

                // ── Step 4: 处理器设置 ────────────────────────────
                var procSettings = new VmProcessorSettings { Count = p.ProcessorCount };
                var procResult = await VmProcessorService.SetVmProcessorAsync(finalVmName, procSettings);
                if (!procResult.Success)
                    throw new InvalidOperationException(procResult.Message);

                // ── Step 5: 内存设置 ──────────────────────────────
                var memSettings = new VmMemorySettings
                {
                    Startup = p.MemoryMb,
                    DynamicMemoryEnabled = p.EnableDynamicMemory,
                    Minimum = p.EnableDynamicMemory ? p.MemoryMb / 2 : p.MemoryMb,
                    Maximum = p.EnableDynamicMemory ? p.MemoryMb * 4 : p.MemoryMb,
                    Buffer = 20,
                    Priority = 50
                };
                var memResult = await VmMemoryService.SetVmMemorySettingsAsync(finalVmName, memSettings, false);
                if (!memResult.Success)
                    throw new InvalidOperationException(memResult.Message);

                // ── Step 6: 网卡 ──────────────────────────────────
                var addNicResult = await VmNetworkService.AddNetworkAdapterAsync(finalVmName);
                if (!addNicResult.Success)
                    throw new InvalidOperationException(addNicResult.Message);
                if (!string.IsNullOrWhiteSpace(p.SwitchName) &&
                    p.SwitchName != Properties.Resources.Common_None)
                {
                    var adapters = await VmNetworkService.GetNetworkAdaptersAsync(finalVmName);
                    var adapter = adapters.FirstOrDefault();
                    if (adapter != null)
                    {
                        adapter.IsConnected = true;
                        adapter.SwitchName = p.SwitchName;
                        var connResult = await VmNetworkService.UpdateConnectionAsync(finalVmName, adapter);
                        if (!connResult.Success)
                            throw new InvalidOperationException(connResult.Message);
                    }
                }

                // ── Step 7: 磁盘 ──────────────────────────────────
                if (p.DiskMode == 0)
                {
                    var diskResult = await VmStorageService.AddDriveAsync(
                        finalVmName,
                        p.Generation == 2 ? "SCSI" : "IDE", 0, 0,
                        "HardDisk", p.VhdPath, false,
                        isNew: true, sizeGb: (int)p.DiskSizeGb);
                    if (!diskResult.Success)
                        throw new InvalidOperationException(diskResult.Message);
                }
                else if (p.DiskMode == 1 && !string.IsNullOrEmpty(p.VhdPath))
                {
                    var diskResult = await VmStorageService.AddDriveAsync(
                        finalVmName,
                        p.Generation == 2 ? "SCSI" : "IDE", 0, 0,
                        "HardDisk", p.VhdPath, false);
                    if (!diskResult.Success)
                        throw new InvalidOperationException(diskResult.Message);
                }

                // ── Step 8: DVD ───────────────────────────────────
                if (!string.IsNullOrWhiteSpace(p.IsoPath) && File.Exists(p.IsoPath))
                {
                    string dvdCtrl = p.Generation == 1 ? "IDE" : "SCSI";
                    int dvdCtrlNum = p.Generation == 1 ? 1 : 0;
                    int dvdLoc = p.Generation == 1 ? 0 : 1;

                    var dvdResult = await VmStorageService.AddDriveAsync(
                        finalVmName, dvdCtrl, dvdCtrlNum, dvdLoc,
                        "DvdDrive", p.IsoPath, false);
                    if (!dvdResult.Success)
                        throw new InvalidOperationException(dvdResult.Message);
                }

                // ── Step 9: Gen2 安全启动 ─────────────────────────
                if (p.Generation == 2)
                {
                    string settingsWql = $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                        $"WHERE VirtualSystemIdentifier = '{vmGuid}' " +
                        $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

                    await WmiApi.WithObjectAsync(
                        wql: settingsWql,
                        modifier: obj =>
                        {
                            if (obj.HasProperty("SecureBootEnabled"))
                                obj["SecureBootEnabled"] = p.EnableSecureBoot;
                        },
                        submitMethod: "ModifySystemSettings",
                        submitParamName: "SystemSettings",
                        wrapInArray: false);
                }

                // ── Step 10: TPM ──────────────────────────────────
                if (p.Generation == 2 && p.EnableTpm)
                {
                    await EnableTpmAsync(finalVmName, vmGuid, svcForScope.Scope);
                }

                // ── Step 11: ISO 优先引导 ─────────────────────────
                // 带安装介质时把光盘引导项提到引导首位(Gen1/Gen2 通用)，避免默认网络(PXE)优先
                // 导致首次开机空等/落到空盘。须在此(VM 已配置完、Step 8~10 设置不再覆盖引导序、
                // 且调用方启动 VM 之前)设置才能在首次开机生效。复用 VmBootService；尽力而为：
                // 其内部已吞异常不会抛出，故不会触发上面的建机回滚，失败也仅影响首启顺序。
                if (!string.IsNullOrWhiteSpace(p.IsoPath) && File.Exists(p.IsoPath))
                {
                    await VmBootService.SetIsoFirstAsync(finalVmName);
                }

                // 启动交由调用方(ConfirmCreateAsync)处理：创建已成功，启动作为独立后续步骤，
                // 由 UI 检查引擎返回并在失败(如内存不足)时弹出原因——在此 await 而不看结果会静默吞掉失败。
                return (true, finalVmName);
            }
            catch (Exception ex)
            {
                // 回滚:DefineSystem 之后任一步骤失败会留下半成品 VM,删掉它再上报错误(回滚失败不掩盖原始错误)
                if (vmCreated)
                {
                    try
                    {
                        var rollback = await VmDeleteService.DeleteVmAsync(finalVmName);
                        if (!rollback.Success)
                            return (false, ex.Message + Environment.NewLine +
                                string.Format(Properties.Resources.Error_VmCreate_RollbackFailed, finalVmName, rollback.Message));
                    }
                    catch (Exception rollbackEx)
                    {
                        // 回滚删除也失败：孤儿半成品 VM 残留，明确告知用户手动清理，同时保留原始错误
                        return (false, ex.Message + Environment.NewLine +
                            string.Format(Properties.Resources.Error_VmCreate_RollbackFailed, finalVmName, rollbackEx.Message));
                    }
                }
                return (false, ex.Message);
            }
        }

        // ── TPM 启用（纯 WMI/CIM）────────────────────────────────────
        // 流程：
        //   1. 取或创建 UntrustedGuardian（root\microsoft\windows\hgs）
        //   2. 生成本地 KeyProtector RawData（MSFT_HgsKeyProtector.NewByGuardians）
        //   3. Msvm_SecurityService.SetKeyProtector（传入 SecuritySettingData XML + RawData）
        //   4. Msvm_SecuritySettingData: TpmEnabled=true, EncryptStateAndVmMigrationTraffic=true
        //      → Msvm_SecurityService.ModifySecuritySettings
        private static async Task EnableTpmAsync(string vmName, string vmGuid, ManagementScope hyperVScope)
        {
            await Task.Run(() =>
            {
                const string hgsScope = @"root\microsoft\windows\hgs";
                var hgsMs = WmiConnectionCache.GetManagementScope(hgsScope, WmiContext.Local);

                // 异步 Job 等待（~2 分钟超时，替代原 while(true) 防挂死）
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
                    using var guardianClass = new ManagementClass(
                        hgsMs, new ManagementPath("MSFT_HgsGuardian"), null);
                    using var createParams = guardianClass.GetMethodParameters("NewByGenerateCertificates");
                    createParams["Name"] = "UntrustedGuardian";
                    createParams["GenerateCertificates"] = true;
                    using var createResult = guardianClass.InvokeMethod("NewByGenerateCertificates", createParams, null);
                    guardian = createResult["cmdletOutput"] as ManagementObject;
                }

                if (guardian == null)
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoGuardian);

                // Step 2: 生成本地 KeyProtector
                using var kpClass = new ManagementClass(
                    hgsMs, new ManagementPath("MSFT_HgsKeyProtector"), null);
                using var kpParams = kpClass.GetMethodParameters("NewByGuardians");
                kpParams["AllowUntrustedRoot"] = true;
                kpParams.Properties["Owner"].Value = guardian;  // 实测确认必须用 Properties[].Value
                using var kpResult = kpClass.InvokeMethod("NewByGuardians", kpParams, null);
                var kpInstance = kpResult["cmdletOutput"] as ManagementBaseObject;
                byte[]? rawData = kpInstance?["RawData"] as byte[];

                if (rawData == null || rawData.Length == 0)
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoKeyProtector);

                // Step 3: 取 Msvm_SecuritySettingData，序列化为 XML
                using var secSettingSearcher = new ManagementObjectSearcher(
                    hyperVScope,
                    new ObjectQuery($"SELECT * FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'"));
                using var secSettingCol = secSettingSearcher.Get();
                using var secSetting = secSettingCol.Cast<ManagementObject>().FirstOrDefault();

                if (secSetting == null)
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoSecuritySettings);

                string secXml = secSetting.GetText(TextFormat.CimDtd20);

                // Step 4: Msvm_SecurityService.SetKeyProtector
                using var secSvcSearcher = new ManagementObjectSearcher(
                    hyperVScope, new ObjectQuery("SELECT * FROM Msvm_SecurityService"));
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

                // Step 5: TpmEnabled=true + EncryptStateAndVmMigrationTraffic=true
                secSetting["TpmEnabled"] = true;
                secSetting["EncryptStateAndVmMigrationTraffic"] = true;
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
        private static async Task<string> GetUniqueVmNameAsync(string baseName, string basePath)
        {
            string candidate = baseName;
            int i = 2;
            while (Directory.Exists(Path.Combine(basePath, candidate)) || await VmNameExistsAsync(candidate))
                candidate = $"{baseName} ({i++})";
            return candidate;
        }

        private static async Task<bool> VmNameExistsAsync(string name)
        {
            var resp = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(name)}'",
                obj => obj["Name"]?.ToString(),
                WmiScope.HyperV);
            return resp.HasData;
        }

    }
}