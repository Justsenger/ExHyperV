using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using ExHyperV.Tools;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.ResourceInspection;

internal static partial class ResourceProbes
{
    internal static readonly ResourceCheck[] Checks =
    [
        new("platform", "宿主与权限", "系统与架构", "Windows ARM64 或 x64，进程与宿主架构一致", "所有本机测试"),
        new("identity", "宿主与权限", "当前执行身份", "记录实际账户及管理员令牌", "权限与宿主设置测试"),
        new("vmms", "宿主与权限", "Hyper-V 管理服务", "VMMS 已启动", "虚拟机管理"),
        new("hyperv", "宿主与权限", "Hyper-V 管理接口", "当前身份能读取管理服务", "虚拟机查询与配置"),
        new("dotnet", "编译环境", ".NET 10 SDK", "当前进程可找到 .NET 10 SDK", "编译 Core 与测试程序"),
        new("msbuild", "编译环境", "Visual Studio MSBuild", "VS 2026 MSBuild 与 .NET 桌面构建组件已安装", "编译 Core 的 COM 引用"),
        new("powershell", "编译环境", "PowerShell 启动环境", "Windows PowerShell 5.1 即可启动资源检查", "资源检查启动脚本"),
        new("iso-support", "编译环境", "ISO 生成与挂载组件", "IMAPI 组件与 Windows 映像挂载命令可用", "ISO 生成与内容核验"),
        new("memory", "容量与目录", "处理器与可用内存", "读取当前容量，执行时按测试需要分配", "临时虚拟机与并行测试"),
        new("volumes", "容量与目录", "磁盘可用空间", "读取测试目录所在磁盘的剩余空间", "磁盘、导入导出、检查点"),
        new("workspace", "容量与目录", "测试缓存目录", "自动创建并检查文件读写", "临时磁盘、导出包及日志"),
        new("resource-selection", "容量与目录", "资源选择记录", "保存后按固定身份复用", "后续自动化测试"),
        new("vms", "虚拟机", "现有虚拟机", "能读取虚拟机身份和当前状态", "选择测试资源"),
        new("templates", "虚拟机", "测试虚拟机模板", "普通测试与 GPU 测试共用，每轮创建完整副本", "电源、网络、控制台、GPU 部署"),
        new("test-vm", "虚拟机", "临时虚拟机创建条件", "管理接口、管理员身份与可写测试目录齐备", "新建专属测试虚拟机"),
        new("switches", "网络与虚拟机条件", "现有虚拟交换机", "能读取交换机清单", "网络配置"),
        new("adapters", "网络与虚拟机条件", "宿主网络适配器", "能读取适配器和连接状态", "网络资源选择"),
        new("gpu", "真实设备", "GPU 与驱动", "读取 NVIDIA / Intel / AMD / Qualcomm 显卡及驱动版本", "GPU 资源盘点"),
        new("gpu-partitions", "真实设备", "GPU 分区能力", "Hyper-V 至少枚举出一个可分区 GPU", "GPU-PV 分配"),
        new("pcie", "真实设备", "PCIe 设备", "发现设备后再核验可直通能力和专用归属", "PCIe 直通"),
        new("pcie-host", "真实设备", "PCIe 宿主条件", "服务器系统或已开启设备分配策略", "PCIe 直通"),
        new("test-pcie", "真实设备", "选定 PCIe 设备", "已选择设备且实例标识和位置路径仍相符", "PCIe 直通"),
        new("disks", "真实设备", "物理测试盘", "选择闲置磁盘并保存身份；系统盘及已占用磁盘置灰", "物理盘与存储直通"),
        new("optical", "真实设备", "测试光驱", "选择光驱并保存设备身份", "光驱相关测试"),
        .. ExecutionChecks
    ];

    internal static async Task<int> RunWorkerAsync(string[] args)
    {
        ResourceResult result;
        try
        {
            if (args.Length is < 2 or > 3 || !Checks.Any(c => c.Id == args[1]))
                throw new ArgumentException("未知的资源检查项。");
            result = await CheckAsync(args[1], InspectionConfiguration.Read(args.Length == 3 ? args[2] : null));
        }
        catch (Exception error) { result = Failure(error); }
        Console.Write(JsonSerializer.Serialize(result, InspectionJson.Options));
        return 0; // The structured result, not process completion, carries readiness.
    }

    internal static ResourceResult Failure(Exception error) => new(
        error is FileNotFoundException or DirectoryNotFoundException ? InspectionStatus.Missing : InspectionStatus.Unavailable,
        error is UnauthorizedAccessException || error is ManagementException { ErrorCode: ManagementStatus.AccessDenied }
            ? "无读取权限"
            : $"读取失败：{error.Message}");

    private static ResourceResult Ready(string detail, params string[] items) => new(InspectionStatus.Ready, detail, items);
    private static ResourceResult Missing(string detail) => new(InspectionStatus.Missing, detail);
    private static ResourceResult Pending(string detail, params string[] items) => new(InspectionStatus.Pending, detail, items);

    private static async Task<ResourceResult> CheckAsync(string id, InspectionConfiguration config)
    {
        switch (id)
        {
            case "platform":
                var os = RuntimeInformation.OSArchitecture;
                var description = $"Build {Environment.OSVersion.Version.Build} · {os}";
                return os is Architecture.Arm64 or Architecture.X64 && os == RuntimeInformation.ProcessArchitecture
                    ? Ready(description) : Missing($"宿主 {os}，进程架构不匹配");
            case "identity":
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                    return admin ? Ready("管理员") : Missing("当前为普通权限，需要管理员身份。");
                }
            case "vmms":
                var service = Query("root/cimv2", "SELECT Name, State FROM Win32_Service WHERE Name='vmms'");
                return service.Count == 0 ? Missing("未安装") :
                    service.Any(s => s["State"] == "Running") ? Ready("运行中") : Missing("未运行");
            case "hyperv":
                return Query("root/virtualization/v2", "SELECT Name FROM Msvm_VirtualSystemManagementService").Count > 0
                    ? Ready("可访问")
                    : new(InspectionStatus.Unavailable, "管理接口未返回数据");
            case "dotnet":
                var sdk = await CommandAsync(FindExecutable("dotnet.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe")), "--list-sdks");
                var sdkLines = Lines(sdk);
                return sdkLines.Any(l => l.StartsWith("10.", StringComparison.Ordinal)) ? Ready("已安装", sdkLines) : Missing("未安装 .NET 10 SDK");
            case "msbuild":
                var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
                if (!File.Exists(vswhere)) return Missing("未找到 VS 安装器");
                var msbuild = await CommandAsync(vswhere, "-latest", "-products", "*", "-version", "[18.0,19.0)", "-requires", "Microsoft.Component.MSBuild", "Microsoft.VisualStudio.Workload.ManagedDesktop", "-find", @"MSBuild\Current\Bin\MSBuild.exe");
                if (string.IsNullOrWhiteSpace(msbuild))
                    msbuild = await CommandAsync(vswhere, "-latest", "-products", "*", "-version", "[18.0,19.0)", "-requires", "Microsoft.Component.MSBuild", "Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools", "-find", @"MSBuild\Current\Bin\MSBuild.exe");
                return string.IsNullOrWhiteSpace(msbuild) ? Missing("缺少 VS 2026 桌面构建组件") : Ready("已安装", Lines(msbuild));
            case "powershell":
                var windowsPowerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                var version = await CommandAsync(windowsPowerShell, "-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()");
                return Version.TryParse(version.Trim(), out var shellVersion) && shellVersion >= new Version(5, 1)
                    ? Ready($"{shellVersion.Major}.{shellVersion.Minor}")
                    : Missing("低于 5.1");
            case "memory":
                var memory = Query("root/cimv2", "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem").Single();
                return Ready($"{Environment.ProcessorCount} 核 · 内存 {GiB(memory["TotalVisibleMemorySize"], 1024)} GiB，可用 {GiB(memory["FreePhysicalMemory"], 1024)} GiB");
            case "iso-support":
                var isoProbe = await CommandAsync(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; $image=New-Object -ComObject IMAPI2FS.MsftFileSystemImage; try { $null=Get-Command Mount-DiskImage,Dismount-DiskImage,Get-DiskImage,Get-Volume -ErrorAction Stop; 'ready' } finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($image) }");
                return isoProbe.Trim() == "ready" ? Ready("IMAPI 与映像挂载命令可用") : Missing("ISO 组件检查失败");
            case "volumes":
                Directory.CreateDirectory(config.WorkDirectory!);
                return WorkspaceSpace(config.WorkDirectory!);
            case "workspace":
                Directory.CreateDirectory(config.WorkDirectory!);
                ResourceRegistration.VerifyDirectory(config.WorkDirectory!);
                return Ready($"读写通过 · {config.WorkDirectory}");
            case "vms":
                // Same locale-independent VM summary source used by Core's VmQueryService.
                var vms = Query("root/virtualization/v2", "SELECT Name, ElementName, EnabledState FROM Msvm_SummaryInformation");
                return new(vms.Count == 0 ? InspectionStatus.Missing : InspectionStatus.Ready,
                    vms.Count == 0 ? "当前清单为空；创建模板后重新检查即可选择。" : $"{vms.Count} 台",
                    vms.Select(v => $"{v["ElementName"]} · {VmState(v["EnabledState"])} · {v["Name"]}").ToArray(),
                    vms.Select(v => new ResourceChoice(v["Name"], v["ElementName"], VmState(v["EnabledState"]))).ToArray());
            case "resource-selection":
                return Ready(config.TemplateVmId is not null || config.PcieDevices.Length > 0 || config.PhysicalDisk is not null || config.OpticalDrive is not null
                    ? "已读取保存的选择；后续启动自动复用。" : "使用默认缓存；模板与设备选择保存后自动复用。");
            case "templates":
                return SelectedTemplate(config.TemplateVmId, "测试虚拟机模板");
            case "test-vm":
                using (var identity = WindowsIdentity.GetCurrent())
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                        return Missing("请使用管理员身份启动测试程序。");
                Directory.CreateDirectory(config.WorkDirectory!);
                ResourceRegistration.VerifyDirectory(config.WorkDirectory!);
                if (Query("root/virtualization/v2", "SELECT Name FROM Msvm_VirtualSystemManagementService").Count == 0)
                    return Missing("请先启动 Hyper-V 管理服务。");
                return Ready("管理接口与测试目录已就绪；执行时创建临时虚拟机。");
            case "switches":
                var switches = Query("root/virtualization/v2", "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch");
                return new(InspectionStatus.Ready, $"{switches.Count} 台", switches.Select(s => $"{s["ElementName"]} · {s["Name"]}").ToArray(),
                    switches.Select(s => new ResourceChoice(s["Name"], s["ElementName"])).ToArray());
            case "adapters":
                var adapters = Query("root/cimv2", "SELECT Name, NetConnectionID, NetConnectionStatus FROM Win32_NetworkAdapter WHERE NetConnectionID IS NOT NULL");
                return Ready($"{adapters.Count} 个", adapters.Select(a => $"{a["NetConnectionID"]} · {a["Name"]} · 连接状态 {a["NetConnectionStatus"]}").ToArray());
            case "gpu":
                return GpuInventory.Describe(Win32Api.GetPresentDisplayAdapters());
            case "gpu-partitions":
                return Inventory(Query("root/virtualization/v2", "SELECT Name FROM Msvm_PartitionableGpu"), "支持分区", "未找到分区资源", g => g["Name"]);
            case "pcie":
                // Use Core's complete candidate selection, including bridge exclusion,
                // PCI/PCIP identity merging, presence and usable location paths.
                var pciResult = await HostPcieService.GetPCIeInfoAsync();
                if (!pciResult.HasData) throw new InvalidOperationException($"PCIe 查询失败：{pciResult.Error}");
                var pci = pciResult.Data.Devices;
                return new(InspectionStatus.Ready, $"{pci.Count} 个候选；在上方勾选用于直通测试的设备。",
                    pci.Select(d => d.FriendlyName).ToArray(), pci.Select(d => new ResourceChoice(d.InstanceId, d.FriendlyName, d.Status, d.Path)).ToArray());
            case "pcie-host":
                if (HostProductTypeService.IsServerSystem()) return Ready("宿主允许设备分配");
                return EvaluateAssignmentPolicy(HostGpuService.GetAssignmentRestrictionsRelaxed());
            case "test-pcie":
                if (config.PcieDevices.Length == 0) return Pending("请在上方勾选用于直通测试的 PCIe 设备，再保存选择。");
                var deviceResult = await HostPcieService.GetPCIeInfoAsync();
                if (!deviceResult.HasData) throw new InvalidOperationException($"PCIe 查询失败：{deviceResult.Error}");
                var devices = deviceResult.Data.Devices;
                var changed = config.PcieDevices.Where(saved => !devices.Any(d => d.InstanceId.Equals(saved.InstanceId, StringComparison.OrdinalIgnoreCase)
                    && d.Path.Equals(saved.LocationPath, StringComparison.OrdinalIgnoreCase))).ToArray();
                return changed.Length == 0 ? Ready($"已指定 {config.PcieDevices.Length} 个设备，身份与位置已核对")
                    : Missing("已选 PCIe 设备消失或位置改变，请清除原选择、保存后重新指定。");
            case "disks":
                return await TestDeviceResources.DisksAsync(config);
            case "optical":
                return await TestDeviceResources.OpticalAsync(config);
            default: return await CheckExecutionConditionAsync(id, config);
        }
    }

    private static ResourceResult SelectedTemplate(string? vmId, string label)
    {
        if (string.IsNullOrWhiteSpace(vmId)) return Pending($"请在上方选择“{label}”，再点“保存并检查”。");
        if (!Guid.TryParse(vmId, out var id)) return Missing($"已保存的{label}标识无效，请重新选择。");
        var rows = Query("root/virtualization/v2", $"SELECT Name, ElementName, EnabledState FROM Msvm_ComputerSystem WHERE Name='{id:D}'");
        return DescribeTemplate(vmId, label, rows.Select(v => new ResourceChoice(v["Name"], v["ElementName"], VmState(v["EnabledState"]))).ToArray());
    }

    internal static ResourceResult DescribeTemplate(string? vmId, string label, ResourceChoice[] choices)
    {
        if (string.IsNullOrWhiteSpace(vmId)) return Pending($"请在上方选择“{label}”，再点“保存并检查”。");
        var vm = choices.SingleOrDefault(v => v.Id.Equals(vmId, StringComparison.OrdinalIgnoreCase));
        return vm is null ? Missing($"已保存的{label}已离开本机清单，请在上方重新选择。")
            : Ready($"已指定 {vm.Name} · {vm.Detail}；测试时创建完整副本。", vm.Id);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

    private static ResourceResult WorkspaceSpace(string directory)
    {
        if (!Directory.Exists(directory)) return Missing("缓存目录创建失败，请检查目录权限。");
        if (!GetDiskFreeSpaceEx(directory, out var available, out var total, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Ready($"测试目录可用 {available / 1073741824d:0.0} / {total / 1073741824d:0.0} GiB", directory);
    }

    private static ResourceResult Inventory(List<Dictionary<string, string>> rows, string found, string absent, Func<Dictionary<string, string>, string>? format = null)
        => rows.Count == 0 ? Missing(absent) : Ready(found, format is null ? [] : rows.Select(format).ToArray());

    internal static List<Dictionary<string, string>> Query(string scope, string query)
    {
        // Hyper-V can silently return zero objects to an unprivileged identity.
        // Verify a mandatory service object before interpreting empty VM/device lists.
        if (scope == "root/virtualization/v2" && !query.Contains("Msvm_VirtualSystemManagementService", StringComparison.Ordinal)
            && Query(scope, "SELECT Name FROM Msvm_VirtualSystemManagementService").Count == 0)
            throw new InvalidOperationException("Hyper-V 管理接口不可读");
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\" + scope.Replace('/', '\\')),
            new ObjectQuery(query), new System.Management.EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(10) });
        using var objects = searcher.Get();
        var rows = new List<Dictionary<string, string>>();
        foreach (ManagementObject obj in objects)
        {
            using (obj)
                rows.Add(obj.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparer.OrdinalIgnoreCase));
        }
        return rows;
    }

    private static string GiB(string value, long multiplier = 1) => double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var number)
        ? (number * multiplier / 1073741824d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "未知";
    private static string VmState(string value) => value switch { "2" => "运行中", "3" => "已关机", "32768" => "已暂停", "32769" => "已保存", _ => "状态 " + value };
    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FindExecutable(string name, string knownPath)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
        }
        if (File.Exists(knownPath)) return knownPath;
        throw new FileNotFoundException($"未找到 {name}；当前进程的 PATH 与已知安装位置均未命中。");
    }

    private static async Task<string> CommandAsync(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Win32Exception("检查程序未能启动。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); // The isolated worker is bounded and terminated by its parent.
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(file)} 检查失败：{await error}");
        return await output;
    }

    internal static ResourceResult EvaluateAssignmentPolicy(ApiResponse<bool> policy)
    {
        if (!policy.HasData) throw new InvalidOperationException($"设备分配策略读取失败：{policy.Error}");
        return policy.Data ? Ready("宿主允许设备分配")
            : Missing("需要服务器系统或已开启的设备分配策略；本轮跳过 PCIe 直通测试。");
    }

    internal static ProcessStartInfo WorkerStart(string id, string? configurationPath)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定测试程序路径。");
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--resource-probe");
        start.ArgumentList.Add(id);
        if (configurationPath is not null) start.ArgumentList.Add(configurationPath);
        return start;
    }
}
