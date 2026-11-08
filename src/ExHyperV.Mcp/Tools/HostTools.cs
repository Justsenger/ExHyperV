using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ExHyperV.Mcp.Hosting;
using ExHyperV.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExHyperV.Mcp.Tools;

[McpServerToolType]
public sealed class HostTools(McpLaunchOptions launch, InvocationJournal journal, PersistentTaskStore tasks)
{
    [McpServerTool(Name = "server_info", Title = "查看 MCP 运行环境", ReadOnly = true, OpenWorld = false)]
    [Description("查看 MCP 与 Core 程序集版本、Windows 架构、进程身份权限和进程编号。")]
    public CallToolResult Info()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return ToolReply.Result(new
        {
            success = true, transport = launch.Transport, authentication = launch.Authentication,
            endpoint = launch.Transport == "http" ? "/mcp" : null,
            stateScope = "process", processId = Environment.ProcessId,
            responseContract = 2, readOnly = launch.ReadOnly, toolsets = launch.Toolsets,
            version = typeof(HostTools).Assembly.GetName().Version?.ToString(),
            coreVersion = typeof(VmQueryService).Assembly.GetName().Version?.ToString(),
            coreModuleId = typeof(VmQueryService).Module.ModuleVersionId,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
        });
    }

    [McpServerTool(Name = "server_requests", Title = "查看请求执行收据", ReadOnly = true, OpenWorld = false)]
    [Description("查询当前账户保存的修改请求记录。跨进程记录仅供核对，资源编号属于创建它的进程。记录包含操作名称和状态，参数原文保持私密。")]
    public CallToolResult Requests(int offset = 0, int limit = 50) => ToolReply.Result(journal.List(offset, limit));

    [McpServerTool(Name = "server_request_release", Title = "释放已核对的结束记录", Destructive = false, OpenWorld = false)]
    [Description("移除已结束的请求收据。执行中、已接收和结果待核实的收据继续保留；释放后该 requestKey 可重新使用。")]
    public CallToolResult ReleaseRequest(string id, [Description("已核对原请求对应的真实资源状态，确认可清理遗留收据。")] bool verified = false)
    { journal.Forget(id, verified); return ToolReply.Result(new { success = true }); }

    [McpServerTool(Name = "server_task_release", Title = "释放已结束的协议任务", Destructive = false, OpenWorld = false)]
    [Description("读取结果后清理标准 Tasks 任务记录，释放记录容量。Core 操作与资源继续由 core_operation_status 和 core_object_release 管理。")]
    public CallToolResult ReleaseTask(string taskId)
    { tasks.Release(taskId); return ToolReply.Result(new { success = true }); }

    [McpServerTool(Name = "host_hyperv_status", Title = "检查 Hyper-V", ReadOnly = true, OpenWorld = false)]
    [Description("按监控程序、VMMS、WMI 顺序检查 Hyper-V，返回 Core 的分阶段诊断、首个问题和原始错误。isInstalled 为 null 表示服务存在性尚待确认；根据 diagnostics 区分 Unknown、NotChecked 与明确缺失。")]
    public static Task<CallToolResult> HyperVStatus() => ToolReply.Run(async () =>
    {
        var status = await HyperVHostService.GetHyperVDiagnosticsAsync();
        return ToolReply.Result(new { success = true, status.IsReady, status.IsInstalled, status.StatusText, diagnostics = status });
    });

    [McpServerTool(Name = "host_system_info", Title = "查看宿主系统", ReadOnly = true, OpenWorld = false)]
    [Description("读取宿主系统、CPU 型号与内存容量说明；Core 在当前进程内缓存这份基础信息。")]
    public static Task<CallToolResult> SystemInfo() => ToolReply.Run(async () =>
        ToolReply.Result(new { success = true, data = await HostSystemInfoService.GetSystemInfoAsync() }));

    [McpServerTool(Name = "host_disks", Title = "列出宿主磁盘", ReadOnly = true, OpenWorld = false)]
    [Description("读取宿主磁盘清单、身份和状态。")]
    public static Task<CallToolResult> Disks() => ToolReply.Management(async () => ToolReply.From(await HostDiskService.GetHostDisksAsync()));

    [McpServerTool(Name = "host_optical_drives", Title = "列出宿主光驱", ReadOnly = true, OpenWorld = false)]
    [Description("读取宿主光驱清单。")]
    public static Task<CallToolResult> OpticalDrives() => ToolReply.Management(async () => ToolReply.From(await HostDiskService.GetHostOpticalDrivesAsync()));

    [McpServerTool(Name = "host_gpus", Title = "列出宿主显卡", ReadOnly = true, OpenWorld = false)]
    [Description("读取显卡 InstanceId、驱动和 IsPartitionable；GPU 部署使用这里返回的稳定设备身份。")]
    public static Task<CallToolResult> Gpus() => ToolReply.Management(async () => ToolReply.From(await HostGpuService.GetHostGpusAsync()));

    [McpServerTool(Name = "host_switches", Title = "列出可用交换机", ReadOnly = true, OpenWorld = false)]
    [Description("读取可用虚拟交换机的 GUID 到名称映射。创建及连接操作使用 GUID；名称用于显示。")]
    public static Task<CallToolResult> Switches() => ToolReply.Management(async () => ToolReply.From(await VmNetworkService.GetAvailableSwitchesAsync()));

    [McpServerTool(Name = "vm_creation_options", Title = "查看虚拟机创建选项", ReadOnly = true, OpenWorld = false)]
    [Description("创建前读取宿主默认目录、支持的配置版本、隔离方式和交换机。")]
    public static Task<CallToolResult> CreationOptions() => ToolReply.Management(async () => ToolReply.From(await VmCreateService.GetCreationOptionsAsync()));
}
