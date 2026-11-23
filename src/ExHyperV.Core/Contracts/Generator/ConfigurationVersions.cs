using System.Reflection;
using System.Text;
using System.Xml.Linq;

internal static partial class ApiDocument
{
    private sealed record ConfigurationHost(int Build, string System, bool Research, string[] Versions);
    private sealed record ConfigurationVersionReference(string Summary, string[] Versions,
        ConfigurationHost[] Hosts, string License);

    private static readonly Lazy<ConfigurationVersionReference> ConfigurationVersions = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Generator.ConfigurationVersions.xml")
            ?? throw new InvalidOperationException("缺少配置版本参考数据。");
        var root = XDocument.Load(stream).Root ?? throw new InvalidOperationException("配置版本参考为空。");
        string Required(XElement element, string name) => element.Attribute(name)?.Value is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"配置版本参考缺少 {name}。");
        string[] versions = Required(root, "versions").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (versions.Length == 0 || versions.Distinct().Count() != versions.Length
            || versions.Any(version => !Version.TryParse(version, out _))
            || !versions.SequenceEqual(versions.OrderByDescending(Version.Parse)))
            throw new InvalidOperationException("配置版本列表必须按版本号降序排列且不重复。");
        var hosts = root.Elements("host").Select(host =>
        {
            string[] supported = Required(host, "versions").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (supported.Length == 0 || supported.Distinct().Count() != supported.Length
                || supported.Except(versions).Any() || !supported.SequenceEqual(supported.OrderByDescending(Version.Parse)))
                throw new InvalidOperationException("宿主的配置版本列表无效。");
            int build = int.Parse(Required(host, "build"), System.Globalization.CultureInfo.InvariantCulture);
            if (build <= 0) throw new InvalidOperationException("宿主 build 无效。");
            return new ConfigurationHost(build, Required(host, "system"), (string?)host.Attribute("channel") == "research", supported);
        }).ToArray();
        if (hosts.Length == 0 || hosts.Select(host => host.Build).Distinct().Count() != hosts.Length)
            throw new InvalidOperationException("宿主 build 为空或重复。");
        return new(root.Element("summary")!.Value.Trim(), versions, hosts, root.Element("license")!.Value.Trim());
    });

    private static void AppendConfigurationVersions(StringBuilder output)
    {
        var reference = ConfigurationVersions.Value;
        output.AppendLine("**配置版本与宿主 build**").AppendLine().AppendLine(reference.Summary).AppendLine();
        output.AppendLine("| 宿主 build | 系统 | 最高已记录版本 | 支持的配置版本 |")
            .AppendLine("| --- | --- | --- | --- |");
        foreach (var host in reference.Hosts)
            output.AppendLine($"| {host.Build} | {host.System}{(host.Research ? "（预览版）" : "")} | {host.Versions[0]} | {string.Join("、", host.Versions)} |");
        output.AppendLine().AppendLine("<!--\n" + reference.License + "\n-->").AppendLine();
    }

    private static string HtmlConfigurationVersions(string memberId)
    {
        var reference = ConfigurationVersions.Value;
        string id = memberId + "--versions";
        var output = new StringBuilder($"<section class=\"configuration-versions\" aria-labelledby=\"{H(id)}\"><div class=\"version-heading\"><h3 id=\"{H(id)}\">宿主与配置版本</h3><span class=\"version-legend\"><span aria-hidden=\"true\"></span>支持</span></div>");
        output.Append("<div class=\"version-table-full\">");
        AppendTable(reference.Hosts);
        output.Append("</div><div class=\"version-table-narrow\">");
        foreach (var hosts in reference.Hosts.Chunk(4)) AppendTable(hosts);
        output.Append("</div></section><!--\n").Append(reference.License).Append("\n-->");
        return output.ToString();

        void AppendTable(ConfigurationHost[] hosts)
        {
            output.Append("<table class=\"version-matrix\" aria-label=\"配置版本与宿主支持关系\"><colgroup><col class=\"version-label-column\">");
            foreach (var _ in hosts) output.Append("<col>");
            output.Append("</colgroup><thead><tr><th class=\"version-corner\" scope=\"col\">配置版本</th>");
            foreach (var host in hosts)
            {
                output.Append($"<th scope=\"col\" class=\"version-host\" aria-label=\"{H(host.System)}，Build {host.Build}\">");
                foreach (string system in host.System.Split(" / ", StringSplitOptions.RemoveEmptyEntries))
                    output.Append($"<span class=\"version-host-name\">{string.Join("<br>", system.Split(" · ").Select(H))}</span>");
                output.Append($"<span class=\"version-build\">{host.Build}</span>");
                if (host.Research) output.Append("<span class=\"version-channel\">预览版</span>");
                output.Append("</th>");
            }
            output.Append("</tr></thead><tbody>");
            int? previousMajor = null;
            foreach (string version in reference.Versions)
            {
                int major = Version.Parse(version).Major;
                output.Append(previousMajor.HasValue && previousMajor != major ? "<tr class=\"version-major-start\">" : "<tr>");
                previousMajor = major;
                output.Append($"<th scope=\"row\">{H(version)}</th>");
                foreach (var host in hosts)
                {
                    bool supported = host.Versions.Contains(version);
                    string status = $"{host.System}（Build {host.Build}） · {version}：{(supported ? "支持" : "未列入支持列表")}";
                    output.Append($"<td class=\"{(supported ? "version-supported" : "version-empty")}\" aria-label=\"{H(status)}\"></td>");
                }
                output.Append("</tr>");
            }
            output.Append("</tbody></table>");
        }
    }
}
