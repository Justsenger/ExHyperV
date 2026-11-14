using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

internal static partial class ApiDocument
{
    // These are existing read models, not a declaration that field selection is implemented.
    private static readonly HashSet<string> FieldDocumentationModels = new(StringComparer.Ordinal)
    {
        "VmInstance", "VmProcessorSettings", "VmMemorySettings", "VmNetworkAdapter",
        "VmStorageAttachment", "VmDiskStatus", "VmGpuAssignment", "VmPcieSettings",
        "VmPcieDeviceSetting", "BootOrderItem", "SpacetimeNode", "VmMmioSettings",
        "VmSecurityInfo", "VmDisplayResolution", "VmBatteryState",
        "VmConsoleState", "AffinityState", "VmCpuCoreMetric", "VmDynamicMemoryData", "GpuUsageData",
        "VmGroup", "CheckpointInfo"
    };

    private static readonly (string Tag, string Label)[] FieldDocumentationSections =
    [
        ("meaning", "详细含义"),
        ("unit", "单位与表示"),
        ("format", "文件格式"),
        ("values", "取值与默认"),
        ("source", "数据来源"),
        ("applicability", "适用条件"),
        ("absence", "缺测与错误"),
        ("verification", "验证记录"),
        ("references", "参考资料"),
        ("modification", "修改对应")
    ];

    private static bool HasFieldDocumentation(TypeEntry type, MemberEntry member) =>
        member.Kind is "属性" or "生成属性" or "record 属性" or "字段"
        && (FieldDocumentationModels.Contains(type.Symbol.Name) || member.Doc.Element("fielddoc") != null);

    private static bool IsQueryField(TypeEntry type, MemberEntry member) => member.Kind == "字段"
        && type.Symbol.GetMembers(member.Name).OfType<IFieldSymbol>().SingleOrDefault()
            is { IsStatic: true, Type: INamedTypeSymbol { Name: "VmQueryField", Arity: 1 } fieldType }
        && fieldType.ContainingNamespace.ToDisplayString() == "ExHyperV.Services";

    private static XElement MemberDescription(TypeEntry type, MemberEntry member)
    {
        if (!IsQueryField(type, member) || member.Doc.Element("fielddoc")?.Element("meaning") is not { } meaning
            || string.IsNullOrWhiteSpace(meaning.Value)) return member.Doc;
        var description = new XElement(member.Doc);
        description.Element("summary")?.Remove();
        description.AddFirst(new XElement("summary", meaning.Nodes()));
        return description;
    }

    private static IEnumerable<(string Label, XElement Content)> FieldDocumentation(TypeEntry type, MemberEntry member)
    {
        if (!HasFieldDocumentation(type, member)) yield break;
        var details = member.Doc.Element("fielddoc");
        bool queryField = IsQueryField(type, member);
        foreach (var section in FieldDocumentationSections)
        {
            if (section.Tag is "source" or "verification" or "references") continue;
            if (queryField && section.Tag is "meaning" or "modification") continue;
            var content = details?.Element(section.Tag);
            if (string.IsNullOrWhiteSpace(content?.Value)) continue;
            if (section.Tag == "modification")
            {
                if (content == null) continue;
                string kind = (string?)content.Attribute("kind") ?? "";
                string prefix = ModificationKinds.GetValueOrDefault(kind, kind);
                if (content.Attribute("input") is { } input) prefix += "；输入：" + input.Value;
                if (content.Attribute("submission") is { } submission) prefix += "；提交方式：" + submission.Value;
                content = new XElement(section.Tag, prefix + "。", content.Nodes());
            }
            string label = queryField ? section.Tag switch
            {
                "unit" => "单位与格式",
                "values" => "取值说明",
                _ => section.Label
            } : section.Label;
            yield return (label, content!);
        }
    }

    private static void ValidateFieldDocumentation(XElement doc, string label, List<string> errors)
    {
        var blocks = doc.Descendants("fielddoc").ToArray();
        if (blocks.GroupBy(block => block.Parent).Any(group => group.Count() > 1))
            errors.Add(label + " 存在重复 fielddoc。");
        foreach (var block in blocks)
            foreach (var group in block.Elements().GroupBy(element => element.Name.LocalName))
            {
                if (!FieldDocumentationSections.Any(section => section.Tag == group.Key))
                    errors.Add(label + " 存在未知字段说明项：" + group.Key);
                if (group.Count() > 1) errors.Add(label + " 存在重复字段说明项：" + group.Key);
                foreach (var element in group)
                    if (element.Name.LocalName == "values" && element.Attribute("kind") is { } kind && kind.Value is not ("options" or "format"))
                        errors.Add(label + " 的 values kind 只支持 options 或 format。");
            }
    }

    private static void AppendFieldDocumentation(StringBuilder output, TypeEntry type, MemberEntry member)
    {
        var rows = FieldDocumentation(type, member).ToArray();
        if (IsQueryField(type, member))
        {
            foreach (var row in rows) output.AppendLine($"**{row.Label}：** {Inline(row.Content)}").AppendLine();
        }
        else if (rows.Length > 0)
        {
            output.AppendLine("**字段说明**").AppendLine();
            output.AppendLine("| 项目 | 说明 |").AppendLine("| --- | --- |");
            foreach (var row in rows) output.AppendLine($"| {row.Label} | {Cell(Inline(row.Content))} |");
            output.AppendLine();
        }
        if (member.Doc.Element("configurationversions") != null)
            AppendConfigurationVersions(output);
    }

    private static string HtmlFieldDocumentation(TypeEntry type, MemberEntry member, IReadOnlyDictionary<string, string> links,
        bool includeConfigurationVersions = true, bool includeSeparateValues = true)
    {
        var rows = FieldDocumentation(type, member)
            .Where(row => includeSeparateValues || row.Content.Name.LocalName != "values" || (string?)row.Content.Attribute("kind") is not ("options" or "format"))
            .ToArray();
        var output = new StringBuilder();
        if (IsQueryField(type, member))
        {
            foreach (var row in rows) output.Append("<div class=\"doc-paragraph\">")
                .Append(HtmlInline(row.Content, links)).Append("</div>");
        }
        else if (rows.Length > 0)
        {
            output.Append("<p><strong>字段说明</strong></p><table class=\"parameter-table field-documentation\"><thead><tr><th>项目</th><th>说明</th></tr></thead><tbody>");
            foreach (var row in rows) output.Append("<tr><td>").Append(H(row.Label)).Append("</td><td>")
                .Append(HtmlInline(row.Content, links)).Append("</td></tr>");
            output.Append("</tbody></table>");
        }
        if (includeConfigurationVersions && member.Doc.Element("configurationversions") != null)
            output.Append(HtmlConfigurationVersions(Anchor(member.Id)));
        return output.ToString();
    }
}
