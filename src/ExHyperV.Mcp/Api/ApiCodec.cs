using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExHyperV.Services;

namespace ExHyperV.Mcp.Api;

public sealed class ApiCodec(ApiCatalog catalog, ApiObjects objects, ApiPolicy? policy = null)
{
    internal bool CanRetain => objects.Count < 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { IncludeFields = true, Converters = { new JsonStringEnumConverter() } };

    internal object? Decode(JsonElement input, Type type, ApiOperation? operation = null)
    {
        if (type.IsByRef) type = type.GetElementType()!;
        if (input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) == null) throw new ArgumentException($"{type.Name} 需要有效值。");
            return null;
        }
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("$handle", out var handle))
        {
            Guid id = handle.GetGuid();
            if (operation != null) objects.Acquire(id, operation.Held);
            object value = objects.Get(id);
            if (type == typeof(ReadOnlyMemory<byte>) && value is byte[] memory) return new ReadOnlyMemory<byte>(memory);
            if (!type.IsInstanceOfType(value)) throw new ArgumentException($"对象实际为 {value.GetType().Name}，参数要求 {type.Name}。");
            return value;
        }
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("$environment", out var environment))
        {
            string name = environment.GetString()!;
            if (type != typeof(string) || !name.StartsWith("EXHYPERV_", StringComparison.Ordinal)) throw new ArgumentException("私密环境参数使用 EXHYPERV_ 前缀和字符串类型。");
            return Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException($"请在 MCP 进程环境中提供 {name}。");
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(Type)) return catalog.ResolveType(input.GetString()!);
        if (type == typeof(JsonElement)) return input.Clone();
        if (type == typeof(ReadOnlyMemory<byte>)) return new ReadOnlyMemory<byte>((byte[])Decode(input, typeof(byte[]), operation)!);
        if (type == typeof(Memory<byte>)) return new Memory<byte>((byte[])Decode(input, typeof(byte[]), operation)!);
        if (type == typeof(byte[]) && input.ValueKind == JsonValueKind.String) return input.GetBytesFromBase64();
        if (type == typeof(object))
        {
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("$type", out var actual)) return Decode(input, catalog.ResolveType(actual.GetString()!), operation);
            return input.ValueKind switch
            {
                JsonValueKind.String => input.GetString(), JsonValueKind.Number => input.TryGetInt64(out long number) ? number : input.GetDouble(),
                JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.Array => input.EnumerateArray().Select(item => Decode(item, typeof(object), operation)).ToArray(),
                _ => input.EnumerateObject().ToDictionary(item => item.Name, item => Decode(item.Value, typeof(object), operation))
            };
        }
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(TimeSpan) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return input.Deserialize(type, Json);
        if (type.IsArray)
        {
            var items = input.EnumerateArray().ToArray();
            var array = Array.CreateInstance(type.GetElementType()!, items.Length);
            for (int i = 0; i < items.Length; i++) array.SetValue(Decode(items[i], type.GetElementType()!, operation), i);
            return array;
        }
        Type? dictionary = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType &&
            item.GetGenericTypeDefinition() is var generic && (generic == typeof(IDictionary<,>) || generic == typeof(IReadOnlyDictionary<,>)));
        if (dictionary != null)
        {
            var arguments = dictionary.GetGenericArguments();
            Type concrete = type.IsInterface || type.IsAbstract ? typeof(Dictionary<,>).MakeGenericType(arguments) : type;
            var result = (IDictionary)Activator.CreateInstance(concrete)!;
            foreach (var property in input.EnumerateObject())
            {
                object key = arguments[0] == typeof(string) ? property.Name : arguments[0].IsEnum ? Enum.Parse(arguments[0], property.Name)
                    : arguments[0] == typeof(Guid) ? Guid.Parse(property.Name) : Convert.ChangeType(property.Name, arguments[0]);
                result.Add(key, Decode(property.Value, arguments[1], operation));
            }
            return result;
        }
        Type? enumerable = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable != null && input.ValueKind == JsonValueKind.Array)
        {
            Type element = enumerable.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var item in input.EnumerateArray()) list.Add(Decode(item, element, operation));
            if (type.IsInstanceOfType(list)) return list;
            Type concrete = type.IsInterface && type.Name.Contains("Set") ? typeof(HashSet<>).MakeGenericType(element) : type;
            return Activator.CreateInstance(concrete, [list]) ?? throw new ArgumentException($"集合 {type.Name} 创建失败。");
        }
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{type.Name} 使用 JSON 对象或 $handle。");
        if (input.TryGetProperty("$type", out var declared))
        {
            Type actual = catalog.ResolveType(declared.GetString()!);
            if (!type.IsAssignableFrom(actual)) throw new ArgumentException("$type 与参数类型要求冲突。");
            type = actual;
        }
        if (type.IsInterface || type.IsAbstract) throw new ArgumentException($"{type.Name} 请传入已有对象的 $handle，或使用 $type 指定 Core 实现类型。");
        var values = input.EnumerateObject().Where(item => item.Name != "$type").ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var constructors = type.GetConstructors().Where(ctor => ctor.GetParameters().All(parameter => parameter.HasDefaultValue || values.ContainsKey(parameter.Name!)))
            .OrderByDescending(ctor => ctor.GetParameters().Count(parameter => values.ContainsKey(parameter.Name!))).ToArray();
        ConstructorInfo? constructor = constructors.FirstOrDefault();
        object instance;
        HashSet<string> assigned = new(StringComparer.OrdinalIgnoreCase);
        if (constructor != null)
        {
            policy?.DemandConstruction(constructor);
            object?[] arguments = constructor.GetParameters().Select(parameter =>
            {
                if (!values.TryGetValue(parameter.Name!, out var value)) return parameter.DefaultValue;
                assigned.Add(parameter.Name!);
                return Decode(value, parameter.ParameterType, operation);
            }).ToArray();
            instance = constructor.Invoke(arguments);
        }
        else if (type.IsValueType) instance = Activator.CreateInstance(type)!;
        else throw new ArgumentException($"{type.Name} 请通过公开构造函数或工厂取得 $handle。");
        foreach (var (name, value) in values)
        {
            if (assigned.Contains(name)) continue;
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property?.SetMethod?.IsPublic == true) { property.SetValue(instance, Decode(value, property.PropertyType, operation)); continue; }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null && !field.IsInitOnly) { field.SetValue(instance, Decode(value, field.FieldType, operation)); continue; }
            throw new ArgumentException($"{type.Name}.{name} 需要通过构造函数或公开写入入口设置。");
        }
        return instance;
    }

    public object? Pack(object? value, bool owned = true, int depth = 8, bool retain = false, Guid? parent = null) =>
        Pack(value, owned, depth, retain, parent, false, new(ReferenceEqualityComparer.Instance));

    // Event snapshots never allocate handles. Dropping a packet then also drops all its data.
    internal object? Snapshot(object? value, int depth = 4) =>
        Pack(value, false, depth, false, null, true, new(ReferenceEqualityComparer.Instance));

    private object? Pack(object? value, bool owned, int depth, bool retain, Guid? parent, bool snapshot, HashSet<object> parents)
    {
        if (value == null) return null;
        Type type = value.GetType();
        if (type.IsPrimitive || value is string or decimal or Guid or DateTime or DateTimeOffset or TimeSpan or JsonElement) return value;
        if (type.IsEnum) return value.ToString();
        if (value is Exception exception) return Pack(ApiExceptionDetails.Capture(exception), owned, Math.Max(depth, 1), false, parent, snapshot, parents);
        if (value is Type reflectedType) return ApiCatalog.TypeName(reflectedType);
        if (value is ReadOnlyMemory<byte> readOnly) value = readOnly.ToArray();
        if (value is Memory<byte> memory) value = memory.ToArray();
        if (value is byte[] bytes)
            return bytes.Length <= 65536 || snapshot ? Convert.ToBase64String(bytes) : new { handle = objects.Add(bytes), length = bytes.Length, encoding = "base64", readWith = "core_binary_read" };
        if (value is IDictionary dictionary && depth > 0)
        {
            var result = new Dictionary<string, object?>();
            foreach (DictionaryEntry item in dictionary) result[item.Key.ToString()!] = Pack(item.Value, owned, depth - 1, retain, parent, snapshot, parents);
            return result;
        }
        if (value is IEnumerable sequence && depth > 0)
            return sequence.Cast<object?>().Select(item => Pack(item, owned, depth - 1, retain, parent, snapshot, parents)).ToArray();
        Guid? id = !snapshot && (retain || ApiValuePolicy.HasIdentity(type)) ? objects.Add(value, owned, parent) : null;
        var data = new Dictionary<string, object?>();
        if (id.HasValue) data["$handle"] = id.Value;
        if (type.Assembly == ApiCatalog.Core || id.HasValue) data["$type"] = ApiCatalog.TypeName(type);
        if (depth <= 0 || !parents.Add(value)) { data["$truncated"] = true; return data; }
        bool ownsChildren = id.HasValue && value is IDisposable or IAsyncDisposable;
        Guid? childParent = ownsChildren ? id : parent;
        bool childOwned = ownsChildren ? false : owned;
        try
        {
            IEnumerable<PropertyInfo> properties = type.IsVisible || type.Assembly != ApiCatalog.Core ? type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                : type.GetInterfaces().Where(item => item.Assembly == ApiCatalog.Core).SelectMany(item => item.GetProperties());
            foreach (var property in properties.DistinctBy(item => item.Name))
            {
                if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0 || property.PropertyType.IsByRefLike) continue;
                if (property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase)) { data[property.Name] = "[私密值]"; continue; }
                try { data[property.Name] = Pack(property.GetValue(value), childOwned, depth - 1, retain, childParent, snapshot, parents); }
                catch (Exception error) { data[property.Name] = new { readError = error.GetBaseException().Message }; }
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                data[field.Name] = field.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase)
                    ? "[私密值]" : Pack(field.GetValue(value), childOwned, depth - 1, retain, childParent, snapshot, parents);
            return data;
        }
        finally { parents.Remove(value); }
    }

    internal static bool Failed(object? result) => result is ExHyperV.Models.GpuDeploymentSnapshot gpu
        ? gpu.State is ExHyperV.Models.GpuDeploymentState.Failed or ExHyperV.Models.GpuDeploymentState.CleanupRequired || gpu.OutcomeUnknown
        : result?.GetType().GetProperty("Success")?.GetValue(result) is false;
}
