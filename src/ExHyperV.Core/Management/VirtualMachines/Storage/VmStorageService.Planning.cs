using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>控制器类型、从零开始的控制器编号和槽位。</summary>
/// <param name="ControllerType">控制器类型，IDE 或 SCSI。</param>
/// <param name="ControllerNumber">从零开始的控制器编号。</param>
/// <param name="Location">从零开始的控制器槽位。</param>
public sealed record VmStorageSlot(string ControllerType, int ControllerNumber, int Location);
/// <summary>新增设备的槽位选择结果；无可用槽位时携带说明。</summary>
/// <param name="Slot">选定槽位；无法分配时为 null。</param>
/// <param name="Error">错误说明；没有报告错误时为 null。</param>
public sealed record VmStorageSlotPlan(VmStorageSlot? Slot, string? Error = null);
/// <summary>按代数与物理设备限制规范化后的介质选择。</summary>
/// <param name="DriveType">规范化后的 HardDisk 或 DvdDrive。</param>
/// <param name="IsNew">是否先创建新介质。</param>
/// <param name="SupportsOptical">该来源和代数是否支持光驱选项。</param>
public sealed record VmStorageSourceChoice(string DriveType, bool IsNew, bool SupportsOptical);

/// <summary>新增存储设备的完整请求；可先准备校验，再交给 AddDriveAsync 执行。</summary>
/// <remarks>现有虚拟磁盘或 ISO 使用 PathOrNumber；空虚拟光驱允许空路径。新 VHD 使用容量、格式及可选父盘，新 ISO 使用来源目录和输出路径。物理硬盘必须提供 PhysicalDisk，物理光驱使用查询所得 PNP 标识；互斥来源参数会被拒绝。</remarks>
public sealed record VmStorageAddRequest
{
    internal string? ExpectedControllerId { get; init; }
    /// <summary>控制器类型字符串，默认 SCSI；也支持 IDE。</summary>
    public string ControllerType { get; init; } = "SCSI";
    /// <summary>从零开始的控制器编号；IDE 为 0 至 1，SCSI 为 0 至 3。</summary>
    public int ControllerNumber { get; init; }
    /// <summary>控制器内从零开始的槽位；IDE 为 0 至 1，SCSI 为 0 至 63。</summary>
    public int Location { get; init; }
    /// <summary>驱动类型字符串 HardDisk 或 DvdDrive，默认 HardDisk。</summary>
    public string DriveType { get; init; } = "HardDisk";
    /// <summary>介质文件路径或物理设备编号字符串；现有虚拟光驱可为空表示无介质。</summary>
    public string? PathOrNumber { get; init; }
    /// <summary>是否挂接宿主物理设备。</summary>
    public bool IsPhysical { get; init; }
    /// <summary>是否先创建新 VHD 或 ISO；物理设备来源不创建新介质。</summary>
    public bool IsNew { get; init; }
    /// <summary>新虚拟磁盘类型，默认 Dynamic；差分磁盘使用 Differencing。</summary>
    public string VhdType { get; init; } = "Dynamic";
    /// <summary>新建差分磁盘的父磁盘路径；其他模式可为 null。</summary>
    public string? ParentPath { get; init; }
    /// <summary>制作 ISO 时的源目录；非新建光盘模式可为 null。</summary>
    public string? IsoSourcePath { get; init; }
    /// <summary>新建 ISO 的输出文件路径；其他模式可为 null。</summary>
    public string? IsoOutputPath { get; init; }
    /// <summary>新建非差分虚拟磁盘的容量，单位为 GiB（1024³ 字节）；其余来源必须为 null。</summary>
    public int? SizeGiB { get; init; }
    /// <summary>新虚拟磁盘扇区格式：Default、512n、512e 或 4kn；非新建虚拟磁盘仅允许 Default。</summary>
    public string SectorFormat { get; init; } = "Default";
    /// <summary>新虚拟磁盘的块大小，单位为字节；null 使用后端默认值，指定时必须大于零。</summary>
    public uint? BlockSizeBytes { get; init; }
    /// <summary>新 ISO 的卷标；null 使用制作器默认值，其他来源必须为 null。</summary>
    public string? IsoVolumeLabel { get; init; }
    /// <summary>GetHostDisksAsync 取得的物理硬盘快照；必须包含编号、正数字节容量及唯一标识或序列号。执行时重新核对所有已提供的身份字段和容量，不信任 CanPassthrough 显示标记。</summary>
    public HostDiskInfo? PhysicalDisk { get; init; }
}

/// <summary>存储请求的准备结果，区分可执行请求与校验或目录操作错误。</summary>
/// <param name="Request">校验后可执行的存储请求；准备失败时为 null。</param>
/// <param name="Error">错误说明；没有报告错误时为 null。</param>
/// <param name="IsError">是否属于准备目录等执行错误，而非普通输入校验提示。</param>
public sealed record VmStorageAddPreparation(VmStorageAddRequest? Request, string? Error = null, bool IsError = false)
{
    /// <summary>Request 不为 null，表示准备完成。</summary>
    public bool Success => Request != null;
}

internal sealed record PhysicalDiskState(bool IsSystem, bool IsBoot, bool IsOffline, bool IsReadOnly,
    string UniqueId, string SerialNumber, long SizeBytes);

/// <summary>读取与配置虚拟机存储挂接，处理虚拟磁盘、光驱和物理磁盘。</summary>
public static partial class VmStorageService
{
    /// <summary>将新磁盘容量文本解析为正整数 GiB。</summary>
    /// <param name="input">以 GiB（1024³ 字节）为单位的容量文本；null 或无效输入使用 128。</param>
    /// <returns>有效正数；为空、格式错误或非正数时返回 128。</returns>
    public static int GetNewDiskSize(string? input) => int.TryParse(input, out int size) && size > 0 ? size : 128;

    /// <summary>按代数、物理磁盘标记和介质类型规范化新增设备来源选项。</summary>
    /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
    /// <param name="physical">是否选择宿主物理设备来源。</param>
    /// <param name="driveType">驱动类型 HardDisk 或 DvdDrive。</param>
    /// <param name="isNew">是否先创建新 VHD 或 ISO；默认 false 表示复用现有来源。</param>
    /// <returns>允许的驱动类型、是否创建新介质以及是否支持光驱。</returns>
    public static VmStorageSourceChoice GetSourceChoice(int generation, bool physical, string driveType, bool isNew)
        => new(physical && generation != 1 && driveType == "DvdDrive" ? "HardDisk" : driveType,
            !physical && isNew, !physical || generation == 1);

    /// <summary>根据代数、运行状态和驱动类型列出允许添加的控制器类型。</summary>
    /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
    /// <param name="isRunning">当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。</param>
    /// <param name="driveType">驱动类型 HardDisk 或 DvdDrive。</param>
    /// <returns>IDE 或 SCSI 类型列表；仅计算规则，不创建控制器。</returns>
    public static IReadOnlyList<string> GetControllerTypes(int generation, bool isRunning, string driveType)
        => generation != 1 ? ["SCSI"] : driveType == "DvdDrive" ? ["IDE"]
            : isRunning ? ["SCSI"] : ["IDE", "SCSI"];

    /// <summary>返回指定控制器类型可选择的从零开始的编号。</summary>
    /// <param name="type">控制器类型 IDE 或 SCSI。</param>
    /// <returns>IDE 为 0、1，SCSI 为 0 至 3。</returns>
    public static IReadOnlyList<int> GetControllerNumbers(string type) => Enumerable.Range(0, type == "IDE" ? 2 : 4).ToArray();

    /// <summary>从已有挂接快照中排除占用位置，计算指定控制器的空闲槽位。</summary>
    /// <param name="type">控制器类型 IDE 或 SCSI。</param>
    /// <param name="number">从零开始的控制器编号。</param>
    /// <param name="items">当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。</param>
    /// <returns>从零开始的槽位列表；不会预留位置。</returns>
    public static IReadOnlyList<int> GetAvailableLocations(string type, int number, IEnumerable<VmStorageAttachment> items)
    {
        var used = items.Where(item => item.ControllerType == type && item.ControllerNumber == number)
            .Select(item => item.ControllerLocation).ToHashSet();
        return Enumerable.Range(0, type == "IDE" ? 2 : 64).Where(location => !used.Contains(location)).ToArray();
    }

    /// <summary>检查指定控制器是否仍有可用槽位。</summary>
    /// <param name="type">控制器类型 IDE 或 SCSI。</param>
    /// <param name="number">从零开始的控制器编号。</param>
    /// <param name="items">当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。</param>
    /// <returns>已满时返回说明，否则为 null。</returns>
    public static string? GetControllerFullError(string type, int number, IEnumerable<VmStorageAttachment> items)
        => GetAvailableLocations(type, number, items).Count == 0
            ? string.Format(Properties.Resources.Error_Storage_CtrlFull, type, number) : null;

    /// <summary>按当前规则和已有设备快照选择首个适合新增驱动的空闲槽位。</summary>
    /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
    /// <param name="isRunning">当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。</param>
    /// <param name="driveType">驱动类型 HardDisk 或 DvdDrive。</param>
    /// <param name="items">当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。</param>
    /// <returns>可用槽位，或没有槽位时的错误说明。</returns>
    public static VmStorageSlotPlan GetBestSlot(int generation, bool isRunning, string driveType, IEnumerable<VmStorageAttachment> items)
    {
        bool gen1 = generation == 1;
        bool dvd = driveType == "DvdDrive";
        if (gen1 && dvd && isRunning) return new(null, Properties.Resources.Error_Storage_Gen1Dvd);
        var snapshot = items.ToArray();
        foreach (string type in GetControllerTypes(generation, isRunning, driveType))
            foreach (int number in GetControllerNumbers(type))
            {
                var free = GetAvailableLocations(type, number, snapshot);
                if (free.Count > 0) return new(new(type, number, free[0]));
            }
        return new(null, gen1 && dvd ? Properties.Resources.Error_Storage_Gen1IdeFull
            : isRunning ? Properties.Resources.Error_Storage_NoScsiRunning
            : !gen1 ? Properties.Resources.Error_Storage_NoScsi : Properties.Resources.Error_Storage_NoSlots);
    }

    /// <summary>校验新增设备请求的控制器类型、编号、槽位及运行状态限制。</summary>
    /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
    /// <param name="isRunning">当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。</param>
    /// <param name="request">待检查的新增存储设备请求。</param>
    /// <param name="items">当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。</param>
    /// <returns>首个错误说明；符合当前快照时为 null。执行时需重新核对槽位。</returns>
    public static string? GetAddSlotError(int generation, bool isRunning, VmStorageAddRequest request,
        IEnumerable<VmStorageAttachment> items)
    {
        if (isRunning && (request.ControllerType == "IDE" || generation == 1 && request.DriveType == "DvdDrive"))
            return request.DriveType == "DvdDrive" ? Properties.Resources.Error_Storage_Gen1Dvd : Properties.Resources.Error_Storage_IdeHotAdd;
        if (items.Any(item => item.ControllerType == request.ControllerType && item.ControllerNumber == request.ControllerNumber
            && item.ControllerLocation == request.Location)) return Properties.Resources.Error_Storage_Occupied;
        if (!GetControllerTypes(generation, isRunning, request.DriveType).Contains(request.ControllerType)
            || !GetControllerNumbers(request.ControllerType).Contains(request.ControllerNumber))
            return Properties.Resources.Error_Storage_ControllerNotFound;
        if (request.Location < 0 || request.Location >= (request.ControllerType == "IDE" ? 2 : 64))
            return Properties.Resources.Error_Storage_NoSlots;
        return null;
    }

    // Mirrors the existing wizard's error order. The only preparation side effect is
    // creating a requested ISO output directory, previously done by the desktop.
    /// <summary>规范化并验证新增设备请求；ISO 制作路径需要时会创建输出目录。</summary>
    /// <param name="request">待检查的新增存储设备请求。</param>
    /// <param name="generation">虚拟机代数，使用 1 或 2。</param>
    /// <param name="isRunning">当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。</param>
    /// <param name="items">当前存储挂接快照，用于检测控制器和槽位占用；方法不会预留位置。</param>
    /// <returns>可执行请求或错误、提示状态；不会创建虚拟机设备，但可能创建宿主目录。</returns>
    public static VmStorageAddPreparation PrepareAddRequest(VmStorageAddRequest request,
        int generation, bool isRunning, IEnumerable<VmStorageAttachment> items)
    {
        var validation = ValidateAddRequest(request);
        if (!validation.Success) return new(null, validation.Error);
        if (GetAddSlotError(generation, isRunning, request, items) is string slotError) return new(null, slotError);
        return PrepareAddSource(request);
    }

    internal static VmStorageAddPreparation PrepareAddSource(VmStorageAddRequest request)
    {
        var validation = ValidateAddRequest(request);
        if (!validation.Success) return new(null, validation.Error);
        if (request.IsPhysical && request.DriveType == "HardDisk")
            request = request with { PathOrNumber = request.PhysicalDisk!.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        bool newIso = request.DriveType == "DvdDrive" && request.IsNew;
        bool emptyDvd = request.DriveType == "DvdDrive" && !request.IsPhysical && !request.IsNew
            && string.IsNullOrWhiteSpace(request.PathOrNumber);
        string? target = emptyDvd ? string.Empty : request.PathOrNumber;
        if (string.IsNullOrWhiteSpace(target) && !newIso && !emptyDvd)
            return new(null, Properties.Resources.Error_Storage_SelectTarget);
        if (!request.IsPhysical)
        {
            if (!request.IsNew && !emptyDvd && !File.Exists(target)) return new(null, Properties.Resources.Error_Storage_FileNotExist);
            if (request.IsNew && request.DriveType == "HardDisk" && File.Exists(target))
                return new(null, Properties.Resources.Error_Storage_FileExists);
        }
        if (request.IsNew && request.DriveType == "HardDisk" && request.VhdType == "Differencing"
            && (string.IsNullOrWhiteSpace(request.ParentPath) || !File.Exists(request.ParentPath)))
            return new(null, Properties.Resources.Error_Storage_ParentRequired);
        if (newIso)
        {
            if (string.IsNullOrWhiteSpace(request.IsoSourcePath)) return new(null, Properties.Resources.Error_Storage_IsoSource);
            if (string.IsNullOrWhiteSpace(request.IsoOutputPath)) return new(null, Properties.Resources.Error_Storage_IsoPath);
            target = request.IsoOutputPath;
            try
            {
                string? directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception error) { return new(null, string.Format(Properties.Resources.Error_Storage_DirFail, error.Message), true); }
            if (!Directory.Exists(request.IsoSourcePath)) return new(null, Properties.Resources.Error_Storage_SourceNoExist);
        }
        return new(request with { PathOrNumber = target });
    }

    internal static ApiResponse ValidateAddRequest(VmStorageAddRequest? request)
    {
        ApiResponse Invalid(string message) => ApiResponse.Fail(message, kind: ApiErrorKind.InvalidArgument);
        if (request is null) return Invalid("存储请求不能为空。");
        if (request.ControllerType is not ("IDE" or "SCSI") || request.DriveType is not ("HardDisk" or "DvdDrive"))
            return Invalid("控制器或驱动类型无效。");
        int controllers = request.ControllerType == "IDE" ? 2 : 4;
        int slots = request.ControllerType == "IDE" ? 2 : 64;
        if (request.ControllerNumber < 0 || request.ControllerNumber >= controllers || request.Location < 0 || request.Location >= slots)
            return Invalid("控制器编号或槽位超出范围。");
        bool newDisk = !request.IsPhysical && request.IsNew && request.DriveType == "HardDisk";
        bool newIso = !request.IsPhysical && request.IsNew && request.DriveType == "DvdDrive";
        if (request.IsPhysical && request.IsNew) return Invalid("物理设备不能同时请求创建新介质。");
        if (!newIso && (request.IsoSourcePath != null || request.IsoOutputPath != null || request.IsoVolumeLabel != null))
            return Invalid("ISO 来源目录、输出路径和卷标仅用于新建 ISO。");
        if (!newDisk && (request.SizeGiB != null || request.BlockSizeBytes != null || request.SectorFormat != "Default"
            || request.VhdType != "Dynamic" || !string.IsNullOrEmpty(request.ParentPath)))
            return Invalid("虚拟磁盘创建参数不能用于当前介质来源。");
        if (newDisk)
        {
            if (request.VhdType is not ("Dynamic" or "Fixed" or "Differencing")
                || request.SectorFormat is not ("Default" or "512n" or "512e" or "4kn") || request.BlockSizeBytes == 0)
                return Invalid("虚拟磁盘类型、扇区格式或块大小无效。");
            if (request.VhdType == "Differencing")
            {
                if (request.SizeGiB != null || string.IsNullOrWhiteSpace(request.ParentPath))
                    return Invalid("差分磁盘必须提供父磁盘，容量由父磁盘决定。");
            }
            else if (request.SizeGiB is not > 0 || !string.IsNullOrEmpty(request.ParentPath))
                return Invalid("新虚拟磁盘必须提供正数 GiB 容量，只有差分磁盘允许父磁盘路径。");
        }
        bool physicalDisk = request.IsPhysical && request.DriveType == "HardDisk";
        if (physicalDisk)
        {
            var disk = request.PhysicalDisk;
            if (disk == null || disk.Number < 0 || disk.SizeBytes <= 0
                || string.IsNullOrWhiteSpace(disk.UniqueId) && string.IsNullOrWhiteSpace(disk.SerialNumber))
                return Invalid("物理硬盘必须提供查询所得编号、正数容量及唯一标识或序列号，不能仅按编号挂接。");
            if (!string.IsNullOrWhiteSpace(request.PathOrNumber)
                && (!int.TryParse(request.PathOrNumber, out int number) || number != disk.Number))
                return Invalid("物理硬盘编号与所选磁盘快照不一致。");
        }
        else if (request.PhysicalDisk != null) return Invalid("物理硬盘快照不能用于其他介质来源。");
        if (request.IsPhysical && request.DriveType == "DvdDrive"
            && (string.IsNullOrWhiteSpace(request.PathOrNumber) || int.TryParse(request.PathOrNumber, out _)))
            return Invalid("物理光驱必须提供宿主查询所得的 PNP 标识。");
        if (newIso && !string.IsNullOrWhiteSpace(request.PathOrNumber)
            && !string.Equals(request.PathOrNumber, request.IsoOutputPath, StringComparison.OrdinalIgnoreCase))
            return Invalid("新 ISO 的介质路径与输出路径不能相互冲突。");
        return ApiResponse.Ok();
    }

    internal static HostDiskInfo? SnapshotPhysicalDisk(HostDiskInfo? disk) => disk == null ? null : new()
    {
        Number = disk.Number, SizeBytes = disk.SizeBytes,
        UniqueId = disk.UniqueId?.Trim() ?? string.Empty, SerialNumber = disk.SerialNumber?.Trim() ?? string.Empty
    };

    internal static bool MatchesPhysicalDisk(HostDiskInfo expected, string? uniqueId, string? serialNumber, long sizeBytes)
        => expected.Number >= 0 && expected.SizeBytes > 0 && expected.SizeBytes == sizeBytes
            && (!string.IsNullOrWhiteSpace(expected.UniqueId) || !string.IsNullOrWhiteSpace(expected.SerialNumber))
            && (string.IsNullOrWhiteSpace(expected.UniqueId) || string.Equals(expected.UniqueId.Trim(), uniqueId?.Trim(), StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(expected.SerialNumber) || string.Equals(expected.SerialNumber.Trim(), serialNumber?.Trim(), StringComparison.OrdinalIgnoreCase));

    internal static ApiResponse ValidatePhysicalDiskSelection(HostDiskInfo expected, PhysicalDiskState? actual)
    {
        if (actual == null) return ApiResponse.Fail("所选物理硬盘已不存在，请重新查询。", kind: ApiErrorKind.NotFound);
        if (actual.IsSystem || actual.IsBoot)
            return ApiResponse.Fail("系统盘或启动盘不能直通给虚拟机。", kind: ApiErrorKind.InvalidState);
        return MatchesPhysicalDisk(expected, actual.UniqueId, actual.SerialNumber, actual.SizeBytes)
            ? ApiResponse.Ok() : ApiResponse.Fail(Properties.Resources.Error_Storage_DiskChanged, kind: ApiErrorKind.Conflict);
    }

    internal static async Task<ApiResponse> RestorePhysicalDiskAsync(HostDiskInfo expected, bool readOnly, bool offline,
        Func<int, Task<ApiResponse<PhysicalDiskState>>> readState,
        Func<int, bool, Task<ApiResponse>> setReadOnly, Func<int, bool, Task<ApiResponse>> setOffline)
    {
        async Task<ApiResponse> VerifyIdentity()
        {
            var current = await readState(expected.Number);
            if (!current.Success) return ApiResponse.Fail(current.Error, kind: ApiErrorKind.CleanupRequired);
            var identity = ValidatePhysicalDiskSelection(expected, current.Data);
            return identity.Success ? ApiResponse.Ok()
                : ApiResponse.Fail("物理磁盘身份已变化，未对替换设备执行恢复。" + identity.Error, kind: ApiErrorKind.CleanupRequired);
        }
        try
        {
            var identity = await VerifyIdentity();
            if (!identity.Success) return identity;
            var readOnlyResult = await setReadOnly(expected.Number, readOnly);
            if (readOnlyResult.ErrorKind == ApiErrorKind.OutcomeUnknown) return readOnlyResult;
            identity = await VerifyIdentity();
            if (!identity.Success) return identity;
            var offlineResult = await setOffline(expected.Number, offline);
            if (offlineResult.ErrorKind == ApiErrorKind.OutcomeUnknown)
                return ApiResponse.Fail(string.Join(Environment.NewLine,
                    new[] { readOnlyResult.Error, offlineResult.Error }.Where(error => !string.IsNullOrEmpty(error))),
                    offlineResult.Code, offlineResult.ErrorSource, offlineResult.Exception, offlineResult.ErrorKind);
            return readOnlyResult.Success && offlineResult.Success ? ApiResponse.Ok()
                : ApiResponse.Fail($"物理磁盘状态未完全恢复：{readOnlyResult.Error} {offlineResult.Error}", kind: ApiErrorKind.CleanupRequired);
        }
        catch (Exception error)
        {
            return ApiResponse.Fail("物理磁盘状态恢复失败：" + error.Message, exception: error, kind: ApiErrorKind.CleanupRequired);
        }
    }

    /// <summary>判断该存储挂接是否支持更换文件路径。</summary>
    /// <param name="item">目标存储挂接；允许 null 的规则入口会返回不允许编辑。</param>
    /// <returns>非空且不是物理磁盘等不可换路径项时为 true。</returns>
    public static bool CanEditStoragePath(VmStorageAttachment? item) => item != null && item.DiskType != "Physical";

    /// <summary>检查指定设备在当前运行状态下能否更换介质路径。</summary>
    /// <param name="item">目标存储挂接；允许 null 的规则入口会返回不允许编辑。</param>
    /// <param name="isRunning">当前状态快照是否属于活动状态；规则方法仅使用此输入，不自行刷新主机状态。</param>
    /// <returns>限制说明；允许继续时为 null。</returns>
    public static string? GetEditStoragePathError(VmStorageAttachment item, bool isRunning)
        => !CanEditStoragePath(item) ? Properties.Resources.Error_Storage_PhysicalMod
            : item.DriveType == "HardDisk" && isRunning && item.ControllerType == "IDE"
                ? Properties.Resources.Error_Storage_VhdRunning : null;

    /// <summary>校验介质路径是否存在；空路径按弹出介质处理。</summary>
    /// <param name="newPath">要引用的介质文件路径；允许空值的光驱校验入口将其视为无介质。</param>
    /// <returns>非空但不存在的路径返回说明，否则为 null。</returns>
    public static string? GetMediaPathError(string? newPath)
        => !string.IsNullOrWhiteSpace(newPath) && !File.Exists(newPath) ? Properties.Resources.Error_Storage_FileNotExist : null;
}
