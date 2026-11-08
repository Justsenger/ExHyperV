using System.ComponentModel;
using System.Text.Json;
using ExHyperV.Mcp.Api;
using ModelContextProtocol.Server;

namespace ExHyperV.Mcp.Tools;

[McpServerResourceType]
public sealed class CoreResources(ApiCatalog catalog)
{
    [McpServerResource(UriTemplate = "exhyperv://core/api", Name = "core_api", Title = "Core API 手册", MimeType = "text/markdown")]
    [Description("当前发布版本的 Core API 使用说明、字段说明和示例。")]
    public string Guide() => catalog.Guide;

    [McpServerResource(UriTemplate = "exhyperv://core/coverage", Name = "core_coverage", Title = "Core 接入范围", MimeType = "application/json")]
    public string Coverage() => JsonSerializer.Serialize(catalog.Coverage());
}
