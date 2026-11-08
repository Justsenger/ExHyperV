using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using ExHyperV.Services;
using ExHyperV.Models;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class HostInventoryApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "HostSystemInfoService.GetSystemInfoAsync()" or "HostGpuService.GetHostGpusAsync()"
        or "HyperVSwitchService.GetBridgeableAdaptersAsync()" or "HyperVSwitchService.GetNetworkInfoAsync()" or "HyperVSwitchService.GetFullSwitchNetworkStateAsync()"
        or "HostPcieService.GetPCIeInfoAsync()" or "HostPcieService.GetControllerDisksAsync()" or "HyperVSchedulerService.GetSchedulerType()"
        or "HyperVSchedulerService.GetConfiguredSchedulerType()" or "HostProductTypeService.GetPendingSwitch()" or "HostProductTypeService.CanApplySwitch()"
        or "HyperVHostService.GetHyperVDiagnosticsAsync()" || p.Id == "T-eb7d67479cd7d470-1";

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api == "HyperVHostService.GetHyperVDiagnosticsAsync()")
        {
            bool Hypervisor() => Convert.ToBoolean(SystemBaseline.Query("SELECT HypervisorPresent FROM Win32_ComputerSystem", "root/cimv2").Single()["HypervisorPresent"]);
            string? ServiceState() => SystemBaseline.Query("SELECT State FROM Win32_Service WHERE Name='vmms'", "root/cimv2").Select(v => Convert.ToString(v["State"])).SingleOrDefault();
            bool before = Hypervisor(); string? service = ServiceState();
            var result = await c.CallAsync(HyperVHostService.GetHyperVDiagnosticsAsync);
            if (before != Hypervisor() || service != ServiceState()) throw new ApiCheckSkippedException("系统运行状态在采样期间变化，留待复测。");
            c.Equal(before ? HyperVCheckState.Passed : HyperVCheckState.NotReady, result.Hypervisor.State, "监控程序诊断与系统基准一致");
            if (!before)
            {
                c.Equal(HyperVCheckState.NotChecked, result.Vmms.State, "前序缺项时 VMMS 标为尚未检查");
                c.Equal(HyperVCheckState.NotChecked, result.Wmi.State, "前序缺项时 WMI 标为尚未检查");
            }
            else
            {
                c.Equal(service == "Running" ? HyperVCheckState.Passed : HyperVCheckState.NotReady, result.Vmms.State, "VMMS 诊断与系统服务状态一致");
                c.Equal(service is not null, result.IsInstalled, "保留明确的安装状态");
                if (service == "Running")
                {
                    bool management = SystemBaseline.Query("SELECT Name FROM Msvm_VirtualSystemManagementService").Count > 0;
                    c.Equal(management ? HyperVCheckState.Passed : HyperVCheckState.NotReady, result.Wmi.State, "WMI 管理服务实例与诊断一致");
                }
                else c.Equal(HyperVCheckState.NotChecked, result.Wmi.State, "服务未运行时后续检查保持未执行");
            }
            var checks = new[] { result.Hypervisor, result.Vmms, result.Wmi };
            c.Equal(checks.All(v => v.State == HyperVCheckState.Passed), result.IsReady, "整体就绪由三个阶段共同决定");
            c.Equal(checks.FirstOrDefault(v => v.State is HyperVCheckState.NotReady or HyperVCheckState.Unknown), result.FirstIssue, "首个问题保留阶段顺序");
            c.Equal(result.IsReady, result.StatusText.Length == 0, "未就绪时提供原因");
            return;
        }
        if (p.Api == "HostSystemInfoService.GetSystemInfoAsync()")
        {
            var os = SystemBaseline.Query("SELECT Caption,OSArchitecture,Version FROM Win32_OperatingSystem", "root/cimv2").Single();
            var cpus = SystemBaseline.Query("SELECT Name,MaxClockSpeed FROM Win32_Processor", "root/cimv2");
            var ram = SystemBaseline.Query("SELECT Capacity FROM Win32_PhysicalMemory", "root/cimv2").Sum(v => Convert.ToDouble(v["Capacity"])) / 1073741824;
            var a = c.Call(HostSystemInfoService.GetSystemInfoAsync); var b = c.Call(HostSystemInfoService.GetSystemInfoAsync);
            c.Require(ReferenceEquals(a, b), "并发调用复用缓存任务"); var result = await a;
            c.Equal(Convert.ToString(os["Caption"])!.Replace("Microsoft ", "") + " (Build " + Convert.ToString(os["Version"])!.Split('.').Last() + ")", result.Caption, "系统名称和构建号");
            c.Equal(Convert.ToString(os["OSArchitecture"]), result.OSArchitecture, "系统位数");
            foreach (var cpu in cpus) c.Require(result.CpuModel.Contains(Convert.ToString(cpu["Name"])!.Trim(), StringComparison.Ordinal), "处理器型号保留系统名称");
            c.Equal(Math.Round(ram, 2).ToString(CultureInfo.InvariantCulture) + " GB", result.MemCap, "物理内存容量"); return;
        }
        if (p.Api == "HostProductTypeService.CanApplySwitch()")
        {
            c.Require(c.Call(() => HostProductTypeService.CanApplySwitch(null)), "无挂起记录允许提交");
            foreach (bool confirmed in new[] { false, true })
            { var record = new HostProductTypePendingSwitch("ServerNT", confirmed, ["recovery-material"]); c.Require(!c.Call(() => HostProductTypeService.CanApplySwitch(record)), "有挂起线索时保留恢复材料"); c.Equal("recovery-material", record.RecoveryPaths.Single(), "输入保持原样"); }
            return;
        }
        if (p.Api == "HostProductTypeService.GetPendingSwitch()")
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ExHyperV\PendingSystemTypeSwitch");
            var result = c.Call(HostProductTypeService.GetPendingSwitch); c.Require(result.Success, result.Error);
            if (key is null && result.IsEmpty) c.Require(true, "原始挂起标记与 API 均为空");
            else { c.Require(result.HasData, "保留系统的挂起记录"); c.Require(result.Data!.RecoveryPaths.Count > 0, "返回恢复材料路径"); foreach (string path in result.Data!.RecoveryPaths) c.Require(Path.IsPathFullyQualified(path), "恢复路径为绝对路径"); }
            return;
        }
        if (p.Api.Contains("Scheduler"))
        {
            HyperVSchedulerType expected;
            if (p.Api.Contains("Configured"))
            {
                var value = await WindowsTestCommand.RunAsync("$s=bcdedit /enum '{current}'; if($LASTEXITCODE){throw ($s -join ' ')}; ($s -join \"`n\")|ConvertTo-Json");
                string text = value.GetString()!; var match = System.Text.RegularExpressions.Regex.Match(text, @"(?im)^hypervisorschedulertype\s+(\S+)");
                if (match.Success) expected = Enum.TryParse<HyperVSchedulerType>(match.Groups[1].Value, true, out var type) ? type : HyperVSchedulerType.Unknown;
                else { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions"); string? type = key?.GetValue("ProductType") as string; expected = type == "WinNT" ? HyperVSchedulerType.Root : type is "ServerNT" or "LanmanNT" ? Environment.OSVersion.Version.Build >= 17763 ? HyperVSchedulerType.Core : HyperVSchedulerType.Classic : HyperVSchedulerType.Unknown; }
                c.Equal(expected, c.Call(HyperVSchedulerService.GetConfiguredSchedulerType), "独立 BCD 配置与产品类型基准");
            }
            else
            {
                var events = await WindowsTestCommand.RunAsync("""
                    $boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime
                    $records=@(Get-WinEvent -FilterHashtable @{LogName='System';ProviderName='Microsoft-Windows-Hyper-V-Hypervisor';Id=2;StartTime=$boot} -ErrorAction SilentlyContinue)
                    @{count=$records.Count}|ConvertTo-Json
                    """, mayMutate: false);
                bool hasEvent = events.GetProperty("count").GetInt32() > 0;
                if (hasEvent != p.Id.EndsWith("-1", StringComparison.Ordinal))
                    throw new ApiCheckSkippedException(hasEvent ? "本次启动存在调度器事件；缺席事件场景留待对应环境。" : "本次启动缺席调度器事件；存在事件场景留待对应环境。");
                byte[] buffer = new byte[16]; int status = NtQuerySystemInformation(91, buffer, 16, out uint length);
                if (status < 0 || length != 16 || buffer[2] > 1 || Environment.OSVersion.Version.Build < 18362) throw new ApiCheckSkippedException("内核未提供可独立核验的运行期调度器字段。");
                expected = buffer[2] == 0 ? HyperVSchedulerType.Unknown : buffer[3] switch { 1 or 2 => HyperVSchedulerType.Classic, 3 => HyperVSchedulerType.Core, 4 => HyperVSchedulerType.Root, _ => HyperVSchedulerType.Unknown };
                if (buffer[2] == 1 && expected == HyperVSchedulerType.Unknown) throw new ApiCheckSkippedException("当前内核调度器编号尚无可核验定义：" + buffer[3]);
                c.Equal(expected, c.Call(HyperVSchedulerService.GetSchedulerType), "独立内核运行期字段；事件日志仅为 Core 的后备来源");
            }
            return;
        }
        if (p.Id == "T-eb7d67479cd7d470-1")
        {
            var svc = SystemBaseline.Query("SELECT State FROM Win32_Service WHERE Name='vmms'", "root/cimv2");
            bool management; try { management = SystemBaseline.Query("SELECT Name FROM Msvm_VirtualSystemManagementService").Count > 0; } catch { management = false; }
            var result = await c.CallAsync(HyperVHostService.GetHyperVStatusAsync);
            bool hypervisor = Convert.ToBoolean(SystemBaseline.Query("SELECT HypervisorPresent FROM Win32_ComputerSystem", "root/cimv2").Single()["HypervisorPresent"]);
            c.Equal(hypervisor && svc.Count > 0, result.IsInstalled, "兼容摘要中的服务存在性仅来自实际执行的检查");
            c.Equal(hypervisor && management && svc.Any(v => Equals(v["State"], "Running")), result.IsReady, "监控程序、VMMS 与管理接口共同决定就绪状态");
            if (result.IsReady) c.Require(management && svc.Any(v => Equals(v["State"], "Running")) && result.StatusText.Length == 0, "就绪状态必须具备可读管理接口、运行服务与空错误文案");
            else c.Require(result.StatusText.Length > 0, "环境缺项提供说明"); return;
        }
        if (p.Api == "HostGpuService.GetHostGpusAsync()")
        {
            var baseline = await WindowsTestCommand.RunAsync("@{items=@(Get-PnpDevice -Class Display -PresentOnly|Where-Object {$_.InstanceId -like 'PCI\\*' -or $_.InstanceId -like '*ACPI*'}|ForEach-Object {@{id=$_.InstanceId;driver=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_DriverVersion').Data}})}|ConvertTo-Json -Depth 5");
            var result = await c.CallAsync(HostGpuService.GetHostGpusAsync); c.Require(result.HasData, result.Error);
            c.Sequence(baseline.GetProperty("items").EnumerateArray().Select(v => v.GetProperty("id").GetString()!.ToUpperInvariant()).Order(), result.Data!.Select(g => g.InstanceId.ToUpperInvariant()).Order(), "在位物理显示适配器身份");
            var partitions = SystemBaseline.Query("SELECT Name FROM Msvm_PartitionableGpu").Select(v => Convert.ToString(v["Name"])).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var g in result.Data) { c.Require(g.Name.Length > 0, "GPU 名称"); if (g.IsPartitionable) c.Require(partitions.Contains(g.PartitionableGpuPath), "分区路径来自 Hyper-V 枚举"); var native = baseline.GetProperty("items").EnumerateArray().Single(v => v.GetProperty("id").GetString()!.Equals(g.InstanceId, StringComparison.OrdinalIgnoreCase)); c.Equal(native.GetProperty("driver").GetString(), g.DriverVersion, "驱动版本 " + g.Name); }
            return;
        }
        if (p.Api.StartsWith("HostPcieService."))
        {
            var native = await WindowsTestCommand.RunAsync("@{items=@(Get-PnpDevice|Where-Object {$_.InstanceId -like 'PCI\\*' -or $_.InstanceId -like 'PCIP\\*'}|Select-Object InstanceId,FriendlyName,Class)}|ConvertTo-Json -Depth 4");
            var ids = native.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("InstanceId").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (p.Api.Contains("GetPCIeInfo"))
            {
                var result = await c.CallAsync(HostPcieService.GetPCIeInfoAsync); c.Require(result.HasData, result.Error);
                var devices = result.Data!.Devices; c.Equal(devices.Count, devices.Select(x => x.InstanceId.ToUpperInvariant()).Distinct().Count(), "PCIe 身份唯一");
                foreach (var d in devices) { c.Require(ids.Contains(d.InstanceId) || ids.Contains("PCIP\\" + d.InstanceId[4..]), "PCIe 对应系统设备节点：" + d.InstanceId); c.Require(d.Path.StartsWith("PCIROOT", StringComparison.OrdinalIgnoreCase), "设备具有 DDA 位置路径"); }
                var vmList = await WindowsTestCommand.RunAsync("@{ids=@(Get-VM|ForEach-Object {$_.Id.ToString()})}|ConvertTo-Json", mayMutate: false);
                var systemVms = vmList.GetProperty("ids").EnumerateArray().Select(v => v.GetGuid()).ToHashSet();
                foreach (var target in result.Data!.VirtualMachines.Where(v => v.VirtualMachineId.HasValue)) c.Require(systemVms.Contains(target.VirtualMachineId!.Value), "分配目标为实际虚拟机 GUID");
            }
            else
            {
                foreach (var id in ids.Where(v => v.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)))
                {
                    var baseline = await WindowsTestCommand.RunAsync("@{disks=@(Get-CimInstance Win32_DiskDrive|Where-Object {(Get-PnpDeviceProperty -InstanceId $_.PNPDeviceID -KeyName 'DEVPKEY_Device_Parent' -ErrorAction SilentlyContinue).Data -eq $a.id}|ForEach-Object {Get-Disk -Number $_.Index}|Select-Object Number,IsBoot,IsSystem,IsOffline)}|ConvertTo-Json -Depth 4", new { id });
                    var actual = await c.CallAsync(() => HostPcieService.GetControllerDisksAsync(id)); c.Require(actual.HasData, actual.Error);
                    c.Sequence(baseline.GetProperty("disks").EnumerateArray().Select(v => v.GetProperty("Number").GetInt32()).Order(), actual.Data!.Select(v => v.Number).Order(), "控制器磁盘关联 " + id);
                    foreach (var d in actual.Data) { var b = baseline.GetProperty("disks").EnumerateArray().Single(v => v.GetProperty("Number").GetInt32() == d.Number); c.Equal(b.GetProperty("IsBoot").GetBoolean(), d.IsBoot, "启动盘标志"); c.Equal(b.GetProperty("IsSystem").GetBoolean(), d.IsSystem, "系统盘标志"); c.Equal(b.GetProperty("IsOffline").GetBoolean(), d.IsOffline, "脱机标志"); }
                }
                var invalid = await c.CallAsync(() => HostPcieService.GetControllerDisksAsync("")); c.Require(!invalid.Success, "空控制器身份返回错误");
            }
            return;
        }
        if (p.Api == "HyperVSwitchService.GetFullSwitchNetworkStateAsync()")
        {
            await using var network = await TemporarySwitch.CreateAsync(c); await using var vm = await TemporaryVm.CreateAsync(c);
            await WindowsTestCommand.RunAsync("$nic=Get-VM -Id $a.id|Get-VMNetworkAdapter; $nic|Set-VMNetworkAdapter -StaticMacAddress $a.mac; $nic|Connect-VMNetworkAdapter -SwitchName $a.name; @{ok=$true}|ConvertTo-Json", new { id = vm.Id, name = network.Name, mac = "02" + vm.Id.ToString("N")[..10] });
            var native = await WindowsTestCommand.RunAsync("@{items=@(Get-VM -Id $a.id|Get-VMNetworkAdapter|Select-Object VMName,MacAddress)}|ConvertTo-Json -Depth 4", new { id = vm.Id });
            var result = await c.CallAsync(() => HyperVSwitchService.GetFullSwitchNetworkStateAsync(network.Name));
            static string Endpoint(string? name, string? mac) => $"{name} · {mac?.Replace(":", "").Replace("-", "").ToUpperInvariant()}";
            c.Sequence(native.GetProperty("items").EnumerateArray().Select(x => Endpoint(x.GetProperty("VMName").GetString(), x.GetProperty("MacAddress").GetString())).Order(), result.Select(x => Endpoint(x.Name, x.MacAddress)).Order(), "专属交换机端点名称与 MAC"); return;
        }
        var adapters = await WindowsTestCommand.RunAsync("$physical=@(Get-CimInstance -Namespace root/StandardCimv2 -ClassName MSFT_NetAdapter|Where-Object ConnectorPresent|Select-Object -ExpandProperty PnPDeviceID); @{items=@(Get-CimInstance Win32_NetworkAdapter|Where-Object {$_.NetConnectionID -and $physical -contains $_.PNPDeviceID}|Select-Object GUID,PNPDeviceID,NetConnectionID)}|ConvertTo-Json -Depth 4");
        if (p.Api.Contains("GetBridgeable"))
        {
            var ports = new[] { "Msvm_ExternalEthernetPort", "Msvm_WiFiPort" }.SelectMany(cls => SystemBaseline.Query("SELECT DeviceID FROM " + cls)).Select(r => Guid.Parse(Convert.ToString(r["DeviceID"])!.Replace("Microsoft:", "", StringComparison.OrdinalIgnoreCase))).ToHashSet();
            var result = await c.CallAsync(HyperVSwitchService.GetBridgeableAdaptersAsync);
            c.Sequence(adapters.GetProperty("items").EnumerateArray().Select(x => Guid.Parse(x.GetProperty("GUID").GetString()!)).Where(ports.Contains).Order(), result.Select(v => v.ConnectionId).Order(), "可桥接物理接口 GUID");
        }
        else
        {
            var result = await c.CallAsync(HyperVSwitchService.GetNetworkInfoAsync);
            var switches = SystemBaseline.Query("SELECT Name,ElementName FROM Msvm_VirtualEthernetSwitch");
            c.Sequence(switches.Select(v => Guid.Parse(Convert.ToString(v["Name"])!)).Order(), result.Switches.Select(v => Guid.Parse(v.Id)).Order(), "交换机完整 GUID 集合");
            foreach (var s in result.Switches) c.Equal(Convert.ToString(switches.Single(v => Guid.Parse(Convert.ToString(v["Name"])!) == Guid.Parse(s.Id))["ElementName"]), s.SwitchName, "交换机名称");
            c.Sequence(adapters.GetProperty("items").EnumerateArray().Select(v => Guid.Parse(v.GetProperty("GUID").GetString()!)).Order(), result.PhysicalAdapters.Select(v => v.ConnectionId).Order(), "物理上游 GUID 集合");
        }
    }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int info, [Out] byte[] buffer, uint size, out uint written);
}
