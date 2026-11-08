using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddStorageRuleCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("VmStorageService.GetSourceChoice()", [c =>
        {
            foreach (int gen in new[] { 1, 2 }) foreach (bool physical in new[] { false, true }) foreach (bool fresh in new[] { false, true }) foreach (string type in new[] { "HardDisk", "DvdDrive" })
            { var choice = c.Call(() => VmStorageService.GetSourceChoice(gen, physical, type, fresh)); c.Equal(physical && gen == 2 && type == "DvdDrive" ? "HardDisk" : type, choice.DriveType, "来源类型"); c.Equal(!physical && fresh, choice.IsNew, "新建介质标志"); c.Equal(!physical || gen == 1, choice.SupportsOptical, "光驱适用性"); }
        }]);
        add("VmStorageService.GetEditStoragePathError()", [c =>
        {
            foreach (bool running in new[] { false, true }) foreach (string controller in new[] { "IDE", "SCSI" }) foreach (string type in new[] { "HardDisk", "DvdDrive" }) foreach (string source in new[] { "Virtual", "Physical" })
            { var item = new VmStorageAttachment { ControllerType = controller, DriveType = type, DiskType = source }; c.Equal(source == "Virtual" && !(running && controller == "IDE" && type == "HardDisk"), c.Call(() => VmStorageService.GetEditStoragePathError(item, running)) is null, $"{source}/{controller}/{type}/运行={running}"); }
        }]);
        add("VmStorageService.GetMediaPathError()", [c =>
        {
            string file = Path.Combine(c.DirectoryPath, "media.iso"); File.WriteAllText(file, "path fixture");
            foreach (string? path in new[] { null, "", " ", file }) c.Equal<string?>(null, c.Call(() => VmStorageService.GetMediaPathError(path)), "空介质或现有路径");
            c.Require(c.Call(() => VmStorageService.GetMediaPathError(file + ".missing")) is not null, "缺失介质返回说明");
        }]);
        add("VmStorageService.GetAddSlotError()", [c =>
        {
            foreach (var (type, count, slots) in new[] { ("IDE", 2, 2), ("SCSI", 4, 64) }) foreach (int controller in new[] { -1, 0, count - 1, count }) foreach (int slot in new[] { -1, 0, slots - 1, slots })
            { var request = new VmStorageAddRequest { ControllerType = type, ControllerNumber = controller, Location = slot }; c.Equal(controller >= 0 && controller < count && slot >= 0 && slot < slots, c.Call(() => VmStorageService.GetAddSlotError(type == "IDE" ? 1 : 2, false, request, [])) is null, $"{type}/{controller}/{slot}"); }
            var occupied = new VmStorageAttachment { ControllerType = "SCSI", ControllerNumber = 0, ControllerLocation = 0 };
            c.Require(c.Call(() => VmStorageService.GetAddSlotError(2, false, new(), [occupied])) is not null, "占用槽位拒绝");
            c.Require(c.Call(() => VmStorageService.GetAddSlotError(1, true, new() { ControllerType = "IDE" }, [])) is not null, "运行时 IDE 热添加拒绝");
        }]);
        add("VmStorageService.GetBestSlot()", [c =>
        {
            foreach (var (gen, type, slots, controllers) in new[] { (1, "IDE", 2, 2), (2, "SCSI", 64, 4) })
            {
                string drive = gen == 1 ? "DvdDrive" : "HardDisk";
                var empty = c.Call(() => VmStorageService.GetBestSlot(gen, false, drive, [])); c.Equal(new VmStorageSlot(type, 0, 0), empty.Slot, "空控制器首槽");
                var full = Enumerable.Range(0, controllers).SelectMany(number => Enumerable.Range(0, slots).Select(slot => new VmStorageAttachment { ControllerType = type, ControllerNumber = number, ControllerLocation = slot })).ToArray();
                var gap = full.Where(item => !(item.ControllerNumber == 0 && item.ControllerLocation == 1)).ToArray();
                c.Equal(new VmStorageSlot(type, 0, 1), c.Call(() => VmStorageService.GetBestSlot(gen, false, drive, gap)).Slot, "选择首个间隙");
                var exhausted = c.Call(() => VmStorageService.GetBestSlot(gen, false, drive, full)); c.Require(exhausted.Slot is null && !string.IsNullOrEmpty(exhausted.Error), "全满返回原因");
            }
        }]);
        add("VmStorageService.GetControllerFullError()", [c =>
        {
            foreach (var (type, slots) in new[] { ("IDE", 2), ("SCSI", 64) })
            { var all = Enumerable.Range(0, slots).Select(i => new VmStorageAttachment { ControllerType = type, ControllerNumber = 0, ControllerLocation = i }).ToArray(); c.Require(c.Call(() => VmStorageService.GetControllerFullError(type, 0, all)) is not null, "全满返回说明"); c.Equal<string?>(null, c.Call(() => VmStorageService.GetControllerFullError(type, 0, all.Skip(1))), "存在间隙允许"); c.Equal<string?>(null, c.Call(() => VmStorageService.GetControllerFullError(type, 0, [])), "空控制器允许"); }
        }]);
        add("VmStorageService.PrepareAddRequest()", [c => PrepareStorage(c, false), c => PrepareStorage(c, true)]);
    }
    private static void PrepareStorage(ApiCaseContext c, bool failure)
    {
        string source = Path.Combine(c.DirectoryPath, "source"); Directory.CreateDirectory(source); string disk = Path.Combine(source, "existing.vhdx"), iso = Path.Combine(source, "existing.iso"); File.WriteAllText(disk, "path fixture"); File.WriteAllText(iso, "path fixture");
        if (failure)
        {
            string occupied = Path.Combine(c.DirectoryPath, "file-as-directory"); File.WriteAllText(occupied, "keep");
            var result = c.Call(() => VmStorageService.PrepareAddRequest(new() { DriveType = "DvdDrive", IsNew = true, IsoSourcePath = source, IsoOutputPath = Path.Combine(occupied, "output.iso") }, 2, false, []));
            c.Require(!result.Success && result.IsError && !string.IsNullOrWhiteSpace(result.Error), "ISO 输出目录被文件占用时报告准备错误"); c.Equal("keep", File.ReadAllText(occupied), "已有文件保留"); return;
        }
        VmStorageAddRequest[] requests = [new() { PathOrNumber = disk }, new() { DriveType = "DvdDrive", PathOrNumber = iso }, new() { DriveType = "DvdDrive", PathOrNumber = "" },
            new() { PathOrNumber = Path.Combine(source, "new.vhdx"), IsNew = true, SizeGiB = 1 }, new() { PathOrNumber = Path.Combine(source, "child.vhdx"), IsNew = true, VhdType = "Differencing", ParentPath = disk },
            new() { DriveType = "DvdDrive", IsNew = true, IsoSourcePath = source, IsoOutputPath = Path.Combine(c.DirectoryPath, "created", "output.iso") }];
        foreach (var request in requests)
        {
            var result = c.Call(() => VmStorageService.PrepareAddRequest(request, 2, false, [])); c.Require(result.Success, "准备来源 " + request.DriveType + "：" + result.Error);
            c.Equal(request.ControllerType, result.Request!.ControllerType, "槽位类型保留");
            if (request.IsoOutputPath is not null) { c.Equal(request.IsoOutputPath, result.Request.PathOrNumber, "新 ISO 路径规范化"); c.Require(Directory.Exists(Path.GetDirectoryName(request.IsoOutputPath)), "ISO 输出目录自动准备"); }
            else c.Equal(request.PathOrNumber, result.Request.PathOrNumber, "来源路径保留");
        }
    }
}
