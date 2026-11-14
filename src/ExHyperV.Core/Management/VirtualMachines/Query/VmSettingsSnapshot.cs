using System.Collections.Frozen;

namespace ExHyperV.Services;

/// <summary>一次配置读取的值、字段状态和采集时间。</summary>
/// <typeparam name="T">配置值模型。</typeparam>
/// <remarks>Values 每次返回独立的编辑副本。States 按配置属性名索引，Available 表示本次取得了底层值；Unsupported 表示宿主未提供属性，Unavailable 表示属性存在但未返回值。底层的“未设置”编码可映射为 null，其状态仍为 Available。</remarks>
public sealed class VmSettingsSnapshot<T> where T : class
{
    private readonly T values;
    private readonly Func<T, T> copy;

    internal VmSettingsSnapshot(T values, IReadOnlyDictionary<string, VmQueryValueState> states, Func<T, T> copy)
    {
        this.copy = copy;
        this.values = copy(values);
        States = states.ToFrozenDictionary(StringComparer.Ordinal);
        ObservedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>本次配置值的独立副本。字段有效性由 States 给出；编辑后通过 changedProperties 选择提交的字段。</summary>
    public T Values => copy(values);

    /// <summary>本次读取中每个可提交配置属性的状态，键为配置模型的属性名。</summary>
    public IReadOnlyDictionary<string, VmQueryValueState> States { get; }

    /// <summary>完成配置映射时的 UTC 时间。</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

internal static class SettingsSnapshot
{
    internal static VmQueryValueState State(IReadOnlyDictionary<string, object?> raw, string property)
        => !raw.TryGetValue(property, out var value) ? VmQueryValueState.Unsupported
            : value == null ? VmQueryValueState.Unavailable : VmQueryValueState.Available;
}
