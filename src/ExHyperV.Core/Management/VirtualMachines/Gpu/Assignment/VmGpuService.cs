using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>虚拟 GPU 分配及来宾驱动部署入口。单项方法提供底层能力；需要分步确认的完整流程使用 GpuDeploymentSession。</summary>
    public class VmGpuService
    {
        private readonly VmQueryService _queryService;
        private readonly AsyncLocal<ConcurrentQueue<string>?> _linkWarnings = new();
        private readonly AsyncLocal<GpuDriverPackagePlan?> _activeDriverPackagePlan = new();
        private readonly AsyncLocal<GpuDeploymentFileOperations?> _deploymentFiles = new();
        private readonly AsyncLocal<GuestVolumeFiles?> _guestVolume = new();

        /// <summary>建立 GPU 服务，复用传入的虚拟机查询服务。</summary>
        /// <param name="queryService">用于虚拟机身份和来宾网络等查询的服务。</param>
        public VmGpuService(VmQueryService queryService)
        {
            _queryService = queryService;
        }

        private class VmDiskTarget
        {
            public bool IsPhysical { get; set; }
            public string Path { get; set; } = string.Empty;        // 虚拟文件的VHDX 路径
            public int PhysicalDiskNumber { get; set; } // 物理硬盘的 Disk Number (e.g. 0, 1, 2)
        }


        private void ApplyOptionalRegistryEnhancement(string hivePath, string mountKey,
            Action<Microsoft.Win32.RegistryKey> edit)
        {
            // 临时特权线程不继承 AsyncLocal，先固定本次部署的警告接收者。
            var volume = _guestVolume.Value ?? throw new InvalidOperationException("Missing guest volume.");
            using var parents = volume.PinDirectory(Path.GetDirectoryName(hivePath)!);
            using var hive = volume.PinFile(hivePath, allowWrites: true);
            var logs = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
            try
            {
                foreach (string log in Directory.EnumerateFiles(Path.GetDirectoryName(hivePath)!, Path.GetFileName(hivePath) + ".*"))
                    logs.Add(volume.PinFile(log, allowWrites: true));
                var warnings = _linkWarnings.Value;
                GpuRegistryEnhancement.Apply(hivePath, mountKey, edit, warning =>
                {
                    CoreDiagnostics.Report("RegistryEnhancementWarning");
                    warnings?.Enqueue(warning);
                });
            }
            finally { foreach (var handle in logs) handle.Dispose(); }
        }


        // 挂载VHDX时寻找可用的盘符
        private char GetFreeDriveLetter()
        {
            var usedLetters = DriveInfo.GetDrives().Select(d => d.Name[0]).ToList();
            for (char c = 'Z'; c >= 'A'; c--)
            {
                if (!usedLetters.Contains(c))
                {
                    return c;
                }
            }
            throw new IOException(Properties.Resources.Error_NoAvailableDriveLetters);
        }

        #region 硬件信息与虚拟机查询
        /// <summary>按稳定身份读取虚拟机 GPU 分配；查询失败不伪装为空清单。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>分配快照或查询错误；缺少宿主显示信息时仍保留设备身份。</returns>
        /// <remarks>旧宿主缺少 HostResource 时，会用第一张可分区 GPU 补充候选信息。精确关联使用 VmGpuFields 查询，并检查字段状态。</remarks>
        public async Task<ApiResponse<IReadOnlyList<VmGpuAssignment>>> GetVmGpuAssignmentsAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return ApiResponse<IReadOnlyList<VmGpuAssignment>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            try
            {
                var adapters = await TryGetVmGpuAdaptersAsync(vmId);
                if (!adapters.Success) return ApiResponse<IReadOnlyList<VmGpuAssignment>>.Fail("读取虚拟 GPU 适配器失败。");
                var hostGpus = await HostGpuService.ReadHostGpusAsync();
                var metaResult = await VmMetadataService.ReadAsync(vmId);
                var guestDriverVersions = metaResult.HasData
                    ? VmMetadataService.ParseGpuDriversMap(metaResult.Data)
                    : null;
                return ApiResponse<IReadOnlyList<VmGpuAssignment>>.Ok(
                    BuildGpuAssignments(adapters.Adapters, hostGpus, guestDriverVersions).AsReadOnly());
            }
            catch (Exception ex) { return ApiResponse<IReadOnlyList<VmGpuAssignment>>.Fail(ex.Message, exception: ex); }
        }

        internal static List<VmGpuAssignment> BuildGpuAssignments(
            IReadOnlyList<(string Id, string InstancePath)> adapters,
            IReadOnlyList<HostGpuInfo> hostGpus,
            IReadOnlyDictionary<string, string>? guestDriverVersions = null)
        {
            var assignments = new List<VmGpuAssignment>();
            foreach (var adapter in adapters)
            {
                var matchedHostGpu = hostGpus.FirstOrDefault(h =>
                    !string.IsNullOrEmpty(h.InstanceId) &&
                    !string.IsNullOrEmpty(adapter.InstancePath) &&
                    (adapter.InstancePath.Contains(h.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                     NormalizeAssignmentDeviceId(h.InstanceId) == NormalizeAssignmentDeviceId(adapter.InstancePath)));

                var assignment = new VmGpuAssignment { AdapterId = adapter.Id };
                if (matchedHostGpu != null)
                {
                    assignment.HostDeviceInstanceId = matchedHostGpu.InstanceId;
                    assignment.Name = matchedHostGpu.Name;
                    assignment.Manu = matchedHostGpu.Manu;
                    assignment.Vendor = matchedHostGpu.Vendor;
                    assignment.DriverVersion = matchedHostGpu.DriverVersion;
                    assignment.MemoryBytes = matchedHostGpu.MemoryBytes;
                    assignment.PartitionableGpuPath = matchedHostGpu.PartitionableGpuPath;
                }
                else
                {
                    assignment.Name = "Unknown Device";
                    assignment.Manu = "Default";
                }

                if (guestDriverVersions != null && guestDriverVersions.Count > 0)
                {
                    string hostKey = VmMetadataService.NormalizeGpuDriverKey(matchedHostGpu?.InstanceId ?? string.Empty);
                    string adapterKey = VmMetadataService.NormalizeGpuDriverKey(adapter.InstancePath);
                    string? recordedVersion = null;
                    if (!string.IsNullOrEmpty(hostKey) && guestDriverVersions.TryGetValue(hostKey, out string? v1))
                        recordedVersion = v1;
                    else if (!string.IsNullOrEmpty(adapterKey) && guestDriverVersions.TryGetValue(adapterKey, out string? v2))
                        recordedVersion = v2;

                    assignment.GuestDriverVersion = recordedVersion;
                }

                assignments.Add(assignment);
            }
            return assignments;
        }

        // Assignment matching keeps the original separator-insensitive UI rule;
        // partitionable-device discovery uses NormalizeDeviceId separately.
        private static string NormalizeAssignmentDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
            var normalizedId = deviceId.ToUpper();
            if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);
            int suffixIndex = normalizedId.IndexOf("#{");
            if (suffixIndex != -1) normalizedId = normalizedId.Substring(0, suffixIndex);
            return normalizedId.Replace('\\', '#').Replace("#", "");
        }

        /// <summary>查询虚拟 GPU 适配器并保留成功标记，区分无设备和查询失败。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="allowDisplayFallback">是否允许读取资源池首项作为显示候选。</param>
        /// <returns>Success 表示查询成功，Adapters 为 Id 与 InstancePath 列表，失败时为空。</returns>
        /// <remarks>旧版宿主缺少适配器资源路径时，使用资源池首项作为候选；多 GPU 环境需进一步核对物理设备。</remarks>
        internal async Task<(bool Success, List<(string Id, string InstancePath)> Adapters)> TryGetVmGpuAdaptersAsync(Guid vmId, bool allowDisplayFallback = true)
        {
            var result = new List<(string Id, string InstancePath)>();
            string scopePath = @"\\.\root\virtualization\v2";
            try
            {
                // Win10 早期 Msvm_GpuPartitionSettingData.HostResource 读不回 → 无法按分区对应物理 GPU。
                // 取主机首张(通常也是唯一)可分区 GPU 的 Name 兜底显示参数：单卡准确；多卡是引擎启动时自选、
                // 本就无法精确对应的固有局限(禁用其他卡强选的老思路已不在)。Win11 走 HostResource 精确路径、到不了这兜底。
                string firstPartitionableGpu = string.Empty;
                if (allowDisplayFallback)
                {
                    var pool = await WmiApi.QueryAsync("SELECT Name FROM Msvm_PartitionableGpu", obj => obj["Name"] as string);
                    if (!pool.Success) return (false, result);
                    firstPartitionableGpu = pool.Data?.FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty;
                }

                string query = $"SELECT * FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}";
                using var searcher = new ManagementObjectSearcher(scopePath, query);
                using var vmCollection = searcher.Get();
                using var computerSystem = vmCollection.Cast<ManagementObject>().FirstOrDefault();
                if (computerSystem == null) return (false, result);

                using var relatedSettings = computerSystem.GetRelated(
                    "Msvm_VirtualSystemSettingData",
                    "Msvm_SettingsDefineState",
                    null, null, null, null, false, null);
                using var virtualSystemSetting = relatedSettings.Cast<ManagementObject>().FirstOrDefault();
                if (virtualSystemSetting == null) return (false, result);

                using var gpuSettingsCollection = virtualSystemSetting.GetRelated(
                    "Msvm_GpuPartitionSettingData",
                    "Msvm_VirtualSystemSettingDataComponent",
                    null, null, null, null, false, null);

                foreach (var gpuSetting in gpuSettingsCollection.Cast<ManagementObject>())
                {
                    using var ownedSetting = gpuSetting;
                    string adapterId = gpuSetting["InstanceID"]?.ToString();
                    string instancePath = string.Empty;

                    string[]? hostResources = gpuSetting.HasProperty("HostResource") ? gpuSetting["HostResource"] as string[] : null;
                    if (hostResources != null && hostResources.Length == 1)
                    {
                        try
                        {
                            using var partitionableGpu = new ManagementObject(hostResources[0]);
                            partitionableGpu.Get();
                            instancePath = partitionableGpu["Name"]?.ToString();
                        }
                        catch (Exception diagnosticError) { CoreDiagnostics.Report("PartitionableGpuLookupFailed", error: diagnosticError); }
                    }

                    // HostResource 空/Unknown(Win10 早期) → 兜底用首张可分区 GPU 的 Name(单卡准确)
                    if (string.IsNullOrEmpty(instancePath) || instancePath.Contains("Unknown"))
                        instancePath = allowDisplayFallback ? firstPartitionableGpu : string.Empty;

                    if (string.IsNullOrEmpty(adapterId))
                    {
                        if (!allowDisplayFallback) return (false, []);
                    }
                    else result.Add((adapterId, instancePath));
                }
            }
            catch (Exception ex)
            {
                CoreDiagnostics.Report("GpuWmiQueryFailed", error: ex);
                return (false, new List<(string Id, string InstancePath)>());
            }
            return (true, result);
        }
        #endregion

        #region 虚拟机状态与控制管理
        /// <summary>检查来宾缓存控制及高位 MMIO 是否满足当前 GPU 配置要求。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>数据为 true 表示满足条件，false 表示需要配置；读取错误为失败响应。</returns>
        public async Task<ApiResponse<bool>> CheckVmForGpuAsync(Guid vmId)
        {
            try
            {
                // 读裸 WMI 的真实属性：HighMmioGapSize(MB) + GuestControlledCacheTypes。
                // 旧实现读 HighMemoryMappedIoSpace/HighMemoryMappedIoBaseAddress(字节)——那是 PowerShell Get-VM 对象的属性，
                // PowerShell 的 HighMemoryMappedIoSpace 属性不属于该 WMI 类，不能用于此处检测。
                var r = await WmiApi.QueryFirstAsync(
                    $"SELECT HighMmioGapSize, GuestControlledCacheTypes FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
                    obj => (
                        HighMmioMb: Convert.ToUInt64(obj["HighMmioGapSize"] ?? 0),
                        Cache: Convert.ToBoolean(obj["GuestControlledCacheTypes"] ?? false)));

                if (!r.HasData) return ApiResponse<bool>.Fail(r.Success ? "找不到虚拟机配置。" : r.Error, r.Code, r.ErrorSource, r.Exception, r.Success ? ApiErrorKind.NotFound : r.ErrorKind);
                if (!r.Data.Cache) return ApiResponse<bool>.Ok(false);

                // 与 ConfigureMmio 会写入的目标比对(同一 ComputeMmioPlan、主机自适应)：已 >= 目标高位 MMIO 间隙即视为达标、无需重配。
                ulong targetMb = VmMmioService.ComputeMmioPlan()?.HighSizeMiB ?? VmMmioService.DefaultHighSizeMiB; // 读不到主机上限时回退默认 256G（同一常量）
                return ApiResponse<bool>.Ok(r.Data.HighMmioMb >= targetMb);
            }
            catch (Exception ex) { return ApiResponse<bool>.Fail(ex.Message, exception: ex); }
        }
        /// <summary>配置 GPU 所需的来宾缓存控制和 MMIO 空间。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>配置执行结果；已完成的探测和电源变化会保留。</returns>
        /// <remarks>可能复用宿主 MMIO 检测结果；没有结果时，检测可能启动并强制关闭目标虚拟机。调用完整部署流程可在 Core 的确认步骤后执行这些操作。</remarks>
    /// <editentry label="配置 GPU 所需环境" category="GPU"/>
        public async Task<ApiResponse> OptimizeVmForGpuAsync(Guid vmId)
        {
            return await VmMmioService.ConfigureMmioAsync(vmId);
        }

        #endregion

        #region 磁盘与分区操作
        /// <summary>在目标虚拟机关闭时扫描磁盘分区，并恢复扫描前的宿主磁盘状态。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="cancellationToken">等待资源锁及磁盘之间检查取消；已开始的磁盘操作完成恢复后才返回。</param>
        /// <returns>分区、扫描错误和独立的恢复错误；失败时分区可能不完整。</returns>
        /// <remarks>同磁盘或映像的 Core 操作跨进程排队，锁覆盖原状态读取、扫描及恢复。借用已有挂载，本次新增挂载在结束后卸载；核对磁盘身份后恢复原状态，并覆盖期间的外部修改。</remarks>
        public async Task<VmPartitionScanResult> ScanVmPartitionsAsync(Guid vmId, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return new([], [], [], true);
            if (vmId == Guid.Empty) return new([], ["虚拟机 GUID 不能为空。"], []);
            var state = await VmConfigurationState.ReadAsync(vmId);
            if (!state.HasData || !state.Data!.IsPoweredOff)
                return new([], [state.Success ? "扫描分区前必须关闭虚拟机。" : state.Error], []);
            var storage = await VmStorageService.GetVmStorageItemsAsync(vmId);
            if (!storage.HasData) return new([], [storage.Error], []);
            var targets = storage.Data!.Where(i => i.DriveType == "HardDisk").Select(i => new VmDiskTarget
            {
                IsPhysical = i.DiskType == "Physical", Path = i.PathOrDiskNumber,
                PhysicalDiskNumber = i.DiskNumber
            }).ToArray();
            var partitions = new List<DiskPartitionInfo>();
            var errors = new List<string>();
            var cleanup = new List<string>();
            bool outcomeUnknown = false;
            foreach (var target in targets)
            {
                if (cancellationToken.IsCancellationRequested) break;
                string label = target.IsPhysical ? $"磁盘 {target.PhysicalDiskNumber}" : target.Path;
                void ReadPartitions(int number, HostDiskSnapshot? original)
                {
                    foreach (var partition in new DiskParser().GetPartitions($@"\\.\PhysicalDrive{number}"))
                    {
                        partition.DiskPath = target.IsPhysical ? target.PhysicalDiskNumber.ToString() : target.Path;
                        partition.DiskDisplayName = target.IsPhysical ? label : Path.GetFileName(target.Path);
                        partition.IsPhysicalDisk = target.IsPhysical;
                        partition.ScannedVmId = vmId;
                        partition.ScannedDiskPath = partition.DiskPath;
                        partition.ScannedPhysical = target.IsPhysical;
                        partition.ScannedDiskIdentity = original;
                        partitions.Add(partition);
                    }
                }
                try
                {
                    if (target.IsPhysical)
                    {
                        await HostDiskService.WithDiskLockAsync(target.PhysicalDiskNumber, async () =>
                        {
                            HostDiskSnapshot? original = null;
                            bool changed = false;
                            bool mutationUnknown = false;
                            try
                            {
                                await VerifyDiskTargetAsync(vmId, target);
                                var snapshot = await HostDiskService.ReadDiskSnapshotAsync(target.PhysicalDiskNumber);
                                if (!snapshot.HasData) throw new IOException(snapshot.Success ? "读取磁盘状态失败。" : snapshot.Error);
                                original = snapshot.Data!;
                                if (original.IsSystem || original.IsBoot || !original.SameDevice(original))
                                    throw new InvalidOperationException("磁盘不允许扫描或无法核对稳定身份。");
                                GpuDiskRecovery.DiskState(vmId, original);
                                changed = true;
                                var readOnly = await HostDiskService.SetDiskReadOnlyCoreAsync(original.Number, true);
                                OperationOutcomeUnknownException.ThrowIfUnknown(readOnly);
                                if (!readOnly.Success) throw new IOException(readOnly.Error);
                                var online = await HostDiskService.SetDiskOfflineStatusCoreAsync(original.Number, false);
                                OperationOutcomeUnknownException.ThrowIfUnknown(online);
                                if (!online.Success) throw new IOException(online.Error);
                                ReadPartitions(original.Number, original);
                                return true;
                            }
                            catch (OperationOutcomeUnknownException error)
                            {
                                mutationUnknown = outcomeUnknown = true;
                                cleanup.Add($"{label}: 底层操作尚未确认结束，未执行磁盘恢复。{error.Message}");
                                throw;
                            }
                            finally
                            {
                                if (changed && original != null && !mutationUnknown)
                                {
                                    try
                                    {
                                        var restored = await HostDiskService.RestoreDiskCoreAsync(original);
                                        outcomeUnknown |= restored.ErrorKind == ApiErrorKind.OutcomeUnknown;
                                        if (!restored.Success) cleanup.Add($"{label}: {restored.Error}");
                                    }
                                    catch (Exception ex) { cleanup.Add($"{label}: {ex.Message}"); }
                                }
                            }
                        }, cancellationToken);
                    }
                    else
                    {
                        await WithTemporaryImageAsync(vmId, target, true, number =>
                        {
                            ReadPartitions(number, null);
                            return Task.FromResult(true);
                        }, error =>
                        {
                            outcomeUnknown |= error.ErrorKind == ApiErrorKind.OutcomeUnknown;
                            cleanup.Add($"{label}: {error.Error}");
                        }, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (OperationOutcomeUnknownException ex) { outcomeUnknown = true; errors.Add($"{label}: {ex.Message}"); }
                catch (Exception ex) { errors.Add($"{label}: {ex.Message}"); }
                if (cleanup.Count > 0 || outcomeUnknown) break;
            }
            return new(partitions.AsReadOnly(), errors.AsReadOnly(), cleanup.AsReadOnly(), cancellationToken.IsCancellationRequested)
                { OutcomeUnknown = outcomeUnknown };
        }

        private static async Task VerifyDiskTargetAsync(Guid vmId, VmDiskTarget target)
        {
            var state = await VmConfigurationState.ReadAsync(vmId);
            if (!state.HasData || !state.Data!.IsPoweredOff)
                throw new InvalidOperationException("虚拟机已不处于关闭状态，已停止磁盘操作。");
            var storage = await VmStorageService.GetVmStorageItemsAsync(vmId);
            if (!storage.HasData) throw new IOException(storage.Error);
            if (!storage.Data!.Any(item => item.DriveType == "HardDisk" &&
                (item.DiskType == "Physical") == target.IsPhysical &&
                (target.IsPhysical ? item.DiskNumber == target.PhysicalDiskNumber :
                    string.Equals(item.PathOrDiskNumber, target.Path, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("磁盘已不属于目标虚拟机，请重新查询。");
        }

        private static Task<T> WithTemporaryImageAsync<T>(Guid vmId, VmDiskTarget target, bool readOnly,
            Func<int, Task<T>> action, Action<ApiResponse> reportCleanup, CancellationToken cancellationToken = default)
            => HostDiskService.WithImageLockAsync(target.Path, async () =>
            {
                await VerifyDiskTargetAsync(vmId, target);
                var recordMount = await GpuDiskRecovery.ImageAsync(vmId, target.Path);
                HostDiskMountResult? mount = null;
                bool cleanupAttempted = false;
                bool actionUnknown = false;
                async Task<bool> ReleaseAsync()
                {
                    cleanupAttempted = true;
                    if (actionUnknown)
                    {
                        reportCleanup(ApiResponse.Fail("映像内操作尚未确认结束，已保留挂载，未执行卸载。", kind: ApiErrorKind.OutcomeUnknown));
                        return false;
                    }
                    try
                    {
                        var released = await HostDiskService.ReleaseScanMountAsync(mount!,
                            path => HostDiskService.DismountVhdxCoreAsync(path, mount!.DiskNumber));
                        if (!released.Success) reportCleanup(released);
                    }
                    catch (Exception error) { reportCleanup(ApiResponse.Fail(error.Message, exception: error, kind: ApiErrorKind.CleanupRequired)); }
                    return true;
                }
                try
                {
                    mount = await HostDiskService.MountVhdxCoreAsync(target.Path, readOnly, cancellationToken);
                    recordMount(mount);
                    if (mount.RequiresInspection)
                        throw new OperationOutcomeUnknownException(ApiResponse.Fail(mount.Error, kind: ApiErrorKind.OutcomeUnknown));
                    if (!mount.Success) throw new IOException(mount.Error);
                    return await HostDiskService.WithDiskLockAsync(mount.DiskNumber!.Value, async () =>
                    {
                        try
                        {
                            // 盘号锁可能排队；取得后重新核对映像仍对应这一块磁盘。
                            var current = await HostDiskService.ReadImageAsync(target.Path);
                            if (!current.HasData || !current.Data!.Attached || current.Data.Number != mount.DiskNumber)
                                throw new IOException("等待期间映像挂载身份发生变化，未访问替代磁盘。");
                            return await action(mount.DiskNumber.Value);
                        }
                        catch (OperationOutcomeUnknownException) { actionUnknown = true; throw; }
                        finally { await ReleaseAsync(); }
                    }, cancellationToken);
                }
                finally
                {
                    // 等锁取消或盘号发现失败，也必须处理本次挂载的清理责任。
                    // 收尾不沿用已取消的令牌；资源锁持续覆盖实际卸载。
                    if (mount != null && !cleanupAttempted)
                    {
                        try
                        {
                            if (mount.DiskNumber.HasValue)
                                await HostDiskService.WithDiskLockAsync(mount.DiskNumber.Value, ReleaseAsync);
                            else await ReleaseAsync();
                        }
                        catch (Exception error) { reportCleanup(ApiResponse.Fail(error.Message, exception: error, kind: ApiErrorKind.CleanupRequired)); }
                    }
                }
            }, cancellationToken);

        #endregion

        #region GPU 分配与解绑
        /// <summary>为虚拟机分配所选宿主 GPU 的分区；执行前重新解析当前可分区资源。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="gpu">GetHostGpusAsync 返回的宿主 GPU，按其 PnP 设备身份重新查询。</param>
        /// <returns>添加和后续验证结果；验证失败时已创建的适配器可能保留。</returns>
        /// <remarks>创建结果或归属待确认时返回 OutcomeUnknown；确认失败且留有本次设备时返回 CleanupRequired。分步核对及清理使用 GpuDeploymentSession。</remarks>
    /// <editentry label="分配 GPU 分区" category="GPU"/>
        public async Task<ApiResponse> AssignGpuPartitionAsync(Guid vmId, HostGpuInfo gpu)
        {
            if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            ArgumentNullException.ThrowIfNull(gpu);
            var hosts = await HostGpuService.GetHostGpusAsync();
            if (!hosts.HasData) return ApiResponse.Fail(hosts.Error, hosts.Code, hosts.ErrorSource, hosts.Exception, hosts.ErrorKind);
            var current = hosts.Data!.FirstOrDefault(item => string.Equals(item.InstanceId, gpu.InstanceId, StringComparison.OrdinalIgnoreCase));
            if (current is null || string.IsNullOrEmpty(current.PartitionableGpuPath))
                return ApiResponse.Fail(Properties.Resources.Error_Gpu_NoPartition, kind: ApiErrorKind.NotFound);
            return (await AssignGpuPartitionWithOwnershipAsync(vmId, current.PartitionableGpuPath)).Response;
        }

        /// <summary>为虚拟机新增绑定指定宿主 GPU 的分区适配器，并进行后续查询验证。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="gpuInstancePath">宿主可分区 GPU 资源路径，通常使用 HostGpuInfo.PartitionableGpuPath。</param>
        /// <returns>操作响应与底层确认的新适配器身份；无法确认创建结果时保留不确定状态，禁止通过清单差异推断身份。</returns>
        /// <remarks>失败后可能保留新增设备；需要跟踪和清理时使用部署会话。</remarks>
        /// <param name="trace">记录本次原生创建调用的结果和资源身份；null 时由内部流程收集。</param>
        internal async Task<GpuAssignmentAttempt> AssignGpuPartitionWithOwnershipAsync(Guid vmId, string gpuInstancePath, WmiCreationTrace? trace = null)
        {
            try
            {
                return await HostResourceLock.RunAsync(VmSettingsWriteLock.Resource(vmId),
                    () => AssignUnderLockAsync(vmId, gpuInstancePath, trace));
            }
            catch (Exception error)
            {
                var failure = ApiFailure.From(error);
                return new(ApiResponse.Fail(failure.Message, failure.Code, failure.Source, error, failure.Kind));
            }
        }

        private async Task<GpuAssignmentAttempt> AssignUnderLockAsync(Guid vmId, string gpuInstancePath, WmiCreationTrace? trace)
        {
            bool ownershipUncertain = false;
            string? createdAdapterId = null;
            try
            {
                // 查 Msvm_PartitionableGpu 拿 WMI 对象路径
                var gpuResp = await WmiApi.QueryAsync(
                    "SELECT * FROM Msvm_PartitionableGpu",
                    obj => new { WmiPath = obj.Path.Path, Name = obj["Name"]?.ToString() ?? "" });
                if (!gpuResp.HasData) return new(ApiResponse.Fail(Properties.Resources.Error_Gpu_NoPartition));
                var matched = gpuResp.Data.FirstOrDefault(g =>
                    string.Equals(g.Name, gpuInstancePath, StringComparison.OrdinalIgnoreCase));
                if (matched == null) return new(ApiResponse.Fail(Properties.Resources.Error_Gpu_NoPartition));
                string gpuWmiPath = matched.WmiPath;

                // 查 ResourcePool → AllocationCapabilities → Default 模板
                var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var poolSearcher = new ManagementObjectSearcher(ms,
                    new ObjectQuery("SELECT * FROM Msvm_ResourcePool WHERE ResourceType = 32770 AND ResourceSubType = 'Microsoft:Hyper-V:GPU Partition' AND Primordial = TRUE"));
                using var poolCollection = poolSearcher.Get();
                using var pool = poolCollection.Cast<ManagementObject>().FirstOrDefault();
                if (pool == null) return new(ApiResponse.Fail(Properties.Resources.Error_Gpu_NoPartition));

                using var capsCollection = pool.GetRelated("Msvm_AllocationCapabilities");
                using var caps = capsCollection.Cast<ManagementObject>().FirstOrDefault();
                if (caps == null) return new(ApiResponse.Fail(Properties.Resources.Error_Gpu_NoPartition));

                using var templateCollection = caps.GetRelated("Msvm_GpuPartitionSettingData");
                using var template = templateCollection.Cast<ManagementObject>()
                    .FirstOrDefault(t => t["InstanceID"]?.ToString()?.EndsWith("\\Default", StringComparison.OrdinalIgnoreCase) == true);
                if (template == null) return new(ApiResponse.Fail(Properties.Resources.Error_Gpu_NoPartition));

                // 设置 HostResource 为 WMI 路径，清空 InstanceID
                template["InstanceID"] = null;
                template["HostResource"] = new string[] { gpuWmiPath };
                string gpuXml = template.GetText(TextFormat.CimDtd20);

                // 查 VM SettingData 路径
                var vmSettingResp = await WmiApi.QueryFirstAsync(
                    $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
                    obj => obj.Path.Path);
                if (!vmSettingResp.HasData) return new(ApiResponse.Fail(Properties.Resources.Error_Vm_GetSettings));

                // 只接收本次创建操作的回执，不通过设备清单的前后差异推断归属。
                ownershipUncertain = true;
                var result = await WmiApi.InvokeCreationAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "AddResourceSettings",
                    "Msvm_GpuPartitionSettingData",
                    p =>
                    {
                        p["AffectedConfiguration"] = vmSettingResp.Data;
                        p["ResourceSettings"] = new string[] { gpuXml };
                    }, trace: trace);

                return await ResolveAssignmentReceiptAsync(vmId, result);
            }
            catch (Exception ex)
            {
                return new(ApiResponse.Fail(ex.Message, exception: ex,
                    kind: ownershipUncertain ? ApiErrorKind.OutcomeUnknown : createdAdapterId != null ? ApiErrorKind.CleanupRequired : ApiErrorKind.OperationFailed),
                    createdAdapterId, ownershipUncertain);
            }
        }
        internal async Task<GpuAssignmentAttempt> ReconcileAssignmentAsync(Guid vmId, WmiCreationTrace trace)
            => await ResolveAssignmentReceiptAsync(vmId, await WmiApi.ObserveCreationAsync(trace));

        private async Task<GpuAssignmentAttempt> ResolveAssignmentReceiptAsync(Guid vmId, WmiCreationResult result)
        {
            bool ownershipUncertain = result.OutcomeUnknown;
            string? createdAdapterId = null;
            try
            {
                var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                var paths = result.CreatedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (paths.Length == 0 && !result.Response.Success && !result.OutcomeUnknown)
                    return new(result.Response);
                if (paths.Length != 1)
                    return new(ApiResponse.Fail(result.Response.Success ? Properties.Resources.Warn_Gpu_RollbackAdapterNotIdentified : result.Response.Error,
                        result.Response.Code, result.Response.ErrorSource, result.Response.Exception, ApiErrorKind.OutcomeUnknown),
                        OwnershipUncertain: true);

                var path = new ManagementPath(paths[0]);
                if (!string.Equals(path.ClassName, "Msvm_GpuPartitionSettingData", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Properties.Resources.Warn_Gpu_RollbackAdapterNotIdentified);
                using var created = new ManagementObject(ms, path, null);
                created.Get();
                string adapterId = created["InstanceID"]?.ToString() ?? "";
                var currentAdapters = await TryGetVmGpuAdaptersAsync(vmId);
                if (!currentAdapters.Success || string.IsNullOrWhiteSpace(adapterId)
                    || !currentAdapters.Adapters.Any(adapter => string.Equals(adapter.Id, adapterId, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(Properties.Resources.Warn_Gpu_RollbackAdapterNotIdentified);

                createdAdapterId = adapterId;
                ownershipUncertain = result.OutcomeUnknown;
                if (ownershipUncertain || !result.Response.Success)
                    return new(ApiResponse.Fail(result.Response.Success ? Properties.Resources.Warn_Gpu_RollbackAdapterNotIdentified : result.Response.Error,
                        result.Response.Code, result.Response.ErrorSource, result.Response.Exception,
                        ownershipUncertain ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired),
                        createdAdapterId, ownershipUncertain);
                return new(ApiResponse.Ok(), createdAdapterId);
            }
            catch (Exception ex)
            {
                return new(ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OutcomeUnknown),
                    createdAdapterId, OwnershipUncertain: true);
            }
        }

        /// <summary>移除所查询的 GPU 分配，执行前重新核对其属于目标虚拟机。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <param name="assignment">GetVmGpuAssignmentsAsync 返回的分配对象。</param>
        /// <returns>移除结果；对象归属不符、设备已移除或查询失败均明确报告。</returns>
    /// <editentry label="移除 GPU 分区" category="GPU"/>
        public Task<ApiResponse> RemoveGpuPartitionAsync(Guid vmId, VmGpuAssignment assignment)
        {
            ArgumentNullException.ThrowIfNull(assignment);
            return RemoveGpuPartitionAsync(vmId, assignment.AdapterId);
        }

        internal Task<ApiResponse> RemoveGpuPartitionAsync(Guid vmId, string adapterId) =>
            VmSettingsWriteLock.RunAsync(vmId, () => RemoveUnderLockAsync(vmId, adapterId));

        private async Task<ApiResponse> RemoveUnderLockAsync(Guid vmId, string adapterId)
        {
            HostResourceLock.AssertHeld(VmSettingsWriteLock.Resource(vmId));
            if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var current = await TryGetVmGpuAdaptersAsync(vmId, allowDisplayFallback: false);
            if (!current.Success) return ApiResponse.Fail("无法读取目标虚拟机的 GPU 分配。");
            var matches = current.Adapters.Where(a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) return ApiResponse.Fail("GPU 适配器身份不唯一。", kind: ApiErrorKind.AmbiguousTarget);
            var targetAdapter = matches.SingleOrDefault();
            if (string.IsNullOrEmpty(adapterId) || string.IsNullOrEmpty(targetAdapter.Id))
                return ApiResponse.Fail("GPU 分配已不存在或不属于目标虚拟机。", kind: ApiErrorKind.Conflict);
            var adapter = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_GpuPartitionSettingData WHERE InstanceID = '{WmiApi.Escape(adapterId)}'",
                obj => obj.Path.Path);
            if (!adapter.HasData) return ApiResponse.Fail(adapter.Success ? "GPU 分配已不存在。" : adapter.Error,
                adapter.Code, adapter.ErrorSource, adapter.Exception, adapter.Success ? ApiErrorKind.NotFound : adapter.ErrorKind);
            var removeResult = await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualSystemManagementService", "RemoveResourceSettings",
                p => p["ResourceSettings"] = new[] { adapter.Data! });
            if (!removeResult.Success) return removeResult;

            return await FinishRemovalAsync(adapterId, targetAdapter.InstancePath,
                () => TryGetVmGpuAdaptersAsync(vmId, allowDisplayFallback: false),
                key => VmMetadataService.RemoveGpuDriverVersionUnderLockAsync(vmId, key));
        }

        internal static async Task<ApiResponse> FinishRemovalAsync(string adapterId, string previousPath,
            Func<Task<(bool Success, List<(string Id, string InstancePath)> Adapters)>> readAfter,
            Func<string, Task<ApiResponse>> clearVersion)
        {
            var removed = OperationStepResult.From("RemoveGpuAdapter", ApiResponse.Ok());
            var after = await readAfter();
            if (!after.Success)
                return ApiResponse.Fail("无法核实移除后的 GPU 清单。", kind: ApiErrorKind.OutcomeUnknown).WithSteps([removed]);
            if (after.Adapters.Any(a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase)))
                return ApiResponse.Fail("移除后仍存在该 GPU 适配器。", kind: ApiErrorKind.Conflict).WithSteps([removed]);
            string key = VmMetadataService.NormalizeGpuDriverKey(previousPath);
            if (key.Length == 0 || after.Adapters.Any(a => string.IsNullOrWhiteSpace(a.InstancePath)))
                return ApiResponse.Fail("无法核实剩余 GPU 的物理归属，已保留驱动记录。", kind: ApiErrorKind.CleanupRequired)
                    .WithSteps([removed]);
            if (after.Adapters.Any(a => VmMetadataService.NormalizeGpuDriverKey(a.InstancePath) == key))
                return ApiResponse.Ok().WithSteps([removed]);
            ApiResponse metadata;
            try { metadata = await clearVersion(key); }
            catch (Exception error)
            {
                var failure = ApiFailure.From(error);
                metadata = ApiResponse.Fail(failure.Message, failure.Code, failure.Source, error, failure.Kind);
            }
            var steps = new[] { removed, OperationStepResult.From("RemoveGpuDriverVersion", metadata) };
            return metadata.Success ? metadata.WithSteps(steps) : ApiResponse.Fail(metadata.Error, metadata.Code,
                metadata.ErrorSource, metadata.Exception, metadata.ErrorKind == ApiErrorKind.OutcomeUnknown ?
                    ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired).WithSteps(steps);
        }
        #endregion

        #region Windows 驱动环境注入
        /// <summary>向指定离线 Windows 系统分区同步宿主 GPU 驱动包及相关文件。</summary>
        /// <param name="vmId">已关闭的目标虚拟机的稳定 GUID。</param>
        /// <param name="gpuInstancePath">宿主 GPU 设备实例或分区资源路径。</param>
        /// <param name="gpuManu">GPU 芯片商名称，用于厂商相关文件处理。</param>
        /// <param name="selectedPartition">同一虚拟机由 ScanVmPartitionsAsync 产生的分区；磁盘来源不得改写。执行前重新核对挂接关系与物理盘身份。</param>
        /// <param name="progressCallback">可选的进展文本回调，应快速返回且不抛出异常；界面更新需调度到界面线程。</param>
        /// <returns>主要驱动同步是否成功及消息；可选文件和注册表增强问题通过 progressCallback 报告警告，不单独使主流程失败。</returns>
        /// <remarks>会临时挂载 VHD 或断开物理盘直通、联机并分配盘符。同磁盘或映像的 Core 操作跨进程排队，锁覆盖使用、卸载及重接；重接后再次恢复原宿主磁盘状态，覆盖期间的外部状态修改，身份不符时停止恢复。注册表增强的加载、写入或卸载失败仅告警，继续部署及原磁盘恢复流程；调用方需提供 progressCallback 接收这些警告。磁盘清理或重接失败返回 CleanupRequired，并保留业务错误；不回滚已复制驱动文件。</remarks>
        /// <editentry label="部署 Windows GPU 驱动" category="GPU"/>
        public Task<ApiResponse> SyncWindowsDriversAsync(
            Guid vmId,
            string gpuInstancePath,
            string gpuManu,
            DiskPartitionInfo selectedPartition,
            Action<string> progressCallback = null)
        {
            var gpus = new[]
            {
                new HostGpuInfo
                {
                    InstanceId = gpuInstancePath ?? string.Empty,
                    PartitionableGpuPath = gpuInstancePath ?? string.Empty,
                    Manu = gpuManu ?? string.Empty
                }
            };
            return SyncWindowsDriversCoreAsync(vmId, gpus, selectedPartition, progressCallback);
        }

        internal async Task<ApiResponse> SyncWindowsDriversCoreAsync(
            Guid vmId,
            IReadOnlyList<HostGpuInfo> gpus,
            DiskPartitionInfo selectedPartition,
            Action<string>? progressCallback = null)
        {
            if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            if (gpus == null || gpus.Count == 0) return ApiResponse.Fail("请至少选择一张宿主显卡。", kind: ApiErrorKind.InvalidArgument);
            if (selectedPartition == null) return ApiResponse.Fail(Properties.Resources.Error_Common_NoPartitionSelected, kind: ApiErrorKind.InvalidArgument);
            if (selectedPartition.ScannedVmId != vmId || selectedPartition.ScannedDiskPath != selectedPartition.DiskPath ||
                selectedPartition.ScannedPhysical != selectedPartition.IsPhysicalDisk)
                return ApiResponse.Fail("分区不是该虚拟机的原始扫描结果，请重新扫描。", kind: ApiErrorKind.Conflict);
            var state = await VmConfigurationState.ReadAsync(vmId);
            if (!state.HasData || !state.Data!.IsPoweredOff) return ApiResponse.Fail("虚拟机必须处于关闭状态。", kind: ApiErrorKind.InvalidState);
            var storage = await VmStorageService.GetVmStorageItemsAsync(vmId);
            if (!storage.HasData) return ApiResponse.Fail(storage.Error, storage.Code, storage.ErrorSource, storage.Exception, storage.ErrorKind);
            if (!storage.Data!.Any(item => item.DriveType == "HardDisk" &&
                (item.DiskType == "Physical") == selectedPartition.IsPhysicalDisk &&
                (selectedPartition.IsPhysicalDisk
                    ? int.TryParse(selectedPartition.DiskPath, out int diskNumber) && item.DiskNumber == diskNumber
                    : string.Equals(item.PathOrDiskNumber, selectedPartition.DiskPath, StringComparison.OrdinalIgnoreCase))))
                return ApiResponse.Fail("扫描的磁盘已不属于目标虚拟机，请重新扫描。", kind: ApiErrorKind.Conflict);

            var diskTarget = new VmDiskTarget
            {
                IsPhysical = selectedPartition.IsPhysicalDisk,
                Path = selectedPartition.IsPhysicalDisk ? null : selectedPartition.DiskPath,
                PhysicalDiskNumber = selectedPartition.IsPhysicalDisk ? int.Parse(selectedPartition.DiskPath) : 0
            };

            try
            {
                string result = await InjectWindowsDriversAsync(vmId, diskTarget, selectedPartition, gpus, progressCallback);
                return result == "OK" ? ApiResponse.Ok() : ApiResponse.Fail(result);
            }
            catch (OperationOutcomeUnknownException ex) { return ex.Result; }
            catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.CleanupRequired); }
        }

        private async Task<string> InjectWindowsDriversAsync(
            Guid vmId, VmDiskTarget diskTarget, DiskPartitionInfo partition, IReadOnlyList<HostGpuInfo> gpus, Action<string>? progressCallback = null)
        {
            if (diskTarget.IsPhysical)
                return await HostDiskService.WithDiskLockAsync(diskTarget.PhysicalDiskNumber, async () =>
                {
                    await VerifyDiskTargetAsync(vmId, diskTarget);
                    return await InjectWindowsDriversCoreAsync(vmId, diskTarget, partition, gpus, -1, progressCallback);
                });
            var cleanup = new List<ApiResponse>();
            string result;
            try
            {
                result = await WithTemporaryImageAsync(vmId, diskTarget, false,
                    number => InjectWindowsDriversCoreAsync(vmId, diskTarget, partition, gpus, number, progressCallback),
                    cleanup.Add);
            }
            catch (Exception error)
            {
                if (error is OperationOutcomeUnknownException || cleanup.Any(item => item.ErrorKind == ApiErrorKind.OutcomeUnknown))
                    throw new OperationOutcomeUnknownException(ApiResponse.Fail(string.Join(Environment.NewLine,
                        new[] { error.Message }.Concat(cleanup.Select(item => item.Error))), exception: error, kind: ApiErrorKind.OutcomeUnknown));
                if (cleanup.Count > 0) throw new AggregateException(error, new IOException(string.Join(Environment.NewLine, cleanup.Select(item => item.Error))));
                throw;
            }
            if (cleanup.Any(item => item.ErrorKind == ApiErrorKind.OutcomeUnknown))
                throw new OperationOutcomeUnknownException(ApiResponse.Fail(string.Join(Environment.NewLine,
                    (result == "OK" ? Enumerable.Empty<string>() : new[] { result }).Concat(cleanup.Select(item => item.Error))), kind: ApiErrorKind.OutcomeUnknown));
            if (cleanup.Count > 0)
                throw new IOException(string.Join(Environment.NewLine,
                    (result == "OK" ? Enumerable.Empty<string>() : new[] { result }).Concat(cleanup.Select(item => item.Error))));
            return result;
        }

        private async Task<string> InjectWindowsDriversCoreAsync(
            Guid vmId, VmDiskTarget diskTarget, DiskPartitionInfo partition, IReadOnlyList<HostGpuInfo> gpus,
            int mountedDiskNumber, Action<string>? progressCallback)
        {
            string assignedDriveLetter = null;
            GpuRecoveryWork? letterRecovery = null;
            int hostDiskNumber = -1;
            string savedCtrlType = "SCSI";
            int savedCtrlNum = 0;
            int savedCtrlLoc = 0;
            string? savedControllerId = null;
            bool isPhysical = diskTarget.IsPhysical;
            bool detachSuccess = false;
            bool mutationUnknown = false;
            HostDiskSnapshot? originalPhysicalDisk = null;
            string? operationFailure = null;
            Exception? operationException = null;
            var linkWarnings = new ConcurrentQueue<string>();
            _linkWarnings.Value = linkWarnings;



            void Log(string msg) => progressCallback?.Invoke(msg);
            string Failure(string message) { operationFailure = message; return message; }

            try
            {
                if (isPhysical)
                {
                    Log(string.Format(Properties.Resources.Msg_Gpu_DismountingDisk, diskTarget.PhysicalDiskNumber));
                    hostDiskNumber = diskTarget.PhysicalDiskNumber;
                    var before = await HostDiskService.ReadDiskSnapshotAsync(hostDiskNumber);
                    if (!before.HasData || !before.Data!.SameDevice(before.Data))
                        return Failure("无法确认物理磁盘身份，未执行驱动同步。");
                    originalPhysicalDisk = before.Data;
                    if (partition.ScannedDiskIdentity is not { } expected || !expected.SameDevice(originalPhysicalDisk))
                        return Failure("物理磁盘身份已改变，请重新扫描。");
                    GpuDiskRecovery.DiskState(vmId, originalPhysicalDisk);
                    var detachResult = await VmStorageService.DetachPhysicalDiskUnderLockAsync(vmId, ToDiskInfo(originalPhysicalDisk),
                        (slot, controllerId) =>
                        {
                            savedControllerId = controllerId;
                            GpuDiskRecovery.Attachment(vmId, originalPhysicalDisk, slot, controllerId);
                        });
                    OperationOutcomeUnknownException.ThrowIfUnknown(detachResult);
                    if (!detachResult.Success) return Failure(Properties.Resources.Error_Gpu_DiskNotFound);
                    savedCtrlType = detachResult.Data!.ControllerType;
                    savedCtrlNum = detachResult.Data!.ControllerNumber;
                    savedCtrlLoc = detachResult.Data!.Location;
                    detachSuccess = true;



                    var online = await HostDiskService.SetDiskOfflineStatusCoreAsync(hostDiskNumber, false);
                    OperationOutcomeUnknownException.ThrowIfUnknown(online);
                    if (!online.Success) return Failure(online.Error);
                    var writable = await HostDiskService.SetDiskReadOnlyCoreAsync(hostDiskNumber, false);
                    OperationOutcomeUnknownException.ThrowIfUnknown(writable);
                    if (!writable.Success) return Failure(writable.Error);

                }
                else
                {
                    hostDiskNumber = mountedDiskNumber;
                }

                Log(string.Format(Properties.Resources.Msg_Gpu_AssignTempDrive, hostDiskNumber, partition.PartitionNumber));

                char suggestedLetter = GetFreeDriveLetter();
                letterRecovery = await GpuDiskRecovery.DriveLetterAsync(vmId, hostDiskNumber, partition.PartitionNumber, suggestedLetter,
                    isPhysical ? null : diskTarget.Path);
                var assignResult = await HostDiskService.AssignPartitionDriveLetterCoreAsync(hostDiskNumber, partition.PartitionNumber, suggestedLetter);
                OperationOutcomeUnknownException.ThrowIfUnknown(assignResult);
                if (!assignResult.Success)
                    return Failure(string.Format(
                        Properties.Resources.Error_Gpu_InjectFailed,
                        Properties.Resources.Error_Gpu_AssignDriveLetterFailed));
                assignedDriveLetter = $"{suggestedLetter}:\\";

                var volResp = await WmiApi.QueryFirstCimAsync(
                    $"SELECT * FROM MSFT_Volume WHERE DriveLetter = '{assignedDriveLetter[0]}'",
                    obj => obj["FileSystem"]?.ToString() ?? "",
                    WmiScope.Storage);

                if (volResp.HasData && string.IsNullOrWhiteSpace(volResp.Data))
                    return Failure(Properties.Resources.Error_Gpu_BitLocker);


                var partitionPaths = await WmiApi.QueryCimAsync(
                    $"SELECT AccessPaths FROM MSFT_Partition WHERE DiskNumber={hostDiskNumber} AND PartitionNumber={partition.PartitionNumber}",
                    row => row["AccessPaths"] as string[] ?? [], WmiScope.Storage);
                if (!partitionPaths.HasData || partitionPaths.Data.Count != 1)
                    throw new IOException("无法核实目标分区的卷路径。");
                string guestRoot = GuestVolumeFiles.PartitionVolume(partitionPaths.Data[0], suggestedLetter);
                using var volume = new GuestVolumeFiles(guestRoot);
                _guestVolume.Value = volume;
                _deploymentFiles.Value = new GpuDeploymentFileOperations(volume, linkWarnings.Enqueue);
                if (!Directory.Exists(Path.Combine(guestRoot, "Windows", "System32")))
                {
                    return Failure(string.Format(Properties.Resources.Error_Gpu_InvalidSystemPart, guestRoot));
                }

                List<GpuDriverPackagePlan> packagePlans = await Task.Run(() =>
                    gpus.Select(gpu =>
                    {
                        string gpuPath = string.IsNullOrEmpty(gpu.PartitionableGpuPath) ? gpu.InstanceId : gpu.PartitionableGpuPath;
                        return GpuDriverPackageResolver.Resolve(gpuPath, gpu.Manu);
                    }).ToList());

                foreach (GpuDriverPackagePlan packagePlan in packagePlans)
                {
                    foreach (string warning in packagePlan.Warnings) Log(warning);
                }

                string hostFileRepository = packagePlans[0].HostFileRepository;
                string destFolder = Path.Combine(guestRoot, "Windows", "System32", "HostDriverStore", "FileRepository");


                Log(Properties.Resources.Msg_Gpu_SyncingFiles);
                await CopyDriverRepositoryAsync(hostFileRepository, destFolder, Log);

                bool includeWow64 = Directory.Exists(Path.Combine(guestRoot, "Windows", "SysWOW64"));
                foreach (GpuDriverPackagePlan packagePlan in packagePlans)
                {
                    _activeDriverPackagePlan.Value = packagePlan;

                    // 注册表提取、文件链接及目录权限准备仍含同步 I/O，放到后台执行。
                    // 回调由调用方切回自己的展示线程；服务本身不依赖 UI 同步上下文。
                    await Task.Run(() => ApplyRegistryPromotions(guestRoot, packagePlan, includeWow64));

                    try
                    {
                        if (packagePlan.Vendor == GpuDriverVendor.Nvidia)
                        {
                            Log(Properties.Resources.Msg_Gpu_InjectingReg);
                            string nvidiaRegResult = await Task.Run(() =>
                            {
                                string result = PatchNvidiaServiceRegistry(guestRoot, packagePlan);
                                ApplyPromotionRules(guestRoot, GpuDriverPromotionBuilder.GetVendorPromotions(packagePlan));
                                return result;
                            });
                            if (!string.Equals(nvidiaRegResult, "OK", StringComparison.Ordinal))
                                Log(nvidiaRegResult);
                        }
                        else if (packagePlan.Vendor == GpuDriverVendor.Intel)
                        {
                            await Task.Run(() =>
                            {
                                ApplyPromotionRules(guestRoot, GpuDriverPromotionBuilder.GetVendorPromotions(packagePlan));
                                PromoteIntelGpuRuntimeRegistry(guestRoot, packagePlan);
                            });
                        }
                        else if (packagePlan.Vendor == GpuDriverVendor.Amd || packagePlan.Vendor == GpuDriverVendor.Qualcomm)
                        {
                            await Task.Run(() =>
                                ApplyPromotionRules(guestRoot, GpuDriverPromotionBuilder.GetVendorPromotions(packagePlan)));
                        }
                    }
                    catch (Exception ex) when (!GuestVolumeBoundaryException.IsBoundaryFailure(ex))
                    {
                        Log($"[GPU Optional] Vendor-specific promotion was incomplete: {ex.Message}");
                    }
                }

                try
                {
                    var vendors = packagePlans
                        .Select(plan => plan.Vendor)
                        .Where(vendor => vendor != GpuDriverVendor.Unknown)
                        .Distinct();
                    await CopyVendorProgramFoldersAsync(vendors, guestRoot, Log);
                }
                catch (Exception ex) when (!GuestVolumeBoundaryException.IsBoundaryFailure(ex))
                {
                    Log($"[GPU Optional] Vendor software directories were not copied completely: {ex.Message}");
                }


                return "OK";
            }
            catch (OperationOutcomeUnknownException)
            {
                mutationUnknown = true;
                throw;
            }
            catch (Exception ex)
            {
                operationException = ex;
                return Failure(string.Format(Properties.Resources.Error_Gpu_InjectFailed, ex.Message));
            }
            finally
            {
                while (linkWarnings.TryDequeue(out string warning))
                {
                    try { Log(warning); }
                    catch (Exception diagnosticError) { CoreDiagnostics.Report("WarningObserverFailed", error: diagnosticError); }
                }
                _linkWarnings.Value = null;
                _activeDriverPackagePlan.Value = null;
                _deploymentFiles.Value = null;
                _guestVolume.Value = null;

                var cleanupErrors = new List<string>();
                if (!mutationUnknown && !string.IsNullOrEmpty(assignedDriveLetter))
                {
                    try
                    {
                        bool sameDisk;
                        if (isPhysical)
                        {
                            var current = await HostDiskService.ReadDiskSnapshotAsync(hostDiskNumber);
                            sameDisk = current.HasData && originalPhysicalDisk?.SameDevice(current.Data!) == true;
                        }
                        else
                        {
                            var image = await HostDiskService.ReadImageAsync(diskTarget.Path);
                            sameDisk = image.HasData && image.Data!.Attached && image.Data.Number == hostDiskNumber;
                        }
                        if (!sameDisk) cleanupErrors.Add("临时盘符所属磁盘无法确认，未修改替代设备。");
                        else
                        {
                            var removed = await HostDiskService.RemovePartitionAccessPathCoreAsync(hostDiskNumber,
                                partition.PartitionNumber, assignedDriveLetter[0]);
                            mutationUnknown |= removed.ErrorKind == ApiErrorKind.OutcomeUnknown;
                            if (!removed.Success) cleanupErrors.Add("临时盘符移除失败：" + removed.Error);
                            else letterRecovery?.Finish();
                        }
                    }
                    catch (Exception ex) { cleanupErrors.Add("临时盘符移除失败：" + ex.Message); }
                }

                if (!mutationUnknown && isPhysical && hostDiskNumber != -1 && detachSuccess)
                {
                    try
                    {
                        // AddDrive 再次核对身份并自行处理直通所需的脱机状态。
                        var remountResult = await VmStorageService.AddDriveUnderLockAsync(vmId, new VmStorageAddRequest
                        {
                            ControllerType = savedCtrlType, ControllerNumber = savedCtrlNum, Location = savedCtrlLoc,
                            ExpectedControllerId = savedControllerId,
                            DriveType = "HardDisk", PathOrNumber = hostDiskNumber.ToString(), IsPhysical = true,
                            PhysicalDisk = ToDiskInfo(originalPhysicalDisk!)
                        });
                        mutationUnknown |= remountResult.ErrorKind == ApiErrorKind.OutcomeUnknown;
                        if (!remountResult.Success) cleanupErrors.Add("物理磁盘重新挂接失败：" + remountResult.Error);
                    }
                    catch (Exception ex) { cleanupErrors.Add("物理磁盘重新挂接失败：" + ex.Message); }
                    // 重新挂接也可能改变宿主属性，最终恢复必须位于它之后。
                    try
                    {
                        if (!mutationUnknown)
                        {
                            var restored = await HostDiskService.RestoreDiskCoreAsync(originalPhysicalDisk!);
                            mutationUnknown |= restored.ErrorKind == ApiErrorKind.OutcomeUnknown;
                            if (!restored.Success) cleanupErrors.Add(restored.Error);
                        }
                    }
                    catch (Exception ex) { cleanupErrors.Add("物理磁盘恢复失败：" + ex.Message); }
                }
                if (cleanupErrors.Count > 0)
                {
                    if (mutationUnknown)
                        throw new OperationOutcomeUnknownException(ApiResponse.Fail(string.Join(Environment.NewLine,
                            (operationFailure == null ? Enumerable.Empty<string>() : new[] { operationFailure }).Concat(cleanupErrors)),
                            exception: operationException, kind: ApiErrorKind.OutcomeUnknown));
                    var cleanupError = new IOException(string.Join(Environment.NewLine, cleanupErrors));
                    if (operationException != null) throw new AggregateException(operationException, cleanupError);
                    if (operationFailure != null) cleanupErrors.Insert(0, operationFailure);
                    throw new IOException(string.Join(Environment.NewLine, cleanupErrors));
                }
            }
        }

        private static HostDiskInfo ToDiskInfo(HostDiskSnapshot snapshot) => new()
        {
            Number = snapshot.Number, UniqueId = snapshot.UniqueId, SerialNumber = snapshot.SerialNumber,
            SizeBytes = snapshot.SizeBytes
        };

        private async Task CopyDriverRepositoryAsync(
            string hostFileRepository,
            string guestFileRepository,
            Action<string> log)
        {
            if (!Directory.Exists(hostFileRepository))
                throw new DirectoryNotFoundException($"Host DriverStore FileRepository was not found: {hostFileRepository}");


            log($"[GPU DriverStore] {hostFileRepository}");
            await CopyDirectoryTreeAsync(hostFileRepository, guestFileRepository, readOnly: true);
        }

        private Task CopyDirectoryTreeAsync(string sourceDirectory, string destinationDirectory, bool readOnly = false) =>
            Task.Run(() =>
            {
                string sourceRoot = Path.GetFullPath(sourceDirectory);
                string destinationRoot = Path.GetFullPath(destinationDirectory);
                var volume = _guestVolume.Value ?? throw new InvalidOperationException("Missing guest volume.");

                var pendingDirs = new Stack<(string Source, string Destination)>();
                pendingDirs.Push((sourceRoot, destinationRoot));
                var filesToCopy = new List<(string Source, string Destination)>();

                while (pendingDirs.Count > 0)
                {
                    var (currentSource, currentDestination) = pendingDirs.Pop();
                    using var directory = volume.PinDirectory(currentDestination, create: true);

                    foreach (string file in Directory.EnumerateFiles(currentSource))
                    {
                        string destFile = Path.Combine(currentDestination, Path.GetFileName(file));
                        filesToCopy.Add((file, destFile));
                    }

                    foreach (string subDir in Directory.EnumerateDirectories(currentSource))
                    {
                        var info = new DirectoryInfo(subDir);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        pendingDirs.Push((subDir, Path.Combine(currentDestination, info.Name)));
                    }
                }

                Parallel.ForEach(
                    filesToCopy,
                    new ParallelOptions { MaxDegreeOfParallelism = 32 },
                    pair => volume.CopyFile(pair.Source, pair.Destination, readOnly));
            });

        private void ApplyRegistryPromotions(
            string assignedDriveLetter,
            GpuDriverPackagePlan packagePlan,
            bool includeWow64)
        {
            try
            {
                ApplyPromotionRules(
                    assignedDriveLetter,
                    GpuDriverPromotionBuilder.CollectRegistryPromotions(packagePlan, includeWow64));
            }
            catch (Exception ex) when (!GuestVolumeBoundaryException.IsBoundaryFailure(ex))
            {
                CoreDiagnostics.Report("RegistryPromotionFailed", error: ex);
            }
        }

        private void ApplyPromotionRules(
            string assignedDriveLetter,
            IEnumerable<GpuFilePromotionRule> rules)
        {
            foreach (GpuFilePromotionRule rule in rules)
            {
                LinkSingleFile(
                    assignedDriveLetter,
                    rule.SourceName,
                    rule.TargetName,
                    rule.TargetSubDir,
                    rule.Mode,
                    rule.PreferredPackageNames,
                    rule.RequirePreferredPackage);
            }
        }

        private void PromoteIntelGpuRuntimeRegistry(
            string assignedDriveLetter,
            GpuDriverPackagePlan packagePlan)
        {
            string systemHiveFile = Path.Combine(
                assignedDriveLetter,
                "Windows",
                "System32",
                "Config",
                "SYSTEM");

            if (!File.Exists(systemHiveFile))
                throw new FileNotFoundException("The offline SYSTEM hive was not found.", systemHiveFile);

            GpuIntelRuntimeSpec? spec = GpuDriverPromotionBuilder.ReadIntelRuntimeSpec(packagePlan);
            if (spec == null) return;

            string displayClassGuid = GpuDriverPromotionBuilder.DisplayClassGuid;
            string offlineHiveName = $"ExHyperVIntelOfflineSystem_{Guid.NewGuid():N}";
            ApplyOptionalRegistryEnhancement(systemHiveFile, offlineHiveName, offlineSystem =>
            {
                IReadOnlyCollection<string> controlSetNames = GetOfflineControlSetNames(offlineSystem);
                string currentControlSetName = GetOfflineCurrentControlSetName(offlineSystem);
                var levelZeroAnchorSubKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // Validate every destination before writing anything. An absent Level Zero anchor is
                // fatal only for the active control set; stale backup control sets are synchronized
                // when possible but must not make an otherwise bootable deployment fail.
                foreach (string controlSetName in controlSetNames)
                {
                    string classRoot = $@"{controlSetName}\Control\Class\{displayClassGuid}";
                    using var existingRuntimeKey = offlineSystem.OpenSubKey(
                        $@"{classRoot}\{spec.DisplayClassSubKeyName}");
                    if (existingRuntimeKey != null && !IsCompatibleIntelRuntimeTarget(existingRuntimeKey))
                    {
                        throw new InvalidOperationException(
                            $"The offline display-class key {spec.DisplayClassSubKeyName} belongs to another display driver. Intel runtime values were not written.");
                    }

                    if (string.IsNullOrWhiteSpace(spec.LevelZeroDriverPath)) continue;

                    string? anchorSubKeyName = FindOfflineHyperVVideoClassSubKeyName(offlineSystem, classRoot);
                    if (!string.IsNullOrWhiteSpace(anchorSubKeyName))
                    {
                        levelZeroAnchorSubKeys[controlSetName] = anchorSubKeyName;
                    }
                    else if (controlSetName.Equals(currentControlSetName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("The offline Microsoft Hyper-V Video display-class key was not found in the active control set.");
                    }
                }

                foreach (string controlSetName in controlSetNames)
                {
                    string classRoot = $@"{controlSetName}\Control\Class\{displayClassGuid}";

                    // Intel NEO gets DeviceRegistryPath from the selected host adapter through
                    // KMTQAITYPE_UMDRIVERPRIVATE. GPU-PV preserves that numeric class-key name,
                    // so populate it before the virtual render device starts for the first time.
                    using (var runtimeKey = offlineSystem.CreateSubKey(
                        $@"{classRoot}\{spec.DisplayClassSubKeyName}",
                        writable: true))
                    {
                        if (runtimeKey == null)
                            throw new InvalidOperationException("Unable to create the offline Intel runtime registry key.");
                        if (!IsCompatibleIntelRuntimeTarget(runtimeKey))
                            throw new InvalidOperationException(
                                $"The offline display-class key {spec.DisplayClassSubKeyName} belongs to another display driver. Intel runtime values were not written.");

                        foreach (GpuRegistryValueSpec runtimeValue in spec.RuntimeValues)
                            runtimeKey.SetValue(runtimeValue.Name, runtimeValue.ToRegistryObject(), runtimeValue.ToRegistryValueKind());
                    }

                    if (!string.IsNullOrWhiteSpace(spec.LevelZeroDriverPath) &&
                        levelZeroAnchorSubKeys.TryGetValue(controlSetName, out string? anchorSubKeyName))
                    {
                        // The Windows Level Zero loader enumerates present guest display DEVNODEs and
                        // reads LevelZeroDriverPath from their software keys. The VRD software key does
                        // not exist until first boot, but Hyper-V Video does, so use that stable display
                        // key as a discovery anchor without replacing any other driver's registration.
                        using var levelZeroAnchorKey = offlineSystem.OpenSubKey(
                            $@"{classRoot}\{anchorSubKeyName}",
                            writable: true);
                        if (levelZeroAnchorKey == null)
                            throw new InvalidOperationException("The offline Microsoft Hyper-V Video registry key could not be opened.");

                        levelZeroAnchorKey.SetValue(
                            "LevelZeroDriverPath",
                            spec.LevelZeroDriverPath,
                            Microsoft.Win32.RegistryValueKind.String);
                    }
                }
            });
        }

        private static IReadOnlyCollection<string> GetOfflineControlSetNames(Microsoft.Win32.RegistryKey offlineSystem)
        {
            var controlSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            controlSets.Add(GetOfflineCurrentControlSetName(offlineSystem));
            using var selectKey = offlineSystem.OpenSubKey("Select");
            foreach (string valueName in new[] { "Default", "LastKnownGood" })
            {
                if (selectKey?.GetValue(valueName) is int controlSetNumber && controlSetNumber > 0)
                    controlSets.Add($"ControlSet{controlSetNumber:D3}");
            }

            return controlSets.ToArray();
        }

        private static string GetOfflineCurrentControlSetName(Microsoft.Win32.RegistryKey offlineSystem)
        {
            using var selectKey = offlineSystem.OpenSubKey("Select");
            return selectKey?.GetValue("Current") is int controlSetNumber && controlSetNumber > 0
                ? $"ControlSet{controlSetNumber:D3}"
                : "ControlSet001";
        }

        private static bool IsCompatibleIntelRuntimeTarget(Microsoft.Win32.RegistryKey runtimeKey)
        {
            string driverDesc = runtimeKey.GetValue("DriverDesc")?.ToString() ?? string.Empty;
            string providerName = runtimeKey.GetValue("ProviderName")?.ToString() ?? string.Empty;
            string infPath = runtimeKey.GetValue("InfPath")?.ToString() ?? string.Empty;
            string matchingDeviceId = runtimeKey.GetValue("MatchingDeviceId")?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(driverDesc) &&
                string.IsNullOrWhiteSpace(providerName) &&
                string.IsNullOrWhiteSpace(infPath) &&
                string.IsNullOrWhiteSpace(matchingDeviceId))
            {
                return true;
            }

            string identity = $"{driverDesc}|{providerName}|{infPath}|{matchingDeviceId}";
            if (identity.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
                matchingDeviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return infPath.Equals("vrd.inf", StringComparison.OrdinalIgnoreCase) ||
                   (providerName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                    driverDesc.Contains("Virtual Render", StringComparison.OrdinalIgnoreCase));
        }

        private static string? FindOfflineHyperVVideoClassSubKeyName(
            Microsoft.Win32.RegistryKey offlineSystem,
            string classRoot)
        {
            using var classKey = offlineSystem.OpenSubKey(classRoot);
            if (classKey == null) return null;

            foreach (string subKeyName in classKey.GetSubKeyNames()
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                Microsoft.Win32.RegistryKey? candidate = null;
                try { candidate = classKey.OpenSubKey(subKeyName); }
                catch (UnauthorizedAccessException) { continue; }
                catch (System.Security.SecurityException) { continue; }
                using (candidate)
                {
                    if (candidate == null) continue;

                    string driverDesc;
                    string providerName;
                    string infPath;
                    try
                    {
                        driverDesc = candidate.GetValue("DriverDesc")?.ToString() ?? string.Empty;
                        providerName = candidate.GetValue("ProviderName")?.ToString() ?? string.Empty;
                        infPath = candidate.GetValue("InfPath")?.ToString() ?? string.Empty;
                    }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (System.Security.SecurityException) { continue; }
                    if ((driverDesc.Contains("Hyper-V Video", StringComparison.OrdinalIgnoreCase) ||
                         infPath.Equals("wvmbusvideo.inf", StringComparison.OrdinalIgnoreCase)) &&
                        providerName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                    {
                        return subKeyName;
                    }
                }
            }

            return null;
        }

        private void LinkSingleFile(
            string assignedDriveLetter,
            string sourceName,
            string targetName,
            string targetSubDir,
            GpuFilePromotionMode promotionMode = GpuFilePromotionMode.PreserveExisting,
            IReadOnlyCollection<string>? preferredPackageNames = null,
            bool requirePreferredPackage = false)
        {
            try
            {
                string guestRepo = Path.Combine(assignedDriveLetter, "Windows", "System32", "HostDriverStore", "FileRepository");
                string hostDestDir = Path.Combine(assignedDriveLetter, "Windows", targetSubDir);
                string hostLinkPath = ResolveGuestPromotionTarget(hostDestDir, targetName);

                // 完整 FileRepository 虽然已复制，但厂商固定映射只能在当前显卡的精确包集合中查找；
                // CopyToVm 调用会显式传入主包，AMD 的特定组件也会显式传入对应伴随包。
                // 这同时阻止旧版或其他显卡遗留在来宾 DriverStore 中的同名文件被误用，
                // Qualcomm 的 ARM64/x86/CHPE 子目录则随主包完整保留。
                GpuDriverPackagePlan? activePlan = _activeDriverPackagePlan.Value;
                IReadOnlyCollection<string>? effectivePackageNames =
                    preferredPackageNames ?? activePlan?.PackageNames;
                bool allowRepositoryFallback = activePlan == null && !requirePreferredPackage;

                var foundFiles = FindDriverSourceFiles(
                    guestRepo,
                    sourceName,
                    effectivePackageNames,
                    allowRepositoryFallback);
                if (foundFiles.Count == 0)
                {
                    if (promotionMode != GpuFilePromotionMode.PreserveExisting || requirePreferredPackage)
                    {
                        string warning = $"[GPU Link] Required promotion source not found: {sourceName}.";
                        CoreDiagnostics.Report("PromotionSourceMissing");
                        _linkWarnings.Value?.Enqueue(warning);
                    }
                    return;
                }
                if (foundFiles.Count > 1)
                {
                    string warning = $"[GPU Link] Promotion source is ambiguous: {sourceName}.";
                    CoreDiagnostics.Report("PromotionSourceAmbiguous");
                    _linkWarnings.Value?.Enqueue(warning);
                    return;
                }

                FileInfo sourceFile = foundFiles[0];
                string hostSourceFile = sourceFile.FullName;
                string relativeSourcePath = Path.GetRelativePath(
                    Path.GetFullPath(assignedDriveLetter),
                    Path.GetFullPath(hostSourceFile));

                if (Path.IsPathRooted(relativeSourcePath) ||
                    relativeSourcePath.Equals("..", StringComparison.Ordinal) ||
                    relativeSourcePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    throw new IOException($"Driver source is outside the mounted guest volume: {hostSourceFile}");
                }

                string guestInternalTarget = Path.Combine(@"C:\", relativeSourcePath);

                string hostLinkDirectory = Path.GetDirectoryName(hostLinkPath)
                    ?? throw new IOException($"Invalid driver promotion target: {targetName}");
                var volume = _guestVolume.Value ?? throw new InvalidOperationException("Missing guest volume.");
                using var parents = volume.PinDirectory(hostLinkDirectory, create: true);
                using var sourceParents = volume.PinDirectory(sourceFile.DirectoryName!);
                using var sourcePin = volume.PinFile(hostSourceFile);
                var files = _deploymentFiles.Value ?? throw new InvalidOperationException("Missing deployment context.");
                if (targetSubDir.Equals("System32", StringComparison.OrdinalIgnoreCase) ||
                    targetSubDir.Contains("SyChpe32", StringComparison.OrdinalIgnoreCase) ||
                    targetSubDir.Contains("SysWOW64", StringComparison.OrdinalIgnoreCase))
                {
                    files.PrepareDirectory(hostDestDir);
                }

                bool isReparsePoint = false;
                bool targetPathExists = File.Exists(hostLinkPath) || Directory.Exists(hostLinkPath);
                bool targetIsDirectory = Directory.Exists(hostLinkPath);
                try
                {
                    isReparsePoint = File.GetAttributes(hostLinkPath).HasFlag(FileAttributes.ReparsePoint);
                    targetPathExists = true;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception diagnosticError) { CoreDiagnostics.Report("LinkAttributesReadFailed", error: diagnosticError); }

                if (isReparsePoint)
                {
                    if (string.Equals(new FileInfo(hostLinkPath).LinkTarget, guestInternalTarget, StringComparison.OrdinalIgnoreCase)) return;
                    throw new GuestVolumeBoundaryException("目标是未确认的重解析点：" + hostLinkPath);
                }

                if (targetPathExists)
                {
                    if (targetIsDirectory)
                    {
                        string warning = $"[GPU Link] Refusing to replace directory {hostLinkPath}.";
                        CoreDiagnostics.Report("PromotionTargetIsDirectory");
                        _linkWarnings.Value?.Enqueue(warning);
                        return;
                    }

                    bool shouldReplace = promotionMode switch
                    {
                        GpuFilePromotionMode.Overwrite => true,
                        GpuFilePromotionMode.WhenNewer => IsSourceNewerThanDestination(
                            sourceFile,
                            hostLinkPath,
                            isReparsePoint,
                            assignedDriveLetter),
                        // 保留原逻辑：普通来宾文件不动，但刷新旧版创建的错误链接。
                        _ => isReparsePoint
                    };

                    if (!shouldReplace) return;
                }
                files.ReplaceWithLink(hostLinkPath, guestInternalTarget, targetPathExists);
            }
            catch (Exception ex) when (!GuestVolumeBoundaryException.IsBoundaryFailure(ex))
            {
                string warning = $"[GPU Link] {sourceName}: {ex.Message}";
                CoreDiagnostics.Report("LinkPromotionFailed", error: ex);
                _linkWarnings.Value?.Enqueue(warning);
            }
        }

        private static string ResolveGuestPromotionTarget(string destinationRoot, string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                throw new IOException("Driver promotion target is empty.");

            string normalizedTarget = targetName.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedTarget))
                throw new IOException($"Driver promotion target must be relative: {targetName}");

            string[] targetParts = normalizedTarget.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            if (targetParts.Length == 0 ||
                targetParts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new IOException($"Invalid driver promotion target: {targetName}");
            }

            string fullRoot = Path.GetFullPath(destinationRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullTarget = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(targetParts)));
            if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Driver promotion target escapes the guest directory: {targetName}");

            return fullTarget;
        }

        private List<FileInfo> FindDriverSourceFiles(
            string guestRepo,
            string sourceName,
            IReadOnlyCollection<string>? preferredPackageNames,
            bool allowRepositoryFallback = true)
        {
            if (!Directory.Exists(guestRepo) || string.IsNullOrWhiteSpace(sourceName)) return new List<FileInfo>();

            string normalizedSource = sourceName.Trim()
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedSource))
                throw new IOException($"Driver source must be relative to FileRepository: {sourceName}");

            string[] sourceParts = normalizedSource.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            if (sourceParts.Length == 0 || sourceParts.Any(part => part == "." || part == ".."))
                throw new IOException($"Invalid DriverStore source path: {sourceName}");

            var volume = _guestVolume.Value ?? throw new InvalidOperationException("Missing guest volume.");
            using var repository = volume.PinDirectory(guestRepo);
            var options = new System.IO.EnumerationOptions { RecurseSubdirectories = sourceParts.Length == 1,
                AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            var matches = new List<FileInfo>();

            void SearchPackageDirectories(IEnumerable<string> packageDirectories)
            {
                string relativeDirectory = Path.GetDirectoryName(normalizedSource) ?? string.Empty;
                string filePattern = Path.GetFileName(normalizedSource);

                foreach (string packageDirectory in packageDirectories)
                {
                    try
                    {
                        string searchRoot = Path.Combine(packageDirectory, relativeDirectory);
                        if (!Directory.Exists(searchRoot)) continue;
                        using var directory = volume.PinDirectory(searchRoot);
                        matches.AddRange(new DirectoryInfo(searchRoot).GetFiles(filePattern, options));
                    }
                    catch (UnauthorizedAccessException ex) { CoreDiagnostics.Report("PromotionSourceAccessDenied", error: ex); }
                    catch (DirectoryNotFoundException) { }
                }
            }

            if (preferredPackageNames != null && preferredPackageNames.Count > 0)
            {
                var preferredDirectories = preferredPackageNames
                    .Where(name => !string.IsNullOrWhiteSpace(name) && Path.GetFileName(name) == name)
                    .Select(name => Path.Combine(guestRepo, name));
                SearchPackageDirectories(preferredDirectories);
            }

            if (matches.Count == 0 && allowRepositoryFallback)
            {
                if (sourceParts.Length > 1)
                {
                    SearchPackageDirectories(Directory.EnumerateDirectories(guestRepo, "*", SearchOption.TopDirectoryOnly));
                }
                else
                {
                    matches.AddRange(new DirectoryInfo(guestRepo).GetFiles(normalizedSource, options));
                }
            }

            return matches
                .OrderBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsSourceNewerThanDestination(
            FileInfo sourceFile,
            string destinationPath,
            bool destinationIsReparsePoint,
            string assignedDriveLetter)
        {
            try
            {
                string timestampPath = destinationPath;
                if (destinationIsReparsePoint)
                {
                    string? linkTarget = new FileInfo(destinationPath).LinkTarget;
                    if (string.IsNullOrWhiteSpace(linkTarget)) return true;

                    if (Path.IsPathFullyQualified(linkTarget))
                    {
                        string? targetRoot = Path.GetPathRoot(linkTarget);
                        if (string.IsNullOrEmpty(targetRoot)) return true;
                        string relativeTarget = Path.GetRelativePath(targetRoot, linkTarget);
                        timestampPath = Path.Combine(assignedDriveLetter, relativeTarget);
                    }
                    else
                    {
                        timestampPath = Path.GetFullPath(
                            Path.Combine(Path.GetDirectoryName(destinationPath) ?? assignedDriveLetter, linkTarget));
                    }

                    // 链接仍指向旧包或旧版错误源时必须刷新，不能只比较时间戳。
                    if (!Path.GetFullPath(timestampPath).Equals(
                            Path.GetFullPath(sourceFile.FullName),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (!File.Exists(timestampPath)) return true;

                string extension = Path.GetExtension(destinationPath);
                if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    FileVersionInfo sourceVersion = FileVersionInfo.GetVersionInfo(sourceFile.FullName);
                    FileVersionInfo destinationVersion = FileVersionInfo.GetVersionInfo(timestampPath);
                    int versionComparison = CompareFileVersions(sourceVersion, destinationVersion);
                    if (versionComparison != 0) return versionComparison > 0;
                }

                return sourceFile.LastWriteTimeUtc > File.GetLastWriteTimeUtc(timestampPath);
            }
            catch
            {
                // 无法确认旧目标版本时刷新链接，避免保留悬空或旧格式链接。
                return true;
            }
        }

        private static int CompareFileVersions(FileVersionInfo left, FileVersionInfo right)
        {
            int comparison = left.FileMajorPart.CompareTo(right.FileMajorPart);
            if (comparison != 0) return comparison;
            comparison = left.FileMinorPart.CompareTo(right.FileMinorPart);
            if (comparison != 0) return comparison;
            comparison = left.FileBuildPart.CompareTo(right.FileBuildPart);
            return comparison != 0
                ? comparison
                : left.FilePrivatePart.CompareTo(right.FilePrivatePart);
        }

        private string PatchNvidiaServiceRegistry(
            string letter,
            GpuDriverPackagePlan packagePlan)
        {
            string systemHiveFile = $@"{letter}\Windows\System32\Config\SYSTEM";
            string offlineHiveName = $"ExHyperVNvidiaOfflineSystem_{Guid.NewGuid():N}";

            try
            {
                if (!File.Exists(systemHiveFile))
                    return Properties.Resources.Error_OfflineLoadVmRegistryFailed;

                GpuNvidiaServiceSpec? spec = GpuDriverPromotionBuilder.ReadNvidiaServiceSpec(packagePlan);
                if (spec == null)
                    return Properties.Resources.Error_ExportLocalRegistryInfoFailed;

                ApplyOptionalRegistryEnhancement(systemHiveFile, offlineHiveName, offlineSystem =>
                {
                    foreach (string controlSetName in GetOfflineControlSetNames(offlineSystem))
                    {
                        using var destinationServiceKey = offlineSystem.CreateSubKey(
                            $@"{controlSetName}\Services\nvlddmkm",
                            writable: true);
                        if (destinationServiceKey == null)
                            throw new InvalidOperationException(
                                $"Unable to create {controlSetName}\\Services\\nvlddmkm in the offline guest.");

                        // Only the kernel-service bootstrap belongs to offline deployment. NVIDIA's
                        // service subkeys contain host PnP bindings, volatile state and installed
                        // feature state; the guest must generate or install those for itself.
                        foreach (GpuRegistryValueSpec serviceValue in spec.Values)
                        {
                            destinationServiceKey.SetValue(
                                serviceValue.Name,
                                serviceValue.ToRegistryObject(),
                                serviceValue.ToRegistryValueKind());
                        }
                    }
                });

                return "OK";
            }
            catch (Exception ex) when (!GuestVolumeBoundaryException.IsBoundaryFailure(ex))
            {
                return string.Format(Properties.Resources.Error_NvidiaRegistryProcessingException, ex.Message);
            }
        }

        private async Task CopyVendorProgramFoldersAsync(
            IEnumerable<GpuDriverVendor> vendors,
            string assignedDriveLetter,
            Action<string> log)
        {
            foreach (GpuProgramFolderSpec folder in GpuDriverPromotionBuilder.CollectVendorProgramFolders(vendors))
            {
                string targetPath = Path.Combine(assignedDriveLetter, folder.RelativePath);
                log?.Invoke(string.Format(Properties.Resources.Log_Gpu_Syncing, folder.SourcePath, targetPath));

                await CopyDirectoryTreeAsync(folder.SourcePath, targetPath);
            }
        }

        #endregion
    }
}
