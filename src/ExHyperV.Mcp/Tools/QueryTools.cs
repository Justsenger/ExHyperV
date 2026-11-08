using System.ComponentModel;
using ExHyperV.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExHyperV.Mcp.Tools;

[McpServerToolType]
public sealed class QueryTools(VmQueryService query)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    [McpServerTool(Name = "vm_query_fields", Title = "查找虚拟机查询字段", ReadOnly = true, OpenWorld = false)]
    [Description("搜索 Core 的完整查询字段目录。返回字段 ID、中文名称、所属集合和数据类型；将 ID 传给 vm_query。")]
    public static CallToolResult Fields(
        [Description("按字段 ID、中文名称或分类搜索；省略时列出全部领域。")] string? search = null,
        [Description("分页起点，从 0 开始。")] int offset = 0,
        [Description("每页条数，1 至 200。")] int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 200) return ToolReply.Error("offset 至少为 0，limit 为 1 至 200。");
        var fields = VmQueryFields.All.Where(field => string.IsNullOrWhiteSpace(search)
            || $"{field.Id} {field.Name} {field.Category}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return ToolReply.Result(new
        {
            success = true, total = fields.Length, offset, limit,
            fields = fields.Skip(offset).Take(limit).Select(field => new
            { field.Id, field.Name, field.Category, field.Collection, valueType = field.ValueType.ToString() })
        });
    }

    [McpServerTool(Name = "vm_query", Title = "查询虚拟机", ReadOnly = true, OpenWorld = false)]
    [Description("按 GUID 和字段查询虚拟机。省略字段返回名称、状态、代数；空字段只返回身份。省略 vmIds 查询全部，空 vmIds 返回空集合。字段和设备集合分别保留读取状态、错误与时间。")]
    public Task<CallToolResult> Query(
        [Description("vm_query_fields 返回的字段 ID 数组。")] string[]? fields = null,
        [Description("虚拟机 GUID 数组；省略为全部，空数组为零台。")] Guid[]? vmIds = null,
        CancellationToken cancellationToken = default) => ToolReply.Run(async () =>
    {
        var selected = (fields ?? ["Identity.Name", "Identity.State", "Identity.Generation"])
            .Select(id => VmQueryFields.Find(id) ?? throw new ArgumentException($"查询字段无效：{id}。请先查询 vm_query_fields。")).ToArray();
        var request = new VmQueryRequest(selected, vmIds);
        if (request.VmIds?.Count != 0) ToolReply.RequireAdministrator();
        await gate.WaitAsync(cancellationToken);
        try { return ToolReply.From(await query.QueryAsync(request, cancellationToken)); }
        finally { gate.Release(); }
    });
}
