using System.IO;
using System.Management;
using ExHyperV.Tools;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    /// <summary>校验创建参数并完成一台虚拟机的配置、存储和网络创建。</summary>
    public static partial class VmCreateService
    {
        private const long DefaultDynamicMemoryMaximumMb = 1_048_576;

        private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";

        internal sealed class CreationMutationException(ApiResponse response) : Exception(response.Error, response.Exception)
        {
            internal ApiResponse Response { get; } = response;
        }

        internal static void RequireCreationMutation(ApiResponse response)
        {
            if (!response.Success) throw new CreationMutationException(response);
        }

        internal static bool MustPreserveCreation(ApiErrorKind kind) =>
            kind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired;
        private const string DefaultSystemSettingsWql =
            "SELECT * FROM Msvm_VirtualSystemSettingData " +
            "WHERE InstanceID = 'Microsoft:Definition\\\\VirtualSystem\\\\Default'";

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

        private static bool IsStandardIsolatedVm(VmCreationParameters p) =>
            p.Generation == 2 && p.IsolationType != "Disabled" &&
            p.IsolationType != "OpenHCL" && !string.IsNullOrEmpty(p.IsolationType);

        /// <summary>读取宿主支持的虚拟机配置版本。</summary>
        /// <returns>成功时为去重并降序排列的版本列表，可为空；宿主能力对象缺失或查询失败返回失败。</returns>
        public static async Task<ApiResponse<List<string>>> GetSupportedVersionsAsync()
        {
            var settings = await QueryCapabilitySettingsAsync(obj => obj["Version"]?.ToString() ?? string.Empty);
            if (!settings.Success) return CreationQueryFailure<List<string>, List<string>>(settings);
            return ApiResponse<List<string>>.Ok((settings.Data ?? []).Where(v => !string.IsNullOrEmpty(v))
                .Distinct().OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0)).ToList());
        }

        private sealed record IsolationItem(string InstanceID, bool IsolationEnabled, int IsolationType);

        /// <summary>读取虚拟机硬件隔离支持情况和可选择的隔离类型。</summary>
        /// <returns>成功时明确区分不支持和支持，类型列表总含 Disabled；读取失败返回错误，不当作不支持。</returns>
        public static async Task<ApiResponse<VmIsolationSupport>> GetIsolationSupportAsync()
        {
            var settings = await QueryCapabilitySettingsAsync(obj => new IsolationItem(
                obj["InstanceID"]?.ToString() ?? string.Empty,
                obj.TryGet<bool>("GuestStateIsolationEnabled") ?? false,
                obj.TryGet<ushort>("GuestStateIsolationType") ?? ushort.MaxValue));
            if (!settings.Success) return CreationQueryFailure<VmIsolationSupport, List<IsolationItem>>(settings);
            var types = (settings.Data ?? []).Where(item => item.IsolationEnabled &&
                    item.InstanceID.Contains("GuestStateIsolationType", StringComparison.OrdinalIgnoreCase))
                .Select(item => (GuestStateIsolationTypeValue)item.IsolationType switch
                {
                    GuestStateIsolationTypeValue.TrustedLaunch => "TrustedLaunch",
                    GuestStateIsolationTypeValue.Vbs => "VBS",
                    GuestStateIsolationTypeValue.SevSnp => "SNP",
                    GuestStateIsolationTypeValue.Tdx => "TDX",
                    GuestStateIsolationTypeValue.Rme => "RME",
                    GuestStateIsolationTypeValue.OpenHcl => "OpenHCL",
                    _ => null
                }).Where(type => type != null).Select(type => type!).Distinct().ToList();
            bool supported = types.Count > 0;
            types.Add("Disabled");
            return ApiResponse<VmIsolationSupport>.Ok(new(supported, types.AsReadOnly()));
        }

        private static async Task<ApiResponse<List<T>>> QueryCapabilitySettingsAsync<T>(Func<ManagementObject, T> map)
        {
            var capability = await WmiApi.QueryFirstAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementCapabilities", obj => obj.Path.Path);
            if (!capability.Success) return CreationQueryFailure<List<T>, string>(capability);
            if (!capability.HasData)
                return ApiResponse<List<T>>.Fail("未找到 Hyper-V 宿主管理能力。", kind: ApiErrorKind.NotFound);
            return await WmiApi.QueryAsync(
                $"ASSOCIATORS OF {{{capability.Data}}} WHERE AssocClass = Msvm_SettingsDefineCapabilities ResultClass = Msvm_VirtualSystemSettingData", map);
        }

        private static ApiResponse<T> CreationQueryFailure<T, TSource>(ApiResponse<TSource> response) =>
            ApiResponse<T>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);

        /// <summary>读取 Hyper-V 宿主的默认配置目录和虚拟硬盘目录。</summary>
        /// <returns>成功时返回宿主目录；查询失败或宿主配置不存在返回失败。宿主属性为空时使用内置配置目录，磁盘目录允许为空。</returns>
        public static async Task<ApiResponse<VmHostDefaultPaths>> GetHostDefaultPathsAsync()
        {
            var response = await WmiApi.QueryFirstAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData",
                obj => new VmHostDefaultPaths(obj.TryGetString("DefaultExternalDataRoot")
                        ?? obj.TryGetString("DefaultVirtualMachinePath") ?? DefaultVmDirectory,
                    obj.TryGetString("DefaultVirtualHardDiskPath") ?? string.Empty));
            if (!response.Success) return response;
            return response.HasData ? response : ApiResponse<VmHostDefaultPaths>.Fail(
                "未找到 Hyper-V 宿主默认目录配置。", kind: ApiErrorKind.NotFound);
        }

        /// <summary>校验一台虚拟机的名称、代数、内存、磁盘和部分隔离选项，不创建资源。</summary>
        /// <param name="p">虚拟机创建参数；新磁盘目录留空时沿用虚拟机目录。</param>
        /// <returns>首个本地化校验错误；校验通过时为 null。</returns>
        public static string? GetCreationError(VmCreationParameters p)
        {
            ArgumentNullException.ThrowIfNull(p);
            if (p.Generation is not (1 or 2)) return "虚拟机代数必须是 1 或 2。";
            if (p.SwitchId == Guid.Empty) return "交换机 GUID 不能为全零；不连接时使用 null。";
            if (VmEditService.GetNameError(p.Name) is string nameError)
                return nameError;
            if (p.DiskMode is < 0 or > 2)
                return "磁盘模式无效；请选择新建、使用已有磁盘或暂不挂接。";

            // Empty new-disk directories are supported: GetNewDiskDirectory falls
            // back to the VM directory. The desktop's picker completeness is separate.
            if (p.DiskMode == 1)
            {
                if (string.IsNullOrWhiteSpace(p.VhdPath))
                    return Properties.Resources.VmPage_SelectExistVhdPath;
                if (!File.Exists(p.VhdPath))
                    return Properties.Resources.VmPage_ExistVhdNotFound;
            }

            if (!string.IsNullOrWhiteSpace(p.IsoPath) && !File.Exists(p.IsoPath))
                return Properties.Resources.VmPage_IsoNotFound;

            if (p.IsolationType == "OpenHCL")
            {
                if (string.IsNullOrWhiteSpace(p.OpenHclIgvmPath) || !File.Exists(p.OpenHclIgvmPath))
                    return Properties.Resources.VmPage_OpenHclIgvmRequired;

                if (!Version.TryParse(p.Version, out var version) || version < new Version(12, 0))
                    return Properties.Resources.VmPage_OpenHclRequiresV12;
            }

            if (p.ProcessorCount < 1)
                return Properties.Resources.VmPage_InvalidCpuCount;
            if (p.MemoryMiB < 32)
                return Properties.Resources.VmPage_InvalidMemory;
            if (p.DiskMode == 0 && p.DiskSizeGiB < 1)
                return Properties.Resources.VmPage_InvalidDiskSize;
            return null;
        }

        /// <summary>根据一份配置创建并注册一台虚拟机，返回实际 GUID 和名称。</summary>
        /// <param name="p">虚拟机创建参数；新磁盘目录留空时沿用虚拟机目录。</param>
        /// <returns>GUID、实际名称和注册状态；失败时返回原始错误、清理错误及保留路径。</returns>
        /// <remarks>每次创建一台虚拟机，成功后处于关机状态。名称或输出路径冲突时自动避让；NameAdjusted、Notice、ActualName、ConfigurationDirectory 和 DiskPath 报告调整结果及实际位置。</remarks>
        /// <instanceentry label="创建虚拟机" category="创建" order="10">
        /// <step>构造 <c>VmCreationParameters</c>，填写名称、配置目录、处理器、内存、磁盘和网络选项。新磁盘目录可以留空，沿用虚拟机目录。</step>
        /// <step>调用 <c>CreateVirtualMachineAsync</c>。如需提交前显示校验结果，可先调用 <c>GetCreationError</c>。</step>
        /// <step>读取结果的 GUID、<c>ActualName</c>、<c>ConfigurationDirectory</c> 和 <c>DiskPath</c>。创建成功但发生自动避让时，<c>NameAdjusted</c> 为 true，<c>Notice</c> 提供说明；可选配置问题见 <c>ConfigurationIssues</c>。</step>
        /// </instanceentry>
        public static async Task<VmCreationResult> CreateVirtualMachineAsync(VmCreationParameters p)
        {
            ArgumentNullException.ThrowIfNull(p);
            p = p.Copy();
            Guid? selectedSwitchId = p.SwitchId;
            if (GetCreationError(p) is string error)
                return new(false, null, null, VmRegistrationState.Unregistered, [], error) { RequestedName = p.Name };

            if (p.IsolationType == "OpenHCL")
            {
                var permissionResult = await Task.Run(() =>
                    HostOpenHclService.GrantFirmwareReadAccess(p.OpenHclIgvmPath));
                if (!permissionResult.Success)
                    return new(false, null, null, VmRegistrationState.Unregistered, [], string.Format(
                        Properties.Resources.Error_VmCreate_OpenHclIgvmPermission,
                        permissionResult.Error)) { RequestedName = p.Name };
            }

            // 总是查重(含手动命名)：撞到已存在的文件夹 / 在册同名 VM 时自动改名 "test3 (2)"…，
            // 避开 DefineSystem/建 VHD 的 ERROR_FILE_EXISTS(0x80070050)。用户预期：同名也应自动改名而非报错。
            string finalVmName;
            string vmHomeFolder;
            try
            {
                (finalVmName, vmHomeFolder) = await ReserveVmDirectoryAsync(p.Name, p.Path, VmNameExistsAsync,
                    p.DiskMode == 0 || (p.DiskMode == 1 && p.CreateDifferencingDisk)
                        ? name => File.Exists(p.GetDiskPath(name)) || Directory.Exists(p.GetDiskPath(name))
                        : null);
            }
            catch (Exception ex)
            {
                return new(false, null, null, VmRegistrationState.Unregistered, [], ex.Message) { RequestedName = p.Name };
            }
            bool vmCreated = false;   // 创建回执确认产生 VM 后置 true；总操作失败仍可能有半成品
            Guid createdVmId = Guid.Empty;
            bool defineSystemInvoked = false;
            bool defineSystemOutcomeUnknown = false;
            bool vmHomeFolderCreated = true;
            bool storageCleanupRequired = false;
            bool configurationOutcomeUnknown = false;
            string? retainedConfigurationCleanupError = null;
            var configurationIssues = new List<ApiResponse>();
            string? ownedVhdPath = null;
            string? ownedVhdDirectoryPath = null;
            string? unconfirmedVhdPath = null;
            string effectiveVhdPath = p.VhdPath;
            bool isStandardIsolatedVm = IsStandardIsolatedVm(p);
            bool effectiveSecureBoot = p.EnableSecureBoot ||
                (p.Generation == 2 && p.IsolationType == "TrustedLaunch");
            bool effectiveTpm = p.EnableTpm || isStandardIsolatedVm;

            void CheckStorageResult(ApiResponse<VmStorageSlot> result, bool createsDisk = false)
            {
                if (result.Success) return;
                if (MustPreserveCreation(result.ErrorKind))
                {
                    storageCleanupRequired = true;
                    // 这是待核查路径，不是本次创建成功的证据，绝不能送入删除清单。
                    if (createsDisk) unconfirmedVhdPath = effectiveVhdPath;
                }
                RequireCreationMutation(ApiResponse.Fail(result.Error, result.Code, result.ErrorSource,
                    result.Exception, result.ErrorKind));
            }

            try
            {
                // Step 1 已在名称选择时原子创建目录；成功返回即取得此叶目录的归属，
                // 但这不意味着后来出现在其中的所有文件都属于本次创建。

                // 新建磁盘：VhdPath 存的是文件夹（手选目录，或默认的 VM 目录），vhdx 文件名恒取最终 VM 名。
                // 磁盘路径随冲突避让后的名称解析；已有磁盘始终按调用方指定的路径挂接。
                if (p.DiskMode == 0)
                {
                    effectiveVhdPath = p.GetDiskPath(finalVmName)!;
                }
                else if (p.DiskMode == 1 && p.CreateDifferencingDisk)
                {
                    string diskFolder = p.GetNewDiskDirectory(finalVmName);
                    if (VmCreationDirectory.TryCreateLeaf(diskFolder))
                        ownedVhdDirectoryPath = diskFolder;
                    effectiveVhdPath = p.GetDiskPath(finalVmName)!;
                }

                // ── Step 2: DefineSystem 创建 VM ──────────────────
                using var svcForScope = WmiApi.GetVirtualSystemManagementService();

                var vssdResp = await WmiApi.WithFirstAsync(
                    DefaultSystemSettingsWql,
                    vssd =>
                    {
                        vssd["ElementName"] = finalVmName;
                        vssd["VirtualSystemSubType"] = p.Generation == 2
                            ? "Microsoft:Hyper-V:SubType:2" : "Microsoft:Hyper-V:SubType:1";
                        vssd["Version"] = p.Version;
                        vssd["ConfigurationDataRoot"] = vmHomeFolder;
                        vssd["SnapshotDataRoot"] = vmHomeFolder;
                        vssd["SwapFileDataRoot"] = vmHomeFolder;
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
                            vssd.TrySet<bool>("GuestStateIsolationEnabled", true);
                            vssd.TrySet<ushort>("GuestStateIsolationType", (ushort)isolationType);
                        }
                        else vssd.TrySetAlways("GuestStateIsolationEnabled", false);

                        if (effectiveSecureBoot)
                            vssd.TrySetAlways("SecureBootEnabled", true);
                        return Task.FromResult(vssd.GetText(TextFormat.CimDtd20));
                    });

                if (!vssdResp.Success) throw new InvalidOperationException(vssdResp.Error);
                if (!vssdResp.HasData)
                    throw new InvalidOperationException(
                        "Hyper-V did not provide the default virtual system settings template.");
                string vssdXml = vssdResp.Data!;

                defineSystemInvoked = true;
                defineSystemOutcomeUnknown = true;
                Task<WmiCreationResult> DefineSystemAsync() => WmiApi.InvokeCreationAsync(
                    ServiceWql,
                    "DefineSystem",
                    "Msvm_ComputerSystem",
                    p2 =>
                    {
                        p2["SystemSettings"] = vssdXml;
                        p2["ResourceSettings"] = Array.Empty<string>();
                        p2["ReferenceConfiguration"] = null;
                    },
                    resultField: "ResultingSystem");

                // DefineSystem 不得观察到主机级 Azure 暂存模式。始终持有共享锁，
                // 防止并发 CPU 或 PCIe 操作在状态检查与创建之间临时开启该模式。
                var defineResp = await HostAzureFeatureSetService
                    .RunTemporarilyDisabledAsync(DefineSystemAsync);

                defineSystemOutcomeUnknown = defineResp.OutcomeUnknown;
                vmCreated = defineResp.CreatedPaths.Length > 0;
                string? vmPath = GetConfirmedCreatedVmPath(defineResp);
                if (vmPath == null)
                {
                    // 正常拒绝且没有创建回执时可以清理空目录；结果未知或存在无法
                    // 唯一定位的创建对象时保留现场。不能用显示名称找替代目标。
                    defineSystemOutcomeUnknown |= defineResp.Response.Success || vmCreated;
                    throw new InvalidOperationException(defineResp.Response.Success
                        ? Properties.Resources.Error_VmCreate_NoSystemPath : defineResp.Response.Error);
                }

                // ── Step 3: 取新 VM 的 Name（GUID）───────────────
                string vmGuid;
                using (var vmObj = new ManagementObject(svcForScope.Scope, new ManagementPath(vmPath), null))
                {
                    vmObj.Get();
                    vmGuid = vmObj["Name"]?.ToString() ?? "";
                }

                if (!Guid.TryParse(vmGuid, out createdVmId) || createdVmId == Guid.Empty)
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoGuid);

                // 失败回执也可能精确确认已经创建的半成品。先保存其 GUID，再抛原始
                // 错误，让回滚处理这个确定对象；真正未知的作业不会进入此分支。
                if (!defineResp.Response.Success)
                    throw new InvalidOperationException(defineResp.Response.Error);

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

                    RequireCreationMutation(openHclResult);
                }

                // ── Step 4: 处理器设置 ────────────────────────────
                var procSettings = new VmProcessorSettings { Count = p.ProcessorCount };
                var procResult = await VmProcessorService.SetVmProcessorAsync(createdVmId, procSettings, [nameof(VmProcessorSettings.Count)]);
                RequireCreationMutation(procResult);

                // ── Step 5: 内存设置 ──────────────────────────────
                var memSettings = new VmMemorySettings
                {
                    Startup = p.MemoryMiB,
                    DynamicMemoryEnabled = p.EnableDynamicMemory,
                    Minimum = p.MemoryMiB,
                    Maximum = p.EnableDynamicMemory ? DefaultDynamicMemoryMaximumMb : p.MemoryMiB,
                    Buffer = 20,
                    Weight = 5000
                };
                var memResult = await VmMemoryService.SetVmMemorySettingsAsync(createdVmId, memSettings, false,
                    [nameof(VmMemorySettings.Startup), nameof(VmMemorySettings.DynamicMemoryEnabled),
                     nameof(VmMemorySettings.Minimum), nameof(VmMemorySettings.Maximum), nameof(VmMemorySettings.Buffer), nameof(VmMemorySettings.Weight)]);
                RequireCreationMutation(memResult);

                // ── Step 6: 网卡 ──────────────────────────────────
                var addNicResult = await VmNetworkService.AddNetworkAdapterAsync(createdVmId);
                if (!addNicResult.Success)
                {
                    retainedConfigurationCleanupError = addNicResult.CleanupError;
                    configurationOutcomeUnknown = addNicResult.AdapterState == VmNetworkAdapterCreationState.Unknown
                        || addNicResult.InitializationState == VmNetworkAdapterInitializationState.Unknown;
                    RequireCreationMutation(ApiResponse.Fail(addNicResult.Error ?? "网卡创建失败。",
                        kind: addNicResult.ErrorKind));
                }
                if (selectedSwitchId is not null)
                {
                    var adapters = await VmNetworkService.GetNetworkAdaptersAsync(createdVmId);
                    if (!adapters.HasData) throw new InvalidOperationException(adapters.Error);
                    var adapter = adapters.Data.FirstOrDefault();
                    if (adapter != null)
                    {
                        adapter.IsConnected = true;
                        var connResult = await VmNetworkService.UpdateConnectionAsync(createdVmId, adapter, selectedSwitchId);
                        RequireCreationMutation(connResult);
                    }
                }

                // ── Step 7: 磁盘 ──────────────────────────────────
                if (p.DiskMode == 0)
                {
                    var diskResult = await VmStorageService.AddDriveAsync(
                        createdVmId, new VmStorageAddRequest
                        {
                            ControllerType = p.Generation == 2 ? "SCSI" : "IDE",
                            PathOrNumber = effectiveVhdPath, IsNew = true, SizeGiB = (int)p.DiskSizeGiB
                        }, createdPath => ownedVhdPath = createdPath);
                    CheckStorageResult(diskResult, createsDisk: true);
                }
                else if (p.DiskMode == 1 && !string.IsNullOrEmpty(p.VhdPath))
                {
                    var diskResult = p.CreateDifferencingDisk
                        ? await VmStorageService.AddDriveAsync(
                            createdVmId, new VmStorageAddRequest
                            {
                                ControllerType = p.Generation == 2 ? "SCSI" : "IDE",
                                PathOrNumber = effectiveVhdPath, IsNew = true,
                                VhdType = "Differencing", ParentPath = p.VhdPath
                            }, createdPath => ownedVhdPath = createdPath)
                        : await VmStorageService.AddDriveAsync(
                            createdVmId, new VmStorageAddRequest
                            {
                                ControllerType = p.Generation == 2 ? "SCSI" : "IDE", PathOrNumber = p.VhdPath
                            });
                    CheckStorageResult(diskResult, createsDisk: p.CreateDifferencingDisk);
                }

                // ── Step 8: DVD ───────────────────────────────────
                if (!string.IsNullOrWhiteSpace(p.IsoPath) && File.Exists(p.IsoPath))
                {
                    string dvdCtrl = p.Generation == 1 ? "IDE" : "SCSI";
                    int dvdCtrlNum = p.Generation == 1 ? 1 : 0;
                    int dvdLoc = p.Generation == 1 ? 0 : 1;

                    var dvdResult = await VmStorageService.AddDriveAsync(
                        createdVmId, new VmStorageAddRequest
                        {
                            ControllerType = dvdCtrl, ControllerNumber = dvdCtrlNum, Location = dvdLoc,
                            DriveType = "DvdDrive", PathOrNumber = p.IsoPath
                        });
                    CheckStorageResult(dvdResult);
                }

                // ── Step 9: Gen2 安全启动 ─────────────────────────
                if (p.Generation == 2)
                {
                    string settingsWql = $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                        $"WHERE VirtualSystemIdentifier = '{vmGuid}' " +
                        $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'";

                    var secureBootResult = await WmiApi.WithObjectAsync(
                        wql: settingsWql,
                        modifier: obj =>
                        {
                            if (!obj.HasProperty("SecureBootEnabled"))
                                throw new NotSupportedException("宿主未提供安全启动配置属性，无法完成请求的配置。");
                            obj["SecureBootEnabled"] = effectiveSecureBoot;
                        },
                        submitMethod: "ModifySystemSettings",
                        submitParamName: "SystemSettings",
                        wrapInArray: false);
                    RequireCreationMutation(secureBootResult);
                }

                // ── Step 10: TPM ──────────────────────────────────
                if (p.Generation == 2 && effectiveTpm)
                {
                    RequireCreationMutation(await EnableTpmAsync(vmGuid, svcForScope.Scope));
                }

                // ── Step 11: ISO 优先引导 ─────────────────────────
                // 带安装介质时把光盘引导项提到引导首位(Gen1/Gen2 通用)，避免默认网络(PXE)优先
                // 导致首次开机空等/落到空盘。须在此(VM 已配置完、Step 8~10 设置不再覆盖引导序、
                // 且调用方启动 VM 之前)设置才能在首次开机生效。复用 VmBootService；尽力而为：
                // 已确认失败只影响首启顺序并作为可选配置问题返回；结果未知则先保留现场。
                if (!string.IsNullOrWhiteSpace(p.IsoPath) && File.Exists(p.IsoPath))
                {
                    var isoFirst = await VmBootService.SetIsoFirstAsync(createdVmId);
                    if (MustPreserveCreation(isoFirst.ErrorKind)) RequireCreationMutation(isoFirst);
                    if (!isoFirst.Success) configurationIssues.Add(isoFirst);
                }

                // 创建与启动是两个操作，由调用方决定是否继续启动。
                return new(true, createdVmId, finalVmName, VmRegistrationState.Registered, [])
                {
                    RequestedName = p.Name,
                    ConfigurationDirectory = vmHomeFolder,
                    DiskPath = p.DiskMode == 2 ? null : Path.GetFullPath(effectiveVhdPath),
                    ConfigurationIssues = configurationIssues.AsReadOnly()
                };
            }
            catch (Exception ex)
            {
                var mutationFailure = (ex as CreationMutationException)?.Response;
                bool mutationUnknown = mutationFailure?.ErrorKind == ApiErrorKind.OutcomeUnknown;
                var retainedPaths = new[] { ownedVhdPath, unconfirmedVhdPath, ownedVhdDirectoryPath,
                    vmHomeFolderCreated ? vmHomeFolder : null }
                    .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                VmCreationResult Failure(VmRegistrationState state, string? cleanupError,
                    IReadOnlyList<string> retained, ApiErrorKind cleanupKind = ApiErrorKind.CleanupRequired) => new(false,
                        createdVmId == Guid.Empty ? null : createdVmId,
                        vmCreated ? finalVmName : null, state, retained, ex.Message, cleanupError)
                    {
                        RequestedName = p.Name,
                        ErrorKind = defineSystemOutcomeUnknown || mutationUnknown ? ApiErrorKind.OutcomeUnknown
                            : mutationFailure?.ErrorKind ?? ApiErrorKind.OperationFailed,
                        OutcomeUnknown = defineSystemOutcomeUnknown || mutationUnknown || configurationOutcomeUnknown
                            || cleanupKind == ApiErrorKind.OutcomeUnknown,
                        CleanupErrorKind = cleanupError == null ? ApiErrorKind.None : cleanupKind,
                        ConfigurationIssues = configurationIssues.AsReadOnly()
                    };

                // 提交后缺少本次创建身份时不能按名称猜测目标，也不能证明原生任务未创建资源。
                if (defineSystemInvoked && (defineSystemOutcomeUnknown || (vmCreated && createdVmId == Guid.Empty)))
                    return Failure(VmRegistrationState.Unknown,
                        "创建提交结果尚未确认，已保留本次操作的文件和目录。", retainedPaths);

                // 存储提交可能仍在运行，或者它自己的回滚未能安全完成。此时继续
                // 注销 VM 或删盘可能与原生作业竞争，保留半成品并明确交回调用方。
                if (storageCleanupRequired || configurationOutcomeUnknown
                    || mutationFailure != null && MustPreserveCreation(mutationFailure.ErrorKind)
                    || retainedConfigurationCleanupError != null)
                    return Failure(VmRegistrationState.Registered,
                        "配置操作尚需核查或清理，已保留本次创建的虚拟机和相关路径，未继续回滚。" +
                        retainedConfigurationCleanupError, retainedPaths,
                        mutationUnknown || configurationOutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);

                string? rollbackError = null;
                if (vmCreated)
                {
                    var rollback = await VmDeleteService.DestroyVmAsync(finalVmName, createdVmId);
                    if (rollback.RegistrationState != VmRegistrationState.Unregistered)
                        return Failure(rollback.RegistrationState, rollback.Error, retainedPaths,
                            rollback.RegistrationState == VmRegistrationState.Unknown
                                ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);
                    if (!rollback.Success) rollbackError = rollback.Error;
                }

                var cleanupErrors = await CleanupOwnedCreationArtifactsAsync(
                    ownedVhdPath, ownedVhdDirectoryPath, vmHomeFolder, vmHomeFolderCreated);
                if (cleanupErrors.Count > 0)
                {
                    string cleanupError = string.Format(Properties.Resources.Error_VmCreate_ArtifactCleanupFailed,
                        string.Join("; ", cleanupErrors));
                    if (rollbackError != null) cleanupError = rollbackError + Environment.NewLine + cleanupError;
                    return Failure(VmRegistrationState.Unregistered, cleanupError, cleanupErrors.AsReadOnly());
                }
                return Failure(VmRegistrationState.Unregistered, rollbackError, []);
            }
        }

        /// <summary>
        /// 删除且仅删除本次创建事务拥有的文件。调用方必须先确认半成品 VM 已成功注销，
        /// 避免留下“VM 仍在册但配置文件已消失”的损坏状态。
        /// </summary>
        internal static async Task<List<string>> CleanupOwnedCreationArtifactsAsync(
            string? ownedVhdPath,
            string? ownedVhdDirectoryPath,
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

            if (!string.IsNullOrWhiteSpace(ownedVhdDirectoryPath))
            {
                bool deleted = await TryDeleteOwnedDirectoryAsync(ownedVhdDirectoryPath);
                if (!deleted)
                    errors.Add(ownedVhdDirectoryPath);
            }

            // 原子创建只证明叶目录属于本次事务，不证明其中每个文件的归属。
            // VMMS 已按创建 GUID 注销；未由它清除的配置或其他调用方后来写入的内容
            // 留在原处并报告，不能为了清理目录而扩大删除范围。
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
                    File.Delete(path);
                    return true;
                }
                catch (DirectoryNotFoundException) { return true; }
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
                    Directory.Delete(path, recursive: false);
                    return true;
                }
                catch (DirectoryNotFoundException) { return true; }
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
        // 使用本地 UntrustedGuardian 生成 KeyProtector，再通过 Msvm_SecurityService 启用 TPM 和状态加密。
        //      → Msvm_SecurityService.ModifySecuritySettings
        private static async Task<ApiResponse> EnableTpmAsync(string vmGuid, ManagementScope hyperVScope)
        {
            return await Task.Run(async () =>
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
                    throw new InvalidOperationException(Properties.Resources.Error_VmCreate_NoGuardian);

                // Step 2: 生成本地 KeyProtector
                using var kpClass = new ManagementClass(
                    hgsMs, new ManagementPath("MSFT_HgsKeyProtector"), null);
                using var kpParams = kpClass.GetMethodParameters("NewByGuardians");
                kpParams["AllowUntrustedRoot"] = true;
                kpParams.Properties["Owner"].Value = guardian;  // 通过 Properties[].Value 写入嵌入对象
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

                var protector = await WmiApi.InvokeOnObjectAsync(secSvc, "SetKeyProtector", parameters =>
                {
                    parameters["SecuritySettingData"] = secXml;
                    parameters["KeyProtector"] = rawData;
                }, timeout: TimeSpan.FromMinutes(2));
                if (!protector.Success) return protector;

                // Step 5: TpmEnabled=true + EncryptStateAndVmMigrationTraffic=true
                secSetting["TpmEnabled"] = true;
                secSetting["EncryptStateAndVmMigrationTraffic"] = true;
                string updatedXml = secSetting.GetText(TextFormat.CimDtd20);

                return await WmiApi.InvokeOnObjectAsync(secSvc, "ModifySecuritySettings",
                    parameters => parameters["SecuritySettingData"] = updatedXml,
                    timeout: TimeSpan.FromMinutes(2));
            });
        }
        internal static string? GetConfirmedCreatedVmPath(WmiCreationResult receipt)
            => !receipt.OutcomeUnknown && receipt.CreatedPaths.Length == 1
                && !string.IsNullOrWhiteSpace(receipt.CreatedPaths[0])
                ? receipt.CreatedPaths[0] : null;

        internal static async Task<(string Name, string Directory)> ReserveVmDirectoryAsync(
            string baseName, string basePath, Func<string, Task<bool>> nameExists,
            Func<string, bool>? outputPathOccupied = null)
        {
            string candidate = baseName;
            int i = 2;
            while (true)
            {
                string directory = Path.GetFullPath(Path.Combine(basePath, candidate));
                // 前两项保留原有命名规则。最后的原子创建才决定归属，不能把
                // Exists 查询当成预留；其他进程先建成时继续尝试下一个名称。
                if (!Directory.Exists(directory) && !File.Exists(directory)
                    && !(outputPathOccupied?.Invoke(candidate) ?? false) && !await nameExists(candidate)
                    && VmCreationDirectory.TryCreateLeaf(directory))
                    return (candidate, directory);
                candidate = $"{baseName} ({i++})";
            }
        }

        private static async Task<bool> VmNameExistsAsync(string name)
        {
            var resp = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(name)}",
                obj => obj["Name"]?.ToString(),
                WmiScope.HyperV);
            if (!resp.Success) throw new InvalidOperationException(resp.Error);
            return resp.HasData;
        }

    }
}
