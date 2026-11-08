using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static partial class ApiDocument
{
    // Design prose and vector figures have one source. They are embedded in the
    // generated document, without fetching a diagram library or separate assets.
    private static object[] HtmlDesigns(ScanResult scan)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Generator.DesignPages.xml")
            ?? throw new InvalidOperationException("缺少设计图材料。");
        var source = XDocument.Load(stream);
        if (source.Root?.Name != "designs") throw new InvalidOperationException("设计图材料的根节点必须是 designs。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return source.Root.Elements("page").Select(page =>
        {
            string id = Required(page, "id"), title = Required(page, "title");
            if (!Regex.IsMatch(id, "^design-[a-z0-9-]+$") || !ids.Add(id))
                throw new InvalidOperationException($"设计图页面身份重复或无效：{id}。");
            string status = Required(page, "status");
            if (status is not ("draft" or "current")) throw new InvalidOperationException($"设计图状态无效：{id}。");
            string summary = page.Element("summary")?.Value.Trim()
                ?? throw new InvalidOperationException($"设计图缺少简介：{id}。");
            var sections = page.Elements("section").Select(section =>
            {
                string sectionName = Required(section, "id");
                string sectionId = id + "--" + sectionName;
                if (!Regex.IsMatch(sectionName, "^[a-z0-9-]+$") || !ids.Add(sectionId))
                    throw new InvalidOperationException($"设计图章节身份重复或无效：{sectionId}。");
                var body = new XElement(section);
                foreach (var reference in body.Descendants("api").ToArray())
                {
                    string name = Required(reference, "name");
                    var matches = scan.Types.Where(type => type.Symbol.Name == name || type.Name == name).ToArray();
                    if (matches.Length != 1) throw new InvalidOperationException($"设计图接口引用不能唯一解析：{name}。");
                    reference.ReplaceWith(new XElement("a", new XAttribute("href", "#" + Anchor(matches[0].Name)),
                        string.IsNullOrWhiteSpace(reference.Value) ? name : reference.Value));
                }
                foreach (var figure in body.Descendants().Where(element => element.Name.LocalName == "svg"))
                {
                    XNamespace svg = "http://www.w3.org/2000/svg";
                    if (figure.Name.Namespace != svg || figure.Attribute("viewBox") == null
                        || figure.Element(svg + "title") == null || figure.Element(svg + "desc") == null)
                        throw new InvalidOperationException($"设计图需要 viewBox、标题和文字说明：{sectionId}。");
                }
                foreach (var elementId in body.Descendants().Attributes("id"))
                    if (!ids.Add(elementId.Value)) throw new InvalidOperationException($"设计图元素身份重复：{elementId.Value}。");
                return new { id = sectionId, title = Required(section, "title"),
                    html = string.Concat(body.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting))) };
            }).ToArray();
            if (sections.Length == 0) throw new InvalidOperationException($"设计图页面没有内容：{id}。");
            return (object)new { id, title, status, summary, sections };
        }).ToArray();

        static string Required(XElement element, string name) =>
            (string?)element.Attribute(name) is { Length: > 0 } value ? value
                : throw new InvalidOperationException($"设计图 {element.Name} 缺少 {name}。");
    }
}
