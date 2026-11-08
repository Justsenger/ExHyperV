using System.Xml.Linq;
using Microsoft.CodeAnalysis;

internal static partial class ApiDocument
{
    private sealed record MemberPresentation(string expression, string valueType, string valueTypeHtml, string? enumTypeId,
        bool callable, string valuesHtml, string descriptionHtml, string parametersHtml,
        string returnsHtml, string exceptionsHtml, string examplesHtml);

    private static MemberPresentation PresentMember(TypeEntry type, MemberEntry member, IReadOnlyDictionary<string, string> links)
    {
        var symbol = type.Symbol.GetMembers().FirstOrDefault(candidate => candidate.GetDocumentationCommentId() == member.Id)
            ?? type.Symbol.GetMembers(member.Name).FirstOrDefault();
        ITypeSymbol? valueType = symbol switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol eventSymbol => eventSymbol.Type,
            IMethodSymbol { MethodKind: MethodKind.Constructor } => type.Symbol,
            IMethodSymbol method => method.ReturnType,
            _ => null
        };
        bool callable = member.Kind is "方法" or "构造函数" or "构造器" or "主构造器" or "运算符" || symbol is IMethodSymbol;
        string valueName = valueType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            ?? (string?)member.Doc.Attribute("propertyType")
            ?? (member.Kind == "主构造器" ? type.Symbol.Name : member.Kind == "生成命令" ? "IRelayCommand" : "");
        var enumType = valueType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0] : valueType;
        string? enumId = enumType?.TypeKind == TypeKind.Enum && links.TryGetValue(enumType.ToDisplayString(), out var enumLink) ? enumLink : null;
        if (valueType == null && links.TryGetValue(valueName.TrimEnd('?'), out var generatedTypeLink)) enumId = generatedTypeLink;
        var doc = member.Doc;
        string Parts(IEnumerable<XElement> elements) => string.Concat(elements.Where(element => !string.IsNullOrWhiteSpace(element.Value))
            .Select(element => "<div class=\"doc-paragraph\">" + HtmlInline(element, links) + "</div>"));
        string Section(params string[] names) => Parts(names.SelectMany(name => doc.Elements(name)));
        var parameters = new XElement("root", doc.Elements().Where(element => element.Name.LocalName is "param" or "typeparam")
            .Select(element => new XElement(element.Name, element.Attributes(), element.Nodes().Where(node => node is not XElement nested || nested.Name.LocalName != "fielddoc"))));
        string exceptions = doc.Elements("exception").Any() ? "<dl class=\"exceptions\">" + string.Concat(doc.Elements("exception")
            .Select(element => "<dt>" + Node(new XElement("see", element.Attributes()), links) + "</dt><dd>" + HtmlInline(element, links) + "</dd>")) + "</dl>" : "";
        string owner = type.Symbol.ContainingNamespace.IsGlobalNamespace ? type.Name : type.Name[(type.Symbol.ContainingNamespace.ToDisplayString().Length + 1)..];
        string expression = member.Kind is "构造器" or "主构造器" ? owner : owner + "." + member.Name;
        return new(expression, valueName, HtmlCode(valueName, links), enumId, callable,
            MemberValues(member, links), MemberProse(type, member, links), HtmlDoc(parameters, links, false),
            Section("returns"), exceptions, Section("example"));
    }

    private static string MemberValues(MemberEntry member, IReadOnlyDictionary<string, string> links)
    {
        var details = member.Doc.Element("fielddoc");
        var sections = new[] { details?.Element("values"), details?.Element("unit"), details?.Element("format"), member.Doc.Element("value") }
            .Where(element => !string.IsNullOrWhiteSpace(element?.Value)).Cast<XElement>().ToArray();
        return string.Concat(sections.Where((element, index) => !sections.Take(index).Any(previous => previous.Value.Contains(element.Value, StringComparison.Ordinal)))
            .Select(element => "<div class=\"doc-paragraph\">" + HtmlInline(element, links) + "</div>"));
    }

    private static string MemberProse(TypeEntry type, MemberEntry member, IReadOnlyDictionary<string, string> links)
    {
        var doc = member.Doc;
        var details = doc.Element("fielddoc");
        var summary = details?.Element("meaning") ?? doc.Element("summary");
        var sections = new[] { summary, details?.Element("applicability"), details?.Element("absence"), doc.Element("remarks") }
            .Where(element => !string.IsNullOrWhiteSpace(element?.Value)).Cast<XElement>().ToArray();
        string prose = string.Concat(sections.DistinctBy(element => element.Value.Trim())
            .Select(element => "<div class=\"doc-paragraph\">" + HtmlInline(element, links) + "</div>"));
        if (doc.Element("instanceentry") is { } steps)
            prose += "<ol class=\"instance-steps\">" + string.Concat(steps.Elements("step").Select(step => "<li>" + HtmlInline(step, links) + "</li>")) + "</ol>";
        return prose;
    }
}
