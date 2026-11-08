using System.Reflection;
using ExHyperV.Mcp.Hosting;

namespace ExHyperV.Mcp.Api;

public sealed class ApiPolicy(ApiCatalog catalog, McpLaunchOptions launch)
{
    public bool Visible(MemberInfo member) => launch.Toolsets.Contains(catalog.Group(member));

    public void Demand(MemberInfo member, bool write = false)
    {
        if (!Visible(member)) throw new UnauthorizedAccessException($"启动配置未启用 {catalog.Group(member)} 工具组。");
        if (launch.ReadOnly && (write || !catalog.ContractReviewed || !catalog.IsReadOnly(member)))
            throw new UnauthorizedAccessException("当前 MCP 为只读模式；此入口需要修改权限或尚未确认为只读。");
    }

    // Decoding a model can invoke constructors. Apply the same policy before that invocation.
    public void DemandConstruction(ConstructorInfo constructor)
    {
        if (!Visible(constructor)) throw new UnauthorizedAccessException("模型所属工具组尚未启用。");
        if (launch.ReadOnly && ApiValuePolicy.HasIdentity(constructor.DeclaringType!))
            throw new UnauthorizedAccessException("只读模式只接受普通参数模型；持续资源通过已开放的查询工具取得。");
    }
}
