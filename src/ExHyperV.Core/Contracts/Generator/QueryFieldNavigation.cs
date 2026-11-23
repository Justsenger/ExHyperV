using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static partial class ApiDocument
{
    // Documentation may refine a display label without changing the runtime field metadata.
    private sealed record QueryFieldNavigation(string key, string label, string category, string expression, string valueType,
        int order, string summaryHtml, string? enumTypeId, string valuesHtml, string descriptionHtml, string detailsHtml);

    private static QueryFieldNavigation? QueryFieldMetadata(TypeEntry type, MemberEntry member, IReadOnlyDictionary<string, string> links)
    {
        if (member.Kind != "字段"
            || type.Symbol.GetMembers(member.Name).OfType<IFieldSymbol>().SingleOrDefault() is not { IsStatic: true } field
            || field.Type is not INamedTypeSymbol { Name: "VmQueryField", Arity: 1 } fieldType
            || fieldType.ContainingNamespace.ToDisplayString() != "ExHyperV.Services") return null;

        var declaration = field.DeclaringSyntaxReferences.Single().GetSyntax() as VariableDeclaratorSyntax;
        var arguments = declaration?.Initializer?.Value switch
        {
            ImplicitObjectCreationExpressionSyntax creation => creation.ArgumentList.Arguments,
            ObjectCreationExpressionSyntax creation => creation.ArgumentList?.Arguments,
            _ => null
        };
        string Text(int index) => arguments is { } values && values.Count > index
            && values[index].Expression is LiteralExpressionSyntax { Token.Value: string text } ? text
            : throw new InvalidOperationException($"查询字段 {type.Name}.{member.Name} 的标识、名称及分类必须可从声明中读取。");
        string label = (string?)member.Doc.Element("fielddoc")?.Attribute("label") ?? Text(1);
        var valueType = fieldType.TypeArguments[0];
        var enumType = valueType is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0] : valueType;
        string? enumTypeId = enumType.TypeKind == TypeKind.Enum && links.TryGetValue(enumType.ToDisplayString(), out var enumLink)
            ? enumLink : null;
        return new(Text(0), label, Text(2), type.Symbol.Name + "." + member.Name,
            valueType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), declaration!.SpanStart,
            QueryTableSummary(member.Doc.Element("summary"), label, links),
            enumTypeId, MemberValues(member, links), MemberProse(type, member, links),
            member.Doc.Element("configurationversions") != null ? HtmlConfigurationVersions(Anchor(member.Id)) : "");
    }

    private static string QueryTableSummary(XElement? summary, string label, IReadOnlyDictionary<string, string> links)
    {
        if (summary == null) return "待补充说明。";
        // Only the table repeats the label in its preceding column. Keep source and detail summaries intact.
        var display = new XElement(summary);
        if (display.FirstNode is XText first)
            first.Value = Regex.Replace(first.Value, @"^\s*" + Regex.Escape(label) + @"\s*[：:]\s*", "");
        string text = display.Value.Trim();
        if (text.Length == 0 || text == label || text == label + "。") return "待补充说明。";
        return HtmlInline(display, links);
    }
}
