using System.ComponentModel;
using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>查询宿主物理磁盘、光驱和分区，并执行磁盘挂载、脱机、只读及盘符操作。</summary>
    /// <remarks>修改需要宿主存储管理权限。Core 对同一磁盘或映像的写操作跨进程排队，不同资源可并行；映像通过卷和文件身份匹配。实际完成情况见各操作结果。</remarks>
    public static partial class HostDiskService
    {
        // 列出主机上的全部物理硬盘，并标注系统盘、已分配、只读等状态；
        // 不可直通的由 CanPassthrough=false 交给 UI 置灰。这样用户看得到每块盘的真实状态，而不是"能直通的才显示"。
        /// <summary>读取宿主磁盘身份、容量及物理盘直通候选状态。</summary>
        /// <returns>按磁盘编号排序的磁盘清单；存储或归属查询失败时返回错误。</returns>
        public static async Task<ApiResponse<List<HostDiskInfo>>> GetHostDisksAsync()
        {
            // VM 配置 GUID → VM 名(一次查全,供"已分配给谁"关联)
            var vmNames = new Dictionary<string, string>();
            var vmResp = await WmiApi.QueryAsync(
                "SELECT ConfigurationID, ElementName FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => (Id: obj["ConfigurationID"]?.ToString() ?? string.Empty, Name: obj["ElementName"]?.ToString() ?? string.Empty),
                WmiScope.HyperV);
            if (!vmResp.Success) return ApiResponse<List<HostDiskInfo>>.Fail(vmResp.Error,
                vmResp.Code, vmResp.ErrorSource, vmResp.Exception, vmResp.ErrorKind);
            if (vmResp.Success && vmResp.Data != null)
                foreach (var vm in vmResp.Data)
                    if (!string.IsNullOrEmpty(vm.Id)) vmNames[vm.Id.ToUpperInvariant()] = vm.Name;

            // 已挂给某 VM 的盘号 → VM 名：物理盘直通 RASD 的 HostResource 解析盘号、InstanceID 里的 GUID 关联 VM。
            var assignedTo = new Dictionary<int, string>();
            var diskDeviceIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hyperVDiskResp = await WmiApi.QueryAsync(
                "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive",
                obj => (DeviceId: obj["DeviceID"]?.ToString() ?? string.Empty,
                        Number: Convert.ToInt32(obj["DriveNumber"] ?? -1)),
                WmiScope.HyperV);
            if (!hyperVDiskResp.Success) return ApiResponse<List<HostDiskInfo>>.Fail(hyperVDiskResp.Error,
                hyperVDiskResp.Code, hyperVDiskResp.ErrorSource, hyperVDiskResp.Exception, hyperVDiskResp.ErrorKind);
            if (hyperVDiskResp.Success && hyperVDiskResp.Data != null)
                foreach (var disk in hyperVDiskResp.Data)
                    if (disk.Number >= 0 && !string.IsNullOrEmpty(disk.DeviceId))
                        diskDeviceIds[disk.DeviceId.Replace("\\\\", "\\")] = disk.Number;

            var attachedResp = await WmiApi.QueryAsync(
                "SELECT InstanceID, HostResource FROM Msvm_ResourceAllocationSettingData WHERE ResourceSubType = 'Microsoft:Hyper-V:Physical Disk Drive'",
                obj => (Inst: obj["InstanceID"]?.ToString() ?? string.Empty, Hr: (obj["HostResource"] as string[])?.FirstOrDefault() ?? string.Empty),
                WmiScope.HyperV);
            if (!attachedResp.Success) return ApiResponse<List<HostDiskInfo>>.Fail(attachedResp.Error,
                attachedResp.Code, attachedResp.ErrorSource, attachedResp.Exception, attachedResp.ErrorKind);
            if (attachedResp.Success && attachedResp.Data != null)
                foreach (var a in attachedResp.Data)
                {
                    if (string.IsNullOrEmpty(a.Hr)) continue;   // Definition 模板(Default/Minimum/…)的 HostResource 为空
                    var dm = System.Text.RegularExpressions.Regex.Match(a.Hr, "DeviceID=\"([^\"]+)\"");
                    if (!dm.Success) continue;
                    string deviceId = dm.Groups[1].Value.Replace("\\\\", "\\");
                    if (!diskDeviceIds.TryGetValue(deviceId, out int dn)) continue;
                    string vmName = string.Empty;
                    var gm = System.Text.RegularExpressions.Regex.Match(a.Inst, "([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})");
                    if (gm.Success) vmNames.TryGetValue(gm.Groups[1].Value.ToUpperInvariant(), out vmName);
                    assignedTo[dn] = vmName ?? string.Empty;
                }

            var diskResp = await WmiApi.QueryCimAsync(
                "SELECT Number, FriendlyName, Size, UniqueId, SerialNumber, IsSystem, IsBoot, BusType, IsOffline, IsReadOnly FROM MSFT_Disk",
                obj => new
                {
                    number = Convert.ToInt32(obj["Number"] ?? -1),
                    busType = Convert.ToUInt16(obj["BusType"] ?? 0),
                    isSystem = Convert.ToBoolean(obj["IsSystem"] ?? false),
                    isBoot = Convert.ToBoolean(obj["IsBoot"] ?? false),
                    isOffline = Convert.ToBoolean(obj["IsOffline"] ?? false),
                    isReadOnly = Convert.ToBoolean(obj["IsReadOnly"] ?? false),
                    sizeBytes = Convert.ToInt64(obj["Size"] ?? 0L),
                    uniqueId = obj["UniqueId"]?.ToString() ?? string.Empty,
                    serialNumber = obj["SerialNumber"]?.ToString()?.Trim() ?? string.Empty,
                    friendlyName = obj["FriendlyName"]?.ToString() ?? ""
                },
                WmiScope.Storage);

            if (!diskResp.Success)
                return ApiResponse<List<HostDiskInfo>>.Fail(
                    diskResp.Error, diskResp.Code, diskResp.ErrorSource, diskResp.Exception, diskResp.ErrorKind);

            var result = diskResp.Data!
                .Where(d => d.number >= 0)
                .OrderBy(d => d.number)
                .Select(d =>
                {
                    // 系统盘和已分配磁盘不可选择；其他磁盘由添加流程尝试脱机并交给 Hyper-V 验证。
                    string status;
                    bool can;
                    if (d.isSystem || d.isBoot) { can = false; status = Properties.Resources.Storage_DiskStatus_System; }
                    else if (assignedTo.TryGetValue(d.number, out var vm))
                    {
                        can = false;
                        status = string.Format(Properties.Resources.Storage_DiskStatus_Assigned,
                            string.IsNullOrEmpty(vm) ? "VM" : vm);
                    }
                    else
                    {
                        can = true;
                        status = (d.isReadOnly && !d.isOffline)
                            ? Properties.Resources.Storage_DiskStatus_ReadOnly
                            : Properties.Resources.Storage_DiskStatus_Available;
                    }

                    return new HostDiskInfo
                    {
                        Number = d.number,
                        FriendlyName = d.friendlyName,
                        SizeGiB = Math.Round(d.sizeBytes / 1073741824.0, 2),
                        SizeBytes = d.sizeBytes,
                        UniqueId = d.uniqueId,
                        SerialNumber = d.serialNumber,
                        Status = status,
                        CanPassthrough = can
                    };
                })
                .ToList();

            return ApiResponse<List<HostDiskInfo>>.Ok(result);
        }

        /// <summary>读取具有 PnP 设备身份的宿主光驱。</summary>
        /// <returns>光驱列表响应；没有匹配光驱时列表为空。</returns>
        public static async Task<ApiResponse<List<HostOpticalDriveInfo>>> GetHostOpticalDrivesAsync()
        {
            var resp = await WmiApi.QueryAsync(
                "SELECT DeviceID, Drive, PNPDeviceID, Caption FROM Win32_CDROMDrive",
                obj => new HostOpticalDriveInfo
                {
                    PnpDeviceId = obj["PNPDeviceID"]?.ToString() ?? string.Empty,
                    Drive = obj["Drive"]?.ToString() ?? string.Empty,
                    Model = obj["Caption"]?.ToString() ?? string.Empty
                },
                WmiScope.CimV2);

            if (!resp.Success)
                return ApiResponse<List<HostOpticalDriveInfo>>.Fail(resp.Error, resp.Code, resp.ErrorSource, resp.Exception, resp.ErrorKind);

            var result = (resp.Data ?? new List<HostOpticalDriveInfo>())
                .Where(o => !string.IsNullOrWhiteSpace(o.PnpDeviceId))
                .ToList();
            return ApiResponse<List<HostOpticalDriveInfo>>.Ok(result);
        }

        /// <summary>将宿主磁盘置为脱机或联机，立即影响该磁盘在宿主的可访问性。</summary>
        /// <returns>存储接口的操作响应；找不到目标磁盘时失败。</returns>
        /// <param name="disk">查询时选中的磁盘身份；身份变化时返回 Conflict。</param>
        /// <param name="isOffline">true 脱机，false 联机。</param>
        public static async Task<ApiResponse> SetDiskOfflineStatusAsync(HostDiskReference disk, bool isOffline)
        {
            if (disk is not { IsValid: true }) return ApiResponse.Fail("请提供查询所得的完整磁盘身份。", kind: ApiErrorKind.InvalidArgument);
            try
            {
                return await WithDiskLockAsync(disk.Number, () => WmiApi.InvokeAsync(
                    $"SELECT * FROM MSFT_Disk WHERE Number = {disk.Number}",
                    isOffline ? "Offline" : "Online", scope: WmiScope.Storage,
                    validateTarget: obj => ValidateDiskReference(disk, ReadDiskReference(obj))));
            }
            catch (Exception error) { return LockFailure(error); }
        }

        internal static async Task<ApiResponse> SetDiskOfflineStatusCoreAsync(int diskNumber, bool isOffline)
        {
            HostResourceLock.AssertHeld(HostDiskResource.Disk(diskNumber));
            return await WmiApi.InvokeAsync($"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}",
                isOffline ? "Offline" : "Online", scope: WmiScope.Storage);
        }

        // 仅供已自行核对控制器磁盘归属的 Core 流程使用，不作为公开的编号修改入口。
        internal static Task<ApiResponse> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
            => WithDiskLockAsync(diskNumber, () => SetDiskOfflineStatusCoreAsync(diskNumber, isOffline));

        /// <summary>修改宿主磁盘的只读属性。</summary>
        /// <returns>存储接口的操作响应；找不到目标磁盘时失败。</returns>
        /// <param name="disk">查询时选中的磁盘身份。</param>
        /// <param name="isReadOnly">true 设置只读，false 取消只读。</param>
        public static async Task<ApiResponse> SetDiskReadOnlyAsync(HostDiskReference disk, bool isReadOnly)
        {
            if (disk is not { IsValid: true }) return ApiResponse.Fail("请提供查询所得的完整磁盘身份。", kind: ApiErrorKind.InvalidArgument);
            try
            {
                return await WithDiskLockAsync(disk.Number, () => WmiApi.InvokeAsync(
                    $"SELECT * FROM MSFT_Disk WHERE Number = {disk.Number}",
                    "SetAttributes", p => p["IsReadOnly"] = isReadOnly, scope: WmiScope.Storage,
                    validateTarget: obj => ValidateDiskReference(disk, ReadDiskReference(obj))));
            }
            catch (Exception error) { return LockFailure(error); }
        }

        internal static async Task<ApiResponse> SetDiskReadOnlyCoreAsync(int diskNumber, bool isReadOnly)
        {
            HostResourceLock.AssertHeld(HostDiskResource.Disk(diskNumber));
            return await WmiApi.InvokeAsync($"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}",
                "SetAttributes", p => p["IsReadOnly"] = isReadOnly, WmiScope.Storage);
        }

        /// <summary>挂载宿主磁盘映像，并取得独占使用期。</summary>
        /// <param name="imagePath">宿主映像完整路径。</param>
        /// <param name="cancellationToken">等待使用权、提交前及磁盘发现期间响应取消；已取得的使用期由调用方释放。</param>
        /// <returns>需释放的使用期对象；先检查 Result，使用结束后等待 ReleaseAsync 或 DisposeAsync。</returns>
        /// <remarks>新挂载请求读写访问、使用无盘符模式；已有挂载沿用实际模式。Result.IsReadOnly 为实际只读状态，未知时为 null，原因见 AccessError。同一映像的其他 Core 操作全程排队；取得使用期前的错误以异常返回。</remarks>
        public static Task<HostDiskMountLease> MountDiskImageAsync(string imagePath,
            CancellationToken cancellationToken = default)
            => MountImageAsync(imagePath, false, false, cancellationToken);

        /// <summary>通过 Hyper-V 挂载虚拟磁盘，并取得独占使用期。</summary>
        /// <param name="imagePath">宿主虚拟磁盘完整路径。</param>
        /// <param name="readOnly">新挂载是否只读；已有挂载保留原访问模式。</param>
        /// <param name="cancellationToken">等待资源锁、提交前及磁盘发现期间响应取消。已提交的原生作业继续执行；已返回的使用期由调用方释放。</param>
        /// <returns>需释放的使用期对象。Result 失败也需释放；本次建立的挂载自动清理，已有挂载保留。</returns>
        /// <remarks>已有挂载沿用原访问模式，见 Result.IsReadOnly。完成文件访问后释放使用期：本次建立的挂载自动卸载，已有挂载保留；其他 Core 调用者等待到清理完成。取得使用权前发生的文件身份、锁或取消异常直接抛出。</remarks>
        /// <example>
        /// <code>
        /// using System.IO;
        /// using ExHyperV.Services;
        /// public static class MountedDiskExample
        /// {
        ///     public static async Task UseAsync(string path, Func&lt;int, bool?, Task&gt; use)
        ///     {
        ///         await using var lease = await HostDiskService.MountVhdxAsync(path, readOnly: true);
        ///         var result = lease.Result;
        ///         if (!result.Success) throw new IOException(result.Error);
        ///         // 只读是新挂载的请求。已有挂载可能是读写；null 表示模式未知。
        ///         await use(result.DiskNumber!.Value, result.IsReadOnly);
        ///         // use 必须等待它启动的全部文件操作结束，随后才由 await using 清理。
        ///     }
        /// }
        /// </code>
        /// </example>
        public static Task<HostDiskMountLease> MountVhdxAsync(string imagePath, bool readOnly = false,
            CancellationToken cancellationToken = default)
            => MountImageAsync(imagePath, true, readOnly, cancellationToken);

        private static Task<HostDiskMountLease> MountImageAsync(string imagePath, bool hyperV,
            bool readOnly, CancellationToken token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
            token.ThrowIfCancellationRequested();
            imagePath = Path.GetFullPath(imagePath);
            return HostDiskMountLease.AcquireAsync(
                action => WithImageLockAsync(imagePath, action, token),
                () => MountImageCoreAsync(imagePath, hyperV, readOnly, token),
                mount => ReleaseScanMountAsync(mount, path => mount.DiskNumber is int number
                    ? WithDiskLockAsync(number, () => DismountImageCoreAsync(path, number, hyperV))
                    : DismountImageCoreAsync(path, null, hyperV)));
        }

        internal static Task<HostDiskMountResult> MountVhdxCoreAsync(string imagePath, bool readOnly = false,
            CancellationToken cancellationToken = default)
            => MountImageCoreAsync(imagePath, true, readOnly, cancellationToken);

        private static async Task<HostDiskMountResult> MountImageCoreAsync(string imagePath, bool hyperV,
            bool readOnly, CancellationToken token)
        {
            HostResourceLock.AssertHeld(HostDiskResource.Image(imagePath));
            token.ThrowIfCancellationRequested();
            var before = await ReadImageAsync(imagePath);
            if (!before.Success) return new(imagePath, null, null, false, before.Error);
            if (before.HasData && before.Data!.Attached)
                return await WithMountAccessAsync(new(imagePath, true, before.Data.Number, false,
                    before.Data.Number.HasValue ? "" : "映像已经挂载，但无法取得宿主磁盘编号。"), before.Data.ImagePath);
            bool submitted = false;
            bool created = false;
            try
            {
                token.ThrowIfCancellationRequested();
                ApiResponse result;
                submitted = true;
                if (hyperV)
                    result = await WmiApi.InvokeAsync("SELECT * FROM Msvm_ImageManagementService",
                        "AttachVirtualHardDisk", p =>
                        {
                            p["Path"] = imagePath;
                            p["ReadOnly"] = readOnly;
                            p["AssignDriveLetter"] = false;
                        }, WmiScope.HyperV);
                else
                {
                    using var image = OpenDiskImage(imagePath);
                    result = await WmiApi.InvokeOnObjectAsync(image,
                        "Mount", p => { p["Access"] = (ushort)2; p["NoDriveLetter"] = true; }, scope: WmiScope.Storage);
                }
                created = result.Success;
                if (!result.Success)
                    return new(imagePath, null, null, false, result.Error + "；挂载状态需要重新查询。");
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var current = await ReadImageAsync(imagePath);
                    if (current.HasData && current.Data!.Attached && current.Data.Number.HasValue)
                        return await WithMountAccessAsync(new(imagePath, true, current.Data.Number, true, ""), current.Data.ImagePath);
                    if (token.IsCancellationRequested)
                        return await WithMountAccessAsync(new(imagePath, true, null, true, "已挂载；已取消后续磁盘发现，仍需释放使用期完成清理。"));
                    if (attempt < 9) await Task.Delay(500, token);
                }
                return await WithMountAccessAsync(new(imagePath, true, null, true, "挂载已完成，但未发现宿主磁盘编号。"));
            }
            catch (OperationCanceledException)
            {
                return await WithMountAccessAsync(new(imagePath, created ? true : submitted ? null : false, null, created,
                    "已取消磁盘发现；请按挂载状态处理剩余资源。"));
            }
            catch (Exception ex)
            {
                return await WithMountAccessAsync(new(imagePath, created ? true : submitted ? null : false, null, created, ex.Message));
            }
        }

        private static async Task<HostDiskMountResult> WithMountAccessAsync(HostDiskMountResult mount, string? attachedPath = null)
        {
            if (mount.IsMounted != true) return mount;
            if (mount.DiskNumber is not int number)
                return mount with { IsReadOnly = null, AccessError = "尚未取得宿主磁盘编号，无法读取实际只读状态。" };
            try
            {
                return await WithDiskLockAsync(number, async () =>
                {
                    // 返回宿主磁盘的当前实际只读状态，不反推 Attach 时的参数。
                    // 本机 Mount-VHD -ReadOnly 后 Msvm_MountedStorageImage.Access 仍报告 2；
                    // MSFT_Disk.IsReadOnly 才与 Get-Disk 的实际状态一致。
                    // https://learn.microsoft.com/windows-hardware/drivers/storage/msft-disk
                    var access = await WmiApi.QueryFirstCimAsync(
                        $"SELECT IsReadOnly FROM MSFT_Disk WHERE Number = {number}",
                        obj => obj["IsReadOnly"] is bool value ? (bool?)value : null, WmiScope.Storage);
                    var current = await ReadDiskImageAsync(attachedPath ?? mount.ImagePath);
                    if (!current.HasData || !current.Data!.Attached || current.Data.Number != number)
                        return mount with { IsReadOnly = null, AccessError = "读取只读状态时，映像与宿主磁盘的对应关系已变化或无法确认。" };
                    return WithMountAccess(mount, access);
                });
            }
            catch (Exception error) { return mount with { IsReadOnly = null, AccessError = error.Message }; }
        }

        internal static HostDiskMountResult WithMountAccess(HostDiskMountResult mount, ApiResponse<bool?> access)
            => access.HasData
                ? mount with { IsReadOnly = access.Data, AccessError = "" }
                : mount with { IsReadOnly = null, AccessError = access.Success
                    ? "未取得可识别的实际挂载访问模式。" : access.Error };

        internal sealed record ImageState(bool Attached, int? Number, string? ImagePath = null);
        private static ManagementObject OpenDiskImage(string imagePath)
        {
            // 此提供程序按键 Get 可取得实例，而普通 SELECT ... WHERE 在实测中返回空集。
            // StorageType=0 让系统识别格式，与扩展名无关。
            string escaped = imagePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var scope = WmiConnectionCache.GetManagementScope(WmiScope.Storage, WmiContext.Local);
            var image = new ManagementObject(scope,
                new ManagementPath($"MSFT_DiskImage.ImagePath=\"{escaped}\",StorageType=0"), null);
            try { image.Get(); return image; }
            catch { image.Dispose(); throw; }
        }

        private static Task<ApiResponse<ImageState>> ReadDiskImageAsync(string imagePath) => Task.Run(() =>
        {
            try
            {
                using var image = OpenDiskImage(imagePath);
                return ApiResponse<ImageState>.Ok(new(Convert.ToBoolean(image["Attached"] ?? false),
                    ImageDiskNumber(image["Number"]), image["ImagePath"]?.ToString() ?? imagePath));
            }
            catch (Exception error)
            {
                if (error is ManagementException wmi)
                    return ApiResponse<ImageState>.Fail(wmi.Message, (int)wmi.ErrorCode, ApiErrorSource.Wmi, wmi,
                        wmi.ErrorCode == ManagementStatus.AccessDenied ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
                if (error is System.Runtime.InteropServices.COMException com)
                    return ApiResponse<ImageState>.Fail(com.Message, com.HResult, ApiErrorSource.Com, com,
                        kind: ApiErrorKind.OperationFailed);
                var failure = LockFailure(error);
                return ApiResponse<ImageState>.Fail(failure.Error, failure.Code, failure.ErrorSource,
                    failure.Exception, failure.ErrorKind);
            }
        });

        internal static async Task<ApiResponse<ImageState>> ReadImageAsync(string imagePath)
        {
            var direct = await ReadDiskImageAsync(imagePath);
            if (!direct.Success || direct.HasData && direct.Data!.Attached) return direct;

            // WMI 实例可能登记了同一文件的另一条路径。只有排除已有别名挂载后，
            // 才能把后续 Attach 认作本次创建；否则清理可能卸掉调用前就存在的挂载。
            var mounted = await WmiApi.QueryCimAsync(
                "SELECT ImagePath, Attached, Number FROM MSFT_DiskImage WHERE Attached = TRUE",
                obj => new ImageState(Convert.ToBoolean(obj["Attached"] ?? false),
                    ImageDiskNumber(obj["Number"]), obj["ImagePath"]?.ToString()), WmiScope.Storage);
            if (!mounted.Success)
                return ApiResponse<ImageState>.Fail(mounted.Error, mounted.Code, mounted.ErrorSource,
                    mounted.Exception, mounted.ErrorKind);
            try
            {
                string resource = HostDiskResource.Image(imagePath);
                var matches = new List<ImageState>();
                foreach (var candidate in mounted.Data ?? new List<ImageState>())
                {
                    if (!candidate.Attached) continue;
                    if (string.IsNullOrWhiteSpace(candidate.ImagePath))
                        return ApiResponse<ImageState>.Fail("存在无法核对文件身份的已挂载映像，未提交新的挂载或卸载。",
                            kind: ApiErrorKind.Conflict);
                    if (HostDiskResource.Image(candidate.ImagePath) == resource) matches.Add(candidate);
                }
                if (matches.Select(item => item.Number).Distinct().Skip(1).Any())
                    return ApiResponse<ImageState>.Fail("同一映像文件对应多个不同挂载，无法确定唯一目标。",
                        kind: ApiErrorKind.Conflict);
                return matches.Count != 0 ? ApiResponse<ImageState>.Ok(matches[0]) : direct;
            }
            catch (Exception error)
            {
                var failure = LockFailure(error);
                return ApiResponse<ImageState>.Fail("无法排除已有别名挂载：" + failure.Error, failure.Code,
                    failure.ErrorSource, failure.Exception, failure.ErrorKind);
            }
        }

        private static int? ImageDiskNumber(object? value)
        {
            ulong number = value == null ? ulong.MaxValue : Convert.ToUInt64(value);
            return number <= int.MaxValue ? (int)number : null;
        }

        /// <summary>显式卸载宿主上的虚拟磁盘，并重新查询确认状态；等待其他 Core 使用者结束使用和清理。</summary>
        /// <param name="imagePath">待卸载的虚拟磁盘完整路径。</param>
        /// <returns>已确认未挂载时成功；查询、分离或状态确认失败均明确报告。</returns>
        /// <remarks>用于有意结束现存挂载。临时使用请释放 HostDiskMountLease，不要在持有同一映像使用期时调用此入口。</remarks>
        public static async Task<ApiResponse> DismountVhdxAsync(string imagePath)
        {
            try
            {
                return await WithImageLockAsync(imagePath, async () =>
                {
                    var state = await ReadImageAsync(imagePath);
                    if (!state.Success) return ApiResponse.Fail(state.Error, state.Code, state.ErrorSource,
                        state.Exception, state.ErrorKind);
                    if (state.HasData && state.Data!.Attached && state.Data.Number is int number)
                        return await WithDiskLockAsync(number, () => DismountVhdxCoreAsync(imagePath, number));
                    // 挂载作业可能尚未公布盘号；此时仍可在映像锁内解除本映像挂载。
                    return await DismountVhdxCoreAsync(imagePath);
                });
            }
            catch (Exception error) { return LockFailure(error); }
        }

        internal static Task<ApiResponse> DismountVhdxCoreAsync(string imagePath)
            => DismountVhdxCoreAsync(imagePath, expectedDiskNumber: null);

        internal static Task<ApiResponse> DismountVhdxCoreAsync(string imagePath, int? expectedDiskNumber)
            => DismountImageCoreAsync(imagePath, expectedDiskNumber, true);

        private static Task<ApiResponse> DismountImageCoreAsync(string imagePath, int? expectedDiskNumber, bool hyperV) => Task.Run(async () =>
        {
            HostResourceLock.AssertHeld(HostDiskResource.Image(imagePath));
            if (expectedDiskNumber.HasValue)
                HostResourceLock.AssertHeld(HostDiskResource.Disk(expectedDiskNumber.Value));
            try
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
                var before = await ReadImageAsync(imagePath);
                if (!before.Success) return ApiResponse.Fail(before.Error, before.Code, before.ErrorSource, before.Exception, before.ErrorKind);
                if (!before.HasData || !before.Data!.Attached) return ApiResponse.Ok();
                if (before.Data.Number != expectedDiskNumber)
                    return ApiResponse.Fail("等待期间映像挂载盘号已变化或尚未确认，未卸载替代挂载。",
                        kind: ApiErrorKind.CleanupRequired);
                string attachedPath = before.Data.ImagePath ?? imagePath;
                HostResourceLock.AssertHeld(HostDiskResource.Image(attachedPath));
                if (!hyperV)
                {
                    using var image = OpenDiskImage(attachedPath);
                    var result = await WmiApi.InvokeOnObjectAsync(image, "Dismount", scope: WmiScope.Storage);
                    if (!result.Success) return result;
                    var remaining = await ReadImageAsync(imagePath);
                    return remaining.Success && (!remaining.HasData || !remaining.Data!.Attached) ? ApiResponse.Ok()
                        : ApiResponse.Fail(remaining.Success ? "卸载后仍检测到挂载。" : remaining.Error,
                            kind: ApiErrorKind.CleanupRequired);
                }
                var scope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                using var svc = WmiApi.GetImageManagementService();
                if (svc == null) return ApiResponse.Fail("未找到虚拟磁盘管理服务。", kind: ApiErrorKind.NotFound);
                using var input = svc.GetMethodParameters("FindMountedStorageImageInstance");
                input["CriterionType"] = (ushort)2;
                input["SelectionCriterion"] = attachedPath;
                using var output = svc.InvokeMethod("FindMountedStorageImageInstance", input, null);
                if (output == null || Convert.ToUInt32(output["ReturnValue"]) != 0 ||
                    output["Image"] is not string mountedPath || string.IsNullOrEmpty(mountedPath))
                    return ApiResponse.Fail("未能定位已挂载映像，未执行卸载。", kind: ApiErrorKind.OperationFailed);
                using var mounted = new ManagementObject(scope, new ManagementPath(mountedPath), null);
                mounted.Get();
                var detach = await WmiApi.InvokeOnObjectAsync(mounted, "DetachVirtualHardDisk");
                if (!detach.Success) return detach;
                var after = await ReadImageAsync(imagePath);
                return after.Success && (!after.HasData || !after.Data!.Attached) ? ApiResponse.Ok()
                    : ApiResponse.Fail(after.Success ? "卸载后仍检测到挂载。" : after.Error,
                        kind: ApiErrorKind.CleanupRequired);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.CleanupRequired);
            }
        });

        internal static Task<ApiResponse<HostDiskSnapshot>> ReadDiskSnapshotAsync(int diskNumber)
            => WmiApi.QueryFirstCimAsync(
                $"SELECT Number, UniqueId, SerialNumber, Size, IsOffline, IsReadOnly, IsSystem, IsBoot FROM MSFT_Disk WHERE Number = {diskNumber}",
                obj => new HostDiskSnapshot(Convert.ToInt32(obj["Number"]), obj["UniqueId"]?.ToString() ?? "",
                    obj["SerialNumber"]?.ToString()?.Trim() ?? "", Convert.ToInt64(obj["Size"]),
                    Convert.ToBoolean(obj["IsOffline"]), Convert.ToBoolean(obj["IsReadOnly"]),
                    Convert.ToBoolean(obj["IsSystem"]), Convert.ToBoolean(obj["IsBoot"])), WmiScope.Storage);

        internal static Task<ApiResponse> RestoreDiskAsync(HostDiskSnapshot original)
            => WithDiskLockAsync(original.Number, () => RestoreDiskCoreAsync(original));

        internal static Task<ApiResponse> RestoreDiskCoreAsync(HostDiskSnapshot original)
        {
            HostResourceLock.AssertHeld(HostDiskResource.Disk(original.Number));
            return RestoreDiskAsync(original, ReadDiskSnapshotAsync,
                SetDiskReadOnlyCoreAsync, SetDiskOfflineStatusCoreAsync);
        }

        // 恢复的是业务开始时的原值；期间外部改动不改变恢复目标。
        // 每次写入前仍复核设备身份，不能向同编号的替代设备写入。
        internal static async Task<ApiResponse> RestoreDiskAsync(HostDiskSnapshot original,
            Func<int, Task<ApiResponse<HostDiskSnapshot>>> read,
            Func<int, bool, Task<ApiResponse>> setReadOnly,
            Func<int, bool, Task<ApiResponse>> setOffline)
        {
            var current = await read(original.Number);
            if (!current.HasData || !original.SameDevice(current.Data!))
                return ApiResponse.Fail("磁盘身份已变化或无法确认，未对替代设备执行恢复。",
                    kind: ApiErrorKind.CleanupRequired);
            var failures = new List<string>();
            try
            {
                var readOnly = await setReadOnly(original.Number, original.IsReadOnly);
                if (readOnly.ErrorKind == ApiErrorKind.OutcomeUnknown) return readOnly;
                if (!readOnly.Success) failures.Add(readOnly.Error);
            }
            catch (Exception error) { failures.Add(error.Message); }
            current = await read(original.Number);
            if (!current.HasData || !original.SameDevice(current.Data!))
                return ApiResponse.Fail("恢复期间磁盘身份发生变化，停止后续修改。", kind: ApiErrorKind.CleanupRequired);
            try
            {
                var offline = await setOffline(original.Number, original.IsOffline);
                if (offline.ErrorKind == ApiErrorKind.OutcomeUnknown)
                    return ApiResponse.Fail(string.Join(Environment.NewLine, failures.Append(offline.Error)),
                        offline.Code, offline.ErrorSource, offline.Exception, offline.ErrorKind);
                if (!offline.Success) failures.Add(offline.Error);
            }
            catch (Exception error) { failures.Add(error.Message); }
            var restored = await read(original.Number);
            if (!restored.HasData || !original.SameDevice(restored.Data!) ||
                restored.Data!.IsOffline != original.IsOffline || restored.Data.IsReadOnly != original.IsReadOnly)
                failures.Add("恢复后的磁盘状态未通过核对。");
            return failures.Count == 0 ? ApiResponse.Ok() :
                ApiResponse.Fail(string.Join(Environment.NewLine, failures), kind: ApiErrorKind.CleanupRequired);
        }

        internal static Task<ApiResponse> ReleaseScanMountAsync(HostDiskMountResult mount,
            Func<string, Task<ApiResponse>> unmount)
            => mount.CreatedByCall ? unmount(mount.ImagePath)
                : Task.FromResult(mount.RequiresInspection
                    ? ApiResponse.Fail("挂载状态未知，请重新核对后清理。", kind: ApiErrorKind.OutcomeUnknown)
                    : ApiResponse.Ok());


        /// <summary>为宿主分区添加指定盘符访问路径。</summary>
        /// <returns>成功响应包含盘符；查询和修改失败保留错误。</returns>
        /// <param name="partition">查询时选中的分区身份，含所属磁盘；身份不匹配时拒绝修改。</param>
        /// <param name="driveLetter">单个盘符字母 A 至 Z，不带冒号或反斜杠。</param>
        public static async Task<ApiResponse<char>> AssignPartitionDriveLetterAsync(
            HostPartitionReference partition, char driveLetter)
        {
            if (partition is not { IsValid: true } || !char.IsAsciiLetter(driveLetter))
                return ApiResponse<char>.Fail("分区身份或盘符无效。", kind: ApiErrorKind.InvalidArgument);
            try
            {
                return await WithDiskLockAsync(partition.Disk.Number, async () =>
                {
                    var result = await ModifyPartitionAccessPathAsync(partition, driveLetter, true);
                    return result.Success ? ApiResponse<char>.Ok(char.ToUpperInvariant(driveLetter)) : ApiResponse<char>.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);
                });
            }
            catch (Exception error)
            {
                var failure = LockFailure(error);
                return ApiResponse<char>.Fail(failure.Error, failure.Code, failure.ErrorSource,
                    failure.Exception, failure.ErrorKind);
            }
        }

        internal static async Task<ApiResponse<char>> AssignPartitionDriveLetterCoreAsync(
            int diskNumber, int partitionNumber, char driveLetter)
        {
            HostResourceLock.AssertHeld(HostDiskResource.Disk(diskNumber));
            driveLetter = char.ToUpperInvariant(driveLetter);
            var result = await WmiApi.InvokeAsync(
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}",
                "AddAccessPath", p => p["AccessPath"] = $"{driveLetter}:\\", WmiScope.Storage);
            return result.Success ? ApiResponse<char>.Ok(driveLetter) :
                ApiResponse<char>.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);
        }

        /// <summary>移除宿主分区的指定盘符访问路径，不删除分区。</summary>
        /// <returns>盘符移除结果；原分区不存在或已被替换时失败。</returns>
        /// <param name="partition">查询时选中的分区身份，含所属磁盘；身份不匹配时拒绝修改。</param>
        /// <param name="driveLetter">要移除的盘符字母。</param>
        public static async Task<ApiResponse> RemovePartitionAccessPathAsync(
            HostPartitionReference partition, char driveLetter)
        {
            if (partition is not { IsValid: true } || !char.IsAsciiLetter(driveLetter))
                return ApiResponse.Fail("分区身份或盘符无效。", kind: ApiErrorKind.InvalidArgument);
            try
            {
                return await WithDiskLockAsync(partition.Disk.Number, async () =>
                {
                    var result = await ModifyPartitionAccessPathAsync(partition, driveLetter, false);
                    return result;
                });
            }
            catch (Exception error) { return LockFailure(error); }
        }

        internal static async Task<ApiResponse> RemovePartitionAccessPathCoreAsync(
            int diskNumber, int partitionNumber, char driveLetter)
        {
            HostResourceLock.AssertHeld(HostDiskResource.Disk(diskNumber));
            var part = await WmiApi.QueryFirstCimAsync(
                $"SELECT PartitionNumber FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}",
                obj => Convert.ToInt32(obj["PartitionNumber"]), WmiScope.Storage);
            if (!part.Success) return ApiResponse.Fail(part.Error, part.Code, part.ErrorSource, part.Exception, part.ErrorKind);
            if (!part.HasData) return ApiResponse.Ok();
            return await WmiApi.InvokeAsync(
                $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}",
                "RemoveAccessPath", p => p["AccessPath"] = $"{char.ToUpperInvariant(driveLetter)}:\\", WmiScope.Storage);
        }


        internal static Task<T> WithDiskLockAsync<T>(int diskNumber, Func<Task<T>> action,
            CancellationToken cancellationToken = default)
            => HostResourceLock.RunAsync(HostDiskResource.Disk(diskNumber), action, cancellationToken);

        internal static async Task<T> WithImageLockAsync<T>(string imagePath, Func<Task<T>> action,
            CancellationToken cancellationToken = default)
        {
            // 文件可能位于网络路径；身份查询也不能阻塞调用方的界面线程。
            string resource = await Task.Run(() => HostDiskResource.Image(imagePath), cancellationToken).ConfigureAwait(false);
            return await HostResourceLock.RunAsync(resource, async () =>
            {
                if (!string.Equals(resource, HostDiskResource.Image(imagePath), StringComparison.Ordinal))
                    throw new IOException("等待期间映像文件身份发生变化，未操作替代文件。");
                return await action();
            }, cancellationToken).ConfigureAwait(false);
        }

        internal static ApiResponse LockFailure(Exception error)
        {
            ApiErrorKind kind = error switch
            {
                AbandonedMutexException or AggregateException => ApiErrorKind.CleanupRequired,
                UnauthorizedAccessException => ApiErrorKind.AccessDenied,
                Win32Exception { NativeErrorCode: 5 } => ApiErrorKind.AccessDenied,
                Win32Exception { NativeErrorCode: 2 or 3 } => ApiErrorKind.NotFound,
                Win32Exception { NativeErrorCode: 1 or 50 or 87 } => ApiErrorKind.Unsupported,
                NotSupportedException => ApiErrorKind.Unsupported,
                InvalidOperationException or IOException => ApiErrorKind.Conflict,
                _ => ApiErrorKind.OperationFailed
            };
            return ApiResponse.Fail(error.Message, error is Win32Exception native ? native.NativeErrorCode : error.HResult,
                error is Win32Exception ? ApiErrorSource.Win32 : ApiErrorSource.None, error, kind);
        }

    }
}
