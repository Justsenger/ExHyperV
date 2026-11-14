using System.Text.Json;
using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestExecution;

// The durable intent precedes creation. Parent cleanup can recover after worker failure.
internal sealed class TemporaryVm : IAsyncDisposable
{
    private const string JournalName = "temporary-vm.json";
    private const string NamePrefix = "EHV-T-";
    internal sealed record Journal(string Name, string Root, Guid? Id = null);
    internal Guid Id { get; private set; }
    internal string Name { get; private set; } = "";
    internal string Root { get; private set; } = "";
    private string JournalDirectory => Path.GetDirectoryName(Root)!;
    private readonly ApiCaseContext context;
    private TemporaryVm(ApiCaseContext context) => this.context = context;
    internal Task StartAsync() => VmTestMemory.PrepareStartAsync(context, this, start: true);
    internal Task EnsureStartCapacityAsync() => VmTestMemory.PrepareStartAsync(context, this, start: false);

    internal static TemporaryVm Reserve(ApiCaseContext context)
    {
        var vm = new TemporaryVm(context)
        {
            Name = NamePrefix + Guid.NewGuid().ToString("N"),
            Root = Path.GetFullPath(Path.Combine(context.DirectoryPath, "vms", Guid.NewGuid().ToString("N"), "vm"))
        };
        Directory.CreateDirectory(vm.Root); Save(vm.JournalDirectory, new(vm.Name, vm.Root)); return vm;
    }

    internal void RecordIdentity(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("创建结果返回空身份。");
        Id = id; Save(JournalDirectory, new(Name, Root, id));
    }

    internal static async Task<TemporaryVm> CreateAsync(ApiCaseContext context, int generation = 2)
    {
        if (generation == 1 && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64)
            throw new ApiCheckSkippedException("ARM64 Hyper-V 仅支持第二代虚拟机；第一代 BIOS 项请在 x64 测试机执行。");
        var vm = Reserve(context);
        try
        {
            var result = await WindowsTestCommand.RunAsync("""
                Import-Module Hyper-V -ErrorAction Stop
                if(Get-VM -Name $a.name -ErrorAction SilentlyContinue){throw '测试名称已存在'}
                $vm=New-VM -Name $a.name -Path $a.root -Generation $a.generation -MemoryStartupBytes 512MB -NoVHD
                Set-VM -VM $vm -AutomaticStartAction Nothing -AutomaticCheckpointsEnabled $false
                Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes 512MB
                @{id=$vm.Id.ToString()}|ConvertTo-Json -Compress
                """, new { name = vm.Name, root = vm.Root, generation });
            vm.Id = Guid.Parse(result.GetProperty("id").GetString()!);
            Save(vm.JournalDirectory, new(vm.Name, vm.Root, vm.Id));
            context.Evidence.Add($"已创建专属空白虚拟机：{vm.Id}，第 {generation} 代，关机状态。");
            return vm;
        }
        catch { await CleanupAsync(vm.JournalDirectory); throw; }
    }

    internal static void ValidateRoot(string directory, Journal journal)
    {
        var expected = Path.GetFullPath(Path.Combine(directory, "vm"));
        if (!string.Equals(expected, Path.GetFullPath(journal.Root), StringComparison.OrdinalIgnoreCase)
            || !(HasOwnedName(NamePrefix) || HasOwnedName("ExHyperV-ApiTest-")))
            throw new IOException("临时虚拟机归属记录校验失败，保留现场。");

        bool HasOwnedName(string prefix) => journal.Name.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(journal.Name[prefix.Length..], "N", out _);
    }

    internal static Task<JsonElement> ReadTemplateFactsAsync(Guid templateId) => WindowsTestCommand.RunAsync("""
        $vm=Get-VM -Id ([guid]$a.id)
        $drives=@($vm|Get-VMHardDiskDrive|Where-Object Path)
        $disks=@($drives|ForEach-Object {Get-VHD -Path $_.Path})
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$bytes=0
        foreach($disk in $disks){
            $current=$disk
            while($current -and $seen.Add($current.Path)){
                $bytes+=$current.FileSize
                $current=if($current.ParentPath){Get-VHD -Path $current.ParentPath}else{$null}
            }
        }
        @{state=$vm.State.ToString();memoryStartupBytes=[long]$vm.MemoryStartup;checkpoints=@($vm|Get-VMSnapshot).Count;disks=$disks.Count;bytes=$bytes;paths=@($drives.Path);physical=@($vm|Get-VMHardDiskDrive|Where-Object {!$_.Path}).Count;dda=@($vm|Get-VMAssignableDevice).Count;gpu=@($vm|Get-VMGpuPartitionAdapter).Count}|ConvertTo-Json
        """, new { id = templateId }, 60, mayMutate: false);

    internal static async Task<TemporaryVm> CopyTemplateAsync(ApiCaseContext context)
        => await Automation.TemplatePowerLease.CopyAsync(context.DirectoryPath, context.Configuration,
            () => CopyStoppedTemplateAsync(context));

    private static async Task<TemporaryVm> CopyStoppedTemplateAsync(ApiCaseContext context)
    {
        if (!Guid.TryParse(context.Configuration.TemplateVmId, out var templateId))
            throw new ApiCheckSkippedException("第一步尚未指定测试虚拟机模板。");
        var facts = await ReadTemplateFactsAsync(templateId);
        if (facts.GetProperty("state").GetString() != "Off") throw new ApiCheckSkippedException("模板状态已变化，本项完整副本测试跳过。");
        if (facts.GetProperty("disks").GetInt32() == 0) throw new ApiCheckSkippedException("指定模板尚未连接可启动的系统磁盘。");
        if (new[] { "physical", "dda", "gpu" }.Any(key => facts.GetProperty(key).GetInt32() > 0))
            throw new ApiCheckSkippedException("模板已挂接物理盘、PCIe 或 GPU；请使用仅含完整虚拟磁盘的干净模板，设备由各项测试按已登记资源分配。");
        long startupBytes = VmTestMemory.TemplateStartupBytes(facts.GetProperty("memoryStartupBytes").GetInt64());
        var sourceBytes = facts.GetProperty("bytes").GetInt64();
        var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(context.DirectoryPath))!).AvailableFreeSpace;
        long required = checked(sourceBytes * 2 + 2L * 1024 * 1024 * 1024);
        if (available < required)
            throw new ApiCheckSkippedException($"完整副本的导出与导入需要约 {required / 1073741824.0:F1} GiB，测试目录现有 {available / 1073741824.0:F1} GiB。");
        var vm = Reserve(context);
        try
        {
            // Hyper-V adds staging, VM and configuration names to this path. Keep
            // the export beside the fixture to stay within its native path limit.
            string export = Path.Combine(context.DirectoryPath, "e-" + Path.GetFileName(vm.JournalDirectory)[..8]);
            if (Path.Exists(export)) throw new IOException("模板导出目录已存在，保留原内容。");
            Directory.CreateDirectory(export);
            // Export the current disk contents as a standalone VM. Source checkpoints
            // stay on the template; GPU tests only receive independently verified disks.
            var exported = await ExHyperV.Services.VmExportService.ExportAsync(templateId, export, true, [],
                ExHyperV.Services.VmExportCheckpointMode.None, null, false);
            context.RequireSettled(exported.OperationStateUnknown, exported.Error ?? "模板导出终态待确认");
            context.Require(exported.Success, exported.Success ? "模板当前状态已导出为独立完整副本" : exported.Error ?? "模板完整副本导出失败");
            var copy = await WindowsTestCommand.RunAsync("""
                $source=Get-VM -Id ([guid]$a.id)
                if($source.State -ne 'Off'){throw '模板状态已变化'}
                if(@($source|Get-VMSnapshot).Count -ne $a.checkpoints){throw '模板检查点已变化'}
                $sourcePaths=@($source|Get-VMHardDiskDrive|Where-Object Path|ForEach-Object Path)
                if(Compare-Object $sourcePaths $a.paths){throw '模板磁盘已变化'}
                if(@($source|Get-VMHardDiskDrive|Where-Object {!$_.Path}).Count -or @($source|Get-VMAssignableDevice).Count -or @($source|Get-VMGpuPartitionAdapter).Count){throw '模板已挂接物理盘、PCIe 或 GPU'}
                $config=@(Get-ChildItem -LiteralPath $a.export -Recurse -Filter *.vmcx)
                if($config.Count -ne 1){throw '独立副本应仅有一个当前配置'}
                $target=Join-Path $a.root 'instance'; $disk=Join-Path $target 'Disks'
                New-Item -ItemType Directory -Force -Path $target,$disk|Out-Null
                $vm=Import-VM -Path $config[0].FullName -Copy -GenerateNewId -VirtualMachinePath $target -VhdDestinationPath $disk -SnapshotFilePath $target -SmartPagingFilePath $target
                $vm|Rename-VM -NewName $a.name
                $vm|Set-VM -AutomaticStartAction Nothing -AutomaticCheckpointsEnabled $false
                $vm|Set-VMMemory -DynamicMemoryEnabled $false -StartupBytes ([long]$a.startupBytes) -MinimumBytes ([long]$a.startupBytes) -MaximumBytes ([long]$a.startupBytes)
                $memory=Get-VMMemory -VM $vm
                if($memory.DynamicMemoryEnabled -or $memory.Startup -ne [long]$a.startupBytes){throw '测试副本内存预算核验失败'}
                # The installed system is copied; setup media still belongs to the
                # template and may grant access only to its original VM identity.
                # Some host builds retain media after Set-VMDvdDrive -Path $null.
                # Recreate only the copied drive, preserving its controller slot.
                foreach($dvd in @(Get-VM -Id $vm.Id|Get-VMDvdDrive|Where-Object Path)){
                    Remove-VMDvdDrive -VMDvdDrive $dvd
                    Add-VMDvdDrive -VMName $vm.Name -ControllerNumber $dvd.ControllerNumber -ControllerLocation $dvd.ControllerLocation
                }
                if(@(Get-VM -Id $vm.Id|Get-VMDvdDrive|Where-Object Path).Count){throw '测试副本仍引用模板安装介质'}
                if(@($vm|Get-VMSnapshot).Count){throw '测试副本仍含检查点'}
                $root=[IO.Path]::GetFullPath($a.root).TrimEnd('\')+'\'
                foreach($drive in @($vm|Get-VMHardDiskDrive)){
                    if(!$drive.Path -or !([IO.Path]::GetFullPath($drive.Path)).StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw '副本磁盘离开专属目录'}
                    $vhd=Get-VHD -Path $drive.Path
                    if($vhd.ParentPath -or $vhd.VhdType -eq 'Differencing'){throw '测试副本仍依赖差分磁盘'}
                }
                if((Get-VM -Id ([guid]$a.id)).State -ne 'Off'){throw '模板运行状态已变化'}
                @{id=$vm.Id.ToString()}|ConvertTo-Json
                """, new { id = templateId, root = vm.Root, name = vm.Name, export = exported.OutputPath, startupBytes,
                    checkpoints = facts.GetProperty("checkpoints").GetInt32(), paths = facts.GetProperty("paths").EnumerateArray().Select(v => v.GetString()).ToArray() }, 1800);
            vm.Id = Guid.Parse(copy.GetProperty("id").GetString()!); Save(vm.JournalDirectory, new(vm.Name, vm.Root, vm.Id));
            context.Evidence.Add("已从指定模板制作完整磁盘副本，独立身份：" + vm.Id);
            context.Evidence.Add($"副本采用固定 {startupBytes / 1073741824d:F1} GiB 内存；模板原配置保留。");
            context.Evidence.Add("副本光驱已弹出模板安装介质；模板光驱保持原配置。");
            return vm;
        }
        catch { await CleanupAsync(vm.JournalDirectory); throw; }
    }

    private static void Save(string directory, Journal journal)
    {
        var path = Path.Combine(directory, JournalName);
        DurableJson.Write(path, journal);
    }

    internal static async Task CleanupAsync(string directory)
    {
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
        {
            if (File.Exists(Path.Combine(parent.FullName, "recovery-hold.txt"))) throw new IOException("原生操作终态待核实，保留虚拟机及关联资源：" + parent.FullName);
            if (parent.Name == "ApiCases") break;
        }
        var children = Path.Combine(directory, "vms");
        if (Directory.Exists(children))
            foreach (var child in Directory.GetDirectories(children))
                if (Guid.TryParseExact(Path.GetFileName(child), "N", out _)) await CleanupAsync(child);
        var path = Path.Combine(directory, JournalName);
        if (!File.Exists(path)) return;
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path), InspectionJson.Options)
            ?? throw new IOException("临时虚拟机归属记录缺失。");
        ValidateRoot(directory, journal);
        await WindowsTestCommand.RunAsync("""
            Import-Module Hyper-V -ErrorAction Stop
            $root=[IO.Path]::GetFullPath($a.root).TrimEnd('\')+'\'
            $all=@(Get-VM)
            $owned=@($all|Where-Object { $_.Id.ToString() -eq $a.id -or
                ([IO.Path]::GetFullPath($_.ConfigurationLocation).TrimEnd('\')+'\').StartsWith($root,[StringComparison]::OrdinalIgnoreCase) })
            foreach($vm in $owned){
                $location=[IO.Path]::GetFullPath($vm.ConfigurationLocation).TrimEnd('\')+'\'
                if(!$location.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw '虚拟机配置目录已离开本项范围，保留现场'}
                if($a.id -and $vm.Id.ToString() -ne $a.id){throw '同一配置目录出现额外虚拟机，保留现场'}
                if($vm.State -ne 'Off'){Stop-VM -VM $vm -TurnOff -Force -Confirm:$false}
                Remove-VM -VM $vm -Force -Confirm:$false
            }
            $left=@(Get-VM|Where-Object { $_.Id.ToString() -eq $a.id -or
                ([IO.Path]::GetFullPath($_.ConfigurationLocation).TrimEnd('\')+'\').StartsWith($root,[StringComparison]::OrdinalIgnoreCase) })
            if($left.Count){throw '临时虚拟机清理后仍有注册记录'}
            @{cleaned=$true}|ConvertTo-Json -Compress
            """, new { root = journal.Root, id = journal.Id?.ToString("D") }, 60);
        File.Delete(path);
    }

    internal static async Task RecoverAsync(string workspace)
    {
        var root = Path.GetFullPath(Path.Combine(workspace, "ApiCases"));
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.GetDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            bool hasJournal = new[] { JournalName, "template-power.json", "host-state.json", "cpu-groups.json", "pcie-device.json", "switch-case.json", "recovery-hold.txt" }.Any(name => File.Exists(Path.Combine(directory, name))) || Directory.Exists(Path.Combine(directory, "vms")) || Directory.Exists(Path.Combine(directory, "switches")) || Directory.Exists(Path.Combine(directory, "groups"));
            if (!hasJournal) continue;
            if (File.Exists(Path.Combine(directory, "recovery-hold.txt"))) throw new IOException("存在待核实的原生任务，请先处理保留现场：" + directory);
            await PcieApiCases.RestoreAsync(directory);
            await CleanupAsync(directory);
            await Automation.TemplatePowerLease.RestoreAsync(directory, ownsHostLease: true);
            await TemporaryDisk.CleanupMountsAsync(directory);
            await TemporaryCpuGroups.CleanupAsync(directory);
            await SwitchApiCases.RestoreAsync(directory);
            await TemporarySwitch.CleanupAllAsync(directory);
            await TemporaryGroup.CleanupAllAsync(directory);
            await HostStateJournal.RecoverAsync(directory);
            // Only a verified direct child with an ownership journal can be recovered here.
            Directory.Delete(directory, recursive: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (File.Exists(Path.Combine(context.DirectoryPath, "recovery-hold.txt"))) return;
        await CleanupAsync(JournalDirectory);
        context.Evidence.Add("专属虚拟机已注销；系统复查确认清理完成。");
    }
}
