using ExHyperV.Mcp.Api;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Mcp;

// Convenience tools and generic API calls share ownership and shutdown cleanup.
public sealed class GpuSessions(ApiObjects objects)
{
    private readonly object gate = new();
    public GpuDeploymentSnapshot Add(GpuDeploymentSession session)
    {
        lock (gate)
        {
            objects.RequireCapacity();
            if (objects.CountOf<GpuDeploymentSession>() >= 64) throw new InvalidOperationException("已保留 64 个部署流程，请先释放已结束的流程。");
            objects.Add(session);
            return session.GetStatus();
        }
    }
    public GpuDeploymentSession Get(Guid id) => objects.Get(id) as GpuDeploymentSession
        ?? throw new ArgumentException("请使用当前 MCP 的 GPU 部署流程编号。");
    public ApiObjects.Lease Use(Guid id) => objects.Use(id);
    public async Task<bool> ReleaseAsync(Guid id)
    {
        var status = Get(id).GetStatus();
        if (status.State is not (GpuDeploymentState.Completed or GpuDeploymentState.Failed or GpuDeploymentState.Cancelled)
            || status.HasPendingCleanup || status.OutcomeUnknown)
            throw new InvalidOperationException("请先结束流程并处理待清理或结果待核实的资源，再释放编号。");
        await objects.ReleaseAsync(id);
        return true;
    }
}
