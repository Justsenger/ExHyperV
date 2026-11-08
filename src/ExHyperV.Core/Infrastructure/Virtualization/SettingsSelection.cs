using System.Reflection;

namespace ExHyperV.Services;

// 保存调用者明确选择的值，不持有可继续编辑的模型；不比较旧值或版本。
internal sealed class SettingsSelection<T> where T : class, new()
{
    private static readonly IReadOnlyDictionary<string, PropertyInfo> Writable = typeof(T).GetProperties()
        .Where(p => p.SetMethod?.IsPublic == true && p.GetMethod?.IsPublic == true)
        .ToDictionary(p => p.Name, StringComparer.Ordinal);
    private readonly Dictionary<string, object?> values;

    private SettingsSelection(Dictionary<string, object?> values) => this.values = values;
    internal IEnumerable<string> Names => values.Keys;
    internal bool Contains(string name) => values.ContainsKey(name);
    internal object? this[string name] => values[name];

    internal static SettingsSelection<T> Capture(T model, IReadOnlyCollection<string> names,
        IEnumerable<string>? allowed = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0) throw new ArgumentException("请明确指定本次修改的字段。", nameof(names));
        var supported = new HashSet<string>(allowed ?? Writable.Keys, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (string.IsNullOrEmpty(name) || !supported.Contains(name) || !Writable.TryGetValue(name, out var property))
                throw new ArgumentException($"不支持修改字段 {name}。", nameof(names));
            values.TryAdd(name, CopyValue(property.GetValue(model)));
        }
        return new(values);
    }

    internal T ApplyTo(T target)
    {
        foreach (var (name, value) in values) Writable[name].SetValue(target, CopyValue(value));
        return target;
    }

    internal T Merge(T current)
    {
        var target = new T();
        foreach (var property in Writable.Values) property.SetValue(target, CopyValue(property.GetValue(current)));
        return ApplyTo(target);
    }

    private static object? CopyValue(object? value) => value switch
    {
        List<int> list => new List<int>(list),
        Array array => array.Clone(),
        _ => value
    };
}
