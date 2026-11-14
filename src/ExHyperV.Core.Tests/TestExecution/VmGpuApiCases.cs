using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ExHyperV.Services;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Vmcx;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmGpuApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmGpuService.构造()" or "VmGpuService.CheckVmForGpuAsync()"
        or "VmGpuService.GetVmGpuAssignmentsAsync()" or "VmGpuService.GetAvailableScriptsAsync()" or "VmGpuService.OptimizeVmForGpuAsync()"
        or "VmGpuService.AssignGpuPartitionAsync()" or "VmGpuService.RemoveGpuPartitionAsync()" or "VmGpuService.ScanVmPartitionsAsync()"
        or "VmGpuService.SyncWindowsDriversAsync()" or "VmGpuService.ProvisionLinuxGpuAsync()"
        or "VmGpuRepairService.ResolveVmcxAsync()" or "VmGpuRepairService.FindStaleGpuPartitionsAsync()" or "VmGpuRepairService.RepairAsync()"
        or "VmGpuRepairService.StaleGpuPartition.SelectCandidate()" || p.Id is "T-319692fabf672c00-1" or "T-319692fabf672c00-3";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var query = new VmQueryService(); var gpu = c.Call(() => new VmGpuService(query));
        if (p.Api == "VmGpuRepairService.StaleGpuPartition.SelectCandidate()")
        {
            var original = new VmGpuRepairService.StaleGpuPartition(Guid.NewGuid(), 7, Guid.NewGuid().ToString(), "old", [new("candidate-A"), new("candidate-B")], false, null);
            c.Require(original.RequiresSelection, "两个候选必须明确选择"); var chosen = c.Call(() => original.SelectCandidate("candidate-b"));
            c.Equal("candidate-B", chosen.SelectedCandidate!.Id, "按身份选择且保留原始大小写"); c.Require(original.RequiresSelection && !chosen.RequiresSelection, "选择返回新对象，原快照保留");
            foreach (string invalid in new[] { "", "GPU model", "0" }) c.Throws<ArgumentException>(() => c.Call(() => original.SelectCandidate(invalid)), "候选身份校验"); return;
        }
        if (p.Api.Contains("GetAvailableScripts")) { await ScriptsAsync(c, gpu); return; }
        if (p.Api.Contains("ProvisionLinux")) { await LinuxAsync(p, c, query, gpu); return; }
        if (p.Api.StartsWith("VmGpuRepairService.")) { await RepairAsync(p, c); return; }
        if (p.Api.Contains("ScanVmPartitions")) { await ScanAsync(p, c, gpu); return; }
        if (p.Api.Contains("SyncWindowsDrivers") || p.Id.StartsWith("T-319692fabf672c00", StringComparison.Ordinal))
        {
            await using var restore = new HostStateJournal(c); restore.ConfigurationElement(AppDataPaths.ConfigFilePath, "MmioCeilingMb");
            await using var vm = await TemporaryVm.CopyTemplateAsync(c);
            var host = await SelectGpuAsync(c);
            var scan = await c.CallAsync(() => gpu.ScanVmPartitionsAsync(vm.Id, c.Token)); c.RequireSettled(scan.OutcomeUnknown || scan.CleanupRequired, string.Join(";", scan.CleanupErrors)); c.Require(scan.Success, string.Join(";", scan.Errors));
            foreach (var partition in scan.Partitions)
                c.Evidence.Add($"分区 {partition.PartitionNumber}：{partition.OsType}，{partition.SizeInBytes / 1073741824d:F1} GiB，BitLocker={partition.IsBitLocker}，磁盘 {partition.DiskDisplayName}");
            if (scan.Partitions.Count == 0)
            {
                // An empty Core scan needs an independent disk baseline before it
                // can be classified as a missing template prerequisite.
                var baseline = await WindowsTestCommand.RunAsync("""
                    $rows=@()
                    foreach($drive in @(Get-VM -Id ([guid]$a.id)|Get-VMHardDiskDrive)){
                        if(!$drive.Path){throw '副本出现物理磁盘'}
                        if((Get-VHD -Path $drive.Path).Attached){throw 'Core 扫描结束后副本磁盘仍挂载'}
                        $mounted=$null
                        try{
                            $mounted=Mount-VHD -Path $drive.Path -ReadOnly -NoDriveLetter -PassThru
                            $disk=$mounted|Get-Disk
                            foreach($part in @(Get-Partition -DiskNumber $disk.Number)){
                                $rows+=@{number=$part.PartitionNumber;size=$part.Size;gpt=$part.GptType;mbr=$part.MbrType}
                            }
                        }finally{if($mounted){Dismount-VHD -Path $drive.Path}}
                    }
                    @{partitions=$rows}|ConvertTo-Json -Depth 4
                    """, new { id = vm.Id }, 60);
                c.Evidence.Add("系统独立分区清单：" + baseline.GetRawText());
                bool hasWindowsData = baseline.GetProperty("partitions").EnumerateArray().Any(v =>
                    v.GetProperty("size").GetUInt64() >= 1073741824UL &&
                    (string.Equals(v.GetProperty("gpt").ToString().Trim('{', '}'), "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7", StringComparison.OrdinalIgnoreCase)
                     || v.GetProperty("mbr").ToString() == "7"));
                c.Require(!hasWindowsData, "Core 分区扫描与系统基本数据分区清单一致");
            }
            var partitions = scan.Partitions.Where(v => v.OsType == OperatingSystemType.Windows && !v.IsBitLocker).ToArray();
            if (partitions.Length == 0 && scan.Partitions.Any(v => v.OsType == OperatingSystemType.Windows && v.IsBitLocker))
                throw new ApiCheckSkippedException("模板的 Windows 分区已启用 BitLocker；离线 GPU 驱动部署需要可直接读取的系统分区。");
            if (partitions.Length != 1) throw new ApiCheckSkippedException("模板副本需有唯一可用 Windows 系统分区；当前识别到 " + partitions.Length + " 个。");
            if (p.Id == "T-126f66d78af91bcd-2")
            {
                var part = partitions[0]; string original = part.DiskPath; part.DiskPath += ".changed";
                var rejected = await c.CallAsync(() => gpu.SyncWindowsDriversAsync(vm.Id, host.InstanceId, host.Manu, part, _ => { })); c.Equal(ApiErrorKind.Conflict, rejected.ErrorKind, "扫描后磁盘身份被改动时拒绝部署"); part.DiskPath = original;
                var paths = await TemporaryDisk.PathsAsync(vm.Id); c.Require(paths.Contains(original, StringComparer.OrdinalIgnoreCase), "拒绝后原磁盘挂接保持"); return;
            }
            await RequireDriverCapacityAsync(c, partitions[0], host);
            Require(c, await c.CallAsync(() => gpu.SyncWindowsDriversAsync(vm.Id, host.InstanceId, host.Manu, partitions[0], text => { if (text.Contains("warning", StringComparison.OrdinalIgnoreCase) || text.Contains("警告")) c.Evidence.Add(text); })), "Windows 驱动同步");
            await VerifyDriverFilesAsync(c, partitions[0], host);
            Require(c, await gpu.OptimizeVmForGpuAsync(vm.Id), "GPU MMIO 准备"); Require(c, await gpu.AssignGpuPartitionAsync(vm.Id, host), "GPU 分配准备");
            var machine = new VmInstance(vm.Id, vm.Name) { StateCode = 2, HasGpuAssignment = true };
            await vm.StartAsync(); await SystemBaseline.WaitStateAsync(vm.Id, 2);
            await WaitGpuSampleAsync(c, query, machine);
            if (p.Id.EndsWith("-3", StringComparison.Ordinal))
            {
                int before = Convert.ToInt32(SystemBaseline.Vm(vm.Id)["ProcessID"]);
                await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Stop-VM -TurnOff -Force; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
                await vm.StartAsync();
                await SystemBaseline.WaitStateAsync(vm.Id, 2); c.Require(before != Convert.ToInt32(SystemBaseline.Vm(vm.Id)["ProcessID"]), "重新启动后工作进程已更换"); await WaitGpuSampleAsync(c, query, machine);
            }
            return;
        }
        await using var target = await TemporaryVm.CreateAsync(c);
        if (p.Api.Contains("构造"))
        {
            var result = await gpu.GetVmGpuAssignmentsAsync(target.Id); c.Require(result.HasData && result.Data!.Count == 0, "新服务使用传入查询依赖读取空白虚拟机"); return;
        }
        if (p.Api.Contains("CheckVmForGpu"))
        {
            ulong required = VmMmioService.ComputeMmioPlan()?.HighSizeMiB ?? VmMmioService.DefaultHighSizeMiB;
            foreach (var (cache, size) in new[] { (false, required), (true, required / 2), (true, required), (true, required + 1) })
            {
                await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Set-VM -GuestControlledCacheTypes ([bool]$a.cache) -HighMemoryMappedIoSpace ([uint64]$a.size*1MB); @{ok=$true}|ConvertTo-Json", new { id = target.Id, cache, size });
                var raw = SystemBaseline.Settings(target.Id);
                ulong actualSize = Convert.ToUInt64(raw["HighMmioGapSize"]);
                bool actualCache = Convert.ToBoolean(raw["GuestControlledCacheTypes"]);
                c.Equal(cache, actualCache, "样本缓存开关");
                c.Evidence.Add($"高位 MMIO：请求 {size} MiB，系统实际 {actualSize} MiB，要求 {required} MiB。");
                if (cache && size < required) c.Require(actualSize < required, "准备低于要求的 MMIO 样本");
                var result = await c.CallAsync(() => gpu.CheckVmForGpuAsync(target.Id)); c.Require(result.HasData, result.Error);
                c.Equal(actualCache && actualSize >= required, result.Data, "按实际配置判定缓存与 MMIO 容量");
            }
            return;
        }
        if (p.Api.Contains("OptimizeVm"))
        {
            await using var restore = new HostStateJournal(c); restore.ConfigurationElement(AppDataPaths.ConfigFilePath, "MmioCeilingMb");
            Require(c, await c.CallAsync(() => gpu.OptimizeVmForGpuAsync(target.Id)), "GPU 配置优化");
            var actual = SystemBaseline.Settings(target.Id); c.Require(Convert.ToBoolean(actual["GuestControlledCacheTypes"]), "系统缓存控制打开");
            var plan = VmMmioService.ComputeMmioPlan(); c.Require(plan.HasValue, "优化产生 MMIO 计划"); c.Equal(plan!.Value.HighSizeMiB, Convert.ToUInt64(actual["HighMmioGapSize"]), "实际高位 MMIO 容量"); return;
        }
        var selected = await SelectGpuAsync(c);
        var initially = await c.CallAsync(() => gpu.GetVmGpuAssignmentsAsync(target.Id)); c.Require(initially.HasData && initially.Data!.Count == 0, "初始分配为空");
        Require(c, await c.CallAsync(() => gpu.AssignGpuPartitionAsync(target.Id, selected)), "按完整宿主 GPU 身份分配");
        var native = await NativeAssignmentsAsync(target.Id); c.Equal(1, native.GetArrayLength(), "系统分区适配器数量");
        var assignments = await c.CallAsync(() => gpu.GetVmGpuAssignmentsAsync(target.Id)); c.Require(assignments.HasData && assignments.Data!.Count == 1, "查询返回唯一新适配器");
        var assignment = assignments.Data![0]; c.Equal(selected.InstanceId.ToUpperInvariant(), assignment.HostDeviceInstanceId.ToUpperInvariant(), "宿主显卡身份"); c.Equal(selected.PartitionableGpuPath, assignment.PartitionableGpuPath, "分区资源路径");
        c.Equal(native[0].GetProperty("Id").GetString(), assignment.AdapterId, "移除句柄对应系统适配器 ID");
        if (p.Id == "T-e2f590463ff5cbcd-2")
        {
            var fake = new HostGpuInfo { InstanceId = "PCI\\VEN_FFFF&DEV_FFFF\\missing", PartitionableGpuPath = "\\\\?\\PCI#VEN_FFFF&DEV_FFFF#missing" };
            var failed = await c.CallAsync(() => gpu.AssignGpuPartitionAsync(target.Id, fake)); c.Require(!failed.Success, "过期 GPU 身份拒绝分配"); c.RequireSettled(failed.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, failed.Error);
            c.Equal(native.GetRawText(), (await NativeAssignmentsAsync(target.Id)).GetRawText(), "失败保留此前分配");
        }
        if (p.Api.Contains("RemoveGpu"))
        {
            Require(c, await c.CallAsync(() => gpu.RemoveGpuPartitionAsync(target.Id, assignment)), "移除指定分区"); c.Equal(0, (await NativeAssignmentsAsync(target.Id)).GetArrayLength(), "系统分配已清空");
            var repeat = await c.CallAsync(() => gpu.RemoveGpuPartitionAsync(target.Id, assignment)); c.Require(!repeat.Success, "过期分配身份得到明确错误");
        }
    }
    internal static async Task<HostGpuInfo> SelectGpuAsync(ApiCaseContext c)
    {
        var result = await HostGpuService.GetHostGpusAsync(); c.Require(result.HasData, result.Error);
        return result.Data!.Where(v => v.IsPartitionable).OrderBy(v => v.InstanceId, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? throw new ApiCheckSkippedException("宿主未枚举出可分区 GPU。");
    }
    internal static async Task WaitGpuSampleAsync(ApiCaseContext c, VmQueryService query, VmInstance vm)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3); VmQueryService.GpuUsageData last = default;
        do
        {
            var values = await c.CallAsync(() => query.GetGpuPerformanceAsync([vm])); c.Require(values.ContainsKey(vm.Id), "GPU 样本保留 VM 身份"); last = values[vm.Id];
            if (last.Sample?.IsValid == true && last.DriverSample?.IsValid == true && last.IsDriverBound)
            { foreach (double value in new[] { last.Gpu3d, last.GpuCopy, last.GpuEncode, last.GpuDecode }) c.Require(double.IsFinite(value) && value >= 0, "GPU 性能值有效"); c.Evidence.Add("Core 已读取到该虚拟机的有效 GPU 性能指标。"); return; }
            await Task.Delay(1000, c.Token);
        } while (DateTime.UtcNow < deadline);
        throw new ApiCheckFailureException("部署并启动后 180 秒内仍缺少有效 GPU 性能样本：" + JsonSerializer.Serialize(last));
    }
    private static async Task<JsonElement> NativeAssignmentsAsync(Guid id)
    { var value = await WindowsTestCommand.RunAsync("@{items=@(Get-VM -Id $a.id|Get-VMGpuPartitionAdapter|Select-Object Id,InstancePath)}|ConvertTo-Json -Depth 4", new { id }); return value.GetProperty("items"); }
    private static void Require(ApiCaseContext c, ApiResponse r, string label) { c.RequireSettled(r.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, label + ": " + r.Error); c.Require(r.Success, label + ": " + r.Error); }
    private static async Task ScanAsync(CatalogTest p, ApiCaseContext c, VmGpuService service)
    {
        await using var physical = p.Id.EndsWith("-2", StringComparison.Ordinal) ? await PhysicalDiskLease.AcquireAsync(c) : null;
        await using var vm = await TemporaryVm.CreateAsync(c); string? file = null;
        // GPU deployment lists OS partitions of at least 1 GiB. Leave room for GPT metadata.
        if (physical is null) file = await TemporaryDisk.AttachAsync(vm, "partition-scan", sizeBytes: 2L * 1024 * 1024 * 1024);
        else { var add = await VmStorageService.AddDriveAsync(vm.Id, new() { IsPhysical = true, PhysicalDisk = physical.Disk, PathOrNumber = physical.Disk.Number.ToString() }); c.RequireSettled(add.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, add.Error); c.Require(add.Success, add.Error); }
        var before = await DiskStateAsync(file, physical?.Disk.Number);
        var result = await c.CallAsync(() => service.ScanVmPartitionsAsync(vm.Id, c.Token)); c.RequireSettled(result.OutcomeUnknown || result.CleanupRequired, string.Join(";", result.CleanupErrors)); c.Require(result.Success, string.Join(";", result.Errors));
        c.Equal(before.GetRawText(), (await DiskStateAsync(file, physical?.Disk.Number)).GetRawText(), "扫描后恢复挂载、离线与只读状态");
        if (file is not null) { c.Require(result.Partitions.Any(p => p.PartitionNumber > 0 && p.SizeInBytes > 0 && p.DiskPath == file), "测试 NTFS 分区及磁盘路径"); c.Equal("partition-scan", await TemporaryDisk.MarkerAsync(file), "扫描保持原文件内容"); }
        var cancelled = await c.CallAsync(() => service.ScanVmPartitionsAsync(vm.Id, new CancellationToken(true))); c.Require(cancelled.IsCancelled && !cancelled.Success && cancelled.CleanupErrors.Count == 0, "提前取消返回明确状态");
    }
    private static Task<JsonElement> DiskStateAsync(string? path, int? number) => WindowsTestCommand.RunAsync(path is null ? "Get-Disk -Number $a.number|Select-Object Number,IsOffline,IsReadOnly|ConvertTo-Json" : "Get-DiskImage -ImagePath $a.path|Select-Object Attached,ImagePath|ConvertTo-Json", new { path, number });
    private static async Task RepairAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        var location = await c.CallAsync(() => VmGpuRepairService.ResolveVmcxAsync(vm.Id)); c.Require(location.HasData, location.Error);
        c.Equal(vm.Id, location.Data!.VmId, "定位稳定身份"); c.Require(File.Exists(location.Data.Path) && Path.GetFullPath(location.Data.Path).StartsWith(vm.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "配置位于本项 VM 目录");
        if (p.Api.Contains("ResolveVmcx")) { await vm.DisposeAsync(); var absent = await c.CallAsync(() => VmGpuRepairService.ResolveVmcxAsync(vm.Id)); c.Equal(ApiErrorKind.NotFound, absent.ErrorKind, "注销后定位报告缺失"); return; }
        var before = await c.CallAsync(() => VmGpuRepairService.FindStaleGpuPartitionsAsync(vm.Id)); c.Require(before.HasData && before.Data!.Count == 0, "空白 VM 无失效 GPU");
        var doc = VmcxDocument.Read(location.Data.Path); var edit = doc.BeginEdit(); var id = edit.AddDevice(VmcxDeviceKind.GpuPartition, @"\\?\PCI#VEN_FFFF&DEV_FFFE#API_TEST#{064092b3-625e-43bf-9eb5-dc845897dd59}\GPUPARAV"); edit.Commit();
        var stale = await c.CallAsync(() => VmGpuRepairService.FindStaleGpuPartitionsAsync(vm.Id)); c.Require(stale.HasData, stale.Error);
        var target = stale.Data!.Single(v => Guid.Parse(v.Instance.Trim('_', '{', '}')) == id); c.Require(!target.CanRebind, "明确缺席的硬件身份没有重绑候选");
        if (p.Api.Contains("RepairAsync"))
        {
            var result = await c.CallAsync(() => VmGpuRepairService.RepairAsync(vm.Id, [target])); c.RequireSettled(result.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, result.Error); c.Require(result.HasData, result.Error); c.Equal(1, result.Data!.Removed, "精确删除一个失效定义"); c.Equal(0, result.Data.Rebound, "零重绑数量");
            c.Require(VmcxDocument.Read(location.Data.Path).GetDevices().All(v => Guid.Parse(v.Instance.Trim('_', '{', '}')) != id), "重新读取配置确认目标已移除");
            var repeated = await c.CallAsync(() => VmGpuRepairService.RepairAsync(vm.Id, [target])); c.Equal(ApiErrorKind.Conflict, repeated.ErrorKind, "过期修复快照拒绝复用");
        }
    }
    private static async Task VerifyDriverFilesAsync(ApiCaseContext c, DiskPartitionInfo partition, HostGpuInfo gpu)
    {
        var package = GpuDriverPackageResolver.Resolve(gpu.InstanceId, gpu.Manu);
        var manifest = await WindowsTestCommand.RunAsync("""
            $mounted=$false
            try {$d=Mount-VHD -Path $a.path -NoDriveLetter -ReadOnly -PassThru|Get-Disk; $mounted=$true
                $volume=Get-Partition -DiskNumber $d.Number -PartitionNumber $a.partition|Get-Volume
                $destination=Join-Path $volume.Path 'Windows\System32\HostDriverStore\FileRepository'
                $source=Join-Path $a.repository $a.package; $target=Join-Path $destination $a.package
                $mismatches=@();$count=0
                foreach($file in Get-ChildItem -LiteralPath $a.repository -File -Recurse){$relative=$file.FullName.Substring($a.repository.TrimEnd('\').Length+1);$copy=Join-Path $destination $relative
                    if(!(Test-Path -LiteralPath $copy) -or (Get-Item -LiteralPath $copy).Length -ne $file.Length){$mismatches+=$relative}}
                foreach($file in Get-ChildItem -LiteralPath $source -File -Recurse){$count++;$relative=$file.FullName.Substring($source.TrimEnd('\').Length+1);$copy=Join-Path $target $relative
                    if(!(Test-Path -LiteralPath $copy) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $copy).Hash){$mismatches+=$relative}}
                @{files=$count;mismatches=$mismatches}|ConvertTo-Json -Depth 3
            }finally{if($mounted){Dismount-VHD -Path $a.path}}
            """, new { path = partition.DiskPath, partition = partition.PartitionNumber, repository = package.HostFileRepository, package = package.PrimaryPackageName }, 600);
        c.Require(manifest.GetProperty("files").GetInt32() > 0, "驱动主包包含可核对文件"); c.Equal(0, manifest.GetProperty("mismatches").GetArrayLength(), "驱动主包逐文件 SHA-256 与宿主一致");
    }
    private static async Task RequireDriverCapacityAsync(ApiCaseContext c, DiskPartitionInfo partition, HostGpuInfo gpu)
    {
        var package = GpuDriverPackageResolver.Resolve(gpu.InstanceId, gpu.Manu);
        long bytes = Directory.EnumerateFiles(package.HostFileRepository, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        long required = checked(bytes + 1024L * 1024 * 1024);
        long hostFree = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(c.DirectoryPath))!).AvailableFreeSpace;
        if (hostFree < required) throw new ApiCheckSkippedException($"GPU 驱动库约 {bytes / 1073741824d:F1} GiB，副本所在磁盘现有 {hostFree / 1073741824d:F1} GiB；需要额外 1 GiB 余量。");
        var volume = await WindowsTestCommand.RunAsync("""
            $mounted=$false
            try{$disk=Mount-VHD -Path $a.path -ReadOnly -NoDriveLetter -PassThru|Get-Disk;$mounted=$true
                [long]($disk|Get-Partition -PartitionNumber $a.partition|Get-Volume).SizeRemaining|ConvertTo-Json
            }finally{if($mounted){Dismount-VHD -Path $a.path}}
            """, new { path = partition.DiskPath, partition = partition.PartitionNumber });
        if (volume.GetInt64() < required) throw new ApiCheckSkippedException($"虚拟机系统分区需容纳 {bytes / 1073741824d:F1} GiB 驱动库及 1 GiB 余量，现有 {volume.GetInt64() / 1073741824d:F1} GiB。");
        c.Evidence.Add($"宿主与副本系统分区均可容纳 {bytes / 1073741824d:F1} GiB 驱动库。");
    }
    private static async Task ScriptsAsync(ApiCaseContext c, VmGpuService service)
    {
        const string url = ResourceInspection.TestNetwork.ScriptIndexUrl;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) }; string json;
        try { json = await client.GetStringAsync(url, c.Token); } catch (HttpRequestException e) { throw new ApiCheckSkippedException("在线脚本索引暂不可达：" + e.Message); }
        var expected = JsonSerializer.Deserialize<LinuxGpuDeploymentScript[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var actual = await c.CallAsync(service.GetAvailableScriptsAsync); c.Sequence(expected.Select(v => v.FileName).Order(), actual.Select(v => v.FileName).Order(), "脚本索引文件名完整性"); c.Sequence(actual.Select(v => v.Name).Order(), actual.Select(v => v.Name), "脚本名称排序");
        foreach (var item in actual) c.Require(item.SourceUrl == url[..^"index.json".Length] + item.FileName, "脚本来源路径");
        var oldProxy = HttpClient.DefaultProxy; try { HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:1"); c.Equal(0, (await c.CallAsync(service.GetAvailableScriptsAsync)).Count, "受控网络故障返回空列表"); } finally { HttpClient.DefaultProxy = oldProxy; }
    }
    private static async Task LinuxAsync(CatalogTest p, ApiCaseContext c, VmQueryService query, VmGpuService service)
    {
        VmTestAccess.RequireCredentials(c);
        await using var vm = await TemporaryVm.CopyTemplateAsync(c); var gpu = await SelectGpuAsync(c);
        var credentials = await VmTestAccess.SshAsync(c, vm);
        var scripts = await service.GetAvailableScriptsAsync(); if (scripts.Count == 0) throw new ApiCheckSkippedException("在线部署脚本索引为空，缺少可执行脚本。");
        string distribution, version;
        using (var client = new Renci.SshNet.SshClient(credentials.Host, credentials.Port, credentials.Username, credentials.Password))
        {
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(15); client.Connect();
            using var info = client.CreateCommand(". /etc/os-release; printf '%s\\n%s\\n' \"$ID\" \"$VERSION_ID\"");
            info.CommandTimeout = TimeSpan.FromSeconds(10);
            var lines = info.Execute().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (info.ExitStatus != 0 || lines.Length != 2) throw new ApiCheckSkippedException("Linux 副本尚未提供可核验的系统 ID 与版本。");
            distribution = lines[0]; version = lines[1];
        }
        var compatible = scripts.Where(v => v.FileName.StartsWith(distribution + "-" + version + "-", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (compatible.Length != 1) throw new ApiCheckSkippedException($"在线索引尚无唯一匹配 {distribution} {version} 的部署脚本，已保留明确的系统版本要求。");
        var script = compatible[0];
        if (p.Id.EndsWith("-2", StringComparison.Ordinal))
        {
            credentials.Password = Guid.NewGuid().ToString("N"); using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var failed = await c.CallAsync(() => service.ProvisionLinuxGpuAsync(vm.Name, gpu.InstanceId, gpu.Manu, script, credentials, _ => { }, limit.Token)); c.Require(failed != "OK", "错误 SSH 密码产生失败结果"); return;
        }
        await using var restore = new HostStateJournal(c); restore.ConfigurationElement(AppDataPaths.ConfigFilePath, "MmioCeilingMb");
        await WindowsTestCommand.RunAsync("Get-VM -Id $a.id|Stop-VM -TurnOff -Force;@{off=$true}|ConvertTo-Json", new { id = vm.Id });
        await SystemBaseline.WaitStateAsync(vm.Id, 3);
        Require(c, await service.OptimizeVmForGpuAsync(vm.Id), "Linux GPU MMIO 准备"); Require(c, await service.AssignGpuPartitionAsync(vm.Id, gpu), "Linux GPU 分配");
        credentials = await VmTestAccess.SshAsync(c, vm);
        var result = await c.CallAsync(() => service.ProvisionLinuxGpuAsync(vm.Name, gpu.InstanceId, gpu.Manu, script, credentials, _ => { }, c.Token)); c.Equal("OK", result, "Linux 部署结果");
        await WaitGpuSampleAsync(c, query, new VmInstance(vm.Id, vm.Name) { StateCode = 2, HasGpuAssignment = true });
    }
}
