using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>GPU 分区的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmGpuFields
{
    /// <summary>宿主 GPU 身份：当前 GPU 分区关联的宿主 GPU 设备标识。</summary>
    /// <fielddoc>
    /// <meaning>当前 GPU 分区关联的宿主 GPU 设备标识。</meaning>
    /// <source>GpuPartitionSettingData 与宿主 GPU 身份清单，读取或派生 HostDeviceInstanceId。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> HostDeviceInstanceId = new("Gpu.HostDeviceInstanceId", "宿主 GPU 身份", "GPU", VmQueryCollection.GpuAssignments, "Gpu", "HostDeviceInstanceId");

    /// <summary>GPU 名称：当前 GPU 分区关联的宿主 GPU 显示名称。</summary>
    /// <fielddoc>
    /// <meaning>当前 GPU 分区关联的宿主 GPU 显示名称。</meaning>
    /// <source>GpuPartitionSettingData 与宿主 GPU 身份清单，读取或派生 Name。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> Name = new("Gpu.Name", "GPU 名称", "GPU", VmQueryCollection.GpuAssignments, "Gpu", "Name");

    /// <summary>GPU 芯片商：关联宿主 GPU 的芯片制造商名称。</summary>
    /// <fielddoc>
    /// <meaning>关联宿主 GPU 的芯片制造商名称。</meaning>
    /// <source>GpuPartitionSettingData 与宿主 GPU 身份清单，读取或派生 Manu。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> Manu = new("Gpu.Manu", "GPU 芯片商", "GPU", VmQueryCollection.GpuAssignments, "Gpu", "Manu");

    /// <summary>GPU 板卡商：关联宿主 GPU 的板卡厂商名称。</summary>
    /// <fielddoc>
    /// <meaning>关联宿主 GPU 的板卡厂商名称。</meaning>
    /// <source>GpuPartitionSettingData 与宿主 GPU 身份清单，读取或派生 Vendor。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> Vendor = new("Gpu.Vendor", "GPU 板卡商", "GPU", VmQueryCollection.GpuAssignments, "Gpu", "Vendor");

    /// <summary>宿主驱动版本：关联宿主 GPU 当前安装的驱动版本。</summary>
    /// <fielddoc>
    /// <meaning>关联宿主 GPU 当前安装的驱动版本。</meaning>
    /// <source>GpuPartitionSettingData 与宿主 GPU 身份清单，读取或派生 DriverVersion。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<string> DriverVersion = new("Gpu.DriverVersion", "宿主驱动版本", "GPU", VmQueryCollection.GpuAssignments, "Gpu", "DriverVersion");

    /// <summary>宿主专用显存容量：关联宿主 GPU 的 DXGI 专用显存容量，单位为字节。</summary>
    /// <fielddoc>
    /// <meaning>当前 GPU 分区所关联宿主 GPU 的专用显存容量。0 是有效容量；未取得时为 Unavailable。</meaning>
    /// <source>按宿主 PnP 实例精确关联 DXGI 适配器，读取 DedicatedVideoMemory。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// <values kind="format">单位为字节；<c>1073741824</c> 表示 1 GiB，<c>0</c> 是有效容量。</values>
    /// </fielddoc>
    public static readonly VmQueryField<ulong> MemoryBytes = new("Gpu.MemoryBytes", "宿主专用显存容量", "GPU", VmQueryCollection.GpuAssignments, "Gpu", "MemoryBytes");

    internal static readonly VmQueryField[] Items = [HostDeviceInstanceId, Name, Manu, Vendor, DriverVersion, MemoryBytes];
}
