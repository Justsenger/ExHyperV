using System.Runtime.InteropServices;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmCreationApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmCreateService.GetCreationOptionsAsync()" or "VmCreateService.GetHostDefaultPathsAsync()"
        or "VmCreateService.GetSupportedVersionsAsync()" or "VmCreateService.GetIsolationSupportAsync()" or "VmCreateService.CreateVirtualMachineAsync()" or "VmProcessorService.GetHostCapabilitiesAsync()";

    internal static List<Dictionary<string, object?>> Capabilities()
    {
        var capability = SystemBaseline.Query("SELECT * FROM Msvm_VirtualSystemManagementCapabilities").Single();
        return SystemBaseline.Query($"ASSOCIATORS OF {{{capability["__PATH"]}}} WHERE ResultClass=Msvm_VirtualSystemSettingData");
    }
    private static string[] Versions() => Capabilities().Select(row => Convert.ToString(row["Version"])).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().OrderByDescending(s => Version.Parse(s)).ToArray();
    private static string[] IsolationTypes()
    {
        var known = new Dictionary<int, string> { [0] = "TrustedLaunch", [1] = "VBS", [2] = "SNP", [3] = "TDX", [4] = "RME", [16] = "OpenHCL" };
        return Capabilities().Where(row => Convert.ToBoolean(row.GetValueOrDefault("GuestStateIsolationEnabled")) && Convert.ToString(row["InstanceID"])!.Contains("GuestStateIsolationType", StringComparison.OrdinalIgnoreCase))
            .Select(row => known.GetValueOrDefault(Convert.ToInt32(row["GuestStateIsolationType"]))).Where(s => s is not null).Select(s => s!).Append("Disabled").Distinct().Order().ToArray();
    }

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api == "VmCreateService.CreateVirtualMachineAsync()") { await CreateAsync(p, c); return; }
        var defaults = SystemBaseline.Query("SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData").Single();
        string vmPath = Convert.ToString(defaults.GetValueOrDefault("DefaultExternalDataRoot") ?? defaults.GetValueOrDefault("DefaultVirtualMachinePath")) ?? VmCreateService.DefaultVmDirectory;
        string diskPath = Convert.ToString(defaults.GetValueOrDefault("DefaultVirtualHardDiskPath")) ?? "";
        string[] versions = Versions(), isolation = IsolationTypes();
        switch (p.Api)
        {
            case "VmCreateService.GetHostDefaultPathsAsync()":
                var paths = await c.CallAsync(VmCreateService.GetHostDefaultPathsAsync); c.Require(paths.HasData, "默认目录读取：" + paths.Error);
                c.Equal(vmPath, paths.Data!.DefaultVmPath, "虚拟机目录"); c.Equal(diskPath, paths.Data.DefaultVhdPath, "磁盘目录"); break;
            case "VmCreateService.GetSupportedVersionsAsync()":
                var supported = await c.CallAsync(VmCreateService.GetSupportedVersionsAsync); c.Require(supported.HasData, "版本读取：" + supported.Error);
                c.Sequence(versions, supported.Data!, "宿主支持版本及降序排列"); break;
            case "VmCreateService.GetIsolationSupportAsync()":
                var isolations = await c.CallAsync(VmCreateService.GetIsolationSupportAsync); c.Require(isolations.HasData, "隔离能力读取：" + isolations.Error);
                c.Sequence(isolation, isolations.Data!.Types.Order(), "隔离类型"); c.Equal(isolation.Length > 1, isolations.Data.Supported, "隔离能力存在标志"); break;
            case "VmProcessorService.GetHostCapabilitiesAsync()":
                var cpu = await c.CallAsync(VmProcessorService.GetHostCapabilitiesAsync); c.Require(cpu.HasData, "处理器能力读取：" + cpu.Error);
                bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
                bool intel = !arm && SystemBaseline.Query("SELECT Manufacturer FROM Win32_Processor", @"root\cimv2").Any(row => Convert.ToString(row["Manufacturer"])?.Contains("Intel", StringComparison.OrdinalIgnoreCase) == true);
                c.Equal(arm, cpu.Data!.IsArm64, "ARM64 平台"); c.Equal(intel, cpu.Data.IsIntel, "Intel 平台"); c.Equal(isolation.Any(type => type is "SNP" or "TDX"), cpu.Data.HardwareIsolationSupported, "硬件隔离能力"); break;
            default:
                var options = await c.CallAsync(VmCreateService.GetCreationOptionsAsync); c.Require(options.HasData, "创建选项读取：" + options.Error);
                c.Equal(vmPath, options.Data!.DefaultVmPath, "配置目录"); c.Equal(diskPath, options.Data.DefaultVhdPath, "磁盘目录");
                c.Sequence(versions, options.Data.Versions, "版本集合"); c.Sequence(isolation, options.Data.IsolationTypes.Order(), "隔离集合");
                var switches = SystemBaseline.Query("SELECT * FROM Msvm_VirtualEthernetSwitch").ToDictionary(row => Guid.Parse(row["Name"]!.ToString()!), row => row["ElementName"]!.ToString()!);
                c.Sequence(switches.Keys.Order(), options.Data.Switches.Keys.Order(), "交换机身份"); foreach (var entry in switches) c.Equal(entry.Value, options.Data.Switches[entry.Key], "交换机名称"); break;
        }
    }

    private static async Task CreateAsync(CatalogTest p, ApiCaseContext c)
    {
        int scenario = int.Parse(p.Id[(p.Id.LastIndexOf('-') + 1)..]);
        string version = (await WindowsTestCommand.RunAsync("@{version=(Get-VMHostSupportedVersion|Where-Object IsDefault|Select-Object -First 1).Version.ToString()}|ConvertTo-Json"))
            .GetProperty("version").GetString()!;
        if (scenario == 1)
        {
            var guardian = await WindowsTestCommand.RunAsync("@{ready=(@(Get-HgsGuardian -Name UntrustedGuardian -ErrorAction SilentlyContinue).Count -eq 1)}|ConvertTo-Json");
            if (!guardian.GetProperty("ready").GetBoolean()) throw new ApiCheckSkippedException("默认创建参数会启用 TPM；宿主尚未准备 UntrustedGuardian，本项等待 TPM 环境就绪。");
            await OneAsync(c, version, options => { options.DiskMode = 0; options.ProcessorCount = 4; options.MemoryMiB = 4096; options.DiskSizeGiB = 128; options.EnableDynamicMemory = true; options.EnableTpm = true; options.EnableSecureBoot = true; });
        }
        else if (scenario == 2)
        {
            foreach (string candidate in Versions()) foreach (int generation in RuntimeInformation.OSArchitecture == Architecture.Arm64 ? new[] { 2 } : new[] { 1, 2 })
                await OneAsync(c, candidate, options => options.Generation = generation);
            foreach (string isolation in IsolationTypes().Where(s => s != "Disabled"))
            {
                string? firmware = isolation == "OpenHCL" ? c.Configuration.SampleFiles.FirstOrDefault(file => Path.GetExtension(file).Equals(".igvm", StringComparison.OrdinalIgnoreCase) && File.Exists(file)) : null;
                if (isolation == "OpenHCL" && firmware is null) throw new ApiCheckSkippedException("普通代数与版本已核验；OpenHCL 创建需要已准备的 IGVM 固件文件，第一步尚未登记该材料。");
                await OneAsync(c, version, options => { options.IsolationType = isolation; options.OpenHclIgvmPath = firmware ?? ""; });
            }
        }
        else if (scenario == 3)
        {
            string source = Path.Combine(c.DirectoryPath, "existing.vhdx"); await WindowsTestCommand.RunAsync("New-VHD -Path $a.path -SizeBytes 1GB -Dynamic|Out-Null; @{ok=$true}|ConvertTo-Json", new { path = source });
            foreach (int mode in new[] { 0, 1, 2 }) await OneAsync(c, version, options => { options.DiskMode = mode; options.VhdPath = mode == 1 ? source : ""; });
            await OneAsync(c, version, options => { options.DiskMode = 1; options.VhdPath = source; options.CreateDifferencingDisk = true; });
            c.Require(File.Exists(source), "已有源盘在全部创建及清理后保留");
        }
        else if (scenario == 4)
        {
            await using var network = await TemporarySwitch.CreateAsync(c);
            string source = Path.Combine(c.DirectoryPath, "iso-source"), image = Path.Combine(c.DirectoryPath, "install.iso"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "setup.txt"), "API fixture");
            IsoApiCases.OnSta(() => IsoBuilderService.BuildUdfIso(source, image, "SETUP"));
            await OneAsync(c, version, options => { options.IsoPath = image; options.SwitchId = network.Id; });
        }
        else
        {
            await using var existing = await TemporaryVm.CreateAsync(c);
            await OneAsync(c, version, options => options.Name = existing.Name, expectAdjusted: true);
            await OneAsync(c, version, options => Directory.CreateDirectory(Path.Combine(options.Path, options.Name)), expectAdjusted: true);
            await using var reserved = TemporaryVm.Reserve(c);
            var request = Request(reserved, version); request.DiskMode = 1; request.VhdPath = Path.Combine(c.DirectoryPath, "vanished.vhdx");
            await WindowsTestCommand.RunAsync("New-VHD -Path $a.path -SizeBytes 1GB -Dynamic|Out-Null; @{ok=$true}|ConvertTo-Json", new { path = request.VhdPath });
            c.Equal<string?>(null, VmCreateService.GetCreationError(request), "源盘消失前预检查通过"); File.Delete(request.VhdPath);
            var failure = await c.CallAsync(() => VmCreateService.CreateVirtualMachineAsync(request)); c.Require(!failure.Success, "源盘消失返回失败");
            c.RequireSettled(failure.OutcomeUnknown || failure.CleanupErrorKind == ExHyperV.Tools.ApiErrorKind.OutcomeUnknown, failure.Error ?? failure.CleanupError ?? "创建回执终态未知");
            c.Require(!SystemBaseline.Query("SELECT * FROM Msvm_ComputerSystem").Any(row => Convert.ToString(row["ElementName"]) == reserved.Name), "失败后没有遗留注册");
        }
    }

    private static VmCreationParameters Request(TemporaryVm vm, string version) => new()
    { Name = vm.Name, Path = vm.Root, Version = version, Generation = 2, ProcessorCount = 1, MemoryMiB = 512, EnableDynamicMemory = false, EnableTpm = false, EnableSecureBoot = false, DiskMode = 2, DiskSizeGiB = 1 };

    private static async Task OneAsync(ApiCaseContext c, string version, Action<VmCreationParameters> configure, bool expectAdjusted = false)
    {
        await using var vm = TemporaryVm.Reserve(c); var request = Request(vm, version); configure(request); string original = JsonSerializer.Serialize(request);
        var result = await c.CallAsync(() => VmCreateService.CreateVirtualMachineAsync(request));
        if (result.VmId.HasValue) vm.RecordIdentity(result.VmId.Value);
        c.RequireSettled(result.OutcomeUnknown || result.CleanupErrorKind == ExHyperV.Tools.ApiErrorKind.OutcomeUnknown, result.Error ?? result.CleanupError ?? "创建回执终态未知");
        c.Require(result.Success && result.VmId.HasValue, $"创建 {request.Generation} 代 / {version} / {request.IsolationType}：{result.Error}；清理：{result.CleanupError}");
        c.Equal(original, JsonSerializer.Serialize(request), "输入参数保持原值"); c.Equal(expectAdjusted, result.NameAdjusted, "名称避让标志");
        c.Equal(result.ActualName, SystemBaseline.Vm(vm.Id)["ElementName"]?.ToString(), "系统名称与结果一致");
        var actual = await WindowsTestCommand.RunAsync("""
            $v=Get-VM -Id ([guid]$a.id); $cpu=$v|Get-VMProcessor; $memory=$v|Get-VMMemory
            @{generation=$v.Generation;version=$v.Version.ToString();cpu=$cpu.Count;memory=$memory.Startup;dynamic=$memory.DynamicMemoryEnabled;
            disks=@($v|Get-VMHardDiskDrive|ForEach-Object {$_.Path});isos=@($v|Get-VMDvdDrive|ForEach-Object {$_.Path});switches=@($v|Get-VMNetworkAdapter|Where-Object SwitchId|ForEach-Object {$_.SwitchId.ToString()})}|ConvertTo-Json
            """, new { id = vm.Id });
        c.Equal(request.Generation, actual.GetProperty("generation").GetInt32(), "代数"); c.Equal(version, actual.GetProperty("version").GetString(), "配置版本");
        c.Equal(request.ProcessorCount, actual.GetProperty("cpu").GetInt32(), "处理器数"); c.Equal(request.MemoryMiB * 1048576, actual.GetProperty("memory").GetInt64(), "启动内存字节数");
        c.Equal(request.EnableDynamicMemory, actual.GetProperty("dynamic").GetBoolean(), "动态内存");
        if (request.Generation == 2)
        {
            var security = await WindowsTestCommand.RunAsync("$v=Get-VM -Id $a.id;@{tpm=($v|Get-VMSecurity).TpmEnabled;secure=(($v|Get-VMFirmware).SecureBoot -eq 'On')}|ConvertTo-Json", new { id = vm.Id });
            bool isolated = request.IsolationType is not ("Disabled" or "OpenHCL" or "");
            c.Equal(request.EnableTpm || isolated, security.GetProperty("tpm").GetBoolean(), "TPM 的系统实际状态");
            c.Equal(request.EnableSecureBoot || request.IsolationType == "TrustedLaunch", security.GetProperty("secure").GetBoolean(), "安全启动的系统实际状态");
            var setting = SystemBaseline.Settings(vm.Id);
            c.Equal(request.IsolationType != "Disabled", Convert.ToBoolean(setting.GetValueOrDefault("GuestStateIsolationEnabled")), "隔离启用状态");
        }
        var disks = actual.GetProperty("disks").EnumerateArray().Select(x => x.GetString()).ToArray(); c.Equal(request.DiskMode == 2 ? 0 : 1, disks.Length, "挂接磁盘数");
        if (request.DiskMode != 2)
        {
            c.Equal(result.DiskPath, disks.Single(), "创建结果与系统磁盘路径"); c.Require(File.Exists(result.DiskPath), "磁盘文件存在");
            var disk = await WindowsTestCommand.RunAsync("$d=Get-VHD -Path $a.path; @{type=$d.VhdType.ToString();size=$d.Size;parent=$d.ParentPath}|ConvertTo-Json", new { path = result.DiskPath });
            if (request.CreateDifferencingDisk) { c.Equal("Differencing", disk.GetProperty("type").GetString(), "差分盘类型"); c.Equal(request.VhdPath, disk.GetProperty("parent").GetString(), "父盘路径"); }
            else if (request.DiskMode == 0) { c.Equal(request.DiskSizeGiB * 1073741824, disk.GetProperty("size").GetInt64(), "新盘容量"); c.Require(Path.GetFullPath(result.DiskPath!).StartsWith(vm.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "空磁盘目录落在本项配置目录"); }
        }
        if (request.IsoPath != "") c.Require(actual.GetProperty("isos").EnumerateArray().Any(x => x.GetString() == request.IsoPath), "ISO 已挂接");
        if (request.SwitchId.HasValue) c.Require(actual.GetProperty("switches").EnumerateArray().Any(x => x.GetString() == request.SwitchId.Value.ToString()), "网络连接指定交换机");
        c.Equal((ushort)3, Convert.ToUInt16(SystemBaseline.Vm(vm.Id)["EnabledState"]), "创建结束保持关机");
    }
}
