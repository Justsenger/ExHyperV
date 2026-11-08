using System.Collections.ObjectModel;

namespace ExHyperV.Services;

/// <summary>一次字段级查询请求；字段来自 VmQueryFields.All 或各领域的字段目录。</summary>
/// <remarks>构造时复制并去重字段和目标。省略目标表示枚举全部虚拟机；显式空目标表示不查询任何虚拟机。空字段集合只枚举身份。请求不包含页面预设、任意 WQL 或写入操作。</remarks>
public sealed class VmQueryRequest
{
    /// <summary>建立一次不可变的字段选择。</summary>
    /// <param name="fields">要返回的独立字段；相同字段重复出现只读取一次。</param>
    /// <param name="vmIds">目标 GUID；null 为全部，空集合为空结果，非空集合按身份筛选。</param>
    /// <exception cref="ArgumentException">含空 GUID、null 字段或不属于内置目录的字段。</exception>
    public VmQueryRequest(IEnumerable<VmQueryField> fields, IEnumerable<Guid>? vmIds = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var selected = fields.ToArray();
        if (selected.Any(f => f == null || !VmQueryFields.All.Contains(f)))
            throw new ArgumentException("查询字段必须来自内置字段目录。", nameof(fields));
        Fields = Array.AsReadOnly(selected.Distinct().ToArray());
        var ids = vmIds?.Distinct().ToArray();
        if (ids?.Contains(Guid.Empty) == true) throw new ArgumentException("虚拟机 GUID 不能为空。", nameof(vmIds));
        VmIds = ids == null ? null : Array.AsReadOnly(ids);
    }

    /// <summary>本次选择的字段，顺序按请求去重后保留。</summary>
    public IReadOnlyList<VmQueryField> Fields { get; }
    /// <summary>目标 GUID；null 表示全部虚拟机。</summary>
    public IReadOnlyList<Guid>? VmIds { get; }
}

/// <summary>字段所属的记录集合；虚拟机级字段与设备级字段分开，避免将设备属性误当全局开关。</summary>
public enum VmQueryCollection
{
    /// <summary>虚拟机本身，包括整台虚拟机的配置开关。</summary>
    VirtualMachine,
    /// <summary>直接所属分组。</summary>
    Groups,
    /// <summary>额外元数据的键值项。</summary>
    Metadata,
    /// <summary>虚拟处理器的逐核心采样。</summary>
    Processors,
    /// <summary>存储控制器上的挂接槽位，包括空光驱。</summary>
    Storage,
    /// <summary>虚拟网卡。</summary>
    NetworkAdapters,
    /// <summary>固件启动顺序中的条目。</summary>
    BootEntries,
    /// <summary>已分配 GPU 分区适配器。</summary>
    GpuAssignments,
    /// <summary>已分配 PCIe 设备。</summary>
    PcieDevices,
    /// <summary>检查点及当前状态节点。</summary>
    Checkpoints
}

/// <summary>内置可查询字段的描述；不能由调用方自行注入底层查询或执行代码。</summary>
public abstract class VmQueryField
{
    internal VmQueryField(string id, string name, string category, VmQueryCollection collection, Type valueType,
        string source, string property, Func<object, object?>? convert = null)
    { Id = id; Name = name; Category = category; Collection = collection; ValueType = valueType; Source = source; Property = property; ConvertValue = convert; }
    /// <summary>稳定字段标识，可供网页或配置文件保存查询选择。</summary>
    public string Id { get; }
    /// <summary>字段中文名称。</summary>
    public string Name { get; }
    /// <summary>字段在查询目录中的所属领域。</summary>
    public string Category { get; }
    /// <summary>值所属的记录集合；VirtualMachine 表示虚拟机级值。</summary>
    public VmQueryCollection Collection { get; }
    /// <summary>成功值的托管类型；失败状态没有有效值。</summary>
    public Type ValueType { get; }
    internal string Source { get; }
    internal string Property { get; }
    internal Func<object, object?>? ConvertValue { get; }
    internal abstract VmQueryValue Make(VmQueryValueState state, object? value, DateTimeOffset time, string? error = null);
}

/// <summary>携带结果类型的字段；使用 VmQueryRow.Get 读取时无需强制转换。</summary>
/// <typeparam name="T">字段成功值的托管类型。</typeparam>
public sealed class VmQueryField<T> : VmQueryField
{
    internal VmQueryField(string id, string name, string category, VmQueryCollection collection, string source,
        string property, Func<object, object?>? convert = null) : base(id, name, category, collection, typeof(T), source, property, convert) { }
    internal override VmQueryValue Make(VmQueryValueState state, object? value, DateTimeOffset time, string? error = null)
        => state == VmQueryValueState.Available && value is not T
            ? new VmQueryValue<T>(VmQueryValueState.Failed, default, time, "字段结果类型与目录不一致：" + Id)
            : new VmQueryValue<T>(state, value is T typed ? typed : default, time, error);
}
