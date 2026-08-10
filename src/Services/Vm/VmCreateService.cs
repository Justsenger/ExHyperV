using System.IO;
using System.Management;
using ExHyperV.Tools;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public static class VmCreateService
    {
        private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

        // Msvm_VirtualSystemSettingData.GuestStateIsolationType.
        // Disabled is represented by GuestStateIsolationEnabled=false, not by a UInt16 value.
        private enum GuestStateIsolationTypeValue : ushort
        {
            TrustedLaunch = 0,
            Vbs = 1,
            SevSnp = 2,
            Tdx = 3,
            Rme = 4,
            OpenHcl = 16,
            Reserved18 = 18,
            Reserved19 = 19
        }

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
                .Where(s => s.IsolationEnabled &&
                    s.InstanceID.IndexOf("GuestStateIsolationType",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(s => (GuestStateIsolationTypeValue)s.IsolationType switch
                {
                    GuestStateIsolationTypeValue.TrustedLaunch => "TrustedLaunch",
                    GuestStateIsolationTypeValue.Vbs => "VBS",
                    GuestStateIsolationTypeValue.SevSnp => "SNP",
                    GuestStateIsolationTypeValue.Tdx => "TDX",
                    GuestStateIsolationTypeValue.Rme => "RME",
                    GuestStateIsolationTypeValue.OpenHcl => "OpenHCL",
                    // 18/19 are present in newer schemas but remain reserved.
                    // Do not surface them until Microsoft assigns public semantics.
                    _ => null
                })
                .Where(type => type is not null)
                .Select(type => type!)
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
            if (p.IsolationType == "OpenHCL")
            {
                if (string.IsNullOrWhiteSpace(p.OpenHclIgvmPath) || !File.Exists(p.OpenHclIgvmPath))
                    return (false, Properties.Resources.VmPage_OpenHclIgvmRequired);

                if (!Version.TryParse(p.Version, out var version) || version < new Version(12, 0))
                    return (false, Properties.Resources.VmPage_OpenHclRequiresV12);

                var permissionResult = await Task.Run(() =>
                    HostOpenHclService.GrantFirmwareReadAccess(p.OpenHclIgvmPath));
                if (!permissionResult.Success)
                    return (false, string.Format(
                        Properties.Resources.Error_VmCreate_OpenHclIgvmPermission,
                        permissionResult.Error));
            }

            // 总是查重(含手动命名)：撞到已存在的文件夹 / 在册同名 VM 时自动改名 "test3 (2)"…，
            // 避开 DefineSystem/建 VHD 的 ERROR_FILE_EXISTS(0x80070050)。用户预期：同名也应自动改名而非报错。
            string finalVmName = await GetUniqueVmNameAsync(p.Name, p.Path);
            bool vmCreated = false;   // DefineSystem 成功后置 true;失败回滚的依据
            bool defineSystemInvoked = false;
            string vmHomeFolder = Path.Combine(p.Path, finalVmName);
            bool vmHomeFolderCreated = false;
            string? ownedVhdPath = null;
            try
            {
                // ── Step 1: 创建目录 ──────────────────────────────
                if (!Directory.Exists(vmHomeFolder))
                {
                    Directory.CreateDirectory(vmHomeFolder);
                    vmHomeFolderCreated = true;
                }

                // 新建磁盘：VhdPath 存的是文件夹（手选目录，或默认的 VM 目录），vhdx 文件名恒取最终 VM 名。
                // 批量创建时各台名字不同 → 盘文件各自唯一，无需另行区分。
                if (p.DiskMode == 0)
                {
                    string diskFolder = p.IsDiskPathManual && !string.IsNullOrWhiteSpace(p.VhdPath)
                        ? p.VhdPath
                        : vmHomeFolder;
                    p.VhdPath = Path.Combine(diskFolder, $"{finalVmName}.vhdx");
                }

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
                    GuestStateIsolationTypeValue isolationType = p.IsolationType switch
                    {
                        "TrustedLaunch" => GuestStateIsolationTypeValue.TrustedLaunch,
                        "VBS" => GuestStateIsolationTypeValue.Vbs,
                        "SNP" => GuestStateIsolationTypeValue.SevSnp,
                        "TDX" => GuestStateIsolationTypeValue.Tdx,
                        "RME" => GuestStateIsolationTypeValue.Rme,
                        "OpenHCL" => GuestStateIsolationTypeValue.OpenHcl,
                        _ => throw new InvalidOperationException(
                            $"Unsupported guest state isolation type: {p.IsolationType}")
                    };

                    // WMI 属性是 Boolean + UInt16；显式写入底层数值，避免依赖字符串枚举转换。
                    vssd.TrySet<bool>("GuestStateIsolationEnabled", true);
                    vssd.TrySet<ushort>("GuestStateIsolationType", (ushort)isolationType);
                }

                string vssdXml = vssd.GetText(TextFormat.CimDtd20);

                defineSystemInvoked = true;
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
                    throw new InvalidOperationException(defineResp.Error);

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

                // New-VM 的 OpenHCL 枚举只写入隔离类型。微软 openvmm 的
                // Set-OpenHCL-HyperV-VM.ps1 还会在已实现 VSSD 上启用相应功能并指定 IGVM 固件；
                // 缺少这两项时 VM 虽能创建，但启动会以“IGVM 映像文件为空”失败。
                if (p.IsolationType == "OpenHCL")
                {
                    string settingsWql = $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                        $"WHERE VirtualSystemIdentifier = '{WmiApi.Escape(vmGuid)}' " +
                        $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

                    var openHclResult = await WmiApi.WithObjectAsync(
                        wql: settingsWql,
                        modifier: obj =>
                        {
                            if (!obj.HasProperty("GuestFeatureSet") || !obj.HasProperty("FirmwareFile"))
                                throw new InvalidOperationException(
                                    "The host does not expose the OpenHCL VSSD properties.");

                            obj["GuestFeatureSet"] = 0x00000201;
                            obj["FirmwareFile"] = p.OpenHclIgvmPath;
                        },
                        submitMethod: "ModifySystemSettings",
                        submitParamName: "SystemSettings",
                        wrapInArray: false);

                    if (!openHclResult.Success)
                        throw new InvalidOperationException(openHclResult.Error);
                }

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
                    // 只把“调用前不存在”的新建磁盘登记为本次所有；现有磁盘无论后续发生什么都不能删除。
                    if (!File.Exists(p.VhdPath))
                        ownedVhdPath = p.VhdPath;

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
                // 创建是一个事务：先注销可能已注册的半成品 VM，确认注销后再删除本次操作新建的
                // VHD/专属目录。现有磁盘、ISO、原有目录从未登记为本次所有，不会被清理。
                // DefineSystem 报错时也回查名称，覆盖“返回失败但实际留下半成品 VM”的边界情况。
                bool registeredVmExists = vmCreated;
                if (!registeredVmExists && defineSystemInvoked)
                {
                    // DefineSystem 返回失败时不能直接假定“什么都没创建”。若连回查也失败，
                    // 保守地保留文件现场，避免误删一台可能仍在册的 VM 的配置。
                    var registration = await WmiApi.QueryFirstAsync(
                        $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(finalVmName)}'",
                        obj => obj["Name"]?.ToString(),
                        WmiScope.HyperV);
                    if (!registration.Success)
                    {
                        return (false, ex.Message + Environment.NewLine +
                            string.Format(Properties.Resources.Error_VmCreate_RollbackFailed,
                                finalVmName, registration.Error));
                    }
                    registeredVmExists = registration.HasData;
                }
                if (registeredVmExists)
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

                var cleanupErrors = await CleanupOwnedCreationArtifactsAsync(
                    ownedVhdPath, vmHomeFolder, vmHomeFolderCreated);
                if (cleanupErrors.Count > 0)
                {
                    return (false, ex.Message + Environment.NewLine +
                        string.Format(Properties.Resources.Error_VmCreate_ArtifactCleanupFailed,
                            string.Join("; ", cleanupErrors)));
                }

                return (false, ex.Message);
            }
        }

        /// <summary>
        /// 删除且仅删除本次创建事务拥有的文件。调用方必须先确认半成品 VM 已成功注销，
        /// 避免留下“VM 仍在册但配置文件已消失”的损坏状态。
        /// </summary>
        private static async Task<List<string>> CleanupOwnedCreationArtifactsAsync(
            string? ownedVhdPath,
            string vmHomeFolder,
            bool vmHomeFolderCreated)
        {
            var errors = new List<string>();

            if (!string.IsNullOrWhiteSpace(ownedVhdPath))
            {
                bool deleted = await TryDeleteOwnedFileAsync(ownedVhdPath);
                if (!deleted)
                    errors.Add(ownedVhdPath);
            }

            // 目录在创建前不存在，因此整个目录树都属于本次事务；Hyper-V 可能在其中生成
            // vmcx/vmgs 等未知数量的过程文件，需要递归清理。原有目录永远不会进入此分支。
            if (vmHomeFolderCreated)
            {
                bool deleted = await TryDeleteOwnedDirectoryAsync(vmHomeFolder);
                if (!deleted)
                    errors.Add(vmHomeFolder);
            }

            return errors;
        }

        private static async Task<bool> TryDeleteOwnedFileAsync(string path)
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (!File.Exists(path)) return true;
                    File.Delete(path);
                    return true;
                }
                catch when (i < 5)
                {
                    await Task.Delay(250);
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        private static async Task<bool> TryDeleteOwnedDirectoryAsync(string path)
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (!Directory.Exists(path)) return true;
                    Directory.Delete(path, recursive: true);
                    return true;
                }
                catch when (i < 5)
                {
                    await Task.Delay(250);
                }
                catch
                {
                    return false;
                }
            }
            return false;
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
        // 批量命名：base-NN（补零位数按数量：5→1 位、100→3 位），起始序号接已有 base-<数字> 的最大值之后连续取 count 个。
        // 在此算好互不冲突的最终名，各台再并行走 CreateVirtualMachineAsync 时 GetUniqueVmNameAsync 恰好都命中空位、不再改名。
        public static async Task<List<string>> BuildBatchNamesAsync(string baseName, string basePath, int count)
        {
            string prefix = baseName + "-";
            int IndexOf(string name)
            {
                if (name == null || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return -1;
                return int.TryParse(name.Substring(prefix.Length), out int k) ? k : -1;
            }

            int maxIdx = 0;
            // 在册 VM 名（不带 Caption 过滤——该属性在本地化系统上被翻译、等值匹配查不到）
            var resp = await WmiApi.QueryAsync(
                "SELECT ElementName FROM Msvm_ComputerSystem",
                obj => obj["ElementName"]?.ToString() ?? string.Empty,
                WmiScope.HyperV);
            foreach (var n in resp.Data ?? new List<string>())
                maxIdx = Math.Max(maxIdx, IndexOf(n));

            // 目录名（防注册已删但文件夹残留占名）
            try
            {
                if (Directory.Exists(basePath))
                    foreach (var dir in Directory.EnumerateDirectories(basePath))
                        maxIdx = Math.Max(maxIdx, IndexOf(Path.GetFileName(dir)));
            }
            catch { }

            string fmt = "D" + count.ToString().Length;
            return Enumerable.Range(maxIdx + 1, count)
                .Select(i => $"{baseName}-{i.ToString(fmt)}")
                .ToList();
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
