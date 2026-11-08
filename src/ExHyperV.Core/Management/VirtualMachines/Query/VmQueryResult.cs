using ExHyperV.Tools;
using System.Collections.ObjectModel;

namespace ExHyperV.Services;

/// <summary>一次读取的状态；只有 Available 的 Value 才是本次有效数据。</summary>
public enum VmQueryValueState
{
    /// <summary>成功读到有效值；false、0 和空集合也可以是有效值。</summary>
    Available,
    /// <summary>调用方没有选择该字段或集合。</summary>
    NotRequested,
    /// <summary>当前提供者明确缺少所需类或属性。</summary>
    Unsupported,
    /// <summary>当前状态不适用，例如未运行虚拟机的性能采样。</summary>
    NotApplicable,
    /// <summary>字段存在但没有取得有效值；不推断为零或关闭。</summary>
    Unavailable,
    /// <summary>读取操作失败，Error 提供原因。</summary>
    Failed,
    /// <summary>计数器正在建立基线，本次还没有有效速率。</summary>
    WarmingUp,
    /// <summary>对象或采样不能唯一归属。</summary>
    Ambiguous
}

/// <summary>供动态调用方枚举的字段结果基类。</summary>
public abstract class VmQueryValue
{
    internal VmQueryValue(VmQueryValueState state, DateTimeOffset time, string? error)
    { State = state; ObservedAtUtc = time; Error = error; }
    /// <summary>本次字段读取状态。</summary>
    public VmQueryValueState State { get; }
    /// <summary>该来源完成读取的 UTC 时间；不是全局原子快照时间。</summary>
    public DateTimeOffset ObservedAtUtc { get; }
    /// <summary>缺测、归属冲突或读取失败的说明。</summary>
    public string? Error { get; }
    /// <summary>来源读取失败的结构化错误；未请求、暂缺值等没有原始错误时为 null。</summary>
    public ApiFailure? Failure { get; internal set; }
    /// <summary>是否取得本次有效数据。</summary>
    public bool HasValue => State == VmQueryValueState.Available;
    /// <summary>动态类型的有效值；无效状态时为 null。</summary>
    public abstract object? UntypedValue { get; }
}

/// <summary>带类型的字段结果；读取 Value 前必须检查 HasValue。</summary>
/// <typeparam name="T">字段值的托管类型。</typeparam>
public sealed class VmQueryValue<T> : VmQueryValue
{
    internal VmQueryValue(VmQueryValueState state, T? value, DateTimeOffset time, string? error) : base(state, time, error) { Value = value; }
    /// <summary>仅 HasValue 为 true 时有效；其他状态下的 default 不是实际采样值。</summary>
    public T? Value { get; }
    /// <summary>供动态调用方读取的值；无效时为 null。</summary>
    public override object? UntypedValue => HasValue ? Value : null;
}

/// <summary>一台虚拟机或一个子对象的只读投影；仅包含请求字段。</summary>
public sealed class VmQueryRow
{
    internal VmQueryRow(string key, IDictionary<string, VmQueryValue> values, IDictionary<VmQueryCollection, VmQueryCollectionResult>? collections = null)
    { Key = key; Values = new ReadOnlyDictionary<string, VmQueryValue>(new Dictionary<string, VmQueryValue>(values)); Collections = new ReadOnlyDictionary<VmQueryCollection, VmQueryCollectionResult>(collections == null ? new Dictionary<VmQueryCollection, VmQueryCollectionResult>() : new Dictionary<VmQueryCollection, VmQueryCollectionResult>(collections)); }
    /// <summary>虚拟机记录为 GUID；子对象为本次来源的稳定身份或明确标注的序号。不可当作任意底层操作路径。</summary>
    /// <remarks>虚拟机、所属分组使用 GUID；元数据使用 VM GUID 与元数据键的组合；引导项使用 VM GUID 与本次顺序号，重排后不能用旧 Key 关联同一引导设备。网卡、存储、GPU、PCIe 和检查点使用当前后端记录身份，移除重建或跨宿主后不保证保留。Key 仅在同一虚拟机和同类集合内关联记录；操作入口仍使用其要求的 GUID 或查询引用。</remarks>
    public string Key { get; }
    /// <summary>按字段稳定标识索引的结果；不包含为了关联而内部读取的依赖字段。</summary>
    public IReadOnlyDictionary<string, VmQueryValue> Values { get; }
    /// <summary>本次选择的子对象集合；每个集合也具有独立读取状态。</summary>
    public IReadOnlyDictionary<VmQueryCollection, VmQueryCollectionResult> Collections { get; }
    /// <summary>按字段描述读取强类型结果；未选择时返回 NotRequested。</summary>
    /// <param name="field">内置字段。</param>
    /// <typeparam name="T">字段值类型，由字段描述推断。</typeparam>
    /// <returns>字段读取状态与值。</returns>
    public VmQueryValue<T> Get<T>(VmQueryField<T> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Values.TryGetValue(field.Id, out var value)
            ? (VmQueryValue<T>)value : new(VmQueryValueState.NotRequested, default, default, null);
    }
    /// <summary>取得子对象集合；没有选择时返回 NotRequested，而不是成功空列表。</summary>
    /// <param name="collection">需要的子对象种类。</param>
    /// <returns>集合状态与记录。</returns>
    public VmQueryCollectionResult GetCollection(VmQueryCollection collection) => Collections.TryGetValue(collection, out var value)
        ? value : new(VmQueryValueState.NotRequested, [], null, null);
}

/// <summary>一个子对象集合的查询结果；Available 且 Rows 为空才表示确认没有对象。</summary>
/// <param name="State">集合枚举状态。</param>
/// <param name="Rows">本次取得的记录；还应逐字段检查读取状态。</param>
/// <param name="Error">集合枚举失败或不适用的原因。</param>
/// <param name="ObservedAtUtc">本集合基础清单完成枚举的 UTC 时间，空清单也有时间；未请求时为 null。各字段可能在不同时间取得，见字段自身的 ObservedAtUtc。</param>
public sealed record VmQueryCollectionResult(VmQueryValueState State, IReadOnlyList<VmQueryRow> Rows, string? Error, DateTimeOffset? ObservedAtUtc)
{
    /// <summary>集合来源读取失败的结构化错误；成功或未请求时为 null。</summary>
    public ApiFailure? Failure { get; init; }
}

/// <summary>查询引擎执行阶段的耗时，用于比较来源成本；并行阶段耗时不能直接相加作为总时间。</summary>
/// <param name="Name">读取阶段标识，不是供调用方执行的命令。</param>
/// <param name="Elapsed">该阶段实际执行耗时。</param>
public sealed record VmQueryTiming(string Name, TimeSpan Elapsed);

/// <summary>自由组合查询的结果；必需的身份枚举失败由外层 ApiResponse 返回，其他失败保留在字段或集合中。</summary>
/// <param name="VirtualMachines">按 GUID 排序的虚拟机投影，不使用页面排序规则。</param>
/// <param name="MissingVmIds">请求中指定但本次清单没有找到的 GUID；不静默忽略。</param>
/// <param name="StartedAtUtc">查询开始时间。</param>
/// <param name="CompletedAtUtc">查询结束时间。多个来源在此区间分别采集，不保证跨来源原子一致。</param>
/// <param name="Timings">各读取阶段的耗时。</param>
public sealed record VmQueryResult(IReadOnlyList<VmQueryRow> VirtualMachines, IReadOnlyList<Guid> MissingVmIds,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, IReadOnlyList<VmQueryTiming> Timings);
