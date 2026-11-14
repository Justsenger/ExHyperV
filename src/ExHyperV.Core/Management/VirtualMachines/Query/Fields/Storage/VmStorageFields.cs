using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>存储的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmStorageFields
{
    /// <summary>挂接身份：存储挂接槽位的标识，用于关联本次查询中的设备记录。</summary>
    /// <fielddoc>
    /// <meaning>存储挂接槽位的标识，用于关联本次查询中的设备记录。</meaning>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 AttachmentId。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> AttachmentId = new("Storage.AttachmentId", "挂接身份", "存储", VmQueryCollection.Storage, "Storage", "AttachmentId");

    /// <summary>驱动器类型：挂接槽位的驱动器类型：硬盘或光驱。</summary>
    /// <fielddoc>
    /// <meaning>挂接槽位的驱动器类型：硬盘或光驱。</meaning>
    /// <values kind="options">HardDisk：硬盘；DvdDrive：光驱。光驱可以没有挂载介质。</values>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 DriveType。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="unavailable">驱动器创建时确定。<see cref="M:ExHyperV.Services.VmStorageService.AddDriveAsync(System.Guid,ExHyperV.Services.VmStorageAddRequest)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> DriveType = new("Storage.DriveType", "驱动器类型", "存储", VmQueryCollection.Storage, "Storage", "DriveType");

    /// <summary>挂接来源类型：槽位的介质来源类型：虚拟磁盘、物理磁盘或空槽位。</summary>
    /// <fielddoc>
    /// <meaning>槽位的介质来源类型：虚拟磁盘、物理磁盘或空槽位。</meaning>
    /// <values kind="options">Virtual：文件映像；Physical：物理设备；Empty：没有介质。</values>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 DiskType。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="unavailable">挂接时确定介质来源。<see cref="M:ExHyperV.Services.VmStorageService.AddDriveAsync(System.Guid,ExHyperV.Services.VmStorageAddRequest)"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> DiskType = new("Storage.DiskType", "挂接来源类型", "存储", VmQueryCollection.Storage, "Storage", "DiskType");

    /// <summary>挂接目标路径：槽位当前挂接的映像文件路径或宿主设备引用。</summary>
    /// <fielddoc>
    /// <meaning>槽位当前挂接的映像文件路径或宿主设备引用。</meaning>
    /// <values>空槽位返回空字符串。</values>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 Path。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="supported" input="drive.DeviceToken、newPath / newIsoPath" submission="所选驱动器介质替换">更新的是驱动器引用的介质路径，不移动文件。空路径表示退出介质；物理磁盘挂接不适用。<see cref="VmStorageService.ModifyHardDrivePathAsync"/>、<see cref="VmStorageService.ModifyDvdDrivePathAsync"/></modification>
    /// </fielddoc>
    public static readonly VmQueryField<string> Path = new("Storage.Path", "挂接目标路径", "存储", VmQueryCollection.Storage, "Storage", "Path");

    /// <summary>控制器类型：挂接槽位所属的控制器类型，例如 IDE 或 SCSI。</summary>
    /// <fielddoc>
    /// <meaning>挂接槽位所属的控制器类型。</meaning>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 ControllerType。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="unavailable">挂接时确定控制器类型。<see cref="M:ExHyperV.Services.VmStorageService.AddDriveAsync(System.Guid,ExHyperV.Services.VmStorageAddRequest)"/></modification>
    /// <values kind="format"><c>IDE</c> 或 <c>SCSI</c>。</values>
    /// </fielddoc>
    public static readonly VmQueryField<string> ControllerType = new("Storage.ControllerType", "控制器类型", "存储", VmQueryCollection.Storage, "Storage", "ControllerType");

    /// <summary>控制器编号：同类存储控制器中的编号，从 0 开始。</summary>
    /// <fielddoc>
    /// <meaning>同类存储控制器中的编号。</meaning>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 ControllerNumber。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="unavailable">挂接时确定控制器。<see cref="M:ExHyperV.Services.VmStorageService.AddDriveAsync(System.Guid,ExHyperV.Services.VmStorageAddRequest)"/></modification>
    /// <values kind="format">从 <c>0</c> 开始的整数编号。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> ControllerNumber = new("Storage.ControllerNumber", "控制器编号", "存储", VmQueryCollection.Storage, "Storage", "ControllerNumber");

    /// <summary>控制器槽位：设备在所属控制器上的槽位编号，从 0 开始。</summary>
    /// <fielddoc>
    /// <meaning>设备在所属控制器上的槽位编号。</meaning>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 ControllerLocation。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="unavailable">挂接时确定槽位。<see cref="M:ExHyperV.Services.VmStorageService.AddDriveAsync(System.Guid,ExHyperV.Services.VmStorageAddRequest)"/></modification>
    /// <values kind="format">从 <c>0</c> 开始的整数编号。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> ControllerLocation = new("Storage.ControllerLocation", "控制器槽位", "存储", VmQueryCollection.Storage, "Storage", "ControllerLocation");

    /// <summary>宿主物理磁盘编号：物理直通盘对应的宿主磁盘编号。</summary>
    /// <fielddoc>
    /// <meaning>物理直通盘对应的宿主磁盘编号。</meaning>
    /// <applicability>适用于物理直通盘。目标当前是否仍存在可读取 IsPassthroughStale。</applicability>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 DiskNumber。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="operation" submission="操作流程">宿主磁盘编号由系统分配。<see cref="M:ExHyperV.Services.VmStorageService.RemoveDriveAsync(System.Guid,ExHyperV.Models.VmStorageAttachment)"/>、<see cref="M:ExHyperV.Services.VmStorageService.AddDriveAsync(System.Guid,ExHyperV.Services.VmStorageAddRequest)"/></modification>
    /// <values kind="format">非负整数，例如 <c>2</c> 对应宿主的磁盘 2。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> DiskNumber = new("Storage.DiskNumber", "宿主物理磁盘编号", "存储", VmQueryCollection.Storage, "Storage", "DiskNumber");

    /// <summary>宿主磁盘型号：物理直通盘对应的宿主磁盘型号。</summary>
    /// <fielddoc>
    /// <meaning>物理直通盘对应的宿主磁盘型号。</meaning>
    /// <applicability>适用于物理直通盘。</applicability>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 DiskModel。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> DiskModel = new("Storage.DiskModel", "宿主磁盘型号", "存储", VmQueryCollection.Storage, "Storage", "DiskModel");

    /// <summary>宿主磁盘序列号：物理直通盘对应的宿主磁盘序列号。</summary>
    /// <fielddoc>
    /// <meaning>物理直通盘对应的宿主磁盘序列号。</meaning>
    /// <applicability>适用于物理直通盘；返回值已去除首尾空白。</applicability>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 SerialNumber。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> SerialNumber = new("Storage.SerialNumber", "宿主磁盘序列号", "存储", VmQueryCollection.Storage, "Storage", "SerialNumber");

    /// <summary>宿主磁盘设备身份：物理直通盘对应的宿主 PnP 设备标识。</summary>
    /// <fielddoc>
    /// <meaning>物理直通盘对应的宿主 PnP 设备标识。</meaning>
    /// <applicability>适用于物理直通盘。</applicability>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 PnpDeviceId。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> PnpDeviceId = new("Storage.PnpDeviceId", "宿主磁盘设备身份", "存储", VmQueryCollection.Storage, "Storage", "PnpDeviceId");

    /// <summary>物理磁盘挂接失效：当前物理磁盘挂接目标是否已从 Hyper-V 物理磁盘清单中消失。</summary>
    /// <fielddoc>
    /// <meaning>当前物理磁盘挂接目标是否已从 Hyper-V 物理磁盘清单中消失。</meaning>
    /// <applicability>适用于物理直通盘。</applicability>
    /// <source>当前配置的 RASD/SASD 控制器、槽位和介质关联；物理盘属性关联宿主磁盘，读取或派生 IsPassthroughStale。</source>
    /// <absence>无法读取宿主物理磁盘清单时返回该来源的失败状态。</absence>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="readonly" />
    /// <values kind="options"><c>true</c>：目标磁盘已不在宿主清单中；<c>false</c>：仍在清单中。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> IsPassthroughStale = new("Storage.IsPassthroughStale", "物理磁盘挂接失效", "存储", VmQueryCollection.Storage, "Storage", "IsPassthroughStale");

    /// <summary>磁盘虚拟容量：磁盘向来宾提供的容量，单位为字节；物理直通盘返回对应宿主磁盘容量。</summary>
    /// <fielddoc>
    /// <meaning>磁盘向来宾提供的容量，单位为字节；物理直通盘返回对应宿主磁盘容量。</meaning>
    /// <applicability>适用于虚拟磁盘和物理直通盘；空槽位返回 NotApplicable。</applicability>
    /// <source>磁盘映像设置与文件大小；同一路径在一次请求内复用，读取或派生 CapacityBytes。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="unavailable" />
    /// <values kind="format">单位为字节；例如 <c>1073741824</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> CapacityBytes = new("Storage.CapacityBytes", "磁盘虚拟容量", "存储", VmQueryCollection.Storage, "DiskFiles", "CapacityBytes");

    /// <summary>虚拟磁盘分配类型：虚拟磁盘的空间分配方式：固定磁盘预先分配容量，动态磁盘随写入增长，差分磁盘保存相对父盘的变化。</summary>
    /// <fielddoc>
    /// <meaning>虚拟磁盘的空间分配方式：固定磁盘预先分配容量，动态磁盘随写入增长，差分磁盘保存相对父盘的变化。</meaning>
    /// <applicability>适用于虚拟磁盘文件。</applicability>
    /// <source>磁盘映像设置与文件大小；同一路径在一次请求内复用，读取或派生 VirtualDiskType。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="unavailable" />
    /// </fielddoc>
    public static readonly VmQueryField<string> VirtualDiskType = new("Storage.VirtualDiskType", "虚拟磁盘分配类型", "存储", VmQueryCollection.Storage, "DiskFiles", "VirtualDiskType");

    /// <summary>磁盘文件大小：虚拟磁盘映像文件当前的长度，单位为字节。</summary>
    /// <fielddoc>
    /// <meaning>虚拟磁盘映像文件在宿主文件系统中的长度。</meaning>
    /// <applicability>适用于虚拟磁盘文件。</applicability>
    /// <source>磁盘映像设置与文件大小；同一路径在一次请求内复用，读取或派生 FileSizeBytes。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="readonly" />
    /// <values kind="format">单位为字节；例如 <c>1073741824</c> 表示 1 GiB。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> FileSizeBytes = new("Storage.FileSizeBytes", "磁盘文件大小", "存储", VmQueryCollection.Storage, "DiskFiles", "FileSizeBytes");

    /// <summary>磁盘读取速率：磁盘当前的读取速率，单位为字节每秒。</summary>
    /// <fielddoc>
    /// <meaning>磁盘当前的读取速率。</meaning>
    /// <source>VmQueryService 的磁盘性能采样，按磁盘身份关联，读取或派生 ReadBytesPerSecond。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="readonly" />
    /// <values kind="format">单位为字节/秒；例如 <c>1048576</c> 表示 1 MiB/s。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> ReadBytesPerSecond = new("Storage.ReadBytesPerSecond", "磁盘读取速率", "存储", VmQueryCollection.Storage, "DiskPerformance", "ReadBytesPerSecond");

    /// <summary>磁盘写入速率：磁盘当前的写入速率，单位为字节每秒。</summary>
    /// <fielddoc>
    /// <meaning>磁盘当前的写入速率。</meaning>
    /// <source>VmQueryService 的磁盘性能采样，按磁盘身份关联，读取或派生 WriteBytesPerSecond。</source>
    /// <references><see cref="VmStorageService"/>。</references>
    /// <modification kind="readonly" />
    /// <values kind="format">单位为字节/秒；例如 <c>1048576</c> 表示 1 MiB/s。</values>
    /// </fielddoc>
    public static readonly VmQueryField<long> WriteBytesPerSecond = new("Storage.WriteBytesPerSecond", "磁盘写入速率", "存储", VmQueryCollection.Storage, "DiskPerformance", "WriteBytesPerSecond");

    internal static readonly VmQueryField[] Items = [AttachmentId, DriveType, DiskType, Path, ControllerType, ControllerNumber, ControllerLocation, DiskNumber, DiskModel, SerialNumber, PnpDeviceId, IsPassthroughStale, CapacityBytes, VirtualDiskType, FileSizeBytes, ReadBytesPerSecond, WriteBytesPerSecond];
}
