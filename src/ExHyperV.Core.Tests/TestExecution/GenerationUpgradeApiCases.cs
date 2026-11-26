using System.Runtime.InteropServices;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class GenerationUpgradeApiCases
{
    internal enum Fixture { Empty, Optical, MixedDisks }

    internal static async Task RunAsync(ApiCaseContext c, Fixture fixture)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !VmCreateService.SupportsGeneration1)
            throw new ApiCheckSkippedException("此用例需要 x64 Hyper-V 宿主。");

        bool optical = fixture == Fixture.Optical;
        var vm = TemporaryVm.Reserve(c);
        var versions = await VmCreateService.GetSupportedVersionsAsync();
        c.Require(versions.HasData && versions.Data!.Count > 0, "读取宿主支持的配置版本");
        string iso = "";
        if (optical)
        {
            var source = Directory.CreateDirectory(Path.Combine(c.DirectoryPath, "media")).FullName;
            File.WriteAllText(Path.Combine(source, "fixture.txt"), "generation upgrade");
            iso = Path.Combine(c.DirectoryPath, "fixture.iso");
            IsoApiCases.OnSta(() => IsoBuilderService.BuildUdfIso(source, iso, "UPGRADE"));
        }
        var created = await VmCreateService.CreateVirtualMachineAsync(new VmCreationParameters
        {
            Name = vm.Name, Path = vm.Root, Generation = 1, Version = versions.Data![0],
            DiskMode = 2, IsoPath = iso, EnableTpm = false, EnableSecureBoot = false, MemoryMiB = 512,
            ProcessorCount = 1, EnableDynamicMemory = false
        });
        c.RequireSettled(created.OutcomeUnknown, created.Error ?? "创建结果待核实");
        c.Require(created.Success && created.VmId.HasValue, created.Error ?? "创建测试虚拟机");
        Guid original = created.VmId.Value;
        vm.RecordIdentity(original);
        try
        {
            string existing = Path.Combine(vm.Root, "keep.vhdx");
            string? existingIdentity = null;
            string[] vhds = [Path.Combine(vm.Root, "first.vhd"), Path.Combine(vm.Root, "second.vhd")];
            if (fixture == Fixture.MixedDisks)
            {
                foreach (var (path, controller, location) in new[] { (vhds[0], "IDE", 0), (vhds[1], "SCSI", 3), (existing, "SCSI", 5) })
                {
                    var added = await VmStorageService.AddDriveAsync(original, new VmStorageAddRequest
                    {
                        ControllerType = controller, Location = location, PathOrNumber = path, IsNew = true, SizeGiB = 1
                    });
                    c.RequireSettled(added.ErrorKind == ExHyperV.Tools.ApiErrorKind.OutcomeUnknown, added.Error);
                    c.Require(added.Success, added.Error);
                }
                existingIdentity = HostDiskResource.Image(existing);
            }
            var initial = SystemBaseline.Settings(original);
            var options = new VmGenerationUpgradeOptions { EnableSecureBoot = true,
                SecureBootTemplateId = new Guid("1734c6e8-3154-4dda-ba5f-a874cc483422") };
            var result = await c.CallAsync(() => optical
                ? VmGenerationUpgradeService.UpgradeToGeneration2Async(original, options, c.Token)
                : VmGenerationUpgradeService.UpgradeToGeneration2Async(original, c.Token));
            c.RequireSettled(result.Status is VmGenerationUpgradeStatus.OutcomeUnknown or VmGenerationUpgradeStatus.CommittedCleanupPending,
                result.Error + " " + string.Join(", ", result.RetainedPaths));
            c.Require(result.Status == VmGenerationUpgradeStatus.Succeeded && result.NewVmId.HasValue, result.FailedStage + " " + result.Error);
            vm.RecordIdentity(result.NewVmId.Value);
            var final = SystemBaseline.Settings(vm.Id);
            c.Require(vm.Id != original && SystemBaseline.Query($"SELECT Name FROM Msvm_ComputerSystem WHERE Name='{original:D}'").Count == 0, "原 GUID 已注销");
            c.Equal("Microsoft:Hyper-V:SubType:2", final["VirtualSystemSubType"], "二代固件");
            c.Equal((ushort)3, SystemBaseline.Vm(vm.Id)["EnabledState"], "关机状态");
            c.Equal(initial["ConfigurationDataRoot"], final["ConfigurationDataRoot"], "原配置根目录");
            c.Equal(initial["ElementName"], final["ElementName"], "原名称");
            c.Equal(optical, final["SecureBootEnabled"], "安全启动");
            c.Equal(false, SystemBaseline.Security(vm.Id)["TpmEnabled"], "TPM 已关闭");
            if (fixture == Fixture.MixedDisks)
            {
                var expected = vhds.Select(path => Path.ChangeExtension(path, ".vhdx")).Append(existing).ToHashSet(StringComparer.OrdinalIgnoreCase);
                c.Require(expected.SetEquals(result.DiskPaths) && expected.All(File.Exists) && vhds.All(path => !File.Exists(path)), "两块 VHD 已转换并完成清理");
                c.Equal(Path.ChangeExtension(vhds[0], ".vhdx"), result.SystemDiskPath, "首选硬盘");
                c.Equal(existingIdentity, HostDiskResource.Image(existing), "原 VHDX 文件身份");
            }
            else c.Require(result.SystemDiskPath == null && result.DiskPaths.Count == 0, "无硬盘回执");
            if (optical)
            {
                c.Equal(options.SecureBootTemplateId.Value, Guid.Parse((string)final["SecureBootTemplateId"]!), "模板 GUID");
                var media = SystemBaseline.Query($"ASSOCIATORS OF {{{final["__PATH"]}}} WHERE ResultClass=Msvm_StorageAllocationSettingData");
                c.Require(media.Any(row => row["HostResource"] is string[] paths && paths.Contains(iso, StringComparer.OrdinalIgnoreCase)), "ISO 介质已恢复");
            }
            var replay = await VmGenerationUpgradeService.UpgradeToGeneration2Async(original, c.Token);
            c.Require(replay.Status == result.Status && replay.NewVmId == vm.Id && replay.RetainedPaths.Count == 0, "原 GUID 重入取得已完成回执");
        }
        finally
        {
            if (!File.Exists(Path.Combine(c.DirectoryPath, "recovery-hold.txt")))
            {
                var cleanup = await VmDeleteService.PurgeVmAsync(vm.Name, vm.Id);
                c.RequireSettled(cleanup.RegistrationState == VmRegistrationState.Unknown, cleanup.Error ?? "测试虚拟机清理结果待核实");
                c.Require(cleanup.Success, cleanup.Error ?? "已清理专属虚拟机");
            }
        }
    }
}
