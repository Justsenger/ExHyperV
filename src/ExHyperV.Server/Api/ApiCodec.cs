using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExHyperV.Services;

namespace ExHyperV.Server.Api;

internal sealed class ApiCodec(ApiCatalog catalog, ApiObjects objects, ApiPolicy? policy = null)
{
    internal bool CanRetain => objects.Count < 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { IncludeFields = true, Converters = { new JsonStringEnumConverter() } };

    internal object? Decode(JsonElement input, Type type, HashSet<Guid>? held = null)
    {
        if (type.IsByRef) type = type.GetElementType()!;
        if (input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) == null) throw new ArgumentException($"{type.Name} 需要有效值。");
            return null;
        }
        // Only an in-process frame producer can transfer ownership of a live resource.
        if (type == typeof(IDisposable))
            throw new ArgumentException("远程构造帧时所有者必须为空。");
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("$handle", out var handle))
        {
            if (handle.ValueKind != JsonValueKind.String || !handle.TryGetGuid(out var id))
                throw new ArgumentException("$handle 需要有效的对象 GUID。");
            if (held != null) objects.Acquire(id, held);
            object value = objects.Get(id);
            if (type == typeof(ReadOnlyMemory<byte>) && value is byte[] memory) return new ReadOnlyMemory<byte>(memory);
            if (type == typeof(Memory<byte>) && value is byte[] buffer) return new Memory<byte>(buffer);
            if (!type.IsInstanceOfType(value)) throw new ArgumentException($"对象实际为 {value.GetType().Name}，参数要求 {type.Name}。");
            return value;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(Type)) return ResolveType(input);
        if (type == typeof(JsonElement)) return input.Clone();
        if (type == typeof(ReadOnlyMemory<byte>)) return new ReadOnlyMemory<byte>((byte[])Decode(input, typeof(byte[]), held)!);
        if (type == typeof(Memory<byte>)) return new Memory<byte>((byte[])Decode(input, typeof(byte[]), held)!);
        if (type == typeof(byte[]) && input.ValueKind == JsonValueKind.String) return input.GetBytesFromBase64();
        if (type == typeof(object))
        {
            if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("$type", out var actual) && ResolveType(actual) is var concreteType && concreteType != typeof(object))
                return Decode(input, concreteType, held);
            return input.ValueKind switch
            {
                JsonValueKind.String => input.GetString(), JsonValueKind.Number when input.TryGetInt64(out long number) => number,
                JsonValueKind.Number => input.GetDouble(),
                JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.Array => input.EnumerateArray().Select(item => Decode(item, typeof(object), held)).ToArray(),
                _ => input.EnumerateObject().ToDictionary(item => item.Name, item => Decode(item.Value, typeof(object), held))
            };
        }
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type == typeof(Guid)
            || type == typeof(TimeSpan) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return input.Deserialize(type, Json);
        if (type.IsArray)
        {
            if (input.ValueKind != JsonValueKind.Array) throw new ArgumentException($"{type.Name} 需要 JSON 数组。");
            var items = input.EnumerateArray().ToArray();
            var array = Array.CreateInstance(type.GetElementType()!, items.Length);
            for (int i = 0; i < items.Length; i++) array.SetValue(Decode(items[i], type.GetElementType()!, held), i);
            return array;
        }
        Type? dictionary = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType &&
            item.GetGenericTypeDefinition() is var generic && (generic == typeof(IDictionary<,>) || generic == typeof(IReadOnlyDictionary<,>)));
        if (dictionary != null)
        {
            if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{type.Name} 需要 JSON 对象。");
            var arguments = dictionary.GetGenericArguments();
            Type concrete = type.IsInterface || type.IsAbstract ? typeof(Dictionary<,>).MakeGenericType(arguments) : type;
            var result = (IDictionary)Activator.CreateInstance(concrete)!;
            foreach (var property in input.EnumerateObject())
            {
                object key = arguments[0] == typeof(string) ? property.Name : arguments[0].IsEnum ? Enum.Parse(arguments[0], property.Name)
                    : arguments[0] == typeof(Guid) ? Guid.Parse(property.Name) : Convert.ChangeType(property.Name, arguments[0]);
                result.Add(key, Decode(property.Value, arguments[1], held));
            }
            return result;
        }
        Type? enumerable = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable != null && input.ValueKind == JsonValueKind.Array)
        {
            Type element = enumerable.GetGenericArguments()[0];
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var item in input.EnumerateArray()) list.Add(Decode(item, element, held));
            if (type.IsInstanceOfType(list)) return list;
            Type concrete = type.IsInterface && type.Name.Contains("Set") ? typeof(HashSet<>).MakeGenericType(element) : type;
            return Activator.CreateInstance(concrete, [list]) ?? throw new ArgumentException($"集合 {type.Name} 创建失败。");
        }
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{type.Name} 使用 JSON 对象或 $handle。");
        if (input.TryGetProperty("$type", out var declared))
        {
            Type actual = ResolveType(declared);
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
                return Decode(value, parameter.ParameterType == typeof(ReadOnlySpan<byte>) ? typeof(byte[]) : parameter.ParameterType, held);
            }).ToArray();
            instance = ApiExecution.InvokeMember(constructor, null, arguments)!;
        }
        else if (type.IsValueType) instance = Activator.CreateInstance(type)!;
        else throw new ArgumentException($"{type.Name} 请通过公开构造函数或工厂取得 $handle。");
        foreach (var (name, value) in values)
        {
            if (assigned.Contains(name)) continue;
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            // A returned stage or rectangle includes its computed interpretation. Verify it without applying a setter.
            if ((type == typeof(ConsoleExtensionStage) && property?.Name == nameof(ConsoleExtensionStage.Code))
                || (type == typeof(ConsoleFrameRegion) && property?.Name == nameof(ConsoleFrameRegion.IsEmpty)))
            {
                if (!Equals(property.GetValue(instance), Decode(value, property.PropertyType, held)))
                    throw new ArgumentException($"{type.Name}.{name} 与构造参数不一致。");
                continue;
            }
            if (property?.SetMethod?.IsPublic == true) { property.SetValue(instance, Decode(value, property.PropertyType, held)); continue; }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null && !field.IsInitOnly) { field.SetValue(instance, Decode(value, field.FieldType, held)); continue; }
            throw new ArgumentException($"{type.Name}.{name} 需要通过构造函数或公开写入入口设置。");
        }
        return instance;
    }

    private Type ResolveType(JsonElement name) => name.ValueKind == JsonValueKind.String
        ? catalog.ResolveType(name.GetString()!) : throw new ArgumentException("类型名称需要 JSON 字符串。");

    public object? Pack(object? value, bool owned = true, int depth = 8, bool retain = false, Guid? parent = null) =>
        Pack(value, owned, depth, retain, parent, false, new(ReferenceEqualityComparer.Instance));

    internal bool TryTransferPacket(object?[] packet, out object?[] arguments, FrameScope? scope = null)
    {
        arguments = packet;
        var frames = packet.OfType<ConsoleFrame>().ToArray();
        if (frames.Length == 0) return true;
        // Collect metadata while the queue still owns every lease. After registration a client can release its ID.
        var metadata = frames.Select(frame => (Dictionary<string, object?>)Snapshot(frame)!).ToArray();
        if (!objects.TryAddFrames(frames, out var ids, scope)) return false;
        for (int i = 0; i < ids.Length; i++) metadata[i]["$handle"] = ids[i];
        int index = 0;
        arguments = packet.Select(value => value is ConsoleFrame ? metadata[index++] : value).ToArray();
        return true;
    }

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
        {
            if (snapshot || !retain && bytes.Length <= 65536) return Convert.ToBase64String(bytes);
            Guid handle = objects.Add(bytes, owned, parent);
            return new Dictionary<string, object?> { ["$handle"] = handle, ["length"] = bytes.Length, ["href"] = $"/api/v1/binaries/{handle}" };
        }
        if (retain && !snapshot && value is IEnumerable)
        {
            Guid handle = objects.Add(value, owned, parent);
            return new Dictionary<string, object?> { ["$handle"] = handle, ["$type"] = ApiCatalog.TypeName(type),
                ["value"] = Pack(value, owned, depth, false, handle, false, parents) };
        }
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
                try { data[property.Name] = Pack(property.GetValue(value), childOwned, depth - 1, false, childParent, snapshot, parents); }
                catch (Exception error) { data[property.Name] = new { readError = DisplayText.Clean(error.GetBaseException().Message) }; }
            }
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                data[field.Name] = field.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || field.Name.Contains("Passphrase", StringComparison.OrdinalIgnoreCase)
                    ? "[私密值]" : Pack(field.GetValue(value), childOwned, depth - 1, false, childParent, snapshot, parents);
            return data;
        }
        finally { parents.Remove(value); }
    }

    internal static bool Failed(object? result) => result is ExHyperV.Models.GpuDeploymentSnapshot gpu
        ? gpu.State is ExHyperV.Models.GpuDeploymentState.Failed or ExHyperV.Models.GpuDeploymentState.CleanupRequired || gpu.OutcomeUnknown
        : result?.GetType().GetProperty("Success")?.GetValue(result) is false;
}
