using System.Collections.ObjectModel;
using System.Globalization;
using ExHyperV.Properties;

namespace ExHyperV.Models;

/// <summary>虚拟机导入来源的容器形式。</summary>
public enum VmImportSourceKind
{
    /// <summary>从已有目录导入。</summary>
    Folder,
    /// <summary>从 ZIP 压缩包导入。</summary>
    Zip
}

/// <summary>导入虚拟机时配置和磁盘的放置方式。</summary>
public enum VmImportPlacementMode
{
    /// <summary>复制到 VmImportOptions 指定的目录；未指定时在宿主默认目录下以导入名称建立子目录。</summary>
    HostDirectories,
    /// <summary>使用现有目录中的文件位置。</summary>
    ExistingDirectory
}

/// <summary>导入预览中的磁盘文件信息。该对象只描述扫描结果，不挂载或导入磁盘。</summary>
public sealed class VmImportDiskPreview
{
    /// <summary>磁盘显示名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>源磁盘文件路径。</summary>
    public string SourcePath { get; init; } = string.Empty;
    /// <summary>控制器及槽位的显示描述；空值表示未取得。</summary>
    public string Controller { get; init; } = string.Empty;
    /// <summary>虚拟磁盘格式标识，例如 VHD 或 VHDX。</summary>
    public string Format { get; init; } = string.Empty;
    /// <summary>虚拟磁盘分配类型的显示描述。</summary>
    public string Type { get; init; } = string.Empty;
    /// <summary>虚拟磁盘的标称容量，单位为字节。</summary>
    public ulong VirtualSize { get; init; }
    /// <summary>源文件实际大小，单位为字节。</summary>
    public long ActualSize { get; init; }
    /// <summary>差分磁盘的父盘路径；未取得或非差分盘时为 null。</summary>
    public string? ParentPath { get; init; }
    /// <summary>扫描时源磁盘文件是否存在。</summary>
    public bool Exists { get; init; }

    /// <summary>控制器槽位显示文本；无信息时显示破折号。</summary>
    public string SlotText => string.IsNullOrWhiteSpace(Controller) ? "—" : Controller;
    /// <summary>磁盘格式与分配类型组合的显示文本。</summary>
    public string KindText => string.Join(" · ", new[] { Format, Type }.Where(x => !string.IsNullOrWhiteSpace(x)));
    /// <summary>虚拟容量显示文本，以 1024 为换算基数。</summary>
    public string VirtualSizeText => FormatBytes(VirtualSize);
    /// <summary>实际文件大小显示文本；负值按 0 处理，以 1024 为换算基数。</summary>
    public string ActualSizeText => FormatBytes((ulong)Math.Max(0, ActualSize));

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

/// <summary>来源配置及导入检查结果。由 Core 创建；修改导入选择请使用 VmImportOptions，此对象不承担配置输入。</summary>
public sealed class VmImportPreview
{
    /// <summary>来源配置中的名称；调用方可以通过 VmImportOptions.Name 指定导入后的名称。</summary>
    public string Name { get; init; } = string.Empty;
    /// <summary>源配置中的虚拟机 GUID；无法取得时为 Guid.Empty。</summary>
    public Guid OriginalGuid { get; init; }
    /// <summary>源 GUID 的带连字符显示文本；新生成的身份在导入后通过 VmImportResult.VmId 取得。</summary>
    public string GuidText => OriginalGuid.ToString("D");
    /// <summary>主配置中识别到的源交换机 GUID，供 VmImportOptions.SwitchMappings 使用；未连接或未识别的记录不产生 GUID。</summary>
    public IReadOnlyList<Guid> SourceSwitchIds { get; internal set; } = Array.Empty<Guid>();
    /// <summary>虚拟机代数。</summary>
    public int Generation { get; init; }
    /// <summary>虚拟机代数的本地化显示文本。</summary>
    public string GenerationText => string.Format(
        Resources.Culture ?? CultureInfo.CurrentUICulture,
        Resources.VmImport_GenerationFormat,
        Generation);
    /// <summary>源虚拟机配置版本。</summary>
    public string ConfigurationVersion { get; init; } = string.Empty;
    /// <summary>源配置记录的创建时间；DateTime.MinValue 表示未知。</summary>
    public DateTime Created { get; init; }
    /// <summary>按当前界面区域格式化的创建时间；未知时显示破折号。</summary>
    public string CreatedText => Created == DateTime.MinValue
        ? "—"
        : Created.ToString("g", Resources.Culture ?? CultureInfo.CurrentUICulture);
    /// <summary>源配置 HostOnlyItems 中的来宾系统类型；缺失或未知时为 Windows。</summary>
    public string OsType { get; init; } = "Windows";
    /// <summary>源配置的虚拟处理器数量。</summary>
    public int ProcessorCount { get; init; }
    /// <summary>是否启用动态内存；null 表示未读取到。当前导入解析器不读取此项。</summary>
    public bool? DynamicMemory { get; init; }
    /// <summary>启动内存，单位为 MiB（1024² 字节）。</summary>
    public ulong StartupMemoryMiB { get; init; }
    /// <summary>动态内存下限，单位为 MiB；null 表示未读取到。当前导入解析器不读取此项。</summary>
    public ulong? MinimumMemoryMiB { get; init; }
    /// <summary>动态内存上限，单位为 MiB；null 表示未读取到。当前导入解析器不读取此项。</summary>
    public ulong? MaximumMemoryMiB { get; init; }
    /// <summary>源配置是否包含保存状态；null 表示未读取到。当前导入解析器不读取此项。</summary>
    public bool? HasSavedState { get; init; }
    /// <summary>保存状态的本地化文本；未读取到时显示未知。</summary>
    public string SavedStateText => HasSavedState switch
    {
        true => Resources.VmImport_Yes,
        false => Resources.VmImport_No,
        null => Resources.Common_Unknown
    };
    /// <summary>源磁盘的只读预览集合。</summary>
    public IReadOnlyList<VmImportDiskPreview> Disks { get; private set; } = Array.Empty<VmImportDiskPreview>();
    /// <summary>来源文件的兼容性问题。目标目录、GUID 和网络映射在提交导入选项时另外校验。</summary>
    public IReadOnlyList<string> CompatibilityIssues { get; private set; } = Array.Empty<string>();
    internal void AddDisk(VmImportDiskPreview disk) => Disks = Array.AsReadOnly(Disks.Append(disk).ToArray());
    internal void AddIssue(string issue) => CompatibilityIssues = Array.AsReadOnly(CompatibilityIssues.Append(issue).ToArray());

    /// <summary>内存摘要；动态模式及上下限均已取得时显示范围，其余只显示启动内存。</summary>
    public string MemoryText => DynamicMemory == true && MinimumMemoryMiB.HasValue && MaximumMemoryMiB.HasValue
        ? string.Format(
        Resources.Culture ?? CultureInfo.CurrentUICulture,
        Resources.VmImport_DynamicMemoryFormat,
        StartupMemoryMiB,
        MinimumMemoryMiB.Value,
        MaximumMemoryMiB.Value)
        : StartupMemoryText;
    /// <summary>启动内存的本地化显示文本。</summary>
    public string StartupMemoryText => string.Format(
        Resources.Culture ?? CultureInfo.CurrentUICulture,
        Resources.VmImport_StaticMemoryFormat,
        StartupMemoryMiB);
    /// <summary>处理器、启动内存和各磁盘容量的组合摘要。</summary>
    public string ConfigSummary
    {
        get
        {
            string diskPart = Disks.Count == 0
                ? Resources.Common_NoDisk
                : string.Join(" + ", Disks
                    .Select(d => d.VirtualSize / 1073741824.0)
                    .OrderByDescending(size => size)
                    .Select(size => size >= 1 ? $"{size:0.#} GB" : $"{size * 1024:0} MB"));

            return string.Format(
                Resources.Culture ?? CultureInfo.CurrentUICulture,
                Resources.Format_VmSummary,
                ProcessorCount,
                StartupMemoryMiB / 1024.0,
                diskPart);
        }
    }
    /// <summary>处理器数量与启动内存的组合摘要。</summary>
    public string ProcessorMemorySummary => string.Format(
        Resources.Culture ?? CultureInfo.CurrentUICulture,
        Resources.VmImport_ProcessorMemoryFormat,
        ProcessorCount,
        StartupMemoryMiB / 1024.0);
    /// <summary>预览中的磁盘数量文本。</summary>
    public string DiskSummary => Disks.Count == 0 ? "0" : Disks.Count.ToString();
}
