using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class HostDiskApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "HostDiskService.GetHostDisksAsync()" or "HostDiskService.GetDiskReferenceAsync()" or "HostDiskService.GetPartitionReferenceAsync()"
        or "HostDiskService.MountDiskImageAsync()" or "HostDiskService.MountVhdxAsync()" or "HostDiskService.DismountVhdxAsync()"
        or "HostDiskService.AssignPartitionDriveLetterAsync()" or "HostDiskService.RemovePartitionAccessPathAsync()"
        or "HostDiskService.SetDiskOfflineStatusAsync()" or "HostDiskService.SetDiskReadOnlyAsync()" or "HostDiskMountLease.ReleaseAsync()" or "HostDiskMountLease.DisposeAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api == "HostDiskService.GetHostDisksAsync()")
        {
            var before = NativeDisks(); var actual = await c.CallAsync(HostDiskService.GetHostDisksAsync); var after = NativeDisks();
            if (!before.Keys.OrderBy(v => v).SequenceEqual(after.Keys.OrderBy(v => v))) throw new ApiCheckSkippedException("采样期间宿主磁盘清单变化，请重试。");
            c.Require(actual.HasData, actual.Error); c.Sequence(before.Keys.OrderBy(v => v), actual.Data!.Select(v => v.Number).OrderBy(v => v), "磁盘编号集合");
            foreach (var disk in actual.Data!) { var raw = before[disk.Number]; c.Equal(Convert.ToInt64(raw["Size"]), disk.SizeBytes, "磁盘容量"); c.Equal(Convert.ToString(raw["UniqueId"]), disk.UniqueId, "磁盘唯一身份"); c.Equal(Convert.ToString(raw["SerialNumber"])?.Trim(), disk.SerialNumber.Trim(), "序列号"); if (Convert.ToBoolean(raw["IsBoot"]) || Convert.ToBoolean(raw["IsSystem"])) c.Require(!disk.CanPassthrough, "系统或启动盘受到保护"); }
            return;
        }
        if (p.Api == "HostDiskService.GetDiskReferenceAsync()")
        {
            foreach (var (number, raw) in NativeDisks())
            { var result = await c.CallAsync(() => HostDiskService.GetDiskReferenceAsync(number)); if (string.IsNullOrWhiteSpace(Convert.ToString(raw["UniqueId"])) && string.IsNullOrWhiteSpace(Convert.ToString(raw["SerialNumber"]))) { c.Equal(ApiErrorKind.Unsupported, result.ErrorKind, "无稳定身份"); continue; } c.Require(result.HasData, result.Error); c.Equal(number, result.Data!.Number, "编号"); c.Equal(Convert.ToString(raw["UniqueId"]), result.Data.UniqueId, "唯一 ID"); c.Equal(Convert.ToInt64(raw["Size"]), result.Data.SizeBytes, "字节容量"); }
            var negative = await c.CallAsync(() => HostDiskService.GetDiskReferenceAsync(-1)); c.Equal(ApiErrorKind.InvalidArgument, negative.ErrorKind, "负编号"); var missing = await c.CallAsync(() => HostDiskService.GetDiskReferenceAsync(int.MaxValue)); c.Equal(ApiErrorKind.NotFound, missing.ErrorKind, "缺席磁盘"); return;
        }
        if (p.Api is "HostDiskService.SetDiskOfflineStatusAsync()" or "HostDiskService.SetDiskReadOnlyAsync()")
        {
            await using var lease = await PhysicalDiskLease.AcquireAsync(c);
            foreach (bool value in new[] { true, false })
            {
                var result = await c.CallAsync(() => p.Api.Contains("Offline") ? HostDiskService.SetDiskOfflineStatusAsync(lease.Disk.Reference, value) : HostDiskService.SetDiskReadOnlyAsync(lease.Disk.Reference, value));
                c.RequireSettled(result.ErrorKind == ApiErrorKind.OutcomeUnknown, result.Error); c.Require(result.Success, result.Error);
                var actual = NativeDisks()[lease.Disk.Number]; c.Equal(value, Convert.ToBoolean(actual[p.Api.Contains("Offline") ? "IsOffline" : "IsReadOnly"]), "实际磁盘标志");
            }
            return;
        }
        await using var vm = await TemporaryVm.CreateAsync(c); string image = await TemporaryDisk.AttachAsync(vm, "mount-marker");
        await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Get-VMHardDiskDrive|Remove-VMHardDiskDrive; $true|ConvertTo-Json", new { id = vm.Id });
        if (p.Api is "HostDiskService.GetPartitionReferenceAsync()" or "HostDiskService.AssignPartitionDriveLetterAsync()" or "HostDiskService.RemovePartitionAccessPathAsync()")
        {
            var setup = await WindowsTestCommand.RunAsync("$d=Mount-VHD -Path $a.path -NoDriveLetter -Passthru|Get-Disk; $p=@($d|Get-Partition|Where-Object Type -eq 'Basic')[0]; @{number=$d.Number;partition=$p.PartitionNumber;guid=$p.Guid;paths=@($p.AccessPaths)}|ConvertTo-Json", new { path = image });
            int number = setup.GetProperty("number").GetInt32(), partition = setup.GetProperty("partition").GetInt32();
            var identity = await HostDiskService.GetDiskReferenceAsync(number); c.Require(identity.HasData, identity.Error);
            if (p.Api.Contains("GetPartition"))
            {
                var result = await c.CallAsync(() => HostDiskService.GetPartitionReferenceAsync(identity.Data!, partition)); c.Require(result.HasData, result.Error); c.Equal(Guid.Parse(setup.GetProperty("guid").GetString()!), result.Data!.PartitionGuid, "分区 GUID");
                var absent = await c.CallAsync(() => HostDiskService.GetPartitionReferenceAsync(identity.Data!, int.MaxValue)); c.Equal(ApiErrorKind.NotFound, absent.ErrorKind, "缺席分区");
                var changed = await c.CallAsync(() => HostDiskService.GetPartitionReferenceAsync(identity.Data! with { UniqueId = "changed" }, partition)); c.Equal(ApiErrorKind.Conflict, changed.ErrorKind, "旧磁盘身份拒绝"); return;
            }
            var part = await HostDiskService.GetPartitionReferenceAsync(identity.Data!, partition); c.Require(part.HasData, part.Error);
            var letter = await WindowsTestCommand.RunAsync("$used=@((Get-Volume).DriveLetter)+@((Get-PSDrive -PSProvider FileSystem).Name); [string](@(90..68|ForEach-Object {[char]$_}|Where-Object {[string]$_ -notin $used})[0])|ConvertTo-Json"); string? text = letter.GetString(); if (string.IsNullOrEmpty(text)) throw new ApiCheckSkippedException("宿主 D 至 Z 盘符均已占用。"); char drive = text[0];
            if (p.Api.Contains("Assign")) { var result = await c.CallAsync(() => HostDiskService.AssignPartitionDriveLetterAsync(part.Data!, char.ToLowerInvariant(drive))); c.Require(result.HasData, result.Error); c.Equal(drive, result.Data, "返回大写盘符"); c.Equal("mount-marker", File.ReadAllText($"{drive}:\\marker.txt"), "盘符映射到目标内容"); }
            else
            {
                await WindowsTestCommand.RunAsync("Add-PartitionAccessPath -DiskNumber $a.number -PartitionNumber $a.partition -AccessPath ($a.letter+':\\'); $true|ConvertTo-Json", new { number, partition, letter = text });
                var result = await c.CallAsync(() => HostDiskService.RemovePartitionAccessPathAsync(part.Data!, drive)); c.Require(result.Success, result.Error);
                var remaining = await WindowsTestCommand.RunAsync("@{paths=@((Get-Partition -DiskNumber $a.number -PartitionNumber $a.partition).AccessPaths)}|ConvertTo-Json", new { number, partition }); c.Require(!remaining.GetProperty("paths").EnumerateArray().Any(v => v.GetString()!.Equals(text + ":\\", StringComparison.OrdinalIgnoreCase)), "指定盘符移除");
            }
            return;
        }
        bool existing = p.Id.EndsWith("-2", StringComparison.Ordinal);
        if (existing || p.Api.Contains("Dismount")) await WindowsTestCommand.RunAsync("Mount-VHD -Path $a.path -ReadOnly -NoDriveLetter; $true|ConvertTo-Json", new { path = image });
        if (p.Api.Contains("Dismount")) { var result = await c.CallAsync(() => HostDiskService.DismountVhdxAsync(image)); c.Require(result.Success, result.Error); c.Require(!await MountedAsync(image), "挂载已解除"); return; }
        if (p.Api.StartsWith("HostDiskMountLease."))
        {
            foreach (bool borrowed in new[] { false, true })
            {
                if (borrowed) await WindowsTestCommand.RunAsync("Mount-VHD -Path $a.path -ReadOnly -NoDriveLetter; $true|ConvertTo-Json", new { path = image });
                var lease = await HostDiskService.MountVhdxAsync(image); c.Require(lease.Result.Success, lease.Result.Error);
                try
                {
                    for (int i = 0; i < 2; i++)
                    { if (p.Api.Contains("Release")) { var result = await c.CallAsync(lease.ReleaseAsync); c.Require(result.Success, result.Error); } else await c.CallAsync(async () => { await lease.DisposeAsync(); return true; }); }
                    c.Equal(borrowed, await MountedAsync(image), "只清理自身建立的挂载");
                }
                finally { await lease.DisposeAsync(); if (borrowed) await WindowsTestCommand.RunAsync("Dismount-VHD -Path $a.path; $true|ConvertTo-Json", new { path = image }); }
            }
            if (p.Api.Contains("Dispose"))
            {
                var failed = await HostDiskMountLease.AcquireAsync(action => action(), () => Task.FromResult(new HostDiskMountResult(image, true, 100, true, "")), _ => Task.FromResult(ApiResponse.Fail("controlled-cleanup-failure")));
                try { await c.CallAsync(async () => { await failed.DisposeAsync(); return true; }); throw new ApiCheckFailureException("清理失败应抛 IOException"); }
                catch (IOException) { c.Require(failed.CleanupResult is { Success: false }, "受控清理失败保留结构化结果"); }
            }
            return;
        }
        var mounted = await c.CallAsync(() => p.Api.Contains("MountDiskImage") ? HostDiskService.MountDiskImageAsync(image) : HostDiskService.MountVhdxAsync(image));
        try
        { c.Require(mounted.Result.Success, mounted.Result.Error); c.Equal(!existing, mounted.Result.CreatedByCall, "挂载所有权"); var native = NativeDisks()[mounted.Result.DiskNumber!.Value]; c.Equal(Convert.ToBoolean(native["IsReadOnly"]), mounted.Result.IsReadOnly, "实际访问模式"); c.Require(await MountedAsync(image), "系统挂载状态"); }
        finally { await mounted.DisposeAsync(); }
        c.Equal(existing, await MountedAsync(image), "释放后挂载状态");
    }
    private static Dictionary<int, Dictionary<string, object?>> NativeDisks() => SystemBaseline.Query("SELECT * FROM MSFT_Disk", @"root\Microsoft\Windows\Storage").ToDictionary(v => Convert.ToInt32(v["Number"]));
    private static async Task<bool> MountedAsync(string path) => (await WindowsTestCommand.RunAsync("[bool](Get-DiskImage -ImagePath $a.path).Attached|ConvertTo-Json", new { path })).GetBoolean();
}
