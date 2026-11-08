using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ExHyperV.Mcp.Api;

// One schema builder describes both discovery and the actual CLR argument decoder.
internal static class ApiSchema
{
    internal static JsonObject Arguments(MemberInfo member, Func<ParameterInfo, string> description)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var parameters = member switch
        {
            MethodBase method => method.GetParameters(),
            PropertyInfo property => property.GetIndexParameters(),
            _ => []
        };
        foreach (var parameter in parameters)
        {
            if (parameter.IsOut || parameter.ParameterType == typeof(CancellationToken)) continue;
            var shape = Type(parameter.ParameterType, input: true);
            if (!parameter.ParameterType.IsValueType && new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable)
                shape = new() { ["anyOf"] = new JsonArray(shape, new JsonObject { ["type"] = "null" }) };
            shape["description"] = description(parameter);
            properties[parameter.Name!] = shape;
            if (!parameter.HasDefaultValue && !ApiCatalog.IsCallback(parameter.ParameterType)) required.Add(parameter.Name);
        }
        return new() { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
    }

    internal static JsonObject Type(Type type, bool input, int depth = 0, HashSet<Type>? path = null)
    {
        if (type.IsByRef) type = type.GetElementType()!;
        if (type == typeof(void) || type == typeof(Task) || type == typeof(ValueTask)) return new() { ["type"] = "null" };
        if (type.IsGenericType && type.GetGenericTypeDefinition() is var generic && (generic == typeof(Task<>) || generic == typeof(ValueTask<>)))
            return Type(type.GetGenericArguments()[0], input, depth, path);
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return new() { ["anyOf"] = new JsonArray(Type(underlying, input, depth, path), new JsonObject { ["type"] = "null" }) };
        if (type.IsGenericParameter || type == typeof(object) || type == typeof(JsonElement)) return new();
        if (type == typeof(bool)) return new() { ["type"] = "boolean" };
        if (type == typeof(char)) return new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 1 };
        if (type.IsEnum) return new() { ["type"] = new JsonArray("string", "integer"),
            ["description"] = "枚举名称：" + string.Join(", ", Enum.GetNames(type))
                + (type.IsDefined(typeof(FlagsAttribute), false) ? "。多个标记可用逗号组合。" : "。有效取值由 Core 核验。") };
        if (type == typeof(Type)) return new() { ["type"] = "string", ["description"] = "Core 目录中的类型全名或基础类型名。" };
        if (type == typeof(string))
            return input ? new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" },
                new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["$environment"] = new JsonObject { ["type"] = "string", ["pattern"] = "^EXHYPERV_" } }, ["required"] = new JsonArray("$environment"), ["additionalProperties"] = false }) }
                : new() { ["type"] = "string" };
        if (type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset))
            return new() { ["type"] = "string", ["format"] = type == typeof(Guid) ? "uuid" : "date-time" };
        if (type == typeof(TimeSpan)) return new() { ["type"] = "string", ["description"] = "时间间隔，例如 00:00:30。" };
        if (type.IsPrimitive || type == typeof(decimal)) return new() { ["type"] = type == typeof(float) || type == typeof(double) || type == typeof(decimal) ? "number" : "integer" };
        if (type == typeof(byte[]) || type == typeof(Memory<byte>) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(ReadOnlySpan<byte>))
            return new() { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string", ["contentEncoding"] = "base64" }, Handle()) };
        if (ApiCatalog.IsCallback(type)) return new() { ["anyOf"] = new JsonArray(
            new JsonObject { ["type"] = "null" }, Handle(),
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["$callback"] = new JsonObject { ["const"] = true } }, ["required"] = new JsonArray("$callback"), ["additionalProperties"] = false }) };
        var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() is var definition
            && (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)));
        if (dictionary != null) return new() { ["type"] = "object", ["additionalProperties"] = depth < 5 ? Type(dictionary.GetGenericArguments()[1], input, depth + 1, path) : new JsonObject() };
        var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (type.IsArray || enumerable != null) return new() { ["type"] = "array", ["items"] = depth < 5 ? Type(type.IsArray ? type.GetElementType()! : enumerable!.GetGenericArguments()[0], input, depth + 1, path) : new JsonObject() };
        path ??= [];
        if (depth >= 5 || !path.Add(type)) return new() { ["type"] = "object", ["description"] = ApiCatalog.TypeName(type) };
        try
        {
            var properties = new JsonObject();
            if (type.Assembly == ApiCatalog.Core) properties["$type"] = new JsonObject { ["type"] = "string", ["description"] = "具体 Core 类型全名。" };
            var constructors = type.GetConstructors();
            var constructorParameters = constructors.SelectMany(ctor => ctor.GetParameters()).ToArray();
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.GetIndexParameters().Length == 0))
            {
                if (input && property.SetMethod?.IsPublic != true && !constructorParameters.Any(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase))) continue;
                var propertyShape = Type(property.PropertyType, input, depth + 1, path);
                properties[property.Name] = property.PropertyType.IsValueType ? propertyShape
                    : new JsonObject { ["anyOf"] = new JsonArray(propertyShape, new JsonObject { ["type"] = "null" }) };
            }
            foreach (var parameter in constructorParameters)
                if (input && !properties.Any(pair => pair.Key.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)))
                    properties[parameter.Name!] = Type(parameter.ParameterType, true, depth + 1, path);
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(field => !input || !field.IsInitOnly))
                properties[field.Name] = Type(field.FieldType, input, depth + 1, path);
            var shape = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = !input };
            // Runtime validates constructor alternatives and required init members as Core specifies.
            return input ? new() { ["anyOf"] = new JsonArray(shape, Handle()) } : shape;
        }
        finally { path.Remove(type); }
    }

    private static JsonObject Handle() => new() { ["type"] = "object", ["properties"] = new JsonObject
        { ["$handle"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" } }, ["required"] = new JsonArray("$handle"), ["additionalProperties"] = false };
}
