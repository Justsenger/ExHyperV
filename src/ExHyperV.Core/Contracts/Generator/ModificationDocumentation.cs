using System.Xml.Linq;

internal static partial class ApiDocument
{
    private static readonly IReadOnlyDictionary<string, string> ModificationKinds = new Dictionary<string, string>
    {
        ["supported"] = "读写",
        ["readonly"] = "只读",
        ["unavailable"] = "只读",
        ["operation"] = "只读"
    };

    private static IEnumerable<(string Key, string Id)> MemberLinkAliases(ScanResult scan) =>
        scan.Types.SelectMany(type => type.Members.SelectMany(member => new[]
        {
            (Key: type.Symbol.Name + "." + member.Name, Id: Anchor(member.Id)),
            (Key: type.Name + "." + member.Name, Id: Anchor(member.Id))
        }))
        .Distinct()
        .GroupBy(item => item.Key, StringComparer.Ordinal).Where(group => group.Count() == 1)
        .Select(group => group.Single());

    private static void ValidateModificationDocumentation(ScanResult scan)
    {
        var links = MemberLinkAliases(scan).Select(item => item.Key)
            .Concat(scan.Types.Select(type => type.Symbol.Name))
            .Concat(scan.Types.SelectMany(type => type.Members.Select(member => member.Id)))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var type in scan.Types)
        foreach (var member in type.Members)
        {
            string label = type.Symbol.Name + "." + member.Name;
            if (member.Kind == "字段" && member.Path.StartsWith("Management/VirtualMachines/Query/Fields/", StringComparison.Ordinal))
            {
                var notes = member.Doc.Element("fielddoc")?.Elements("modification").ToArray() ?? [];
                if (notes.Length != 1) { scan.Errors.Add(label + " 必须有且只有一项查询与修改对应说明。"); continue; }
                var note = notes[0];
                if (note.Value.Contains("changedProperties", StringComparison.Ordinal)
                    || note.Value.Contains("nameof(", StringComparison.Ordinal))
                    scan.Errors.Add(label + " 的提交步骤应写在修改方法的参数或示例中，不能混入字段说明。");
                string kind = (string?)note.Attribute("kind") ?? "";
                if (!ModificationKinds.ContainsKey(kind))
                    scan.Errors.Add(label + " 修改状态无效。");
                if (kind == "supported" && (string.IsNullOrWhiteSpace((string?)note.Attribute("input"))
                    || string.IsNullOrWhiteSpace((string?)note.Attribute("submission"))))
                    scan.Errors.Add(label + " 缺少修改输入或提交方式。");
                var references = note.Descendants("see").Select(element => (string?)element.Attribute("cref") ?? "").ToArray();
                if (kind is "supported" or "operation" && references.Length == 0)
                    scan.Errors.Add(label + " 缺少修改或操作入口。");
                foreach (string reference in references)
                    if (!links.Contains(reference)) scan.Errors.Add(label + " 引用了未公开、已改名或不明确的入口：" + reference);
            }
            foreach (string tag in new[] { "editentry", "queryentry", "instanceentry" })
            {
                string area = tag switch { "editentry" => "修改", "queryentry" => "查询", _ => "实例" };
                var entries = member.Doc.Elements(tag).ToArray();
                if (entries.Length > 1) scan.Errors.Add(label + " 同类目录入口不能重复。");
                foreach (var entry in entries)
                {
                    string category = (string?)entry.Attribute("category") ?? "";
                    if (string.IsNullOrWhiteSpace((string?)entry.Attribute("label")) ||
                        !NavigationCatalog.Any(item => IsEntryPath(item.Path, area, category)))
                        scan.Errors.Add(label + " " + area + "入口的名称或分类无效。");
                    if (member.Kind != "方法") scan.Errors.Add(label + " 目录入口必须是方法。");
                    if (!MemberNavigationPaths(type, member).Any(path => IsEntryPath(path, area, category)))
                        scan.Errors.Add(label + " 目录入口与方法的功能归属不一致。");
                    if (tag == "instanceentry" &&
                        (!int.TryParse((string?)entry.Attribute("order"), out int order) || order < 0 ||
                         !entry.Elements("step").Any() || entry.Elements("step").Any(step => string.IsNullOrWhiteSpace(step.Value))))
                        scan.Errors.Add(label + " 实例入口需要明确排序及非空调用步骤。");
                    if (tag == "instanceentry" && entry.Attribute("mode") is XAttribute mode && string.IsNullOrWhiteSpace(mode.Value))
                        scan.Errors.Add(label + " 实例模式名称不能为空。");
                }
            }
        }
        foreach (var group in scan.Types.SelectMany(type => type.Members)
            .Select(member => member.Doc.Element("instanceentry")).OfType<XElement>()
            .GroupBy(entry => ((string?)entry.Attribute("category"), (string?)entry.Attribute("label")))
            .Where(group => group.Count() > 1))
        {
            var modes = group.Select(entry => (string?)entry.Attribute("mode")).ToArray();
            if (modes.Any(string.IsNullOrWhiteSpace) || modes.Distinct(StringComparer.Ordinal).Count() != modes.Length)
                scan.Errors.Add(group.Key.Item2 + " 的同名实例入口必须具有各不相同的模式名称。");
        }
    }

    private static bool IsEntryPath(string[] path, string area, string category) =>
        area == "实例"
            ? path.Length == 4 && path[2] == area && path[3] == category
            : path.Length == 4 && ((path[2] == area && path[3] == category) || (path[2] == category && path[3] == area));

    private static object? ModificationMetadata(MemberEntry member, IReadOnlyDictionary<string, string> links, IReadOnlySet<string> memberLinks)
    {
        var note = member.Doc.Element("fielddoc")?.Element("modification");
        if (note == null) return null;
        string kind = (string)note.Attribute("kind")!;
        string[] memberIds = note.Descendants("see")
            .Select(reference => (string?)reference.Attribute("cref"))
            .Where(reference => reference != null && links.TryGetValue(reference, out var id) && memberLinks.Contains(id))
            .Select(reference => links[reference!]).Distinct().ToArray();
        string membersHtml = string.Join("、", note.Descendants("see")
            .Where(reference => reference.Attribute("cref") is { } cref && links.TryGetValue(cref.Value, out var id) && memberLinks.Contains(id))
            .DistinctBy(reference => (string?)reference.Attribute("cref"))
            .Select(reference => Node(reference, links)));
        var description = new XElement(note);
        description.Elements("see").Remove();
        string html = string.IsNullOrWhiteSpace(description.Value) ? "" : HtmlInline(note, links);
        return new { kind, status = ModificationKinds[kind], input = (string?)note.Attribute("input") ?? "",
            membersHtml, memberIds, submission = (string?)note.Attribute("submission") ?? "", html };
    }

    private static object? NavigationEntryMetadata(MemberEntry member, string tag)
    {
        var note = member.Doc.Element(tag);
        return note == null ? null : new { label = (string)note.Attribute("label")!, category = (string)note.Attribute("category")!,
            order = (int?)note.Attribute("order") ?? 0, mode = (string?)note.Attribute("mode") };
    }
}
