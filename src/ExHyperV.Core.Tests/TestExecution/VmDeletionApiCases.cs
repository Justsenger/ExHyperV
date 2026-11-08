using ExHyperV.Services;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmDeletionApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmDeleteService.DeleteVmAsync()" or "VmDeleteService.PreviewPurgeAsync()" or "VmDeleteService.PurgeVmAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c); string own = await TemporaryDisk.AttachAsync(vm, "owned-content");
        if (p.Api == "VmDeleteService.DeleteVmAsync()")
        {
            var result = await c.CallAsync(() => VmDeleteService.DeleteVmAsync(vm.Name, vm.Id)); c.Require(result.Success, "注销虚拟机：" + result.Error + " " + result.CleanupError);
            c.Equal(0, SystemBaseline.Query($"SELECT * FROM Msvm_ComputerSystem WHERE Name='{vm.Id:D}'").Count, "系统注册清除");
            c.Require(File.Exists(own), "保留磁盘文件"); c.Equal("owned-content", await TemporaryDisk.MarkerAsync(own), "保留磁盘内容");
            c.Require(result.RecoveryPath is not null && Directory.Exists(result.RecoveryPath), "保留恢复配置目录");
            c.Require(Directory.EnumerateFiles(result.RecoveryPath!, "*.vmcx", SearchOption.AllDirectories).Any(), "恢复目录含配置文件"); return;
        }
        await using var other = await TemporaryVm.CreateAsync(c);
        string shared = await TemporaryDisk.AttachAsync(other, "shared-content");
        string parent = Path.Combine(c.DirectoryPath, "protected-parent.vhdx"), child = Path.Combine(vm.Root, "child.vhdx"), sibling = Path.Combine(other.Root, "sibling.vhdx");
        string unrelated = Path.Combine(vm.Root, "keep-user-file.txt"); File.WriteAllText(unrelated, "preserve");
        await WindowsTestCommand.RunAsync("""
            $v=Get-VM -Id ([guid]$a.id); $o=Get-VM -Id ([guid]$a.other)
            $v|Add-VMHardDiskDrive -Path $a.shared
            New-VHD -Path $a.parent -SizeBytes 1GB -Dynamic|Out-Null
            New-VHD -Path $a.child -ParentPath $a.parent -Differencing|Out-Null
            New-VHD -Path $a.sibling -ParentPath $a.parent -Differencing|Out-Null
            $v|Add-VMHardDiskDrive -Path $a.child; $o|Add-VMHardDiskDrive -Path $a.sibling
            @{ok=$true}|ConvertTo-Json
            """, new { id = vm.Id, other = other.Id, shared, parent, child, sibling });
        // Include checkpoint and planned configurations. Get-VMHardDiskDrive alone only lists current attachments.
        var attachments = SystemBaseline.Query("SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceSubType='Microsoft:Hyper-V:Virtual Hard Disk'");
        string[] missing = attachments.SelectMany(row => row["HostResource"] as string[] ?? [])
            .Where(path => Path.IsPathFullyQualified(path) && MissingFile(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (p.Api == "VmDeleteService.PreviewPurgeAsync()")
        {
            var before = Directory.GetFiles(c.DirectoryPath, "*", SearchOption.AllDirectories).Order().ToArray();
            var result = await c.CallAsync(() => VmDeleteService.PreviewPurgeAsync(vm.Id)); c.Require(result.HasData, "彻底删除预览：" + result.Error);
            var preview = result.Data!;
            c.Evidence.AddRange(preview.DiskDependencyIssues);
            c.Require(!preview.DiskFiles.Contains(shared, StringComparer.OrdinalIgnoreCase) && !preview.DiskFiles.Contains(parent, StringComparer.OrdinalIgnoreCase), "共享磁盘与有外部子盘的父盘保留");
            c.Require(preview.RetainedDiskFiles.Contains(shared, StringComparer.OrdinalIgnoreCase), "预览说明共享盘保留");
            c.Sequence(before, Directory.GetFiles(c.DirectoryPath, "*", SearchOption.AllDirectories).Order(), "预览保持文件集合");
            c.Require(File.Exists(unrelated) && SystemBaseline.Vm(vm.Id).Count > 0, "预览保持注册及额外文件");
            if (missing.Length > 0 && preview.DiskDependencyIssues.Count > 0
                && preview.DiskDependencyIssues.All(issue => missing.Any(path => issue.Contains(path, StringComparison.OrdinalIgnoreCase))))
            {
                c.Require(new[] { own, child }.All(path => preview.RetainedDiskFiles.Contains(path, StringComparer.OrdinalIgnoreCase)
                    && !preview.DiskFiles.Contains(path, StringComparer.OrdinalIgnoreCase)), "无法确认依赖时预览保留候选磁盘");
                throw new ApiCheckSkippedException("已核验保护行为；专属盘删除预览需要其他虚拟机的磁盘引用可读。缺失：" + string.Join("、", missing));
            }
            c.Require(preview.DiskFiles.Contains(own, StringComparer.OrdinalIgnoreCase) && preview.DiskFiles.Contains(child, StringComparer.OrdinalIgnoreCase), "预览包括专属磁盘和专属差分子盘");
        }
        else
        {
            var result = await c.CallAsync(() => VmDeleteService.PurgeVmAsync(vm.Name, vm.Id));
            c.Equal(0, SystemBaseline.Query($"SELECT * FROM Msvm_ComputerSystem WHERE Name='{vm.Id:D}'").Count, "目标注册清除");
            c.Require(File.Exists(parent) && File.Exists(sibling) && File.Exists(shared), "其他虚拟机依赖的文件保留");
            c.Equal("shared-content", await TemporaryDisk.MarkerAsync(shared), "共享盘内容保持"); c.Equal("preserve", File.ReadAllText(unrelated), "额外文件保持");
            c.Equal(other.Id, Guid.Parse(Convert.ToString(SystemBaseline.Vm(other.Id)["Name"])!), "其他虚拟机保持注册");
            c.Sequence(new[] { shared, sibling }.Order(), (await TemporaryDisk.PathsAsync(other.Id)).Order(), "其他虚拟机磁盘连接保持");
            if (!result.Success && missing.Any(path => result.CleanupError?.Contains(path, StringComparison.OrdinalIgnoreCase) == true))
            {
                c.Equal(VmRegistrationState.Unregistered, result.RegistrationState, "返回实际注销状态");
                c.Require(new[] { own, child }.All(path => File.Exists(path) && result.RetainedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)), "无法确认依赖时保留并报告磁盘");
                c.Evidence.Add(result.CleanupError!);
                throw new ApiCheckSkippedException("已核验注销与磁盘保护；删除专属盘需要其他虚拟机的磁盘引用可读。缺失：" + string.Join("、", missing));
            }
            c.Require(result.Success, "彻底删除：" + result.Error + " " + result.CleanupError);
            c.Require(!File.Exists(own) && !File.Exists(child), "目标专属磁盘清理");
        }
    }

    private static bool MissingFile(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }
}
