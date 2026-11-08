namespace ExHyperV.Services;

// 在执行时的原始属性上规划局部写入，未选择的数值不经过显示模型重新编码。
internal sealed class SettingsValues(IReadOnlyDictionary<string, object?> current)
{
    private readonly Dictionary<string, object?> values = new(current, StringComparer.Ordinal);
    internal Dictionary<string, object?> Writes { get; } = new(StringComparer.Ordinal);
    internal bool HasProperty(string name) => values.ContainsKey(name);
    internal object? this[string name]
    {
        get => values[name];
        set
        {
            if (!HasProperty(name)) throw new InvalidOperationException($"宿主不提供属性 {name}。");
            values[name] = value;
            if (Equals(current[name], value)) Writes.Remove(name);
            else Writes[name] = value;
        }
    }
    internal void TrySet<T>(string name, T value)
    {
        if (HasProperty(name) && value != null) this[name] = value;
    }
    internal void TrySetAlways<T>(string name, T value)
    {
        if (HasProperty(name)) this[name] = value;
    }
}
