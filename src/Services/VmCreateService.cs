using System.IO;
using ExHyperV.Models;
using System.Management;
using ExHyperV.Api;

namespace ExHyperV.Services
{
    public class VmCreateService
    {
        private readonly VmStorageService _storage = new();
        private readonly VmNetworkService _network = new();
        private readonly VmPowerService _power = new();

        private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

        public async Task<List<string>> GetSupportedVersionsAsync()
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

        public async Task<(bool Supported, List<string> Types)> GetIsolationSupportAsync()
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
        public async Task<(string DefaultVmPath, string DefaultVhdPath)> GetHostDefaultPathsAsync()
        {
            var resp = await WmiApi.QueryFirstAsync(
                "SELECT DefaultVirtualMachinePath, DefaultVirtualHardDiskPath FROM Msvm_VirtualSystemManagementServiceSettingData",
                obj => (
                    VmPath: obj["DefaultVirtualMachinePath"]?.ToString() ?? @"C:\ProgramData\Microsoft\Windows\Hyper-V",
                    VhdPath: obj["DefaultVirtualHardDiskPath"]?.ToString() ?? ""
                ),
                WmiScope.HyperV);

            if (resp.HasData)
                return (resp.Data.VmPath, resp.Data.VhdPath);

            return (@"C:\ProgramData\Microsoft\Windows\Hyper-V", "");
        }

        public async Task<(bool Success, string Message)> CreateVirtualMachineAsync(VmCreationParams p)
        {
            string finalVmName = p.IsManualName ? p.Name : await GetUniqueVmNameAsync(p.Name, p.Path);
            try
            {
                // ── Step 1: 创建目录 ──────────────────────────────
                string vmHomeFolder = Path.Combine(p.Path, finalVmName);
                if (!Directory.Exists(vmHomeFolder))
                    Directory.CreateDirectory(vmHomeFolder);

                if (p.DiskMode == 0)
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

                string? vmPath = defineResp.Data?.FirstOrDefault();
                if (string.IsNullOrEmpty(vmPath))
                    return (false, "DefineSystem 未返回 VM 路径");

                // ── Step 3: 取新 VM 的 Name（GUID）───────────────
                string vmGuid;
                using (var vmObj = new ManagementObject(svcForScope.Scope, new ManagementPath(vmPath), null))
                {
                    vmObj.Get();
                    vmGuid = vmObj["Name"]?.ToString() ?? "";
                }

                if (string.IsNullOrEmpty(vmGuid))
                    return (false, "无法获取新建 VM 的 GUID");

                // ── Step 4: 处理器设置 ────────────────────────────
                var procSettings = new VmProcessorSettings { Count = p.ProcessorCount };
                var procService = new VmProcessorService();
                await procService.SetVmProcessorAsync(finalVmName, procSettings);

                // ── Step 5: 内存设置 ──────────────────────────────
                var memSettings = new VmMemorySettings
                {
                    Startup = p.MemoryMb,
                    DynamicMemoryEnabled = p.EnableDynamicMemory,
                    Minimum = p.EnableDynamicMemory ? p.MemoryMb / 2 : p.MemoryMb,
                    Maximum = p.EnableDynamicMemory ? p.MemoryMb * 4 : p.MemoryMb,
                };
                var memService = new VmMemoryService();
                await memService.SetVmMemorySettingsAsync(finalVmName, memSettings, false);

                // ── Step 6: 网卡 ──────────────────────────────────
                await _network.AddNetworkAdapterAsync(finalVmName);
                if (!string.IsNullOrWhiteSpace(p.SwitchName) &&
                    p.SwitchName != ExHyperV.Properties.Resources.none)
                {
                    var adapters = await _network.GetNetworkAdaptersAsync(finalVmName);
                    var adapter = adapters.FirstOrDefault();
                    if (adapter != null)
                    {
                        adapter.IsConnected = true;
                        adapter.SwitchName = p.SwitchName;
                        await _network.UpdateConnectionAsync(finalVmName, adapter);
                    }
                }

                // ── Step 7: 磁盘 ──────────────────────────────────
                if (p.DiskMode == 0)
                {
                    await _storage.AddDriveAsync(
                        finalVmName,
                        p.Generation == 2 ? "SCSI" : "IDE", 0, 0,
                        "HardDisk", p.VhdPath, false,
                        isNew: true, sizeGb: (int)p.DiskSizeGb);
                }
                else if (p.DiskMode == 1 && !string.IsNullOrEmpty(p.VhdPath))
                {
                    await _storage.AddDriveAsync(
                        finalVmName,
                        p.Generation == 2 ? "SCSI" : "IDE", 0, 0,
                        "HardDisk", p.VhdPath, false);
                }

                // ── Step 8: DVD ───────────────────────────────────
                if (!string.IsNullOrWhiteSpace(p.IsoPath) && File.Exists(p.IsoPath))
                {
                    string dvdCtrl = p.Generation == 1 ? "IDE" : "SCSI";
                    int dvdCtrlNum = p.Generation == 1 ? 1 : 0;
                    int dvdLoc = p.Generation == 1 ? 0 : 1;

                    await _storage.AddDriveAsync(
                        finalVmName, dvdCtrl, dvdCtrlNum, dvdLoc,
                        "DvdDrive", p.IsoPath, false);
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

                // ── Step 11: 启动 ─────────────────────────────────
                if (p.StartAfterCreation)
                    await _power.ExecuteControlActionAsync(finalVmName, "Start");

                return (true, finalVmName);
            }
            catch (Exception ex)
            {
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
        private async Task EnableTpmAsync(string vmName, string vmGuid, ManagementScope hyperVScope)
        {
            await Task.Run(() =>
            {
                const string hgsScope = @"root\microsoft\windows\hgs";
                var hgsMs = WmiConnectionCache.GetManagementScope(hgsScope, WmiContext.Local);

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
                    throw new InvalidOperationException("无法获取或创建 UntrustedGuardian");

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
                    throw new InvalidOperationException("无法生成本地 KeyProtector");

                // Step 3: 取 Msvm_SecuritySettingData，序列化为 XML
                using var secSettingSearcher = new ManagementObjectSearcher(
                    hyperVScope,
                    new ObjectQuery($"SELECT * FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'"));
                using var secSettingCol = secSettingSearcher.Get();
                using var secSetting = secSettingCol.Cast<ManagementObject>().FirstOrDefault();

                if (secSetting == null)
                    throw new InvalidOperationException("无法找到 VM 的 SecuritySettingData");

                string secXml = secSetting.GetText(TextFormat.CimDtd20);

                // Step 4: Msvm_SecurityService.SetKeyProtector
                using var secSvcSearcher = new ManagementObjectSearcher(
                    hyperVScope, new ObjectQuery("SELECT * FROM Msvm_SecurityService"));
                using var secSvcCol = secSvcSearcher.Get();
                using var secSvc = secSvcCol.Cast<ManagementObject>().FirstOrDefault();

                if (secSvc == null)
                    throw new InvalidOperationException("无法找到 Msvm_SecurityService");

                using var kpInParams = secSvc.GetMethodParameters("SetKeyProtector");
                kpInParams["SecuritySettingData"] = secXml;
                kpInParams["KeyProtector"] = rawData;
                using var kpOut = secSvc.InvokeMethod("SetKeyProtector", kpInParams, null);
                int kpRet = Convert.ToInt32(kpOut["ReturnValue"]);
                if (kpRet != 0)
                    throw new InvalidOperationException($"SetKeyProtector 失败，返回码：{kpRet}");

                // Step 5: TpmEnabled=true + EncryptStateAndVmMigrationTraffic=true
                secSetting["TpmEnabled"] = true;
                secSetting["EncryptStateAndVmMigrationTraffic"] = true;
                string updatedXml = secSetting.GetText(TextFormat.CimDtd20);

                using var modInParams = secSvc.GetMethodParameters("ModifySecuritySettings");
                modInParams["SecuritySettingData"] = updatedXml;
                using var modOut = secSvc.InvokeMethod("ModifySecuritySettings", modInParams, null);
                int modRet = Convert.ToInt32(modOut["ReturnValue"]);
                if (modRet != 0 && modRet != 4096)
                    throw new InvalidOperationException($"ModifySecuritySettings 失败，返回码：{modRet}");

                if (modRet == 4096)
                {
                    string jobPath = modOut["Job"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(jobPath))
                    {
                        using var job = new ManagementObject(hyperVScope, new ManagementPath(jobPath), null);
                        while (true)
                        {
                            job.Get();
                            ushort state = (ushort)job["JobState"];
                            if (state == 7) break;
                            if (state > 7)
                                throw new InvalidOperationException($"TPM Job 失败，状态：{state}");
                            System.Threading.Thread.Sleep(300);
                        }
                    }
                }
            });
        }
        private async Task<string> GetUniqueVmNameAsync(string baseName, string basePath)
        {
            string candidate = baseName;
            int i = 2;
            while (Directory.Exists(Path.Combine(basePath, candidate)) || await VmNameExistsAsync(candidate))
                candidate = $"{baseName} ({i++})";
            return candidate;
        }

        private async Task<bool> VmNameExistsAsync(string name)
        {
            var resp = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(name)}' AND Caption = 'Virtual Machine'",
                obj => obj["Name"]?.ToString(),
                WmiScope.HyperV);
            return resp.HasData;
        }

    }
}