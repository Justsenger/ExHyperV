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
            output.AppendLine($"| {host.Build} | {host.System}{(host.Research ? "（研究构建）" : "")} | {host.Versions[0]} | {string.Join("、", host.Versions)} |");
        output.AppendLine().AppendLine("<!--\n" + reference.License + "\n-->").AppendLine();
    }

    private static string HtmlConfigurationVersions(string memberId)
    {
        var reference = ConfigurationVersions.Value;
        string id = memberId + "--versions";
        var output = new StringBuilder($"<section class=\"configuration-versions\" aria-labelledby=\"{H(id)}\"><h3 id=\"{H(id)}\">宿主与配置版本</h3><p class=\"version-legend\"><span aria-hidden=\"true\"></span>着色格表示支持</p>");
        output.Append("<div class=\"version-table-wrap\" tabindex=\"0\" role=\"region\" aria-label=\"宿主与配置版本对照表，可横向滚动\"><table class=\"version-matrix\"><thead><tr><th scope=\"col\">宿主 / 配置版本</th>");
        foreach (string version in reference.Versions) output.Append($"<th scope=\"col\"><code>{H(version)}</code></th>");
        output.Append("</tr></thead><tbody>");
        foreach (var host in reference.Hosts)
        {
            output.Append($"<tr><th scope=\"row\"><span class=\"version-host\">{H(host.System)}</span><code class=\"version-build\">Build {host.Build}</code>");
            if (host.Research) output.Append("<span class=\"version-channel\">研究构建</span>");
            output.Append("</th>");
            foreach (string version in reference.Versions)
            {
                bool supported = host.Versions.Contains(version);
                string status = $"{host.System}（Build {host.Build}） · {version}：{(supported ? "支持" : "未列入支持列表")}";
                output.Append($"<td class=\"{(supported ? "version-supported" : "version-empty")}\" aria-label=\"{H(status)}\" title=\"{H(status)}\">");
                output.Append("</td>");
            }
            output.Append("</tr>");
        }
        output.Append("</tbody></table></div></section><!--\n").Append(reference.License).Append("\n-->");
        return output.ToString();
    }
}
