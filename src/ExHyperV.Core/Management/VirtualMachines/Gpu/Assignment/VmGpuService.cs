using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;
using Renci.SshNet;

namespace ExHyperV.Services
{
    /// <summary>虚拟 GPU 分配及来宾驱动部署入口。单项方法提供底层能力；需要分步确认的完整流程使用 GpuDeploymentSession。</summary>
    public class VmGpuService
    {
        private readonly VmQueryService _queryService;
        private readonly AsyncLocal<ConcurrentQueue<string>?> _linkWarnings = new();
        private readonly AsyncLocal<GpuDriverPackagePlan?> _activeDriverPackagePlan = new();
        private readonly AsyncLocal<GpuDeploymentFileOperations?> _deploymentFiles = new();

        private static readonly string[] IntelGpuRuntimeRegistryValueNames =
        {
            "OpenCLDriverName",
            "OpenCLDriverNameWow",
            "LevelZeroDriverPath",
            "DriverStorePathForComputeRuntime",
            "DriverStorePathForMediaSDK",
            "DriverStorePathForVPL",
            "DriverStorePathForMDF",
            "VulkanDriverName",
            "VulkanDriverNameWow",
            "OpenGLDriverName",
            "OpenGLDriverNameWow",
            "OpenGLFlags",
            "OpenGLFlagsWow",
            "OpenGLInstalled",
            "OpenGLVersion",
            "OpenGLVersionWow",
            "ContentProtectionDriverName",
            "ControlApiPath",
            "ControlApiPath32"
        };

        private enum FilePromotionMode
        {
            PreserveExisting,
            WhenNewer,
            Overwrite
        }

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
        private const string ScriptBaseUrl = "https://raw.githubusercontent.com/Justsenger/ExHyperV/2.0.0/src/ExHyperV.Core/Linux/script/";


        private void ApplyOptionalRegistryEnhancement(string hivePath, string mountKey,
            Action<Microsoft.Win32.RegistryKey> edit)
        {
            // 临时特权线程不继承 AsyncLocal，先固定本次部署的警告接收者。
            var warnings = _linkWarnings.Value;
            GpuRegistryEnhancement.Apply(hivePath, mountKey, edit, warning =>
            {
                Debug.WriteLine(warning);
                warnings?.Enqueue(warning);
            });
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
                return ApiResponse<IReadOnlyList<VmGpuAssignment>>.Ok(BuildGpuAssignments(adapters.Adapters, await HostGpuService.ReadHostGpusAsync()).AsReadOnly());
            }
            catch (Exception ex) { return ApiResponse<IReadOnlyList<VmGpuAssignment>>.Fail(ex.Message, exception: ex); }
        }

        internal static List<VmGpuAssignment> BuildGpuAssignments(
            IReadOnlyList<(string Id, string InstancePath)> adapters, IReadOnlyList<HostGpuInfo> hostGpus)
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
        /// <returns>Success 表示查询成功；Adapters 为 Id 与 InstancePath 列表，失败时为空。</returns>
        /// <remarks>旧版宿主缺少适配器资源路径时，使用资源池首项作为候选；多 GPU 环境需进一步核对物理设备。</remarks>
        internal async Task<(bool Success, List<(string Id, string InstancePath)> Adapters)> TryGetVmGpuAdaptersAsync(Guid vmId)
        {
            var result = new List<(string Id, string InstancePath)>();
            string scopePath = @"\\.\root\virtualization\v2";
            try
            {
                // Win10 早期 Msvm_GpuPartitionSettingData.HostResource 读不回 → 无法按分区对应物理 GPU。
                // 取主机首张(通常也是唯一)可分区 GPU 的 Name 兜底显示参数：单卡准确；多卡是引擎启动时自选、
                // 本就无法精确对应的固有局限(禁用其他卡强选的老思路已不在)。Win11 走 HostResource 精确路径、到不了这兜底。
                var partGpuResp = await WmiApi.QueryAsync(
                    "SELECT Name FROM Msvm_PartitionableGpu",
                    obj => obj["Name"]?.ToString() ?? "");
                if (!partGpuResp.Success) return (false, result);
                string firstPartitionableGpu = partGpuResp.Data?.FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "";

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
                    string adapterId = gpuSetting["InstanceID"]?.ToString();
                    string instancePath = string.Empty;

                    string[] hostResources = (string[])gpuSetting["HostResource"];
                    if (hostResources != null && hostResources.Length > 0)
                    {
                        try
                        {
                            using var partitionableGpu = new ManagementObject(hostResources[0]);
                            partitionableGpu.Get();
                            instancePath = partitionableGpu["Name"]?.ToString();
                        }
                        catch { }
                    }

                    // HostResource 空/Unknown(Win10 早期) → 兜底用首张可分区 GPU 的 Name(单卡准确)
                    if (string.IsNullOrEmpty(instancePath) || instancePath.Contains("Unknown"))
                        instancePath = firstPartitionableGpu;

                    if (!string.IsNullOrEmpty(adapterId))
                        result.Add((adapterId, instancePath));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI Query Error: {ex.Message}");
                return (false, new List<(string Id, string InstancePath)>());
            }
            return (true, result);
        }
        #endregion

        #region 虚拟机状态与控制管理
        // SSH重新连接
        private async Task<bool> WaitForVmToBeResponsiveAsync(string host, int port, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromMinutes(1)) // 1分钟总超时
            {
                if (cancellationToken.IsCancellationRequested) return false;
                try
                {
                    using (var client = new TcpClient())
                    {
                        var connectTask = client.ConnectAsync(host, port);
                        if (await Task.WhenAny(connectTask, Task.Delay(2000, cancellationToken)) == connectTask)
                        {
                            await connectTask;
                            return true;
                        }
                    }
                }
                catch { }
                await Task.Delay(5000, cancellationToken);
            }
            return false; // 超时
        }

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

        internal async Task<ApiResponse> RemoveGpuPartitionAsync(Guid vmId, string adapterId)
        {
            if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var current = await TryGetVmGpuAdaptersAsync(vmId);
            if (!current.Success) return ApiResponse.Fail("无法读取目标虚拟机的 GPU 分配。");
            if (string.IsNullOrEmpty(adapterId) || !current.Adapters.Any(a => string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase)))
                return ApiResponse.Fail("GPU 分配已不存在或不属于目标虚拟机。", kind: ApiErrorKind.Conflict);
            var adapter = await WmiApi.QueryFirstAsync(
                $"SELECT * FROM Msvm_GpuPartitionSettingData WHERE InstanceID = '{WmiApi.Escape(adapterId)}'",
                obj => obj.Path.Path);
            if (!adapter.HasData) return ApiResponse.Fail(adapter.Success ? "GPU 分配已不存在。" : adapter.Error,
                adapter.Code, adapter.ErrorSource, adapter.Exception, adapter.Success ? ApiErrorKind.NotFound : adapter.ErrorKind);
            return await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualSystemManagementService", "RemoveResourceSettings",
                p => p["ResourceSettings"] = new[] { adapter.Data! });
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
        public async Task<ApiResponse> SyncWindowsDriversAsync(
            Guid vmId,
            string gpuInstancePath,
            string gpuManu,
            DiskPartitionInfo selectedPartition,
            Action<string> progressCallback = null)
        {
            if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
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
                string result = await InjectWindowsDriversAsync(vmId, diskTarget, selectedPartition, gpuManu, gpuInstancePath, progressCallback);
                return result == "OK" ? ApiResponse.Ok() : ApiResponse.Fail(result);
            }
            catch (OperationOutcomeUnknownException ex) { return ex.Result; }
            catch (Exception ex) { return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.CleanupRequired); }
        }

        private async Task<string> InjectWindowsDriversAsync(
            Guid vmId, VmDiskTarget diskTarget, DiskPartitionInfo partition, string gpuManu, string gpuInstancePath, Action<string> progressCallback = null)
        {
            if (diskTarget.IsPhysical)
                return await HostDiskService.WithDiskLockAsync(diskTarget.PhysicalDiskNumber, async () =>
                {
                    await VerifyDiskTargetAsync(vmId, diskTarget);
                    return await InjectWindowsDriversCoreAsync(vmId, diskTarget, partition, gpuManu, gpuInstancePath, -1, progressCallback);
                });
            var cleanup = new List<ApiResponse>();
            string result;
            try
            {
                result = await WithTemporaryImageAsync(vmId, diskTarget, false,
                    number => InjectWindowsDriversCoreAsync(vmId, diskTarget, partition, gpuManu, gpuInstancePath, number, progressCallback),
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
            Guid vmId, VmDiskTarget diskTarget, DiskPartitionInfo partition, string gpuManu, string gpuInstancePath,
            int mountedDiskNumber, Action<string> progressCallback)
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
            _deploymentFiles.Value = new GpuDeploymentFileOperations(linkWarnings.Enqueue);


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


                if (!Directory.Exists(Path.Combine(assignedDriveLetter, "Windows", "System32")))
                {
                    return Failure(string.Format(Properties.Resources.Error_Gpu_InvalidSystemPart, assignedDriveLetter));
                }

                GpuDriverPackagePlan packagePlan = await Task.Run(() =>
                    GpuDriverPackageResolver.Resolve(gpuInstancePath, gpuManu));
                _activeDriverPackagePlan.Value = packagePlan;
                foreach (string warning in packagePlan.Warnings) Log(warning);
                string destFolder = Path.Combine(assignedDriveLetter, "Windows", "System32", "HostDriverStore", "FileRepository");

                Directory.CreateDirectory(destFolder);

                Log(Properties.Resources.Msg_Gpu_SyncingFiles);
                await CopyDriverRepositoryAsync(packagePlan.HostFileRepository, destFolder, Log);

                // 注册表提取、文件链接及目录权限准备仍含同步 I/O，放到后台执行。
                // 回调由调用方切回自己的展示线程；服务本身不依赖 UI 同步上下文。
                await Task.Run(() => PromoteRegistryDefinedFiles(assignedDriveLetter, packagePlan)); // 微软注册表文件提取

                try
                {
                    if (packagePlan.Vendor == GpuDriverVendor.Nvidia)
                    {
                        Log(Properties.Resources.Msg_Gpu_InjectingReg);
                        string nvidiaRegResult = await Task.Run(() =>
                        {
                            string result = PatchNvidiaServiceRegistry(assignedDriveLetter, packagePlan);
                            PromoteNvidiaFiles(assignedDriveLetter);
                            return result;
                        });
                        if (!string.Equals(nvidiaRegResult, "OK", StringComparison.Ordinal))
                            Log(nvidiaRegResult);
                    }
                    else if (packagePlan.Vendor == GpuDriverVendor.Intel)
                    {
                        await Task.Run(() =>
                        {
                            PromoteIntelGpuFiles(assignedDriveLetter);
                            PromoteIntelGpuRuntimeRegistry(assignedDriveLetter, packagePlan.DisplayClassSubKeyName);
                        });
                    }
                    else if (packagePlan.Vendor == GpuDriverVendor.Amd)
                    {
                        await Task.Run(() => PromoteAmdGpuFiles(assignedDriveLetter, packagePlan));
                    }
                    else if (packagePlan.Vendor == GpuDriverVendor.Qualcomm)
                    {
                        await Task.Run(() => PromoteQualcommGpuFiles(assignedDriveLetter));
                    }
                }
                catch (Exception ex)
                {
                    Log($"[GPU Optional] Vendor-specific promotion was incomplete: {ex.Message}");
                }

                try
                {
                    await CopyVendorProgramFoldersAsync(packagePlan.Vendor, assignedDriveLetter, Log);
                }
                catch (Exception ex)
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
                    catch { }
                }
                _linkWarnings.Value = null;
                _activeDriverPackagePlan.Value = null;
                _deploymentFiles.Value = null;

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

        private static async Task CopyDriverRepositoryAsync(
            string hostFileRepository,
            string guestFileRepository,
            Action<string> log)
        {
            if (!Directory.Exists(hostFileRepository))
                throw new DirectoryNotFoundException($"Host DriverStore FileRepository was not found: {hostFileRepository}");

            if (Directory.Exists(guestFileRepository)) RemoveReadOnlyAttribute(guestFileRepository);
            else Directory.CreateDirectory(guestFileRepository);

            log($"[GPU DriverStore] {hostFileRepository}");
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "robocopy.exe",
                Arguments = $"\"{hostFileRepository}\" \"{guestFileRepository}\" /E /R:1 /W:1 /MT:32 /XJ /NDL /NJH /NJS /NC /NS",
                CreateNoWindow = true,
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Unable to start robocopy.exe.");
            await process.WaitForExitAsync();
            // Robocopy 0..7 are success states; 8 and above mean at least one copy failure.
            if (process.ExitCode >= 8)
                throw new IOException($"Copying DriverStore FileRepository failed with robocopy exit code {process.ExitCode}.");

            await Task.Run(() => SetFolderReadOnly(guestFileRepository));
        }

        private void PromoteRegistryDefinedFiles(
            string assignedDriveLetter,
            GpuDriverPackagePlan packagePlan)
        {
            string classGuidPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
                using var classKey = baseKey.OpenSubKey(classGuidPath);
                if (classKey == null) return;

                void ProcessAdapterKey(Microsoft.Win32.RegistryKey adapterKey)
                {
                    ProcessPromotionRegistryKey(
                        adapterKey,
                        "CopyToVmWhenNewer",
                        assignedDriveLetter,
                        "System32",
                        packagePlan.PrimaryPackageNames);
                    ProcessPromotionRegistryKey(
                        adapterKey,
                        "CopyToVmOverwrite",
                        assignedDriveLetter,
                        "System32",
                        packagePlan.PrimaryPackageNames);

                    if (Directory.Exists(Path.Combine(assignedDriveLetter, "Windows", "SysWOW64")))
                    {
                        ProcessPromotionRegistryKey(
                            adapterKey,
                            "CopyToVmWhenNewerWow64",
                            assignedDriveLetter,
                            "SysWOW64",
                            packagePlan.PrimaryPackageNames);
                        ProcessPromotionRegistryKey(
                            adapterKey,
                            "CopyToVmOverwriteWow64",
                            assignedDriveLetter,
                            "SysWOW64",
                            packagePlan.PrimaryPackageNames);
                    }
                }

                string selectedSubKeyName = packagePlan.DisplayClassSubKeyName;
                if (!string.IsNullOrEmpty(selectedSubKeyName))
                {
                    using var selectedKey = classKey.OpenSubKey(selectedSubKeyName);
                    if (selectedKey != null)
                    {
                        ProcessAdapterKey(selectedKey);
                        return;
                    }
                }

                throw new InvalidOperationException("The selected display-class registry key could not be opened.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Universal Registry Promotion error: {ex.Message}");
            }
        }

        private void PromoteNvidiaFiles(string assignedDriveLetter)
        {
            // --- 1. System32 (64位核心) ---
            string s32 = "System32";
            LinkSingleFile(assignedDriveLetter, "MCU.exe", "MCU.exe", s32);
            LinkSingleFile(assignedDriveLetter, "nvapi64.dll", "nvapi64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvcpl.dll", "nvcpl.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvcuda_loader64.dll", "nvcuda.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvcudadebugger.dll", "nvcudadebugger.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvcuvid64.dll", "nvcuvid.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvdebugdump.exe", "nvdebugdump.exe", s32);
            LinkSingleFile(assignedDriveLetter, "nvEncodeAPI64.dll", "nvEncodeAPI64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "NvFBC64.dll", "NvFBC64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvidia-pcc.exe", "nvidia-pcc.exe", s32);
            LinkSingleFile(assignedDriveLetter, "nvidia-smi.exe", "nvidia-smi.exe", s32);
            LinkSingleFile(assignedDriveLetter, "NvIFR64.dll", "NvIFR64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvinfo.pb", "nvinfo.pb", s32);
            LinkSingleFile(assignedDriveLetter, "nvml_loader.dll", "nvml.dll", s32);
            LinkSingleFile(assignedDriveLetter, "nvofapi64.dll", "nvofapi64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "OpenCL64.dll", "OpenCL.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-x64.dll", "vulkan-1.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-x64.dll", "vulkan-1-999-0-0-0.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-x64.exe", "vulkaninfo.exe", s32);

            // --- 2. System32 特殊子目录 ---
            LinkSingleFile(assignedDriveLetter, "license.txt", "license.txt", @"System32\drivers\NVIDIA Corporation");
            LinkSingleFile(assignedDriveLetter, "dbInstaller.exe", "dbInstaller.exe", @"System32\drivers\NVIDIA Corporation\Drs");
            LinkSingleFile(assignedDriveLetter, "nvdrsdb.bin", "nvdrsdb.bin", @"System32\drivers\NVIDIA Corporation\Drs");

            // --- 3. lxss (WSL Linux 支持) ---
            string lxssPath = @"System32\lxss\lib";
            LinkSingleFile(assignedDriveLetter, "libcuda_loader.so", "libcuda.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libcuda_loader.so", "libcuda.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libcuda_loader.so", "libcuda.so.1.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libcudadebugger.so.1", "libcudadebugger.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvcuvid.so.1", "libnvcuvid.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvcuvid.so.1", "libnvcuvid.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvdxdlkernels.so", "libnvdxdlkernels.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-encode.so.1", "libnvidia-encode.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-encode.so.1", "libnvidia-encode.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-ml_loader.so", "libnvidia-ml.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-ngx.so.1", "libnvidia-ngx.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-opticalflow.so.1", "libnvidia-opticalflow.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvidia-opticalflow.so.1", "libnvidia-opticalflow.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvoptix_loader.so.1", "libnvoptix.so.1", lxssPath);
            LinkSingleFile(assignedDriveLetter, "libnvwgf2umx.so", "libnvwgf2umx.so", lxssPath);
            LinkSingleFile(assignedDriveLetter, "nvidia-ngx-updater", "nvidia-ngx-updater", lxssPath);
            LinkSingleFile(assignedDriveLetter, "nvidia-smi", "nvidia-smi", lxssPath);

            // --- 4. SysWOW64 (32位兼容层) ---
            string sw64 = "SysWOW64";
            LinkSingleFile(assignedDriveLetter, "nvapi.dll", "nvapi.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "nvcuda_loader32.dll", "nvcuda.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "nvcuvid32.dll", "nvcuvid.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "nvEncodeAPI.dll", "nvEncodeAPI.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "NvFBC.dll", "NvFBC.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "NvIFR.dll", "NvIFR.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "nvofapi.dll", "nvofapi.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "OpenCL32.dll", "OpenCL.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-x86.dll", "vulkan-1.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-x86.dll", "vulkan-1-999-0-0-0.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-x86.exe", "vulkaninfo.exe", sw64);
        }
        private void PromoteIntelGpuFiles(string assignedDriveLetter)
        {
            string s32 = "System32";
            string sw64 = "SysWOW64";
            // --- 1. System32 (64位原生组件) ---
            // 基础管理与 API
            LinkSingleFile(assignedDriveLetter, "ControlLib.dll", "ControlLib.dll", s32);
            LinkSingleFile(assignedDriveLetter, "intel_gfx_api-x64.dll", "intel_gfx_api-x64.dll", s32);

            // 视频加速核心 (Intel Media SDK / VPL)
            LinkSingleFile(assignedDriveLetter, "mfx_loader_dll_hw64.dll", "libmfxhw64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vpl_dispatcher_64.dll", "libvpl.dll", s32);
            LinkSingleFile(assignedDriveLetter, "mfxplugin64_hw.dll", "mfxplugin64_hw.dll", s32);

            // Vulkan 核心
            LinkSingleFile(assignedDriveLetter, "vulkan-1-64.dll", "vulkan-1.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-64.dll", "vulkan-1-999-0-0-0.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-64.exe", "vulkaninfo.exe", s32);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-64.exe", "vulkaninfo-1-999-0-0-0.exe", s32);

            // 计算接口 (OneAPI / Level Zero)
            LinkSingleFile(assignedDriveLetter, "ze_intel_gpu_raytracing.dll", "ze_intel_gpu_raytracing.dll", s32);
            LinkSingleFile(assignedDriveLetter, "ze_loader.dll", "ze_loader.dll", s32);
            LinkSingleFile(assignedDriveLetter, "ze_tracing_layer.dll", "ze_tracing_layer.dll", s32);
            LinkSingleFile(assignedDriveLetter, "ze_validation_layer.dll", "ze_validation_layer.dll", s32);

            // --- 2. SysWOW64 (32位兼容组件) ---
            // 基础管理
            LinkSingleFile(assignedDriveLetter, "ControlLib32.dll", "ControlLib32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "IntelControlLib32.dll", "IntelControlLib32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "intel_gfx_api-x86.dll", "intel_gfx_api-x86.dll", sw64);

            // 32位视频加速
            LinkSingleFile(assignedDriveLetter, "mfx_loader_dll_hw32.dll", "libmfxhw32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vpl_dispatcher_32.dll", "libvpl.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "mfxplugin32_hw.dll", "mfxplugin32_hw.dll", sw64);

            // 32位 Vulkan
            LinkSingleFile(assignedDriveLetter, "vulkan-1-32.dll", "vulkan-1.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkan-1-32.dll", "vulkan-1-999-0-0-0.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-32.exe", "vulkaninfo.exe", sw64);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo-32.exe", "vulkaninfo-1-999-0-0-0.exe", sw64);
        }

        private void PromoteIntelGpuRuntimeRegistry(
            string assignedDriveLetter,
            string selectedClassSubKeyName)
        {
            const string displayClassGuid = "{4d36e968-e325-11ce-bfc1-08002be10318}";
            string systemHiveFile = Path.Combine(
                assignedDriveLetter,
                "Windows",
                "System32",
                "Config",
                "SYSTEM");

            if (!File.Exists(systemHiveFile))
                throw new FileNotFoundException("The offline SYSTEM hive was not found.", systemHiveFile);

            using var hostBaseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            if (string.IsNullOrWhiteSpace(selectedClassSubKeyName))
                throw new InvalidOperationException("Unable to map the selected Intel GPU-PV instance to its display-class registry key.");

            using var hostAdapterKey = hostBaseKey.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Class\{displayClassGuid}\{selectedClassSubKeyName}");
            if (hostAdapterKey == null)
                throw new InvalidOperationException("The selected Intel display-class registry key does not exist.");

            var runtimeValues = new List<(string Name, object Value, Microsoft.Win32.RegistryValueKind Kind)>();
            foreach (string valueName in IntelGpuRuntimeRegistryValueNames)
            {
                object? value = hostAdapterKey.GetValue(
                    valueName,
                    null,
                    Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) continue;

                Microsoft.Win32.RegistryValueKind valueKind;
                try { valueKind = hostAdapterKey.GetValueKind(valueName); }
                catch { continue; }

                runtimeValues.Add((
                    valueName,
                    RewriteDriverStoreRegistryValue(value),
                    valueKind));
            }

            if (runtimeValues.Count == 0)
                throw new InvalidOperationException("The selected Intel driver does not expose GPU runtime registry values.");

            string? levelZeroDriverPath = runtimeValues
                .FirstOrDefault(item => item.Name.Equals("LevelZeroDriverPath", StringComparison.OrdinalIgnoreCase))
                .Value as string;

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
                        $@"{classRoot}\{selectedClassSubKeyName}");
                    if (existingRuntimeKey != null && !IsCompatibleIntelRuntimeTarget(existingRuntimeKey))
                    {
                        throw new InvalidOperationException(
                            $"The offline display-class key {selectedClassSubKeyName} belongs to another display driver; Intel runtime values were not written.");
                    }

                    if (string.IsNullOrWhiteSpace(levelZeroDriverPath)) continue;

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
                        $@"{classRoot}\{selectedClassSubKeyName}",
                        writable: true))
                    {
                        if (runtimeKey == null)
                            throw new InvalidOperationException("Unable to create the offline Intel runtime registry key.");
                        if (!IsCompatibleIntelRuntimeTarget(runtimeKey))
                            throw new InvalidOperationException(
                                $"The offline display-class key {selectedClassSubKeyName} belongs to another display driver; Intel runtime values were not written.");

                        foreach (var runtimeValue in runtimeValues)
                            runtimeKey.SetValue(runtimeValue.Name, runtimeValue.Value, runtimeValue.Kind);
                    }

                    if (!string.IsNullOrWhiteSpace(levelZeroDriverPath) &&
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
                            levelZeroDriverPath,
                            Microsoft.Win32.RegistryValueKind.String);
                    }
                }
            });
        }

        private static object RewriteDriverStoreRegistryValue(object value)
        {
            static string Rewrite(string text) => Regex.Replace(
                text,
                @"(?i)([\\/])System32([\\/])DriverStore([\\/])",
                "$1System32$2HostDriverStore$3");

            return value switch
            {
                string text => Rewrite(text),
                string[] texts => texts.Select(Rewrite).ToArray(),
                _ => value
            };
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

        private void PromoteAmdGpuFiles(
            string assignedDriveLetter,
            GpuDriverPackagePlan packagePlan)
        {
            string s32 = "System32";
            string sw64 = "SysWOW64";
            IReadOnlyCollection<string> selectedPackageNames = packagePlan.PrimaryPackageNames;
            IReadOnlyCollection<string> openClPackageNames = packagePlan.AmdOpenClPackageNames;
            IReadOnlyCollection<string> amdWinPackageNames = packagePlan.AmdWinPackageNames;

            // --- 1. System32 (64位原生组件) ---
            // 核心渲染与 API
            LinkSingleFile(assignedDriveLetter, "atidxxstub64.dll", "atidxx64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdxcstub64.dll", "amdxc64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdxc64.so", "amdxc64.so", s32); // WSL支持

            // 基础管理库
            LinkSingleFile(assignedDriveLetter, "amdadlx64.dll", "amdadlx64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdave64.dll", "amdave64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdgfxinfo64.dll", "amdgfxinfo64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdlvr64.dll", "amdlvr64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdpcom64.dll", "amdpcom64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amfrt64.dll", "amfrt64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "atiadlxx.dll", "atiadlxx.dll", s32);
            LinkSingleFile(assignedDriveLetter, "atimpc64.dll", "atimpc64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "atisamu64.dll", "atisamu64.dll", s32);

            // 服务与工具
            LinkSingleFile(assignedDriveLetter, "amdsasrv64.dll", "amdsasrv64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "amdsacli64.dll", "amdsacli64.dll", s32);
            LinkSingleFile(assignedDriveLetter, "atieclxx.exe", "atieclxx.exe", s32);
            LinkSingleFile(assignedDriveLetter, "atieah64.exe", "atieah64.exe", s32);
            LinkSingleFile(assignedDriveLetter, "EEURestart.exe", "EEURestart.exe", s32);
            LinkSingleFile(assignedDriveLetter, "GameManager64.dll", "GameManager64.dll", s32);

            // DDA before/after parity: display-package top-level runtime entries.
            LinkSingleFile(assignedDriveLetter, "amdmiracast.dll", "amdmiracast.dll", s32, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "amf-mft-mjpeg-decoder64.dll", "amf-mft-mjpeg-decoder64.dll", s32, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "atidemgy.dll", "atidemgy.dll", s32, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "atimuixx.dll", "atimuixx.dll", s32, FilePromotionMode.PreserveExisting, selectedPackageNames);

            // The OpenCL/HIP companion package is declared by the active display INF and has the same DriverVer.
            // Never fall back to a different amdocl generation when multiple driver versions coexist.
            LinkSingleFile(assignedDriveLetter, "amd_comgr_2.dll", "amd_comgr_2.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "amdhip64_6.dll", "amdhip64_6.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "amdmmcl.dll", "amdmmcl.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "amdmmcl6.dll", "amdmmcl6.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "clinfo.exe", "clinfo.exe", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "hiprt02000_amd.hipfb", "hiprt02000_amd.hipfb", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "hiprt0200064.dll", "hiprt0200064.dll", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "oro_compiled_kernels.hipfb", "oro_compiled_kernels.hipfb", s32, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);

            // AMD Windows support components. RapidFire requires the Microsoft VC++ runtime at load time;
            // that external prerequisite is intentionally not installed by GPU driver injection.
            LinkSingleFile(assignedDriveLetter, "amdlogum.exe", "amdlogum.exe", s32, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "dgtrayicon.exe", "dgtrayicon.exe", s32, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "Rapidfire64.dll", "Rapidfire64.dll", s32, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "RapidFireServer64.dll", "RapidFireServer64.dll", s32, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);

            // 资源与数据
            LinkSingleFile(assignedDriveLetter, "atiapfxx.blb", "atiapfxx.blb", s32);
            LinkSingleFile(assignedDriveLetter, "ativvsva.dat", "ativvsva.dat", s32);
            LinkSingleFile(assignedDriveLetter, "ativvsvl.dat", "ativvsvl.dat", s32);
            LinkSingleFile(assignedDriveLetter, "AMDKernelEvents.mc", "AMDKernelEvents.man", s32);
            LinkSingleFile(assignedDriveLetter, "detoured64.dll", "detoured.dll", s32);

            // --- 2. SysWOW64 (32位兼容组件) ---
            // 核心渲染
            LinkSingleFile(assignedDriveLetter, "atidxxstub32.dll", "atidxx32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdxcstub32.dll", "amdxc32.dll", sw64);

            // 基础管理库
            LinkSingleFile(assignedDriveLetter, "amdadlx32.dll", "amdadlx32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdave32.dll", "amdave32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdgfxinfo32.dll", "amdgfxinfo32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdlvr32.dll", "amdlvr32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdpcom32.dll", "amdpcom32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amfrt32.dll", "amfrt32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "atimpc32.dll", "atimpc32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "atisamu32.dll", "atisamu32.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "GameManager32.dll", "GameManager32.dll", sw64);

            // 32位特殊命名映射
            LinkSingleFile(assignedDriveLetter, "atiadlxy.dll", "atiadlxx.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "detoured32.dll", "detoured.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "amdsacli32.dll", "amdsacli32.dll", sw64, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "amf-mft-mjpeg-decoder32.dll", "amf-mft-mjpeg-decoder32.dll", sw64, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "atiadlxy.dll", "atiadlxy.dll", sw64, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "atieah32.exe", "atieah32.exe", sw64, FilePromotionMode.PreserveExisting, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "amd_comgr32.dll", "amd_comgr32.dll", sw64, FilePromotionMode.PreserveExisting, openClPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "Rapidfire.dll", "Rapidfire.dll", sw64, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);
            LinkSingleFile(assignedDriveLetter, "RapidFireServer.dll", "RapidFireServer.dll", sw64, FilePromotionMode.PreserveExisting, amdWinPackageNames, requirePreferredPackage: true);

            // 资源
            LinkSingleFile(assignedDriveLetter, "atiapfxx.blb", "atiapfxx.blb", sw64);
            LinkSingleFile(assignedDriveLetter, "ativvsva.dat", "ativvsva.dat", sw64);
            LinkSingleFile(assignedDriveLetter, "ativvsvl.dat", "ativvsvl.dat", sw64);

            // --- 3. Vulkan Loader 与工具 ---
            // amdvlk*.dll 是 AMD ICD，不是系统 Vulkan Loader；这里严格按 AMD INF 的映射提升。
            LinkSingleFile(assignedDriveLetter, "vulkan64.dll", "vulkan-1.dll", s32, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkan64.dll", "vulkan-1-999-0-0-0.dll", s32, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo64.exe", "vulkaninfo.exe", s32, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo64.exe", "vulkaninfo-1-999-0-0-0.exe", s32, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkan32.dll", "vulkan-1.dll", sw64, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkan32.dll", "vulkan-1-999-0-0-0.dll", sw64, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo32.exe", "vulkaninfo.exe", sw64, FilePromotionMode.WhenNewer, selectedPackageNames);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo32.exe", "vulkaninfo-1-999-0-0-0.exe", sw64, FilePromotionMode.WhenNewer, selectedPackageNames);
        }

        private void PromoteQualcommGpuFiles(string assignedDriveLetter)
        {
            string s32 = "System32";
            string sw64 = "SysWOW64";
            string sc32 = "SyChpe32";

            // --- 1. System32 (原生 ARM64 组件) ---
            LinkSingleFile(assignedDriveLetter, "OpenCL.dll", "OpenCL.dll", s32);
            LinkSingleFile(assignedDriveLetter, "qcdxkmsuc8380.mbn", "qcdxkmsuc8380.mbn", s32);
            LinkSingleFile(assignedDriveLetter, "qcdxkmsucpurwa.mbn", "qcdxkmsucpurwa.mbn", s32);
            LinkSingleFile(assignedDriveLetter, "qchdcpumd8380.dll", "qchdcpumd8380.dll", s32);
            LinkSingleFile(assignedDriveLetter, "vulkan-1.dll", "vulkan-1.dll", s32, FilePromotionMode.WhenNewer);
            LinkSingleFile(assignedDriveLetter, "vulkan-1.dll", "vulkan-1-999-0-0-0.dll", s32, FilePromotionMode.WhenNewer);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo.exe", "vulkaninfo.exe", s32, FilePromotionMode.WhenNewer);
            LinkSingleFile(assignedDriveLetter, "vulkaninfo.exe", "vulkaninfo-1-999-0-0-0.exe", s32, FilePromotionMode.WhenNewer);

            // --- 2. SysWOW64 (标准 x86 组件) ---
            LinkSingleFile(assignedDriveLetter, "qcdx11x86um.dll", "qcdx11x86um.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcdx12x86um.dll", "qcdx12x86um.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcdxdmlx86.dll", "qcdxdmlx86.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcdxsdx86.dll", "qcdxsdx86.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcegpx86.dll", "qcegpx86.dll", sw64);
            LinkSingleFile(assignedDriveLetter, "qcgpux86compilercore.DLL", "qcgpux86compilercore.DLL", sw64);
            LinkSingleFile(assignedDriveLetter, "qcvidencx86um.DLL", "qcvidencum.DLL", sw64);

            // --- 3. SyChpe32 (CHPE 仿真加速组件) ---
            LinkSingleFile(assignedDriveLetter, "qcdx11chpeum.dll", "qcdx11x86um.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcdx12chpeum.dll", "qcdx12x86um.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcdxdmlchpe.dll", "qcdxdmlx86.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcdxsdchpe.dll", "qcdxsdx86.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcegpchpe.dll", "qcegpx86.dll", sc32);
            LinkSingleFile(assignedDriveLetter, "qcgpuchpecompilercore.dll", "qcgpux86compilercore.DLL", sc32);

            // MFT promotion remains gated on guest video-context support.
        }
        private void ProcessPromotionRegistryKey(
            Microsoft.Win32.RegistryKey adapterKey,
            string subKeyName,
            string assignedDriveLetter,
            string targetSubDir,
            IReadOnlyCollection<string> preferredPackageNames)
        {
            using var promotionKey = adapterKey.OpenSubKey(subKeyName);
            if (promotionKey == null) return;

            FilePromotionMode promotionMode = subKeyName.Contains("Overwrite", StringComparison.OrdinalIgnoreCase)
                ? FilePromotionMode.Overwrite
                : FilePromotionMode.WhenNewer;

            foreach (var valName in promotionKey.GetValueNames())
            {
                var val = promotionKey.GetValue(valName);
                string sourceSearch = null;
                string targetLinkName = null;

                if (val is string[] pairs && pairs.Length > 0)
                {
                    sourceSearch = pairs[0];
                    targetLinkName = (pairs.Length > 1) ? pairs[1] : pairs[0];
                }
                else if (val is string single)
                {
                    sourceSearch = targetLinkName = single;
                }

                if (!string.IsNullOrEmpty(sourceSearch) && !string.IsNullOrEmpty(targetLinkName))
                {
                    LinkSingleFile(
                        assignedDriveLetter,
                        sourceSearch,
                        targetLinkName,
                        targetSubDir,
                        promotionMode,
                        preferredPackageNames);
                }
            }
        }

        private void LinkSingleFile(
            string assignedDriveLetter,
            string sourceName,
            string targetName,
            string targetSubDir,
            FilePromotionMode promotionMode = FilePromotionMode.PreserveExisting,
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
                    if (promotionMode != FilePromotionMode.PreserveExisting || requirePreferredPackage)
                    {
                        string warning = $"[GPU Link] Required promotion source not found: {sourceName}.";
                        Debug.WriteLine(warning);
                        _linkWarnings.Value?.Enqueue(warning);
                    }
                    return;
                }
                if (foundFiles.Count > 1)
                {
                    string warning = $"[GPU Link] Promotion source is ambiguous: {sourceName}.";
                    Debug.WriteLine(warning);
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
                Directory.CreateDirectory(hostLinkDirectory);
                var files = _deploymentFiles.Value ?? new GpuDeploymentFileOperations(
                    warning => _linkWarnings.Value?.Enqueue(warning));
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
                catch { }

                if (targetPathExists)
                {
                    if (targetIsDirectory)
                    {
                        string warning = $"[GPU Link] Refusing to replace directory {hostLinkPath}.";
                        Debug.WriteLine(warning);
                        _linkWarnings.Value?.Enqueue(warning);
                        return;
                    }

                    bool shouldReplace = promotionMode switch
                    {
                        FilePromotionMode.Overwrite => true,
                        FilePromotionMode.WhenNewer => IsSourceNewerThanDestination(
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
            catch (Exception ex)
            {
                string warning = $"[GPU Link] {sourceName}: {ex.Message}";
                Debug.WriteLine(warning);
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

        private static List<FileInfo> FindDriverSourceFiles(
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
                        SearchOption searchOption = sourceParts.Length == 1
                            ? SearchOption.AllDirectories
                            : SearchOption.TopDirectoryOnly;
                        matches.AddRange(new DirectoryInfo(searchRoot).GetFiles(filePattern, searchOption));
                    }
                    catch (UnauthorizedAccessException) { }
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
                    matches.AddRange(new DirectoryInfo(guestRepo).GetFiles(normalizedSource, SearchOption.AllDirectories));
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

        private static void SetFolderReadOnly(string folderPath)
        {
            var directory = new DirectoryInfo(folderPath);
            directory.Attributes |= FileAttributes.ReadOnly;

            foreach (var subDirectory in directory.GetDirectories())
            {
                SetFolderReadOnly(subDirectory.FullName);
            }

            foreach (var file in directory.GetFiles())
            {
                file.Attributes |= FileAttributes.ReadOnly;
            }
        }

        private static void RemoveReadOnlyAttribute(string folderPath)
        {
            var directory = new DirectoryInfo(folderPath);
            directory.Attributes &= ~FileAttributes.ReadOnly;

            foreach (var subDirectory in directory.GetDirectories())
            {
                RemoveReadOnlyAttribute(subDirectory.FullName);
            }

            foreach (var file in directory.GetFiles())
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
            }
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

                using var hostBaseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    Microsoft.Win32.RegistryHive.LocalMachine,
                    Microsoft.Win32.RegistryView.Registry64);
                using var hostServiceKey = hostBaseKey.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\nvlddmkm");
                if (hostServiceKey == null)
                    return Properties.Resources.Error_ExportLocalRegistryInfoFailed;

                string packageDirectory = Path.Combine(
                    packagePlan.HostFileRepository,
                    packagePlan.PrimaryPackageName);
                string[] driverFiles = Directory.GetFiles(
                    packageDirectory,
                    "nvlddmkm.sys",
                    SearchOption.AllDirectories);
                if (driverFiles.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"The selected NVIDIA package must contain exactly one nvlddmkm.sys; found {driverFiles.Length}: {packageDirectory}");
                }

                string relativeDriverPath = Path.GetRelativePath(
                    packagePlan.HostFileRepository,
                    driverFiles[0]);
                if (relativeDriverPath.StartsWith("..", StringComparison.Ordinal) ||
                    Path.IsPathRooted(relativeDriverPath))
                {
                    throw new InvalidOperationException(
                        $"The selected NVIDIA kernel driver is outside FileRepository: {driverFiles[0]}");
                }

                string guestImagePath =
                    $@"\SystemRoot\System32\HostDriverStore\FileRepository\{relativeDriverPath}";

                string[] serviceValueNames =
                {
                    "Type",
                    "Start",
                    "ErrorControl",
                    "Group"
                };

                var serviceValues = new List<(
                    string Name,
                    object Value,
                    Microsoft.Win32.RegistryValueKind Kind)>();
                foreach (string valueName in serviceValueNames)
                {
                    object? value = hostServiceKey.GetValue(
                        valueName,
                        null,
                        Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null)
                    {
                        throw new InvalidOperationException(
                            $"The host nvlddmkm service is missing the required value {valueName}.");
                    }

                    serviceValues.Add((
                        valueName,
                        value,
                        hostServiceKey.GetValueKind(valueName)));
                }

                serviceValues.Add((
                    "ImagePath",
                    guestImagePath,
                    Microsoft.Win32.RegistryValueKind.ExpandString));

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
                        foreach (var serviceValue in serviceValues)
                        {
                            destinationServiceKey.SetValue(
                                serviceValue.Name,
                                serviceValue.Value,
                                serviceValue.Kind);
                        }
                    }
                });

                return "OK";
            }
            catch (Exception ex)
            {
                return string.Format(Properties.Resources.Error_NvidiaRegistryProcessingException, ex.Message);
            }
        }

        private static async Task CopyVendorProgramFoldersAsync(
            GpuDriverVendor vendor,
            string assignedDriveLetter,
            Action<string> log)
        {
            string[] directoryNames = vendor switch
            {
                GpuDriverVendor.Nvidia => new[] { "NVIDIA Corporation", "NVIDIA" },
                GpuDriverVendor.Amd => new[] { "AMD", "ATI Technologies" },
                GpuDriverVendor.Intel => new[] { "Intel", "Intel Corporation" },
                GpuDriverVendor.Qualcomm => new[] { "Qualcomm", "Qualcomm Incorporated" },
                _ => Array.Empty<string>()
            };

            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };

            string[] sourceFolders = roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .SelectMany(root => directoryNames.Select(name => Path.Combine(root, name)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var sourcePath in sourceFolders)
            {
                if (!Directory.Exists(sourcePath)) continue;
                string root = Path.GetPathRoot(sourcePath)
                    ?? throw new IOException($"Unable to determine the root of vendor directory: {sourcePath}");
                string relativePath = Path.GetRelativePath(root, sourcePath);

                string targetPath = Path.Combine(assignedDriveLetter, relativePath);

                log?.Invoke(string.Format(Properties.Resources.Log_Gpu_Syncing, sourcePath, targetPath));

                Directory.CreateDirectory(targetPath);
                using Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "robocopy.exe",
                    Arguments = $"\"{sourcePath}\" \"{targetPath}\" /E /R:1 /W:1 /MT:32 /XJ /NDL /NJH /NJS /NC /NS",
                    CreateNoWindow = true,
                    UseShellExecute = false
                }) ?? throw new InvalidOperationException("Unable to start robocopy.exe.");
                await process.WaitForExitAsync();
                if (process.ExitCode >= 8)
                    throw new IOException(
                        $"Copying vendor program directory {sourcePath} failed with robocopy exit code {process.ExitCode}.");
            }
        }

        #endregion

        #region Linux 驱动环境与脚本部署
        private async Task UploadLocalFilesAsync(SshCredentials credentials, string remoteDirectory)
        {
            string systemWslLibPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "lxss", "lib");

            if (Directory.Exists(systemWslLibPath))
            {
                var allFiles = Directory.GetFiles(systemWslLibPath);
                foreach (var filePath in allFiles)
                {
                    string fileName = Path.GetFileName(filePath);
                    await SshService.UploadFileAsync(credentials, filePath, $"{remoteDirectory}/{fileName}");
                }
            }
        }

        /// <summary>从在线索引读取可选 Linux GPU 部署脚本。</summary>
        /// <returns>新的脚本列表；请求超时、解析或网络失败时返回空列表。</returns>
        /// <remarks>索引请求超时为 5 秒；无脚本或查询失败均返回空列表。</remarks>
        public async Task<List<LinuxGpuDeploymentScript>> GetAvailableScriptsAsync()
        {
            var allScripts = new List<LinuxGpuDeploymentScript>();

            // 从应用维护的在线索引加载商店版本允许使用的部署脚本。
            try
            {
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                string jsonUrl = $"{ScriptBaseUrl}index.json";
                var jsonString = await httpClient.GetStringAsync(jsonUrl);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var remoteScripts = JsonSerializer.Deserialize<List<LinuxGpuDeploymentScript>>(jsonString, options);

                if (remoteScripts != null)
                {
                    foreach (var item in remoteScripts)
                    {
                        item.Name = string.Format(Properties.Resources.VmGPUService_LogOnline, item.Name);
                        item.SourceUrl = $"{ScriptBaseUrl}{item.FileName}";
                        allScripts.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Remote script index fetch failed: {ex.Message}");
            }

            return allScripts
                .OrderBy(x => x.Name)
                .ToList();
        }

        // 支持重启循环的部署函数
        /// <summary>通过 SSH 上传 GPU 文件并执行所选 Linux 部署脚本，必要时启动或重启来宾。</summary>
        /// <param name="vmName">目标虚拟机名称。</param>
        /// <param name="gpuInstancePath">宿主 GPU 设备路径，用于定位驱动包。</param>
        /// <param name="gpuManufacturer">GPU 芯片商名称。</param>
        /// <param name="script">下载并执行的部署脚本描述。</param>
        /// <param name="credentials">SSH 凭据及脚本选项；本方法会改写 Host 为选出的 IPv4 地址。</param>
        /// <param name="progressCallback">进展文本回调，应快速返回且不抛出异常；界面更新需调度到界面线程。</param>
        /// <param name="ct">协作式取消令牌，在等待和部分步骤之间检查；SSH 命令和文件传输继续执行至完成。</param>
        /// <returns>成功时返回 OK，否则通常返回错误说明；取消也可能转换为失败文本。</returns>
        /// <remarks>脚本会修改来宾软件包、内核模块、服务和环境；失败时可能保留部分修改。SSH 命令可能长时间运行。</remarks>
        /// <editentry label="部署 Linux GPU 驱动" category="GPU"/>
        public Task<string> ProvisionLinuxGpuAsync(
            string vmName,
            string gpuInstancePath,
            string gpuManufacturer,
            LinuxGpuDeploymentScript script,
            SshCredentials credentials,
            Action<string> progressCallback,
            CancellationToken ct)
        {
            return Task.Run(async () =>
            {
                void Log(string msg) => progressCallback?.Invoke(msg);

                try
                {
                    var currentState = await _queryService.GetVmStateAsync(vmName);
                    if (currentState != "2")
                    {
                        Log(Properties.Resources.Log_Gpu_StartingVm);
                        await VmPowerService.ExecuteControlActionAsync(vmName, "Start");
                        await Task.Delay(5000);
                    }

                    Log(Properties.Resources.Msg_Gpu_LinuxWaitingIp);
                    var adapters = await VmNetworkService.GetNetworkAdaptersAsync(vmName);
                    if (adapters == null || adapters.Count == 0) return Properties.Resources.Error_Gpu_NoMac;
                    string macAddress = adapters[0].MacAddress;
                    string vmIpAddress = await VmIpService.LookupAsync(vmName, macAddress);
                    string targetIp = Ipv4.SelectBest(!string.IsNullOrWhiteSpace(credentials.Host) ? credentials.Host : vmIpAddress);

                    if (string.IsNullOrEmpty(targetIp)) return Properties.Resources.Error_Gpu_NoIpv4;
                    credentials.Host = targetIp;

                    if (!await WaitForVmToBeResponsiveAsync(credentials.Host, credentials.Port, ct))
                        return Properties.Resources.Error_Gpu_SshTimeout;

                    string remoteTempDir = "/tmp/exhyperv_deploy";
                    using (var client = new SshClient(credentials.Host, credentials.Port, credentials.Username, credentials.Password))
                    {
                        client.Connect();
                        using var command = client.RunCommand($"mkdir -p {remoteTempDir}/drivers {remoteTempDir}/lib");
                        client.Disconnect();
                    }

                    Log(Properties.Resources.Log_Gpu_UploadingDriverWsl);
                    GpuDriverPackagePlan packagePlan = await Task.Run(() =>
                        GpuDriverPackageResolver.Resolve(gpuInstancePath, gpuManufacturer));
                    string sourceDriverPath = packagePlan.HostFileRepository;
                    await SshService.UploadDirectoryAsync(credentials, sourceDriverPath, $"{remoteTempDir}/drivers");
                    await UploadLocalFilesAsync(credentials, $"{remoteTempDir}/lib");

                    string remoteScriptPath = $"{remoteTempDir}/{script.FileName}";

                    string proxyEnv = string.Empty;
                    if (credentials.UseProxy && !string.IsNullOrEmpty(credentials.ProxyHost))
                    {
                        string proxyUrl = $"http://{credentials.ProxyHost}:{credentials.ProxyPort}";
                        proxyEnv = $"http_proxy='{proxyUrl}' https_proxy='{proxyUrl}' HTTP_PROXY='{proxyUrl}' HTTPS_PROXY='{proxyUrl}' ";
                    }

                    Log(string.Format(Properties.Resources.Log_Gpu_DownloadingScript, script.Name));
                    string downloadCmd = $"{proxyEnv}sh -c \"wget -q -O {remoteScriptPath} {script.SourceUrl} || curl -fL {script.SourceUrl} -o {remoteScriptPath}\"";

                    await SshService.ExecuteSingleCommandAsync(credentials, downloadCmd, Log);
                    await SshService.ExecuteSingleCommandAsync(credentials, $"chmod +x {remoteScriptPath}", Log);
                    bool isSuccess = false;
                    int maxAttempts = 3;

                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        if (ct.IsCancellationRequested) return Properties.Resources.Msg_Gpu_Cancelled;

                        bool rebootNeeded = false;
                        string graphicsArg = credentials.InstallGraphics ? "true" : "false";
                        // 代理地址单引号包裹 + 转义,防 ProxyHost 含 $()/反引号注入(与上面密码同款转义);正常主机名行为等价
                        string proxyArg = credentials.UseProxy ? $"'http://{credentials.ProxyHost.Replace("'", "'\\''")}:{credentials.ProxyPort}'" : "\"\"";

                        string execCmd = $"echo '{credentials.Password.Replace("'", "'\\''")}' | sudo -S -E -p '' bash {remoteScriptPath} deploy {graphicsArg} {proxyArg}";

                        Log(string.Format(Properties.Resources.Log_Gpu_ExecutingAttempt, attempt));

                        await SshService.ExecuteCommandAndCaptureOutputAsync(credentials, execCmd, line =>
                        {
                            Log(line);
                            if (line.Contains("[STATUS: SUCCESS]")) isSuccess = true;
                            if (line.Contains("[STATUS: REBOOT_REQUIRED]")) rebootNeeded = true;
                        }, TimeSpan.FromMinutes(60));

                        if (isSuccess) break;

                        if (rebootNeeded)
                        {
                            Log(Properties.Resources.Log_Gpu_RebootRequired);
                            await VmPowerService.ExecuteControlActionAsync(vmName, "Restart");
                            await Task.Delay(10000); // 等待开始关机
                            if (!await WaitForVmToBeResponsiveAsync(credentials.Host, credentials.Port, ct))
                                return Properties.Resources.Error_Gpu_RebootNoResponse;

                            Log(Properties.Resources.Log_Gpu_VmBackOnline);
                            continue; // 重新进入循环执行同一脚本
                        }

                        // 如果既没成功也没重启信号，通常是脚本内部报错 set -e 触发了
                        if (!isSuccess) return Properties.Resources.Error_Gpu_ScriptNoSuccess;
                    }

                    return isSuccess ? "OK" : Properties.Resources.Error_Gpu_MaxReboots;

                }
                catch (Exception ex) { return string.Format(Properties.Resources.Error_Gpu_LinuxGeneric, ex.Message); }
            });
        }
        #endregion
    }
}
