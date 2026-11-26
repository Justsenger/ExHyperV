internal static partial class ApiDocument
{
    private static readonly Dictionary<string, string> ReferenceLabels = new(StringComparer.Ordinal);

    private static void IndexReferenceLabels(ScanResult scan)
    {
        ReferenceLabels.Clear();
        foreach (var type in scan.Types)
        {
            string owner = type.Symbol.ContainingNamespace.IsGlobalNamespace ? type.Name
                : type.Name[(type.Symbol.ContainingNamespace.ToDisplayString().Length + 1)..];
            ReferenceLabels[type.Name] = owner;
            if (type.Symbol.GetDocumentationCommentId() is { } typeId) ReferenceLabels[typeId] = owner;
            foreach (var member in type.Members)
                ReferenceLabels[member.Id] = member.Kind is "构造器" or "主构造器" ? owner : owner + "." + member.Name;
        }
    }

    // Keep the full documentation ID for link resolution; show the C# owner and member name.
    private static string ReferenceLabel(string reference)
    {
        if (ReferenceLabels.TryGetValue(reference, out var label)) return label;
        if (reference.Length < 3 || reference[1] != ':') return reference;
        string name = reference[2..].Split('(')[0];
        int memberSeparator = name.LastIndexOf('.');
        int start = reference[0] == 'T' ? memberSeparator
            : memberSeparator > 0 ? name.LastIndexOf('.', memberSeparator - 1) : -1;
        return name[(start + 1)..];
    }
}
