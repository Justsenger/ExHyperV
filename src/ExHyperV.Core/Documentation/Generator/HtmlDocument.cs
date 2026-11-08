using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static partial class ApiDocument
{
    private static string RenderHtml(ScanResult scan, string version, string markdown, SourceIdentity source)
    {
        var businessSections = scan.Types.SelectMany(type => type.Doc.Elements("businessdoc")).ToArray();
        var businessCategories = CategoriesFor(scan).Where(path => path[0] == "业务层面" && path.Length == 2).Select(path => path[1]).ToHashSet(StringComparer.Ordinal);
        if (businessSections.GroupBy(section => (string?)section.Attribute("category") ?? "").Any(group => group.Count() != 1 || !businessCategories.Contains(group.Key)))
            throw new InvalidOperationException("业务说明的分类不存在或重复。");
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in scan.Types)
        {
            links[type.Name] = Anchor(type.Name);
            if (type.Symbol.GetDocumentationCommentId() is string id) links[id] = Anchor(type.Name);
            foreach (var member in type.Members) links[member.Id] = Anchor(member.Id);
        }
        foreach (var group in scan.Types.GroupBy(t => t.Symbol.Name).Where(g => g.Count() == 1))
            links[group.Key] = Anchor(group.Single().Name);
        foreach (var alias in MemberLinkAliases(scan)) links[alias.Key] = alias.Id;
        var memberLinks = scan.Types.SelectMany(type => type.Members).Select(member => Anchor(member.Id)).ToHashSet(StringComparer.Ordinal);

        var types = scan.Types.Select(type => new
        {
            id = Anchor(type.Name), name = type.Name,
            shortName = type.Symbol.ContainingNamespace.IsGlobalNamespace ? type.Name : type.Name[(type.Symbol.ContainingNamespace.ToDisplayString().Length + 1)..],
            @namespace = type.Symbol.ContainingNamespace.ToDisplayString(),
            kind = type.Symbol.IsRecord ? "record" : type.Symbol.TypeKind switch
            {
                TypeKind.Class => "类", TypeKind.Struct => "结构体", TypeKind.Interface => "接口",
                TypeKind.Enum => "枚举", TypeKind.Delegate => "委托", _ => "类型"
            },
            area = NavigationKey(DeclaredNavigationPaths(type)[0]), navigationPaths = NavigationPaths(type),
            signature = type.Signature, signatureHtml = HtmlCode(type.Signature, links),
            baseTypeHtml = type.Symbol.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType and not SpecialType.System_Enum } parent
                ? HtmlTypeName(parent, links) : "",
            interfacesHtml = string.Join(", ", type.Symbol.Interfaces.Select(symbol => HtmlTypeName(symbol, links))),
            derivedTypesHtml = string.Join(", ", scan.Types.Where(candidate => SymbolEqualityComparer.Default.Equals(candidate.Symbol.BaseType, type.Symbol))
                .Select(candidate => HtmlTypeName(candidate.Symbol, links))),
            source = type.Path, sources = type.Members.Select(member => member.Path).Prepend(type.Path).Distinct(StringComparer.Ordinal).ToArray(),
            summaryHtml = HtmlInline(type.Doc.Element("summary"), links),
            descriptionHtml = HtmlDoc(new XElement(type.Doc.Name, type.Doc.Elements().Where(element => element.Name.LocalName != "param")), links, includeSummary: false),
            members = type.Members.Select(member => new
            {
                id = Anchor(member.Id), name = member.Name == ".ctor" ? type.Symbol.Name : member.Name,
                navigationPath = MemberNavigationPaths(type, member)[0],
                navigationPaths = MemberNavigationPaths(type, member), source = member.Path,
                kind = member.Kind, group = HtmlMemberGroup(member), order = member.Line, signature = member.Signature,
                signatureHtml = HtmlCode(member.Signature, links),
                presentation = PresentMember(type, member, links),
                queryField = QueryFieldMetadata(type, member, links),
                hostEntry = HostEntryMetadata(type, member, scan),
                modification = ModificationMetadata(member, links, memberLinks),
                editEntry = NavigationEntryMetadata(member, "editentry"),
                queryEntry = NavigationEntryMetadata(member, "queryentry"),
                instanceEntry = NavigationEntryMetadata(member, "instanceentry"),
                summaryHtml = HtmlInline(member.Doc.Element("summary"), links),
                summaryText = Regex.Replace(member.Doc.Element("summary")?.Value ?? "", @"\s+", " ").Trim()
            }).ToArray()
        }).ToArray();
        var data = new
        {
            version,
            source = new { revision = source.Revision, contentSha256 = source.ContentSha256 },
            defaultTypeId = "query-fields",
            queryPath = NavigationPath("Management/VirtualMachines/Query/VmQueryService.cs"),
            categoryAliases = new[]
            {
                (Old: "业务层面 / 控制台", Current: "业务层面 / 连接"),
                (Old: "业务层面 / 键盘", Current: "业务层面 / 控制 / 键盘"),
                (Old: "业务层面 / 鼠标", Current: "业务层面 / 控制 / 鼠标")
            }.ToDictionary(entry => Anchor("category:" + entry.Old), entry => Anchor("category:" + entry.Current)),
            types, guides = HtmlGuides(markdown, scan), designs = HtmlDesigns(scan),
            metadataKeys = scan.Types.SelectMany(type => type.Members.Where(member => member.Doc.Element("metadatakey") != null)
                .Select(member => new
                {
                    navigationPaths = NavigationPaths(type),
                    key = MetadataKeyValue(type, member),
                    purpose = (string?)member.Doc.Element("metadatakey")!.Attribute("purpose") ?? "",
                    content = member.Doc.Element("metadatakey")!.Value.Trim()
                })).ToArray(),
            businessUsage = businessSections.Select(section => new
            {
                category = (string?)section.Attribute("category") ?? throw new InvalidOperationException("业务说明缺少分类。"),
                html = "<p class=\"lead\">" + HtmlInline(section.Element("summary"), links) + "</p>"
                    + string.Concat(section.Elements().Where(element => element.Name.LocalName != "summary").Select(element => Node(element, links)))
            }).ToArray(),
            hostUsage = HostUsageSections.Select(section => new
            {
                id = Anchor("hostusage:" + section.Area + "/" + section.Category),
                area = section.Area, category = section.Category, title = section.Title,
                html = HtmlDoc(scan.Types.Single(type => type.Symbol.Name == section.Owner).Doc, links, includeSummary: false)
            }).ToArray(),
            categories = CategoriesFor(scan).Select(path => new
            {
                id = Anchor("category:" + NavigationKey(path)), path, area = NavigationKey(path),
                isEmpty = !types.Any(type => type.navigationPaths.Any(item =>
                    NavigationKey(item) == NavigationKey(path) || NavigationKey(item).StartsWith(NavigationKey(path) + " / ", StringComparison.Ordinal))),
                guideId = path[0] == "使用说明" ? "guide-" + GuideSlug(path[1]) : null
            }).ToArray()
        };
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Generator.WebTemplate.html")
            ?? throw new InvalidOperationException("缺少网页模板。");
        using var reader = new StreamReader(stream);
        string template = reader.ReadToEnd();
        const string marker = "__API_DATA__";
        if (template.IndexOf(marker, StringComparison.Ordinal) < 0 || template.IndexOf(marker, StringComparison.Ordinal) != template.LastIndexOf(marker, StringComparison.Ordinal))
            throw new InvalidOperationException("网页模板必须恰好包含一个数据标记。");
        using var styleStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Generator.WebStyles.css")
            ?? throw new InvalidOperationException("缺少网页样式。");
        using var styleReader = new StreamReader(styleStream);
        using var fontStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Generator.Comfortaa.woff2")
            ?? throw new InvalidOperationException("缺少网页英文字体。");
        using var fontBytes = new MemoryStream();
        fontStream.CopyTo(fontBytes);
        using var licenseStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Generator.Comfortaa-OFL.txt")
            ?? throw new InvalidOperationException("缺少网页字体许可证。");
        using var licenseReader = new StreamReader(licenseStream);
        string styles = styleReader.ReadToEnd().Replace("__BRAND_FONT__", Convert.ToBase64String(fontBytes.ToArray()), StringComparison.Ordinal);
        // The default encoder escapes HTML-significant characters, including </script> in documentation.
        // The standalone document includes its styles, Latin font and that font's complete license.
        return template.Replace("__WEB_STYLES__", styles, StringComparison.Ordinal)
            .Replace("__FONT_LICENSE__", WebUtility.HtmlEncode(licenseReader.ReadToEnd()), StringComparison.Ordinal)
            .Replace(marker, JsonSerializer.Serialize(data), StringComparison.Ordinal);
    }

    private static string MetadataKeyValue(TypeEntry type, MemberEntry member) =>
        type.Symbol.GetMembers(member.Name).OfType<IFieldSymbol>().SingleOrDefault() is { IsConst: true, ConstantValue: string value }
            ? value : throw new InvalidOperationException($"{type.Name}.{member.Name} 的元数据键说明必须标在字符串常量上。");

    private static string HtmlMemberGroup(MemberEntry member) => member.Kind switch
    {
        "构造器" or "主构造器" => "构造函数",
        "属性" or "生成属性" or "record 属性" => "属性",
        "生成命令" => "命令",
        "事件" => "事件",
        "枚举值" => "枚举值",
        "字段" => "字段与常量",
        _ when member.Signature.Contains(" operator ", StringComparison.Ordinal) => "运算符",
        _ when Regex.IsMatch(member.Signature, @"\bprotected\b") => "受保护方法",
        _ when member.Signature.Contains(" static ", StringComparison.Ordinal) => "静态方法",
        _ => "方法"
    };

    private static string H(string text) => WebUtility.HtmlEncode(text);
    private static string HtmlTypeName(INamedTypeSymbol symbol, IReadOnlyDictionary<string, string> links)
    {
        string label = H(symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        return links.TryGetValue(symbol.ToDisplayString(), out string? id) ? $"<a href=\"#{H(id)}\"><code>{label}</code></a>" : $"<code>{label}</code>";
    }
    private static string HtmlCode(string code, IReadOnlyDictionary<string, string> links)
    {
        var output = new StringBuilder();
        foreach (var token in SyntaxFactory.ParseTokens(code))
        {
            output.Append(H(token.LeadingTrivia.ToFullString()));
            string content = H(token.Text);
            if (token.IsKind(SyntaxKind.IdentifierToken) && links.TryGetValue(token.ValueText, out var id))
                content = $"<a href=\"#{H(id)}\">{content}</a>";
            else if (SyntaxFacts.IsKeywordKind(token.Kind())) content = $"<span class=\"keyword\">{content}</span>";
            else if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.NumericLiteralToken)) content = $"<span class=\"literal\">{content}</span>";
            output.Append(content).Append(H(token.TrailingTrivia.ToFullString()));
        }
        return output.ToString();
    }

    private static string HtmlInline(XElement? element, IReadOnlyDictionary<string, string> links) => element == null ? "" : string.Concat(element.Nodes().Select(node => Node(node, links))).Trim();

    private static string Node(XNode node, IReadOnlyDictionary<string, string> links) => node switch
    {
        XText text => H(Regex.Replace(text.Value, @"\s+", " ")),
        XElement e when e.Name.LocalName is "c" or "paramref" or "typeparamref" => "<code>" + H(e.Attribute("name")?.Value ?? e.Value.Trim()) + "</code>",
        XElement e when e.Name.LocalName == "see" => HtmlSee(e, links),
        XElement e when e.Name.LocalName == "code" => "<pre><code>" + H(Dedent(e.Value)) + "</code></pre>",
        XElement e when e.Name.LocalName == "para" => "<div class=\"doc-paragraph\">" + HtmlInline(e, links) + "</div>",
        XElement e when e.Name.LocalName == "list" => "<ul>" + string.Concat(e.Elements("item").Select(item => "<li>" + HtmlInline(item, links) + "</li>")) + "</ul>",
        XElement e when e.Name.LocalName == "fielddoc" => "",
        XElement e => HtmlInline(e, links), _ => ""
    };

    private static string HtmlSee(XElement element, IReadOnlyDictionary<string, string> links)
    {
        string? reference = (string?)element.Attribute("cref");
        string label = element.Value.Length > 0 ? element.Value : reference ?? (string?)element.Attribute("langword") ?? (string?)element.Attribute("href") ?? "";
        if (reference != null && links.TryGetValue(reference, out var id)) return $"<a href=\"#{H(id)}\"><code>{H(label)}</code></a>";
        if (element.Attribute("href") is XAttribute href && SafeUrl(href.Value)) return $"<a href=\"{H(href.Value)}\">{H(label)}</a>";
        return "<code>" + H(label) + "</code>";
    }

    private static string HtmlDoc(XElement doc, IReadOnlyDictionary<string, string> links, bool includeSummary)
    {
        var output = new StringBuilder();
        if (includeSummary && doc.Element("summary") is XElement summary)
            output.Append("<div class=\"doc-paragraph\">").Append(HtmlInline(summary, links)).Append("</div>");
        if (doc.Element("instanceentry") is XElement instanceGuide)
        {
            output.Append("<p><strong>调用顺序</strong></p><ol class=\"instance-steps\">");
            foreach (var step in instanceGuide.Elements("step")) output.Append("<li>").Append(HtmlInline(step, links)).Append("</li>");
            output.Append("</ol>");
        }
        foreach (string tag in new[] { "typeparam", "param" })
        {
            var entries = doc.Elements(tag).ToArray();
            if (entries.Length == 0) continue;
            output.Append("<table class=\"parameter-table\"><thead><tr><th>").Append(tag == "param" ? "参数" : "类型参数")
                .Append("</th><th>说明</th></tr></thead><tbody>");
            foreach (var entry in entries) output.Append("<tr><td><code>").Append(H((string?)entry.Attribute("name") ?? ""))
                .Append("</code></td><td>").Append(HtmlInline(new XElement(entry.Name, entry.Nodes().Where(node => node is not XElement nested || nested.Name.LocalName != "fielddoc")), links)).Append("</td></tr>");
            output.Append("</tbody></table>");
        }
        foreach (var (tag, label) in new[] { ("returns", "返回值"), ("value", "属性值"), ("remarks", "") })
            if (doc.Element(tag) is XElement element) output.Append("<div class=\"doc-paragraph\">")
                .Append(label.Length == 0 ? "" : "<strong>" + label + "：</strong>")
                .Append(HtmlInline(element, links)).Append("</div>");
        if (doc.Elements("exception").Any())
        {
            output.Append("<p><strong>异常</strong></p><dl class=\"exceptions\">");
            foreach (var exception in doc.Elements("exception")) output.Append("<dt><code>").Append(H((string?)exception.Attribute("cref") ?? ""))
                .Append("</code></dt><dd>").Append(HtmlInline(exception, links)).Append("</dd>");
            output.Append("</dl>");
        }
        foreach (var example in doc.Elements("example")) output.Append("<details class=\"example\"><summary>示例</summary>")
            .Append(HtmlInline(example, links)).Append("</details>");
        return output.ToString();
    }

    private static object[] HtmlGuides(string markdown, ScanResult scan)
    {
        string content = Regex.Replace(markdown, @"<!-- api-reference:start -->.*?<!-- api-reference:end -->", "", RegexOptions.Singleline);
        content = Regex.Replace(content, @"<!--.*?-->", "", RegexOptions.Singleline);
        var headings = Regex.Matches(content, @"(?m)^## (.+)$").ToArray();
        var guides = new List<object>();
        foreach (var (heading, index) in headings.Select((h, i) => (h, i)))
        {
            string title = heading.Groups[1].Value.Trim();
            int start = heading.Index + heading.Length, end = index + 1 < headings.Length ? headings[index + 1].Index : content.Length;
            string body = content[start..end].Trim();
            if (body.Length == 0) continue;
            string html = GuideMarkdown(body);
            guides.Add(new { id = "guide-" + GuideSlug(title), title, html,
                summaryHtml = Regex.Match(html, @"^<p>(.*?)</p>", RegexOptions.Singleline).Groups[1].Value,
                relatedTypeIds = scan.Types.Where(type => DeclaredNavigationPaths(type)
                    .Any(path => path[0] == "使用说明" && path[1] == title))
                    .Select(type => Anchor(type.Name)).ToArray() });
        }
        foreach (var path in CategoriesFor(scan).Where(path => path[0] == "使用说明"))
            if (!headings.Any(heading => heading.Groups[1].Value.Trim() == path[1]))
                throw new InvalidOperationException($"缺少使用说明章节：{path[1]}。");
        return guides.ToArray();
    }

    private static string GuideSlug(string text) => Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}\p{M}_\-\s]", "").Replace(' ', '-');
    private static bool SafeUrl(string url) => url.StartsWith('#') || (Uri.TryCreate(url, UriKind.Absolute, out var value) && value.Scheme is "https" or "http");

    // Only the hand-maintained introductory sections use Markdown. Interface details are rendered from XML.
    private static string GuideMarkdown(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
        var result = new StringBuilder();
        var paragraph = new List<string>();
        string? list = null;
        void Flush()
        {
            if (paragraph.Count > 0) result.Append("<p>").Append(GuideInline(string.Join(" ", paragraph))).Append("</p>");
            paragraph.Clear();
        }
        void EndList() { if (list != null) { result.Append("</").Append(list).Append('>'); list = null; } }
        void BeginList(string kind, string start = "1")
        {
            if (list == kind) return;
            EndList(); list = kind; result.Append('<').Append(kind);
            if (kind == "ol" && start != "1") result.Append(" start=\"").Append(H(start)).Append('"');
            result.Append('>');
        }
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                Flush(); EndList(); var code = new StringBuilder();
                while (++i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal)) code.AppendLine(lines[i]);
                result.Append("<pre><code>").Append(H(code.ToString().TrimEnd())).Append("</code></pre>");
            }
            else if (Regex.Match(line, @"^(#{3,6}) (.+)$") is { Success: true } heading)
            {
                Flush(); EndList(); string title = heading.Groups[2].Value;
                int level = heading.Groups[1].Length - 1;
                result.Append("<h").Append(level).Append(" id=\"guide-section-").Append(H(GuideSlug(title))).Append("\">")
                    .Append(GuideInline(title)).Append("</h").Append(level).Append('>');
            }
            else if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && IsGuideTableSeparator(lines[i + 1]))
            {
                Flush(); EndList();
                string[] headers = GuideTableCells(line);
                if (GuideTableCells(lines[i + 1]).Length != headers.Length) throw new InvalidOperationException("说明表格的列数不一致。");
                result.Append("<table class=\"guide-table\"><thead><tr>");
                foreach (string header in headers) result.Append("<th>").Append(GuideInline(header)).Append("</th>");
                result.Append("</tr></thead><tbody>");
                i++;
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('|'))
                {
                    string[] cells = GuideTableCells(lines[++i]);
                    if (cells.Length != headers.Length) throw new InvalidOperationException("说明表格的列数不一致：" + lines[i]);
                    result.Append("<tr>");
                    foreach (string cell in cells) result.Append("<td>").Append(GuideInline(cell)).Append("</td>");
                    result.Append("</tr>");
                }
                result.Append("</tbody></table>");
            }
            else if (Regex.Match(line, @"^(\d+)\. (.+)$") is { Success: true } ordered)
            {
                Flush(); BeginList("ol", ordered.Groups[1].Value);
                result.Append("<li>").Append(GuideInline(ordered.Groups[2].Value)).Append("</li>");
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                Flush(); BeginList("ul");
                result.Append("<li>").Append(GuideInline(line[2..])).Append("</li>");
            }
            else if (string.IsNullOrWhiteSpace(line)) { Flush(); EndList(); }
            else { EndList(); paragraph.Add(line); }
        }
        Flush(); EndList(); return result.ToString();
    }

    private static bool IsGuideTableSeparator(string line) => line.TrimStart().StartsWith('|')
        && GuideTableCells(line).All(cell => Regex.IsMatch(cell, @"^:?-{3,}:?$"));

    private static string[] GuideTableCells(string line)
    {
        string content = line.Trim();
        if (content.StartsWith('|')) content = content[1..];
        if (content.EndsWith('|')) content = content[..^1];
        var cells = new List<string>(); var cell = new StringBuilder(); bool code = false;
        for (int i = 0; i < content.Length; i++)
        {
            char value = content[i];
            if (value == '\\' && i + 1 < content.Length && content[i + 1] == '|') { cell.Append('|'); i++; }
            else if (value == '`') { code = !code; cell.Append(value); }
            else if (value == '|' && !code) { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(value);
        }
        cells.Add(cell.ToString().Trim()); return cells.ToArray();
    }

    private static string GuideInline(string source)
    {
        var result = new StringBuilder(); int offset = 0;
        foreach (Match match in Regex.Matches(source, @"`([^`]+)`|\*\*(.+?)\*\*|\[([^\]]+)\]\(([^)]+)\)"))
        {
            result.Append(H(source[offset..match.Index]));
            if (match.Groups[1].Success) result.Append("<code>").Append(H(match.Groups[1].Value)).Append("</code>");
            else if (match.Groups[2].Success) result.Append("<strong>").Append(H(match.Groups[2].Value)).Append("</strong>");
            else
            {
                string href = match.Groups[4].Value;
                if (href.StartsWith('#') && !href.StartsWith("#api-", StringComparison.Ordinal)) href = "#guide-" + href[1..];
                if (SafeUrl(href)) result.Append("<a href=\"").Append(H(href)).Append("\">").Append(H(match.Groups[3].Value)).Append("</a>");
                else result.Append(H(match.Groups[3].Value));
            }
            offset = match.Index + match.Length;
        }
        return result.Append(H(source[offset..])).ToString();
    }
}
