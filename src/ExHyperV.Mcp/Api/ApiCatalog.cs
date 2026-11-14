using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Text.Json;
using ExHyperV.Services;

namespace ExHyperV.Mcp.Api;

// The running Core assembly is authoritative. XML and the existing API guide supply Chinese text.
public sealed class ApiCatalog
{
    public static readonly Assembly Core = typeof(VmQueryService).Assembly;
    internal const BindingFlags PublicMembers = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    public IReadOnlyDictionary<string, Type> Types { get; }
    public IReadOnlyDictionary<string, MemberInfo> Members { get; }
    private readonly Dictionary<string, XElement> comments;
    private readonly Dictionary<string, string> categories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> groups = new(StringComparer.Ordinal);
    private readonly HashSet<string> readOnlyMembers = new(StringComparer.Ordinal);
    public string Guide { get; }
    public string? ReferenceFingerprint { get; }
    private string? contractFingerprint;
    public string ContractFingerprint => contractFingerprint ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n", Members.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => System.Text.Json.JsonSerializer.Serialize(new
        {
            id = pair.Key,
            metadata = ContractMetadata(pair.Value),
            parameters = pair.Value is MethodBase method ? method.GetParameters().Select(parameter => new
                { parameter.Name, type = TypeName(parameter.ParameterType), parameter.IsOut, parameter.HasDefaultValue,
                    defaultValue = parameter.HasDefaultValue ? Convert.ToString(parameter.DefaultValue, System.Globalization.CultureInfo.InvariantCulture) : null }).ToArray() : null
        })))))).ToLowerInvariant();
    private object ContractMetadata(MemberInfo member)
        => JsonSerializer.SerializeToElement(Describe(member, detail: false)).EnumerateObject()
            .Where(property => property.Name is not ("description" or "category"))
            .ToDictionary(property => property.Name, property => property.Value.Clone());

    public bool ContractReviewed
    {
        get
        {
            using var source = typeof(ApiCatalog).Assembly.GetManifestResourceStream("Core.Contracts.sha256");
            if (source == null) return false;
            using var reader = new StreamReader(source);
            return reader.ReadToEnd().Trim() == ContractFingerprint;
        }
    }

    public ApiCatalog() : this(ReadResource("Core.API.md"), ReadResource("Core.API.json")) { }

    internal ApiCatalog(string guide, string metadata)
    {
        string xml = Path.ChangeExtension(Core.Location, ".xml");
        comments = File.Exists(xml) ? XDocument.Load(xml).Descendants("member")
            .ToDictionary(node => (string)node.Attribute("name")!, node => node) : [];
        Guide = guide;
        using var model = JsonDocument.Parse(metadata);
        if (model.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Unsupported Core API metadata version.");
        ReferenceFingerprint = model.RootElement.GetProperty("source").GetProperty("contentSha256").GetString();
        foreach (var type in model.RootElement.GetProperty("types").EnumerateArray())
        {
            string id = type.GetProperty("uid").GetString()!;
            categories[id] = type.GetProperty("area").GetString()!;
            groups[id] = type.GetProperty("toolGroup").GetString()!;
            foreach (var member in type.GetProperty("members").EnumerateArray())
            {
                string memberId = member.GetProperty("uid").GetString()!;
                categories[memberId] = string.Join(" / ", member.GetProperty("navigationPath").EnumerateArray().Select(part => part.GetString()));
                groups[memberId] = member.GetProperty("toolGroup").GetString()!;
                if (member.GetProperty("readOnly").GetBoolean()) readOnlyMembers.Add(memberId);
            }
        }
        Types = Core.GetExportedTypes().Where(type => type.Namespace?.StartsWith("ExHyperV.", StringComparison.Ordinal) == true && type.Namespace != "ExHyperV.Properties")
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToDictionary(type => TypeName(type));
        var members = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
        foreach (var type in Types.Values)
        {
            members[Id(type)] = type;
            foreach (var member in type.GetMembers(PublicMembers | BindingFlags.DeclaredOnly))
                if (member is ConstructorInfo or PropertyInfo or EventInfo
                    || member is FieldInfo field && !field.IsSpecialName
                    || member is MethodInfo method && (!method.IsSpecialName || method.Name.StartsWith("op_")))
                    members[Id(member)] = member;
        }
        Members = members;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(ApiCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException($"Missing API resource: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public MemberInfo Find(string id) => Members.TryGetValue(id, out var member) ? member
        : throw new ArgumentException("API 标识已变化或未找到，请重新查询 core_api_list。");

    public string Category(MemberInfo member) => categories.GetValueOrDefault(Id(member))
        ?? categories.GetValueOrDefault(Id(member as Type ?? member.DeclaringType!), "Core");

    public string Group(MemberInfo member) => groups.GetValueOrDefault(Id(member))
        ?? groups.GetValueOrDefault(Id(member as Type ?? member.DeclaringType!), "core");

    public bool IsReadOnly(MemberInfo member) => readOnlyMembers.Contains(Id(member)) && !ApiValuePolicy.IsHelper(member);

    public Type ResolveType(string name)
    {
        if (Types.TryGetValue(name, out var type)) return type;
        if (name.EndsWith("[]", StringComparison.Ordinal)) return ResolveType(name[..^2]).MakeArrayType();
        int bracket = name.IndexOf('[');
        if (bracket > 0 && name.EndsWith(']'))
        {
            Type definition = ResolveType(name[..bracket]);
            var arguments = new List<string>();
            int depth = 0, start = bracket + 1;
            for (int i = start; i < name.Length - 1; i++)
            {
                if (name[i] == '[') depth++;
                else if (name[i] == ']') depth--;
                else if (name[i] == ',' && depth == 0) { arguments.Add(name[start..i].Trim()); start = i + 1; }
            }
            arguments.Add(name[start..^1].Trim());
            return definition.MakeGenericType(arguments.Select(ResolveType).ToArray());
        }
        return name switch
        {
            "string" or "System.String" => typeof(string), "bool" or "System.Boolean" => typeof(bool),
            "byte" or "System.Byte" => typeof(byte), "int" or "System.Int32" => typeof(int),
            "uint" or "System.UInt32" => typeof(uint), "long" or "System.Int64" => typeof(long),
            "ulong" or "System.UInt64" => typeof(ulong), "short" or "System.Int16" => typeof(short),
            "ushort" or "System.UInt16" => typeof(ushort), "float" or "System.Single" => typeof(float),
            "double" or "System.Double" => typeof(double), "decimal" or "System.Decimal" => typeof(decimal),
            "object" or "System.Object" => typeof(object), "Guid" or "System.Guid" => typeof(Guid),
            "TimeSpan" or "System.TimeSpan" => typeof(TimeSpan), "DateTime" or "System.DateTime" => typeof(DateTime),
            "DateTimeOffset" or "System.DateTimeOffset" => typeof(DateTimeOffset),
            "System.Collections.Generic.List`1" => typeof(List<>), "System.Collections.Generic.IReadOnlyList`1" => typeof(IReadOnlyList<>),
            "System.Collections.Generic.IEnumerable`1" => typeof(IEnumerable<>), "System.Collections.Generic.HashSet`1" => typeof(HashSet<>),
            "System.Collections.Generic.Dictionary`2" => typeof(Dictionary<,>), "System.Nullable`1" => typeof(Nullable<>),
            _ => throw new ArgumentException($"类型 {name} 请使用 Core 目录内的完整名称或基础类型名。")
        };
    }

    public MemberInfo Close(MemberInfo member, string[]? typeArguments, string[]? methodArguments, object? target)
    {
        var declaring = member.DeclaringType;
        if (declaring?.IsGenericTypeDefinition == true)
        {
            Type? closed = target?.GetType();
            if (closed?.IsGenericType == true && closed.GetGenericTypeDefinition() == declaring) { }
            else closed = declaring.MakeGenericType((typeArguments ?? []).Select(ResolveType).ToArray());
            member = closed.GetMembers(PublicMembers).Single(item => item.MetadataToken == member.MetadataToken && item.Module == member.Module);
        }
        if (member is MethodInfo { IsGenericMethodDefinition: true } method)
            return method.MakeGenericMethod((methodArguments ?? []).Select(ResolveType).ToArray());
        return member;
    }

    public object Describe(MemberInfo member, bool detail = true)
    {
        string id = Id(member);
        comments.TryGetValue(id, out var doc);
        var modification = doc?.Element("fielddoc")?.Element("modification");
        var declaring = member as Type ?? member.DeclaringType!;
        var parameters = member switch { MethodBase method => method.GetParameters(), PropertyInfo property => property.GetIndexParameters(), _ => [] };
        return new
        {
            apiId = id, name = member.Name, type = TypeName(declaring), kind = member.MemberType.ToString(),
            category = Category(member), group = Group(member), readOnly = IsReadOnly(member), auxiliary = ApiValuePolicy.IsHelper(member),
            description = Text(doc?.Element("summary")) is { Length: > 0 } summary ? summary : $"{declaring.Name} 的公开{(member is ConstructorInfo ? "构造入口" : member is MethodInfo ? "方法" : "成员")}：{member.Name}",
            details = detail ? Text(doc?.Element("remarks")) : null,
            returns = detail ? Text(doc?.Element("returns")) : null,
            fieldDetails = detail ? doc?.Element("fielddoc")?.Elements().ToDictionary(element => element.Name.LocalName, Text) : null,
            fieldModification = detail && modification != null ? new
            {
                kind = (string?)modification.Attribute("kind"), input = (string?)modification.Attribute("input"),
                submission = (string?)modification.Attribute("submission"), description = Text(modification)
            } : null,
            exceptions = detail ? doc?.Elements("exception").Select(element => new
                { type = (string?)element.Attribute("cref"), description = Text(element) }).ToArray() : null,
            returnType = member switch { MethodInfo method => TypeName(method.ReturnType), PropertyInfo property => TypeName(property.PropertyType), FieldInfo field => TypeName(field.FieldType), _ => null },
            isStatic = member switch { MethodBase method => method.IsStatic, PropertyInfo property => property.GetMethod?.IsStatic ?? property.SetMethod?.IsStatic, FieldInfo field => field.IsStatic, EventInfo evt => evt.AddMethod?.IsStatic, _ => null },
            canRead = member is PropertyInfo p ? p.GetMethod?.IsPublic == true : member is FieldInfo,
            canWrite = member is PropertyInfo p2 ? Writable(p2) : member is FieldInfo { IsInitOnly: false, IsLiteral: false },
            inputSchema = detail ? ApiSchema.Arguments(member, parameter => Text(doc?.Elements("param").FirstOrDefault(node => (string?)node.Attribute("name") == parameter.Name))) : null,
            valueSchema = detail ? ApiSchema.Type(member switch { MethodInfo method => method.ReturnType, PropertyInfo property => property.PropertyType, FieldInfo field => field.FieldType, ConstructorInfo ctor => ctor.DeclaringType!, Type model => model, _ => typeof(object) }, input: false) : null,
            initializable = member is PropertyInfo initial && initial.SetMethod?.IsPublic == true,
            parameters = detail ? parameters.Select(parameter => new
            {
                name = parameter.Name, type = TypeName(parameter.ParameterType), parameter.IsOut,
                byReference = parameter.ParameterType.IsByRef, optional = parameter.HasDefaultValue,
                defaultValue = parameter.HasDefaultValue ? parameter.DefaultValue?.ToString() : null,
                suppliedByMcp = parameter.ParameterType == typeof(CancellationToken) || IsCallback(parameter.ParameterType),
                description = Text(doc?.Elements("param").FirstOrDefault(node => (string?)node.Attribute("name") == parameter.Name))
            }).ToArray() : null,
            genericTypeParameters = declaring.IsGenericTypeDefinition ? declaring.GetGenericArguments().Select(type => type.Name).ToArray() : [],
            genericMethodParameters = member is MethodInfo { IsGenericMethodDefinition: true } generic ? generic.GetGenericArguments().Select(type => type.Name).ToArray() : [],
            enumValues = member is Type { IsEnum: true } enumeration ? Enum.GetValues(enumeration).Cast<object>().Select(value => new { name = value.ToString(), value = Convert.ChangeType(value, Enum.GetUnderlyingType(enumeration)) }).ToArray() : null
        };
    }

    public static bool IsCallback(Type type) => typeof(Delegate).IsAssignableFrom(type)
        || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IProgress<>);

    public static bool Writable(PropertyInfo property) => property.SetMethod?.IsPublic == true
        && !property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));

    public object Coverage() => new
    {
        coreVersion = Core.GetName().Version?.ToString(), coreModuleId = Core.ManifestModule.ModuleVersionId,
        referenceFingerprint = ReferenceFingerprint,
        contractFingerprint = ContractFingerprint, contractReviewed = ContractReviewed,
        types = Types.Count, members = Members.Count,
        unclassifiedTypes = Types.Values.Where(type => !categories.ContainsKey(Id(type))).Select(Id).ToArray(),
        byKind = Members.Values.GroupBy(member => member is Type ? "Type" : member.MemberType.ToString()).ToDictionary(group => group.Key, group => group.Count()),
        transportGaps = Members.Values.Where(member => member switch
        {
            MethodBase callable => callable.GetParameters().Any(parameter => parameter.ParameterType.IsPointer
                || parameter.ParameterType.IsByRefLike && parameter.ParameterType != typeof(ReadOnlySpan<byte>)),
            PropertyInfo property => property.PropertyType.IsByRefLike && !(property.DeclaringType == typeof(ConsoleFrame) && property.Name == "Pixels"),
            _ => false
        }).Select(Id).ToArray()
    };

    public static string TypeName(Type type)
    {
        if (type.IsByRef) return TypeName(type.GetElementType()!) + "&";
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (type.IsGenericParameter) return type.Name;
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
            return (type.GetGenericTypeDefinition().FullName ?? type.Name).Replace('+', '.') + "[" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + "]";
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    public static string Id(MemberInfo member)
    {
        if (member is Type type) return "T:" + (type.FullName ?? type.Name).Replace('+', '.');
        string prefix = member switch { MethodBase => "M:", PropertyInfo => "P:", FieldInfo => "F:", EventInfo => "E:", _ => throw new ArgumentException("成员种类无效。") };
        string name = member is ConstructorInfo ? "#ctor" : member.Name.Replace('.', '#');
        if (member is MethodInfo { IsGenericMethodDefinition: true } method) name += "``" + method.GetGenericArguments().Length;
        string owner = (member.DeclaringType!.FullName ?? member.DeclaringType.Name).Replace('+', '.');
        var parameters = member switch { MethodBase callable => callable.GetParameters(), PropertyInfo property => property.GetIndexParameters(), _ => [] };
        string args = parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(parameter => XmlType(parameter.ParameterType))) + ")";
        string result = member is MethodInfo conversion && name is "op_Implicit" or "op_Explicit" ? "~" + XmlType(conversion.ReturnType) : "";
        return prefix + owner + "." + name + args + result;
    }

    private static string XmlType(Type type)
    {
        if (type.IsByRef) return XmlType(type.GetElementType()!) + "@";
        if (type.IsArray) return XmlType(type.GetElementType()!) + (type.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]");
        if (type.IsGenericParameter) return (type.DeclaringMethod == null ? "`" : "``") + type.GenericParameterPosition;
        if (type.IsGenericType) return Regex.Replace((type.GetGenericTypeDefinition().FullName ?? type.Name).Replace('+', '.'), @"`\d+", "") + "{" + string.Join(",", type.GetGenericArguments().Select(XmlType)) + "}";
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    private static string Text(XElement? element) => element == null ? "" : Regex.Replace(NodeText(element), @"\s+", " ").Trim();

    private static string NodeText(XNode node)
    {
        if (node is XText text) return text.Value;
        if (node is not XElement element) return "";
        string content = string.Concat(element.Nodes().Select(NodeText));
        if (element.Name.LocalName is "see" or "seealso")
        {
            string? target = (string?)element.Attribute("cref") ?? (string?)element.Attribute("href");
            if (string.IsNullOrWhiteSpace(content)) return target ?? "";
            return target == null || content == target ? content : $"{content} ({target})";
        }
        if (element.Name.LocalName is "paramref" or "typeparamref") return (string?)element.Attribute("name") ?? content;
        return element.Name.LocalName is "para" or "p" or "code" or "item" ? $" {content} " : content;
    }
}
