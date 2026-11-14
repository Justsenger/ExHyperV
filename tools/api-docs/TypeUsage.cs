using Microsoft.CodeAnalysis;
using System.Text.RegularExpressions;

internal static partial class ApiDocument
{
    private static object MemberTypeUsage(TypeEntry type, MemberEntry member, IReadOnlyDictionary<string, string> links, ScanResult scan)
    {
        var symbol = type.Symbol.GetMembers().FirstOrDefault(candidate => candidate.GetDocumentationCommentId() == member.Id)
            ?? type.Symbol.GetMembers(member.Name).FirstOrDefault();
        string[] References(ITypeSymbol? value)
        {
            if (value is IArrayTypeSymbol array) return References(array.ElementType);
            if (value is not INamedTypeSymbol named) return [];
            string? key = named.OriginalDefinition.GetDocumentationCommentId();
            var own = key != null && links.TryGetValue(key, out var id) ? new[] { id } : [];
            var derived = own.Length == 0 ? [] : scan.Types.Where(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.Symbol.BaseType?.OriginalDefinition, named.OriginalDefinition))
                .Select(candidate => Anchor(candidate.Name)).ToArray();
            return own.Concat(derived).Concat(named.TypeArguments.SelectMany(References)).Distinct().ToArray();
        }
        string[] exceptions = member.Doc.Elements("exception").Select(element => (string?)element.Attribute("cref"))
            .Where(key => key != null && links.ContainsKey(key)).Select(key => links[key!]).Distinct().ToArray();
        if (symbol is IMethodSymbol method)
            return new
            {
                parameters = method.Parameters.Select(parameter => new { name = parameter.Name, types = References(parameter.Type) }).ToArray(),
                returns = method.MethodKind == MethodKind.Constructor ? Array.Empty<string>() : References(method.ReturnType),
                value = Array.Empty<string>(), exceptions
            };
        var valueType = symbol switch
        {
            IPropertySymbol property => property.Type,
            IFieldSymbol field => field.Type,
            IEventSymbol eventSymbol => eventSymbol.Type,
            _ => null
        };
        // ObservableProperty members are supplied by the source generator, not this scan.
        // Resolve their recorded declaration through the same type links as the signature.
        string[] valueReferences = valueType == null
            ? Regex.Matches(HtmlCode((string?)member.Doc.Attribute("propertyType") ?? "", links), "href=\"#([^\"]+)\"")
                .Select(match => match.Groups[1].Value).Distinct().ToArray()
            : References(valueType);
        return new { parameters = Array.Empty<object>(), returns = Array.Empty<string>(), value = valueReferences, exceptions };
    }
}
