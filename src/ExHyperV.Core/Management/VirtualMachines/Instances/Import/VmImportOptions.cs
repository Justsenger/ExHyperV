using System.Collections.ObjectModel;

namespace ExHyperV.Models;

/// <summary>导入时对虚拟机唯一身份的处理方式；由调用方明确选择。</summary>
public enum VmImportGuidMode
{
    /// <summary>保留源 GUID；与宿主已有虚拟机冲突时失败。</summary>
    Preserve,
    /// <summary>让 Hyper-V 生成新 GUID，即使源 GUID 没有冲突也生成；仅适用于复制导入。</summary>
    GenerateNew
}

/// <summary>一台虚拟机的导入选择。传给 ImportAsync；Core 在开始导入前复制并校验这些值。</summary>
/// <remarks>未指定名称、目录和交换机映射时保留原有导入规则。GUID 策略必须在构造时明确传入。
/// 目录为完整目标路径。使用原目录注册时，沿用原路径。</remarks>
/// <example>
/// <para>复制为新身份，并将预览中列出的连接映射到指定交换机。名称和目录由上层选择，不由 Core 自动命名。</para>
/// <code>
/// using ExHyperV.Models;
/// public static class ImportOptionsExample
/// {
///     public static VmImportOptions Create(VmImportPreview preview, string name,
///         string configurationDirectory, string diskDirectory, Guid targetSwitchId) =>
///         new(VmImportGuidMode.GenerateNew)
///         {
///             Name = name,
///             ConfigurationDirectory = configurationDirectory,
///             DiskDirectory = diskDirectory,
///             SwitchMappings = preview.SourceSwitchIds.ToDictionary(id => id, _ => (Guid?)targetSwitchId)
///         };
/// }
/// </code>
/// </example>
public sealed class VmImportOptions
{
    /// <summary>创建一次导入的选项，并明确保留原身份还是生成新身份。</summary>
    /// <param name="guidMode">GUID 策略；使用原目录注册时必须为 Preserve。</param>
    public VmImportOptions(VmImportGuidMode guidMode) => GuidMode = guidMode;

    /// <summary>GUID 处理方式；选择保留且发生冲突时失败。</summary>
    public VmImportGuidMode GuidMode { get; }
    /// <summary>导入后的名称；null 保留来源名称，空白名称无效。</summary>
    public string? Name { get; init; }
    /// <summary>完整的配置目标目录。null 使用“宿主默认配置目录/导入名称”；必须为绝对路径，不能覆盖有内容的目录。</summary>
    public string? ConfigurationDirectory { get; init; }
    /// <summary>完整的磁盘目标目录。null 使用“宿主默认磁盘目录/导入名称”；宿主未设磁盘目录时使用配置目标目录。</summary>
    public string? DiskDirectory { get; init; }
    /// <summary>源交换机 GUID 到目标交换机 GUID 的映射；值为 null 表示明确断开该源交换机对应的连接。</summary>
    /// <remarks>源 GUID 可从 Preview.SourceSwitchIds 取得，目标 GUID 从宿主交换机查询取得。同一源交换机上的连接使用同一映射。
    /// 没有映射的连接沿用原规则：原交换机仍存在则保留，不存在则断开。明确指定的目标不存在时失败，不改接同名交换机。
    /// 调用开始时复制字典，复制期间请保持内容不变；源和目标 GUID 均须为非空值。</remarks>
    public IReadOnlyDictionary<Guid, Guid?> SwitchMappings { get; init; } =
        new ReadOnlyDictionary<Guid, Guid?>(new Dictionary<Guid, Guid?>());
}
