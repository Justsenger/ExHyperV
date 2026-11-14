using ExHyperV.Services;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static class PacketCaptureAvailability
{
    // Load Core's bindings without opening a monitor, session or stream.
    internal static ResourceResult Inspect(Func<IDisposable>? load = null)
    {
        try
        {
            using var api = (load ?? (() => new PacketMonitorApi()))();
            return new(InspectionStatus.Ready, "独立抓包接口可用；可与现有捕获共存");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or BadImageFormatException)
        { return new(InspectionStatus.Missing, "此系统缺少 Core 所需的 Packet Monitor 公开接口。", [error.Message]); }
        catch (Exception error)
        { return new(InspectionStatus.Unavailable, "抓包接口加载失败，请查看明细。", [error.Message]); }
    }
}
