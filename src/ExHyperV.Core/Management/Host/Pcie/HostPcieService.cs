using System.Diagnostics;
using ExHyperV.Tools;
using ExHyperV.Models;

namespace ExHyperV.Services
{


    /// <summary>PCIe 存储控制器名下的物理磁盘，用于直通前确认能否脱机。</summary>
    /// <param name="Number">宿主磁盘编号。</param>
    /// <param name="FriendlyName">磁盘显示名称。</param>
    /// <param name="IsSystem">是否包含系统分区。</param>
    /// <param name="IsBoot">是否为启动磁盘。</param>
    /// <param name="IsOffline">是否已经脱机。</param>
    public readonly record struct ControllerDisk(int Number, string FriendlyName, bool IsSystem, bool IsBoot, bool IsOffline)
    {
        // 磁盘号会复用；执行时用内部身份快照验证预览中的同一块盘。
        internal HostDiskSnapshot? Snapshot { get; init; }
    }

    // 真实系统和隔离回归检查共用同一协调流程；替身只替换实际设备读写。
    internal sealed record PcieControllerDiskBackend(
        Func<string, Task<ApiResponse<List<ControllerDisk>>>> Read,
        Func<int, bool, Task<ApiResponse>> SetOffline,
        Func<HostDiskSnapshot, Task<ApiResponse>> Restore);

    internal static class PcieControllerDiskOperation
    {
        internal static string Resource(string instanceId) => "0:pcie-controller:" + instanceId.ToUpperInvariant();

        internal static async Task<ApiResponse> ExecuteAsync(string instanceId,
            IReadOnlyList<ControllerDisk>? expected, bool offlineApproved, bool leavingHost,
            Func<Task<ApiResponse>> transfer, PcieControllerDiskBackend backend,
            Func<Task<ApiResponse>>? validateAssignment = null, bool transferRequired = true)
        {
            try
            {
                return await HostResourceLock.RunAsync(Resource(instanceId), async () =>
                {
                    var discovered = await backend.Read(instanceId);
                    if (!discovered.HasData) return ReadFailure(discovered);
                    var beforeLock = discovered.Data!.ToArray();
                    Task<ApiResponse> Run() => ExecuteUnderLockAsync(instanceId, beforeLock, expected,
                        offlineApproved, leavingHost, transfer, backend, validateAssignment, transferRequired);
                    // 控制器还没有可见磁盘时仍保有控制器锁，并在执行前再查一次关联。
                    return beforeLock.Length == 0 ? await Run() :
                        await HostResourceLock.RunAsync(beforeLock.Select(d => HostDiskResource.Disk(d.Number)), Run);
                });
            }
            catch (Exception error)
            {
                return HostDiskService.LockFailure(error);
            }
        }

        private static async Task<ApiResponse> ExecuteUnderLockAsync(string instanceId, ControllerDisk[] beforeLock,
            IReadOnlyList<ControllerDisk>? expected, bool offlineApproved, bool leavingHost,
            Func<Task<ApiResponse>> transfer, PcieControllerDiskBackend backend,
            Func<Task<ApiResponse>>? validateAssignment, bool transferRequired)
        {
            HostResourceLock.AssertHeld(Resource(instanceId));
            foreach (var disk in beforeLock) HostResourceLock.AssertHeld(HostDiskResource.Disk(disk.Number));
            var read = await backend.Read(instanceId);
            if (!read.HasData) return ReadFailure(read);
            var originals = read.Data!.ToArray();
            if (!SameDisks(beforeLock, originals) || (expected != null && !SameDisks(expected, originals)))
                return Changed();
            if (leavingHost && originals.Any(IsCritical))
                return ApiResponse.Fail("控制器关联了系统或启动磁盘，未执行设备转移。", kind: ApiErrorKind.InvalidState);
            // 审批只覆盖预览时列出的联机盘，不能把后来恢复联机的盘悄悄加入。
            if (expected != null && originals.Any(d => !d.IsOffline &&
                    (!offlineApproved || !expected.Any(e => e.Number == d.Number && !e.IsOffline))))
                return ApiResponse.Fail("待脱机磁盘已变化，请重新确认后分配。", kind: ApiErrorKind.Conflict);

            if (validateAssignment != null)
            {
                var identity = await validateAssignment().ConfigureAwait(false);
                if (!identity.Success) return identity;
            }
            if (!transferRequired) return ApiResponse.Ok();

            var touched = new List<HostDiskSnapshot>();
            bool transferStarted = false;
            ApiResponse outcome = ApiResponse.Ok();
            try
            {
                if (expected != null)
                {
                    foreach (var disk in originals.Where(d => !d.IsOffline))
                    {
                        read = await backend.Read(instanceId);
                        if (!read.HasData) { outcome = ReadFailure(read); break; }
                        if (!SameDisks(originals, read.Data!) || read.Data!.Any(IsCritical))
                        { outcome = Changed(); break; }
                        touched.Add(disk.Snapshot!); // 部分副作用后失败也必须恢复。
                        outcome = await backend.SetOffline(disk.Number, true);
                        if (!outcome.Success) break;
                    }
                }
                if (outcome.Success)
                {
                    // 外部工具不遵循锁；转移前最后核对一次归属及关键标志。
                    read = await backend.Read(instanceId);
                    if (!read.HasData) outcome = ReadFailure(read);
                    else if (!SameDisks(originals, read.Data!) || (leavingHost && read.Data!.Any(IsCritical))) outcome = Changed();
                    else
                    {
                        transferStarted = true;
                        outcome = await transfer();
                    }
                }
            }
            catch (CoreOperationException error) { outcome = error.Result; }
            catch (Exception error)
            {
                outcome = ApiResponse.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed);
            }
            if (outcome.Success || outcome.ErrorKind == ApiErrorKind.OutcomeUnknown) return outcome;

            var errors = new List<string>();
            // 设备转移本身也可能影响状态；失败后恢复执行入口采集的全部关联盘。
            var restore = transferStarted ? originals.Select(d => d.Snapshot!) : touched;
            foreach (var original in restore)
            {
                try
                {
                    var restored = await backend.Restore(original);
                    if (restored.ErrorKind == ApiErrorKind.OutcomeUnknown)
                        return ApiResponse.Fail(outcome.Error + "\n" + restored.Error, restored.Code,
                            restored.ErrorSource, restored.Exception, ApiErrorKind.OutcomeUnknown);
                    if (!restored.Success) errors.Add($"磁盘 #{original.Number} 恢复失败：{restored.Error}");
                }
                catch (Exception error) { errors.Add($"磁盘 #{original.Number} 恢复失败：{error.Message}"); }
            }
            return errors.Count == 0 ? outcome : ApiResponse.Fail(outcome.Error + "\n" + string.Join("\n", errors),
                exception: outcome.Exception, kind: ApiErrorKind.CleanupRequired);
        }

        internal static async Task<ApiResponse> OnlineAsync(string instanceId, ControllerDisk expected,
            PcieControllerDiskBackend backend)
        {
            try
            {
                return await HostResourceLock.RunAsync(new[] { Resource(instanceId), HostDiskResource.Disk(expected.Number) }, async () =>
                {
                    var read = await backend.Read(instanceId);
                    if (!read.HasData) return ReadFailure(read);
                    var matching = read.Data!.Where(d => d.Number == expected.Number).ToArray();
                    if (matching.Length != 1 || !SameDisks(new[] { expected }, matching) || IsCritical(matching[0]))
                        return Changed();
                    return await backend.SetOffline(expected.Number, false);
                });
            }
            catch (Exception error) { return HostDiskService.LockFailure(error); }
        }

        private static bool IsCritical(ControllerDisk disk) => disk.IsSystem || disk.IsBoot ||
            disk.Snapshot is { IsSystem: true } or { IsBoot: true };

        private static bool SameDisks(IReadOnlyList<ControllerDisk> expected, IReadOnlyList<ControllerDisk> actual) =>
            expected.Count == actual.Count && expected.Select(d => d.Number).Distinct().Count() == expected.Count &&
            actual.Select(d => d.Number).Distinct().Count() == actual.Count && expected.All(e =>
                e.Snapshot != null && actual.Any(a => a.Number == e.Number && a.Snapshot != null && e.Snapshot.SameDevice(a.Snapshot)));

        private static ApiResponse Changed() => ApiResponse.Fail(
            "控制器关联磁盘或设备身份已变化，请重新查询后操作；未对替代设备执行修改。", kind: ApiErrorKind.Conflict);
        private static ApiResponse ReadFailure(ApiResponse<List<ControllerDisk>> response) => ApiResponse.Fail(
            string.IsNullOrEmpty(response.Error) ? "无法确认控制器关联磁盘。" : response.Error,
            response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
    }

    /// <summary>读取宿主 PCIe/DDA 分配状态并执行设备转移。</summary>
    /// <remarks>变更需要管理员、Hyper-V 和设备管理权限，可能关闭 VM、禁用宿主设备或改变设备归属。交互式操作优先使用 PcieAssignmentSession 获取确认步骤。</remarks>
    public static class HostPcieService
    {
        // 字符串标记仅供内部执行流程使用；公开契约由 PcieAssignmentTarget 表示归属。
        // 内部入口仍归一化旧版本产生的中英文快照。
        /// <summary>表示设备归属宿主的固定标识。</summary>
        internal static readonly string HostKey = "\u200BHost";
        /// <summary>设备已从宿主卸除且未分配给 VM 的稳定状态值。</summary>
        internal static readonly string DismountedKey = "\u200BDismounted";

        internal static string NormalizeAssignmentTarget(string target) =>
            IsLegacyResourceValue(target, "Host", "\u200B") ? HostKey : target;

        internal static string NormalizeAssignmentStatus(string status)
        {
            status = NormalizeAssignmentTarget(status);
            return IsLegacyResourceValue(status, "Status_Dismounted") ? DismountedKey : status;
        }

        internal static string GetAssignmentTargetKey(PcieAssignmentTarget target)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (target.Kind == PcieAssignmentKind.Unassigned)
                throw new ArgumentException("PCIe 分配目标必须是宿主或虚拟机；未分配只能表示当前状态。", nameof(target));
            return target.ToInternalStatus();
        }

        /// <summary>将设备分配身份转换为本地化显示文本，不用于反向识别身份。</summary>
        /// <returns>宿主/已卸除的本地化名称，或 VM 名称。</returns>
        /// <param name="status">分配状态身份，可为 HostKey、DismountedKey 或 VM 名称。</param>
        internal static string GetAssignmentDisplayName(string status)
        {
            status = NormalizeAssignmentStatus(status);
            return status == HostKey ? Properties.Resources.Host
                : status == DismountedKey ? Properties.Resources.Status_Dismounted : status;
        }

        private static bool IsLegacyResourceValue(string value, string resourceName, string prefix = "") =>
            value == prefix + Properties.Resources.ResourceManager.GetString(resourceName,
                System.Globalization.CultureInfo.GetCultureInfo("en-US")) ||
            value == prefix + Properties.Resources.ResourceManager.GetString(resourceName,
                System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));

        private static string GetPureId(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return string.Empty;
            int idx = instanceId.IndexOf(@"\VEN_", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? instanceId.Substring(idx) : instanceId;
        }

        private readonly record struct PciListEntry(
            PciDeviceInfo Metadata,
            string InstanceId,
            string Path,
            string Status);

        /// <summary>将 PCI LocationPath 的最后一个功能号归零，用于关联同一多功能设备的各功能。</summary>
        /// <returns>同卡匹配键；空输入返回空字符串。</returns>
        /// <param name="locationPath">设备的 PCI LocationPath。</param>
        internal static string CardKey(string? locationPath)
        {
            if (string.IsNullOrEmpty(locationPath)) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(
                locationPath, @"#PCI\(([0-9A-Fa-f]{2})[0-9A-Fa-f]{2}\)$", "#PCI(${1}00)");
        }

        /// <summary>查询指定 PCIe 存储控制器关联的宿主物理磁盘。</summary>
        /// <returns>直接连接到控制器的磁盘及系统、启动、脱机标志；无匹配时为空列表，查询失败返回错误。</returns>
        /// <param name="controllerInstanceId">控制器的完整 PnP InstanceId。</param>
        public static async Task<ApiResponse<List<ControllerDisk>>> GetControllerDisksAsync(string controllerInstanceId)
        {
            var result = new List<ControllerDisk>();
            if (string.IsNullOrEmpty(controllerInstanceId)) return ApiResponse<List<ControllerDisk>>.Fail("控制器 PnP 身份不能为空。", kind: ApiErrorKind.InvalidArgument);

            // 该控制器名下的磁盘号（磁盘 PnP 父级 == 控制器）
            var ddResp = await WmiApi.QueryAsync(
                "SELECT Index, PNPDeviceID FROM Win32_DiskDrive",
                obj => new { Number = Convert.ToInt32(obj["Index"] ?? -1), Pnp = obj["PNPDeviceID"]?.ToString() ?? "" },
                WmiScope.CimV2);
            if (!ddResp.Success) return ApiResponse<List<ControllerDisk>>.Fail(ddResp.Error, ddResp.Code, ddResp.ErrorSource, ddResp.Exception, ddResp.ErrorKind);

            var diskNumbers = new HashSet<int>();
            foreach (var d in ddResp.Data ?? [])
            {
                if (d.Number < 0 || string.IsNullOrEmpty(d.Pnp)) continue;
                if (string.Equals(Win32Api.GetDeviceParent(d.Pnp), controllerInstanceId, StringComparison.OrdinalIgnoreCase))
                    diskNumbers.Add(d.Number);
            }
            if (diskNumbers.Count == 0) return ApiResponse<List<ControllerDisk>>.Ok(result);

            // 这些磁盘的系统/启动/在线状态
            var mdResp = await WmiApi.QueryCimAsync(
                "SELECT Number, FriendlyName, UniqueId, SerialNumber, Size, IsSystem, IsBoot, IsOffline, IsReadOnly FROM MSFT_Disk",
                obj => new ControllerDisk(
                    Convert.ToInt32(obj["Number"] ?? -1),
                    obj["FriendlyName"]?.ToString() ?? string.Empty,
                    Convert.ToBoolean(obj["IsSystem"] ?? false),
                    Convert.ToBoolean(obj["IsBoot"] ?? false),
                    Convert.ToBoolean(obj["IsOffline"] ?? false))
                {
                    Snapshot = new HostDiskSnapshot(Convert.ToInt32(obj["Number"]),
                        obj["UniqueId"]?.ToString() ?? "", obj["SerialNumber"]?.ToString()?.Trim() ?? "",
                        Convert.ToInt64(obj["Size"]), Convert.ToBoolean(obj["IsOffline"]),
                        Convert.ToBoolean(obj["IsReadOnly"]), Convert.ToBoolean(obj["IsSystem"]),
                        Convert.ToBoolean(obj["IsBoot"]))
                },
                WmiScope.Storage);
            if (!mdResp.Success) return ApiResponse<List<ControllerDisk>>.Fail(mdResp.Error, mdResp.Code, mdResp.ErrorSource, mdResp.Exception, mdResp.ErrorKind);
            result.AddRange((mdResp.Data ?? []).Where(x => diskNumbers.Contains(x.Number)));
            if (result.Count != diskNumbers.Count)
                return ApiResponse<List<ControllerDisk>>.Fail("控制器关联磁盘尚未完成枚举，请重新查询后分配。", kind: ApiErrorKind.Conflict);
            return ApiResponse<List<ControllerDisk>>.Ok(result);
        }

        /// <summary>读取宿主 PCIe 设备、当前归属与可选虚拟机目标。</summary>
        /// <returns>设备与稳定 GUID 目标快照；显式 WMI 查询失败返回错误，原生 PnP 枚举的部分失败仍可能表现为设备缺项或空清单。</returns>
        public static Task<ApiResponse<PcieHostInfo>> GetPCIeInfoAsync() => ReadPcieInfoAsync(false);

        private static async Task<ApiResponse<PcieHostInfo>> ReadPcieInfoAsync(bool requireCompleteAssignments)
        {
            var deviceList = new List<PcieDeviceInfo>();
            var vmTargets = new List<PcieAssignmentTarget>();

            return await Task.Run(async () =>
            {
                try
                {
                    var pciInfoProvider = new PciIds();
                    await pciInfoProvider.EnsureInitializedAsync();
                    var vmDeviceAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    var vmResp = await WmiApi.QueryAsync(
                        "SELECT Name, ElementName FROM Msvm_ComputerSystem",
                        obj => (Id: obj["Name"]?.ToString(), Name: obj["ElementName"]?.ToString()), WmiScope.HyperV);
                    if (!vmResp.Success) return ApiResponse<PcieHostInfo>.Fail(vmResp.Error, vmResp.Code, vmResp.ErrorSource, vmResp.Exception, vmResp.ErrorKind);
                    foreach (var vm in vmResp.Data ?? [])
                        if (Guid.TryParse(vm.Id, out var id) && id != Guid.Empty)
                            vmTargets.Add(PcieAssignmentTarget.ForVirtualMachine(id, vm.Name));

                    // WMI 替换 Get-VMAssignableDevice：
                    // Msvm_ComputerSystem → Msvm_VirtualSystemSettingData(Realized) → Msvm_PciExpressSettingData → HostResource
                    foreach (var target in vmTargets)
                    {
                        var settingResp = await WmiApi.QueryAsync(
                            $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData " +
                            $"WHERE {VmIdentity.SettingsPredicate(target.VirtualMachineId!.Value)}",
                            obj => obj["InstanceID"]?.ToString() ?? string.Empty,
                            WmiScope.HyperV);
                        if (!settingResp.Success) return ApiResponse<PcieHostInfo>.Fail(settingResp.Error, settingResp.Code, settingResp.ErrorSource, settingResp.Exception, settingResp.ErrorKind);
                        if (!settingResp.HasData || settingResp.Data.Count == 0)
                            return ApiResponse<PcieHostInfo>.Fail("查询期间虚拟机配置已变化，请重新读取 PCIe 归属。", kind: ApiErrorKind.Conflict);
                        if (requireCompleteAssignments && settingResp.Data.Any(string.IsNullOrWhiteSpace))
                            return ApiResponse<PcieHostInfo>.Fail("无法确认虚拟机配置身份，未执行 PCIe 分配。", kind: ApiErrorKind.InvalidState);

                        foreach (var settingId in (settingResp.Data ?? []).Where(s => !string.IsNullOrEmpty(s)))
                        {
                            string escapedSettingId = WmiApi.Escape(settingId);
                            var pciResp = await WmiApi.QueryAsync(
                                $"SELECT HostResource FROM Msvm_PciExpressSettingData " +
                                $"WHERE InstanceID LIKE '{escapedSettingId}\\\\%'",
                                obj => obj["HostResource"] is string[] hr && hr.Length > 0 ? hr[0] : null,
                                WmiScope.HyperV);

                            if (!pciResp.Success) return ApiResponse<PcieHostInfo>.Fail(pciResp.Error, pciResp.Code, pciResp.ErrorSource, pciResp.Exception, pciResp.ErrorKind);
                            if (requireCompleteAssignments && (pciResp.Data ?? []).Any(string.IsNullOrWhiteSpace))
                                return ApiResponse<PcieHostInfo>.Fail("PCIe 设备资源引用缺失，无法确认当前归属。", kind: ApiErrorKind.InvalidState);
                            foreach (var hostResource in (pciResp.Data ?? []).Where(r => r != null))
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(
                                    hostResource!, @"DeviceID=""Microsoft:[^\\]+\\\\(.+?)""");
                                if (!match.Success)
                                {
                                    if (requireCompleteAssignments)
                                        return ApiResponse<PcieHostInfo>.Fail("无法解析 PCIe 设备资源引用，未执行分配。", kind: ApiErrorKind.InvalidState);
                                    continue;
                                }
                                string rawId = match.Groups[1].Value.Replace("\\\\", "\\");
                                string pureId = GetPureId(rawId);
                                if (!string.IsNullOrEmpty(pureId))
                                {
                                    if (requireCompleteAssignments && vmDeviceAssignments.TryGetValue(pureId, out var owner)
                                        && !owner.Equals(target.ToInternalStatus(), StringComparison.OrdinalIgnoreCase))
                                        return ApiResponse<PcieHostInfo>.Fail("同一 PCIe 设备出现多个虚拟机归属，未执行分配。", kind: ApiErrorKind.AmbiguousTarget);
                                    vmDeviceAssignments[pureId] = target.ToInternalStatus();
                                }
                                else if (requireCompleteAssignments)
                                    return ApiResponse<PcieHostInfo>.Fail("PCIe 设备资源身份无效，未执行分配。", kind: ApiErrorKind.InvalidState);
                            }
                        }
                    }

                    // ── 2. 枚举所有 PCI 设备（Win32Api，替换 Get-PnpDevice）──────────
                    var allPciDevices = await Task.Run(() => Win32Api.GetAllDevices());
                    var childrenByParent = allPciDevices
                        .Where(d => !string.IsNullOrWhiteSpace(d.ParentInstanceId))
                        .GroupBy(d => d.ParentInstanceId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => g.ToList(),
                            StringComparer.OrdinalIgnoreCase);

                    // ── 3. 按设备身份合并 PCI/PCIP 节点并判定当前归属 ──
                    // Service 是功能驱动名，不是设备存在性或 DDA 可用性的依据；未装驱动时合法为空。
                    // PnP 在位状态判定宿主设备，WMI 判定 VM 归属，在位 PCIP 判定“已卸除”。
                    foreach (var entry in ResolvePciListEntries(allPciDevices, vmDeviceAssignments))
                    {
                        PciDeviceInfo pciDev = entry.Metadata;
                        string vendor = pciInfoProvider.GetVendorFromInstanceId(
                            pciDev.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                                ? pciDev.InstanceId
                                : entry.InstanceId,
                            pciDev.Manufacturer);
                        string classType = pciDev.Class;
                        string displayClassType = classType;

                        // System 类常被总线/复合设备驱动用于 PCIe 父节点，无法体现下层功能。
                        // 只在界面中附加真实存在的非 PCI 后代类别；原始 ClassType、直通路径、
                        // 名称、图标和排序均保持父设备本身。遇到另一个 PCI 节点即停止，
                        // 避免把可独立分配的下游 PCIe 设备归入当前父设备。
                        if (string.Equals(classType, "System", StringComparison.OrdinalIgnoreCase))
                        {
                            var childClasses = GetNonPciDescendants(pciDev.InstanceId, childrenByParent)
                                .Where(d => d.IsPresent
                                    && !string.IsNullOrWhiteSpace(d.Class)
                                    && !string.Equals(d.Class, classType, StringComparison.OrdinalIgnoreCase))
                                .Select(d => d.Class)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            if (childClasses.Count > 0)
                            {
                                string childClassList = string.Join(
                                    Properties.Resources.Common_ClassListSeparator,
                                    childClasses);
                                displayClassType = string.Format(
                                    Properties.Resources.PCIePage_ClassWithChildTypes,
                                    classType,
                                    childClassList);
                            }
                        }

                        deviceList.Add(new PcieDeviceInfo
                        {
                            FriendlyName = string.IsNullOrWhiteSpace(pciDev.FriendlyName)
                                ? entry.InstanceId
                                : pciDev.FriendlyName,
                            Assignment = vmTargets.FirstOrDefault(target => target.ToInternalStatus() == entry.Status)
                                ?? PcieAssignmentTarget.FromInternalStatus(entry.Status),
                            ClassType = classType,
                            DisplayClassType = displayClassType,
                            InstanceId = entry.InstanceId,
                            Path = entry.Path,
                            Vendor = vendor
                        });
                    }

                    // 按类型排序
                    deviceList.Sort((a, b) =>
                    {
                        static int GetOrder(PcieDeviceInfo d)
                        {
                            string cls = d.ClassType ?? "";
                            string name = d.FriendlyName ?? "";
                            if (cls.Equals("Display", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("显卡", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Graphics", StringComparison.OrdinalIgnoreCase))
                                return 0;
                            // NPU/AI 加速器：紧随显卡置于顶部，但不抢显卡的最前位（当前能直通、尚不可用）
                            if (cls.Equals("ComputeAccelerator", StringComparison.OrdinalIgnoreCase))
                                return 1;
                            if (name.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("声音", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("Media", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("Sound", StringComparison.OrdinalIgnoreCase))
                                return 2;
                            if (cls.Equals("Net", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("NetClient", StringComparison.OrdinalIgnoreCase))
                                return 3;
                            if (cls.Equals("USB", StringComparison.OrdinalIgnoreCase))
                                return 4;
                            if (cls.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("HDC", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase))
                                return 5;
                            return 6;
                        }
                        return GetOrder(a).CompareTo(GetOrder(b));
                    });
                    return ApiResponse<PcieHostInfo>.Ok(new(deviceList.AsReadOnly(), vmTargets.AsReadOnly()));
                }
                catch (Exception ex)
                {
                    return ApiResponse<PcieHostInfo>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
                }
            });
        }

        /// <summary>
        /// 按设备实例身份合并 PCI（宿主设备）与 PCIP（Hyper-V 可分配设备）。
        /// PureId 是状态判断的身份；LocationPath 仅作为 DDA 操作参数，不能用于把换卡后的历史节点
        /// 与当前设备合并。非在位且没有 VM 分配记录的节点只是 PnP 历史记录，不生成列表项。
        /// </summary>
        private static List<PciListEntry> ResolvePciListEntries(
            IReadOnlyList<PciDeviceInfo> allDevices,
            IReadOnlyDictionary<string, string> vmDeviceAssignments)
        {
            static bool IsPci(PciDeviceInfo d) =>
                d.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase);

            static bool IsPcip(PciDeviceInfo d) =>
                d.InstanceId.StartsWith("PCIP\\", StringComparison.OrdinalIgnoreCase);

            static int MetadataScore(PciDeviceInfo d)
            {
                int score = 0;
                if (!string.IsNullOrWhiteSpace(d.FriendlyName)) score += 8;
                if (!string.IsNullOrWhiteSpace(d.Class)) score += 4;
                if (!string.IsNullOrWhiteSpace(d.Manufacturer)) score += 2;
                if (!string.IsNullOrWhiteSpace(d.Service)
                    && !string.Equals(d.Service, "pcip", StringComparison.OrdinalIgnoreCase)) score++;
                return score;
            }

            static string? GetPciRootPath(PciDeviceInfo? d) =>
                d?.LocationPaths.FirstOrDefault(p =>
                    p.StartsWith("PCIROOT", StringComparison.OrdinalIgnoreCase));

            static string GetPciOperationInstanceId(string pureId, IEnumerable<PciDeviceInfo> devices)
            {
                // 优先使用系统实际记录的 PCI InstanceId，避免从非标准 ID 猜测。
                string? recordedPciId = devices
                    .Where(IsPci)
                    .OrderByDescending(d => d.IsPresent)
                    .ThenByDescending(MetadataScore)
                    .Select(d => d.InstanceId)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(recordedPciId)) return recordedPciId;

                // 标准 PCI/PCIP ID 的 PureId 从“\VEN_”开始；PCIP 仅有当前节点时需要还原
                // DismountSettingData/PNP Enable 使用的 PCI 前缀。
                if (pureId.StartsWith("\\", StringComparison.Ordinal))
                    return "PCI" + pureId;
                if (pureId.StartsWith("PCIP\\", StringComparison.OrdinalIgnoreCase))
                    return "PCI\\" + pureId[5..];
                if (pureId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
                    return pureId;
                return string.Empty;
            }

            var deviceGroups = allDevices
                .Where(d => IsPcip(d)
                    || (IsPci(d)
                        && !string.Equals(d.Service, "pci", StringComparison.OrdinalIgnoreCase)))
                .Select(d => (Device: d, PureId: GetPureId(d.InstanceId)))
                .Where(x => !string.IsNullOrWhiteSpace(x.PureId))
                .GroupBy(x => x.PureId, StringComparer.OrdinalIgnoreCase);

            var entries = new List<PciListEntry>();
            foreach (var deviceGroup in deviceGroups)
            {
                string pureId = deviceGroup.Key;
                var devices = deviceGroup.Select(x => x.Device).ToList();

                var presentPci = devices
                    .Where(d => IsPci(d) && d.IsPresent)
                    .OrderByDescending(MetadataScore)
                    .FirstOrDefault();

                var presentPcip = devices
                    .Where(d => IsPcip(d) && d.IsPresent)
                    .OrderByDescending(MetadataScore)
                    .FirstOrDefault();

                vmDeviceAssignments.TryGetValue(pureId, out string? assignedVm);

                // 状态机只有三种可见状态；其余节点都是历史记录：
                // WMI 有精确身份的分配记录 → VM
                // 当前 PCI 在位                 → 主机
                // 当前 PCIP 在位                → 已卸除
                // 均不满足                      → 拔除/换卡后的历史节点，隐藏
                string status;
                if (!string.IsNullOrEmpty(assignedVm))
                    status = assignedVm;
                else if (presentPci != null)
                    status = HostKey;
                else if (presentPcip != null)
                    status = DismountedKey;
                else
                    continue;

                // 路径只取当前节点；VM 分配状态下当前节点可能已转为 phantom，才允许使用同身份的
                // 历史 PCI/PCIP 路径。没有 PCIROOT 路径的设备不能执行 DDA，不展示不可操作的行。
                string? path = GetPciRootPath(presentPci)
                    ?? GetPciRootPath(presentPcip)
                    ?? (!string.IsNullOrEmpty(assignedVm)
                        ? devices.Select(GetPciRootPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
                        : null);
                if (string.IsNullOrWhiteSpace(path)) continue;

                PciDeviceInfo metadata = presentPci
                    ?? devices.Where(IsPci).OrderByDescending(MetadataScore).FirstOrDefault()
                    ?? presentPcip
                    ?? devices.OrderByDescending(MetadataScore).First();

                string operationInstanceId = GetPciOperationInstanceId(pureId, devices);
                if (string.IsNullOrWhiteSpace(operationInstanceId)) continue;

                entries.Add(new PciListEntry(
                    metadata,
                    operationInstanceId,
                    path,
                    status));
            }

            return entries;
        }

        private static IEnumerable<PciDeviceInfo> GetNonPciDescendants(
            string rootInstanceId,
            IReadOnlyDictionary<string, List<PciDeviceInfo>> childrenByParent)
        {
            if (string.IsNullOrWhiteSpace(rootInstanceId)) yield break;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                rootInstanceId
            };
            var pending = new Queue<string>();
            pending.Enqueue(rootInstanceId);

            while (pending.Count > 0)
            {
                string parentId = pending.Dequeue();
                if (!childrenByParent.TryGetValue(parentId, out var children)) continue;

                foreach (var child in children)
                {
                    if (!visited.Add(child.InstanceId)) continue;

                    // 下一个 PCI/PCIP 节点拥有自己的直通边界，不属于当前父设备的逻辑功能。
                    if (child.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                        || child.InstanceId.StartsWith("PCIP\\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    yield return child;
                    pending.Enqueue(child.InstanceId);
                }
            }
        }


        /// <summary>按当前归属和稳定目标执行 PCIe 设备卸除、分配或归还。</summary>
        /// <param name="target">目标宿主或带非空 GUID 的虚拟机；不能使用 Unassigned。</param>
        /// <param name="current">查询得到的设备归属；执行时重新核对，已经变化则返回 Conflict。</param>
        /// <param name="instanceId">设备的完整 PnP InstanceId，应与 path 来自同一设备查询结果。</param>
        /// <param name="path">同一设备的 DDA LocationPath；不得与另一设备的 InstanceId 拼接。</param>
        /// <param name="progress">可选的人类可读进度接收器。</param>
        /// <returns>步骤执行结果，失败可能发生在部分步骤已完成后；应刷新设备实际归属。</returns>
        /// <remarks>
        /// <para>分配给 VM 时将宿主关机动作设为 TurnOff；GuestControlledCacheTypes 关闭时，先强制关闭 VM 再启用该设置。已完成的 VM 配置和电源变化会保留。卸除宿主设备时，RequireAcsSupport 与 RequireDeviceMitigations 均为 false。</para>
        /// <para>同一控制器及关联盘的操作跨 Core 进程排队。联机数据盘须先脱机。失败且原归属或目标为宿主时，尝试归还宿主并恢复同一磁盘的原联机、只读状态，覆盖期间的外部修改。OutcomeUnknown 时先核实，恢复失败为 CleanupRequired。归还后的设备归属通过重新查询确认。</para>
        /// </remarks>
        public static Task<ApiResponse> ExecutePCIeOperationAsync(PcieAssignmentTarget target,
            PcieAssignmentTarget current, string instanceId, string path, IProgress<string>? progress = null)
        {
            string targetKey = GetAssignmentTargetKey(target);
            ArgumentNullException.ThrowIfNull(current);
            return ExecutePCIeOperationAsync(targetKey, current.ToInternalStatus(), instanceId, path, progress);
        }

        internal static ApiResponse ValidateAssignmentKeys(string targetKey, string currentKey)
        {
            static bool IsVm(string key) => Guid.TryParse(key, out var id) && id != Guid.Empty;
            if (targetKey != HostKey && !IsVm(targetKey))
                return ApiResponse.Fail("PCIe 目标必须是宿主或虚拟机 GUID，不能使用旧显示名称。", kind: ApiErrorKind.InvalidArgument);
            if (currentKey != HostKey && currentKey != DismountedKey && !IsVm(currentKey))
                return ApiResponse.Fail("PCIe 当前虚拟机归属缺少有效 GUID，请重新查询。", kind: ApiErrorKind.InvalidArgument);
            return ApiResponse.Ok();
        }

        internal static Task<ApiResponse> ExecutePCIeOperationAsync(string targetKey, string currentKey,
            string instanceId, string path, IProgress<string>? progress = null)
            => ExecuteCoordinatedAsync(targetKey, currentKey, instanceId, path, null, false, progress);

        internal static Task<ApiResponse> ExecuteStorageAssignmentAsync(string targetKey, string currentKey,
            string instanceId, string path, IReadOnlyList<ControllerDisk> expectedDisks, bool offlineApproved)
            => ExecuteCoordinatedAsync(targetKey, currentKey, instanceId, path, expectedDisks, offlineApproved);

        private static async Task<ApiResponse> ExecuteCoordinatedAsync(string targetKey, string currentKey,
            string instanceId, string path, IReadOnlyList<ControllerDisk>? expectedDisks,
            bool offlineApproved, IProgress<string>? progress = null)
        {
            targetKey = NormalizeAssignmentTarget(targetKey);
            currentKey = NormalizeAssignmentStatus(currentKey);
            var valid = ValidateAssignmentKeys(targetKey, currentKey);
            if (!valid.Success) return valid;
            if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(path))
                return ApiResponse.Fail("设备 PnP 身份和位置路径不能为空。", kind: ApiErrorKind.InvalidArgument);
            return await PcieControllerDiskOperation.ExecuteAsync(instanceId, expectedDisks, offlineApproved,
                currentKey == HostKey && targetKey != HostKey,
                () => ExecutePCIeOperationCoreAsync(targetKey, currentKey, instanceId, path, progress),
                ControllerDiskBackend, () => ValidateAssignmentAsync(currentKey, instanceId, path),
                !string.Equals(targetKey, currentKey, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<ApiResponse> ValidateAssignmentAsync(string currentKey, string instanceId, string path)
        {
            var snapshot = await ReadPcieInfoAsync(true).ConfigureAwait(false);
            if (!snapshot.HasData)
                return ApiResponse.Fail(snapshot.Error, snapshot.Code, snapshot.ErrorSource,
                    snapshot.Exception, snapshot.Success ? ApiErrorKind.InvalidState : snapshot.ErrorKind);
            return ValidateAssignmentSnapshot(snapshot.Data!.Devices, currentKey, instanceId, path);
        }

        internal static ApiResponse ValidateAssignmentSnapshot(IEnumerable<PcieDeviceInfo> devices,
            string currentKey, string instanceId, string path)
        {
            var entries = devices.ToArray();
            var matches = entries.Where(device =>
                string.Equals(device.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                return ApiResponse.Fail("PCIe 设备不存在或身份不唯一，请重新查询。",
                    kind: matches.Length == 0 ? ApiErrorKind.NotFound : ApiErrorKind.AmbiguousTarget);
            var match = matches[0];
            if (!string.Equals(match.Path, path, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(match.Status, currentKey, StringComparison.OrdinalIgnoreCase)
                || entries.Count(device => string.Equals(device.Path, path, StringComparison.OrdinalIgnoreCase)) != 1)
                return ApiResponse.Fail("PCIe 设备的位置或当前归属已变化，未执行转移。", kind: ApiErrorKind.Conflict);
            return ApiResponse.Ok();
        }

        private static readonly PcieControllerDiskBackend ControllerDiskBackend = new(
            GetControllerDisksAsync, HostDiskService.SetDiskOfflineStatusCoreAsync, HostDiskService.RestoreDiskCoreAsync);

        internal static Task<ApiResponse> OnlineReturnedControllerDiskAsync(string instanceId, ControllerDisk disk)
            => PcieControllerDiskOperation.OnlineAsync(instanceId, disk, ControllerDiskBackend);

        private static async Task<ApiResponse> ExecutePCIeOperationCoreAsync(string targetKey, string currentKey,
            string instanceId, string path, IProgress<string>? progress)
        {
            HostResourceLock.AssertHeld(PcieControllerDiskOperation.Resource(instanceId));
            targetKey = NormalizeAssignmentTarget(targetKey);
            currentKey = NormalizeAssignmentStatus(currentKey);
            var valid = ValidateAssignmentKeys(targetKey, currentKey);
            if (!valid.Success) return valid;
            if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(path))
                return ApiResponse.Fail("设备 PnP 身份和位置路径不能为空。", kind: ApiErrorKind.InvalidArgument);
            bool deviceOperationStarted = false;
            try
            {
                var identity = await ValidateAssignmentAsync(currentKey, instanceId, path).ConfigureAwait(false);
                if (!identity.Success) return identity;
                foreach (string key in new[] { targetKey, currentKey }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Guid.TryParse(key, out var vmId)) continue;
                    var vm = await WmiApi.QueryFirstAsync(
                        $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}", obj => obj["Name"]?.ToString());
                    if (!vm.Success) return ApiResponse.Fail(vm.Error, vm.Code, vm.ErrorSource, vm.Exception, vm.ErrorKind);
                    if (!vm.HasData) return ApiResponse.Fail("PCIe 分配涉及的虚拟机已不存在，请重新查询。", kind: ApiErrorKind.NotFound);
                }
                if (string.Equals(targetKey, currentKey, StringComparison.OrdinalIgnoreCase)) return ApiResponse.Ok();
                var operations = PCIeCommands(targetKey, instanceId, path, currentKey);
                if (operations.Any(operation => operation.RequiresVmOff))
                {
                    progress?.Report(Properties.Resources.Msg_PCIe_ShuttingDownVm);
                    var stopped = await VmPowerService.EnsureStoppedForDeviceChangeAsync(Guid.Parse(targetKey));
                    if (!stopped.Success) return stopped;
                }
                foreach (var operation in operations)
                {
                    progress?.Report(operation.Message);
                    deviceOperationStarted = true;
                    var outcome = await ExecuteOperationAsync(operation, instanceId);
                    if (!outcome.Success)
                    {
                        if (outcome.ErrorKind == ApiErrorKind.OutcomeUnknown) return outcome;
                        var restored = await RollbackToHostIfNeededAsync(targetKey, currentKey, instanceId, path);
                        return restored.Success ? outcome : ApiResponse.Fail(outcome.Error + "\n设备恢复失败：" + restored.Error,
                            restored.Code, restored.ErrorSource, restored.Exception,
                            restored.ErrorKind == ApiErrorKind.OutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);
                    }
                }
                return ApiResponse.Ok();
            }
            catch (CoreOperationException ex) when (ex.Result.ErrorKind == ApiErrorKind.OutcomeUnknown)
            {
                return ex.Result;
            }
            catch (Exception ex)
            {
                if (deviceOperationStarted)
                {
                    try
                    {
                        var restored = await RollbackToHostIfNeededAsync(targetKey, currentKey, instanceId, path);
                        if (!restored.Success)
                            return ApiResponse.Fail($"{ex.Message}\n设备恢复失败：{restored.Error}", restored.Code,
                                restored.ErrorSource, ex,
                                restored.ErrorKind == ApiErrorKind.OutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);
                    }
                    catch (Exception cleanup)
                    {
                        return ApiResponse.Fail($"{ex.Message}\n设备恢复失败：{cleanup.Message}",
                            exception: new AggregateException(ex, cleanup), kind: ApiErrorKind.CleanupRequired);
                    }
                }
                return ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed);
            }
        }

        // 失败回滚：仅当操作前设备在主机（主机→VM，即 currentVmName==HostKey），或目标就是主机（→主机方向）时，
        // 把设备挂载并启用回主机，避免中途失败（尤其 Dismount 之后 AddDevice 失败）把设备卡在“已卸除”、
        // 主机和虚拟机都访问不到。只有确认挂回成功才启用；挂回失败或结果不明必须报告。
        // VM→VM、卸除→VM 原始态非主机，保持“已卸除”（可手动再分配恢复），不强行拉回。
        private static async Task<ApiResponse> RollbackToHostIfNeededAsync(string targetVmName, string currentVmName, string instanceId, string path)
        {
            if (currentVmName != HostKey && targetVmName != HostKey) return ApiResponse.Ok();
            var mounted = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_AssignableDeviceService",
                "MountAssignableDevice",
                p => p["DeviceLocationPath"] = path,
                WmiScope.HyperV);
            return mounted.Success ? Win32Api.EnablePnpDevice(instanceId) : mounted;
        }


        // ── PCIe 操作类型 ──────────────────────────────────────────────
        private enum PCIeOpType { Wmi, WmiSilent, PnpEnable, PnpDisable }

        private record PCIeOperation(
            string Message,
            PCIeOpType Type,
            Func<Task<ApiResponse>>? WmiAction = null,
            bool RequiresVmOff = false);   // true=该步必须 VM Off（目前仅 SetGuestCache），据此决定执行前是否先关机

        private static async Task<ApiResponse> ExecuteOperationAsync(PCIeOperation operation, string instanceId)
        {
            switch (operation.Type)
            {
                case PCIeOpType.PnpEnable: return Win32Api.EnablePnpDevice(instanceId);
                case PCIeOpType.PnpDisable: return Win32Api.DisablePnpDevice(instanceId);
                case PCIeOpType.Wmi: return await operation.WmiAction!();
                case PCIeOpType.WmiSilent:
                    var result = await operation.WmiAction!();
                    // 兼容设备的已确认失败可以忽略；未结束的任务不能当成已完成再启用设备。
                    if (result.ErrorKind == ApiErrorKind.OutcomeUnknown) return result;
                    await Task.Delay(1000);
                    return ApiResponse.Ok();
                default: return ApiResponse.Fail("未知 PCIe 操作类型。", kind: ApiErrorKind.InvalidArgument);
            }
        }

        private static List<PCIeOperation> PCIeCommands(string targetKey, string instanceId, string path, string currentKey)
        {
            var ops = new List<PCIeOperation>();

            // WmiSilent：某些设备不支持标准 Mount，已确认的失败沿用兼容处理；结果未知必须停止。
            PCIeOperation MountDeviceSilent(string locationPath) => new(
                Properties.Resources.Status_MountingDevice, PCIeOpType.WmiSilent,
                WmiAction: () => WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_AssignableDeviceService",
                    "MountAssignableDevice",
                    p => { p["DeviceLocationPath"] = locationPath; },
                    WmiScope.HyperV));

            PCIeOperation AddDevice(string devInstanceId, string locationPath, Guid vmId) => new(
                Properties.Resources.Status_MountingDevice, PCIeOpType.Wmi,
                WmiAction: async () =>
                {
                    var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);

                    // 拿 PciExpress Default 模板
                    using var templateSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            "SELECT * FROM Msvm_PciExpressSettingData WHERE InstanceID LIKE '%Default%'"));
                    using var templateCol = templateSearcher.Get();
                    using var template = templateCol.Cast<System.Management.ManagementObject>().FirstOrDefault();
                    if (template is null) return ApiResponse.Fail("Cannot find PciExpress Default template");

                    // 刚 Dismount 完，可分配设备注册可能滞后，重试几次避免偶发查不到（取出 __PATH 字符串即可，不留 COM 对象）
                    string escapedLocationPath = WmiApi.Escape(locationPath);
                    string? pcipPath = null;
                    for (int attempt = 0; attempt < 5 && string.IsNullOrEmpty(pcipPath); attempt++)
                    {
                        if (attempt > 0) await Task.Delay(500);
                        using var pcipSearcher = new System.Management.ManagementObjectSearcher(ms,
                            new System.Management.ObjectQuery(
                                $"SELECT * FROM Msvm_PciExpress WHERE LocationPath='{escapedLocationPath}'"));
                        using var pcipCol = pcipSearcher.Get();
                        using var pcipDevice = pcipCol.Cast<System.Management.ManagementObject>().FirstOrDefault();
                        pcipPath = pcipDevice?["__PATH"]?.ToString();
                    }
                    if (string.IsNullOrEmpty(pcipPath)) return ApiResponse.Fail($"Cannot find PciExpress device at: {locationPath}");

                    template["HostResource"] = new string[] { pcipPath };

                    // 拿 VM VirtualSystemSettingData 路径
                    using var vmSettingSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}"));
                    using var vmSettingCol = vmSettingSearcher.Get();
                    using var vmSetting = vmSettingCol.Cast<System.Management.ManagementObject>().FirstOrDefault();
                    if (vmSetting is null) return ApiResponse.Fail($"Cannot find VM setting: {vmId:D}");

                    // AddResourceSettings
                    return await WmiApi.InvokeAsync(
                        "SELECT * FROM Msvm_VirtualSystemManagementService",
                        "AddResourceSettings",
                        p => {
                            p["AffectedConfiguration"] = vmSetting["__PATH"]?.ToString();
                            p["ResourceSettings"] = new string[] { template.GetText(System.Management.TextFormat.CimDtd20) };
                        },
                        WmiScope.HyperV);
                });

            PCIeOperation DismountDevice(string devInstanceId, string locationPath) => new(
                Properties.Resources.Dismountdevice, PCIeOpType.Wmi,
                WmiAction: () => WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_AssignableDeviceService",
                    "DismountAssignableDevice",
                    p => {
                        var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                        using var cls = new System.Management.ManagementClass(ms, new System.Management.ManagementPath("Msvm_AssignableDeviceDismountSettingData"), null);
                        using var inst = cls.CreateInstance();
                        inst["DeviceInstancePath"] = devInstanceId;
                        inst["DeviceLocationPath"] = locationPath;
                        inst["RequireAcsSupport"] = false;
                        inst["RequireDeviceMitigations"] = false;
                        p["DismountSettingData"] = inst.GetText(System.Management.TextFormat.CimDtd20);
                    },
                    WmiScope.HyperV));

            PCIeOperation RemoveDevice(string devInstanceId, string locationPath, Guid vmId) => new(
                Properties.Resources.Dismountdevice, PCIeOpType.Wmi,
                WmiAction: async () =>
                {
                    var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);

                    // 拿 VM 的 Realized VirtualSystemSettingData
                    using var vmSettingSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}"));
                    using var vmSettingCol = vmSettingSearcher.Get();
                    using var vmSetting = vmSettingCol.Cast<System.Management.ManagementObject>().FirstOrDefault();
                    if (vmSetting is null) return ApiResponse.Fail($"Cannot find VM setting: {vmId:D}");

                    string settingId = vmSetting["InstanceID"]?.ToString() ?? "";
                    string escapedSettingId = WmiApi.Escape(settingId);

                    // 找到该 VM 下匹配 pureId 的 PciExpressSettingData
                    using var pciSettingSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            $"SELECT * FROM Msvm_PciExpressSettingData WHERE InstanceID LIKE '{escapedSettingId}\\\\%'"));
                    using var pciSettingCol = pciSettingSearcher.Get();

                    string pureId = GetPureId(devInstanceId);
                    System.Management.ManagementObject? targetSetting = null;
                    foreach (System.Management.ManagementObject obj in pciSettingCol)
                    {
                        if (obj["HostResource"] is string[] hr && hr.Length > 0
                            && hr[0].Contains(pureId.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase))
                        {
                            targetSetting = obj;
                            break;
                        }
                        obj.Dispose();
                    }
                    if (targetSetting is null) return ApiResponse.Fail($"Cannot find PciExpress setting for location: {locationPath}");

                    using (targetSetting)
                    {
                        // RemoveResourceSettings
                        return await WmiApi.InvokeAsync(
                            "SELECT * FROM Msvm_VirtualSystemManagementService",
                            "RemoveResourceSettings",
                            p => p["ResourceSettings"] = new string[] { targetSetting["__PATH"]?.ToString() ?? "" },
                            WmiScope.HyperV);
                    }
                });

            // AutomaticShutdownAction 可在虚拟机运行时修改。
            PCIeOperation SetAutoStop(Guid vmId) => new(
                Properties.Resources.PCIeService_SetShutdownToTurnOff, PCIeOpType.Wmi,
                WmiAction: () => WmiApi.WithObjectAsync(
                    $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
                    obj => obj["AutomaticShutdownAction"] = (ushort)2,
                    submitMethod: "ModifySystemSettings",
                    submitParamName: "SystemSettings",
                    wrapInArray: false,
                    scope: WmiScope.HyperV,
                    serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService"));

            // 此字段仅能在虚拟机关闭时修改。
            PCIeOperation SetGuestCache(Guid vmId) => new(
                Properties.Resources.Action_EnableCpuCacheControl, PCIeOpType.Wmi,
                WmiAction: () => WmiApi.WithObjectAsync(
                    $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
                    obj => obj["GuestControlledCacheTypes"] = true,
                    submitMethod: "ModifySystemSettings",
                    submitParamName: "SystemSettings",
                    wrapInArray: false,
                    scope: WmiScope.HyperV,
                    serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService"),
                RequiresVmOff: true);   // 写合并缓存是静态设置，必须 VM Off 才能改

            // 已启用 GuestControlledCacheTypes 时无需关机或重复设置。
            bool guestCacheAlreadySet = false;
            if (targetKey != HostKey)
            {
                var cacheResp = WmiApi.QueryAsync(
                    $"SELECT GuestControlledCacheTypes FROM Msvm_VirtualSystemSettingData " +
                    $"WHERE {VmIdentity.SettingsPredicate(Guid.Parse(targetKey))}",
                    obj => obj["GuestControlledCacheTypes"] is bool b && b,
                    WmiScope.HyperV).GetAwaiter().GetResult();
                if (!cacheResp.Success) throw new InvalidOperationException(cacheResp.Error);
                if (cacheResp.Data is not { Count: > 0 }) throw new InvalidOperationException("目标虚拟机配置已不存在。");
                guestCacheAlreadySet = cacheResp.Data.Any(x => x);
            }

            if (currentKey == DismountedKey && targetKey == HostKey)
            {
                // 已卸除 → 主机：Mount 静默处理，某些设备（核显/NPU）不支持标准 Mount 但实际可用
                ops.Add(MountDeviceSilent(path));
                ops.Add(new(Properties.Resources.Status_EnablingDevice, PCIeOpType.PnpEnable));
            }
            else if (currentKey == DismountedKey && targetKey != HostKey)
            {
                ops.Add(SetAutoStop(Guid.Parse(targetKey)));
                if (!guestCacheAlreadySet) ops.Add(SetGuestCache(Guid.Parse(targetKey)));
                ops.Add(AddDevice(instanceId, path, Guid.Parse(targetKey)));
            }
            else if (currentKey == HostKey)
            {
                ops.Add(SetAutoStop(Guid.Parse(targetKey)));
                if (!guestCacheAlreadySet) ops.Add(SetGuestCache(Guid.Parse(targetKey)));
                ops.Add(new(Properties.Resources.Disabledevice, PCIeOpType.PnpDisable));
                ops.Add(DismountDevice(instanceId, path));
                ops.Add(AddDevice(instanceId, path, Guid.Parse(targetKey)));
            }
            else if (targetKey != HostKey && currentKey != HostKey)
            {
                ops.Add(SetAutoStop(Guid.Parse(targetKey)));
                if (!guestCacheAlreadySet) ops.Add(SetGuestCache(Guid.Parse(targetKey)));
                ops.Add(RemoveDevice(instanceId, path, Guid.Parse(currentKey)));
                ops.Add(AddDevice(instanceId, path, Guid.Parse(targetKey)));
            }
            else if (targetKey == HostKey && currentKey != HostKey)
            {
                // VM → 主机：Mount 静默处理，对齐 PS 版本行为
                ops.Add(RemoveDevice(instanceId, path, Guid.Parse(currentKey)));
                ops.Add(MountDeviceSilent(path));
                ops.Add(new(Properties.Resources.Status_EnablingDevice, PCIeOpType.PnpEnable));
            }

            return ops;
        }
    }
}
