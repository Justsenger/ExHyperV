using System.Drawing;
using System.Drawing.Imaging;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddConfigurationRuleCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("VmSecurityService.GetInterlocks()", [c => SecurityMatrix(c, "interlocks")]);
        add("VmSecurityService.GetCapabilities()", [c => SecurityMatrix(c, "capabilities")]);
        add("VmSecurityService.GetSettingError()", [c => SecurityMatrix(c, "error")]);
        add("VmMemoryService.NormalizeMemoryBackingSettings()", [c =>
        {
            foreach (string? changed in new string?[] { null, "BackingPageSize", "BackingType", "EnableColdHint", "Weight" })
            {
                var m = new VmMemorySettings { Startup = 512, Weight = 4321, BackingPageSize = 2, BackingType = 1, EnableColdHint = true, EnableHotHint = true, EnableEpf = true, EnablePrivateCompressionStore = true };
                c.Call(() => VmMemoryService.NormalizeMemoryBackingSettings(m, changed));
                if (changed == "Weight") { c.Equal((byte?)2, m.BackingPageSize, "其他设置保留页大小"); c.Equal((byte?)1, m.BackingType, "其他设置保留后备类型"); }
                else if (changed == "BackingType") { c.Equal((byte?)1, m.BackingPageSize, "选择虚拟后备回退为 2 MiB 页"); c.Equal((byte?)1, m.BackingType, "保留虚拟后备"); }
                else { c.Equal((byte?)2, m.BackingPageSize, "保留 1 GiB 页"); c.Equal((byte?)0, m.BackingType, "选择物理后备"); c.Sequence(new bool?[] { false, false, false, false }, new[] { m.EnableColdHint, m.EnableHotHint, m.EnableEpf, m.EnablePrivateCompressionStore }, "物理后备关闭联动功能"); }
                c.Equal(512L, m.Startup, "容量保持"); c.Equal(4321u, m.Weight, "权重保持");
            }
            var absent = new VmMemorySettings { BackingType = 0 }; c.Call(() => VmMemoryService.NormalizeMemoryBackingSettings(absent, null));
            c.Sequence(new bool?[] { null, null, null, null }, new[] { absent.EnableColdHint, absent.EnableHotHint, absent.EnableEpf, absent.EnablePrivateCompressionStore }, "缺测字段保留 null");
        }]);
        add("VmProcessorService.GetChangeError()", [c =>
        {
            var fields = new (string Name, object Value, bool Runtime)[] { ("Count", 2, false), ("Reservation", 100ul, true), ("Limit", 50000ul, true), ("RelativeWeight", 200, true), ("ExposeVirtualizationExtensions", true, false), ("CompatibilityForMigrationEnabled", true, false), ("EnableHostResourceProtection", true, true) };
            foreach (var field in fields) foreach (bool off in new[] { false, true })
            {
                var before = new VmProcessorSettings(); var after = new VmProcessorSettings(); typeof(VmProcessorSettings).GetProperty(field.Name)!.SetValue(after, field.Value);
                c.Equal(off || field.Runtime, c.Call(() => VmProcessorService.GetChangeError(before, after, off, new(true, false, true))) is null, field.Name + "，已关机=" + off);
            }
            c.Require(c.Call(() => VmProcessorService.GetChangeError(new(), new() { CpuBrandString = "CPU" }, true, new(false, true, false))) is not null, "ARM64 拒绝 x64 品牌字段");
            c.Require(c.Call(() => VmProcessorService.GetChangeError(new(), new() { EnablePerfmonPebs = true }, true, new(true, false, true))) is not null, "PEBS 要求先启用 PMU");
            c.Equal<string?>(null, c.Call(() => VmProcessorService.GetChangeError(new(), new() { EnablePerfmonPmu = true, EnablePerfmonPebs = true }, true, new(true, false, true))), "PEBS 与 PMU 同时启用允许");
        }]);
        add("VmProcessorSettings.Supports()", [c =>
        {
            var model = new VmProcessorSettings { SupportedProps = new(StringComparer.OrdinalIgnoreCase) { "virtualquantity", "PerfCpuFreqCapMhz", "ExtendedVirtualizationExtensions" } };
            var expected = new[] { VmProcessorCapability.ProcessorCount, VmProcessorCapability.FrequencyMaximum, VmProcessorCapability.HardwareIsolation };
            foreach (var item in Enum.GetValues<VmProcessorCapability>()) c.Equal(expected.Contains(item), c.Call(() => model.Supports(item)), "能力 " + item);
            c.Equal(false, c.Call(() => model.Supports((VmProcessorCapability)int.MaxValue)), "未知能力");
            model.SupportedProps.Clear(); foreach (var item in expected) c.Equal(false, c.Call(() => model.Supports(item)), "能力缺席 " + item);
        }]);
        add("VmCreationParameters 默认值", [c =>
        {
            var p = c.Call(() => new VmCreationParameters());
            c.Equal("NewVM", p.Name, "名称"); c.Equal("8.0", p.Version, "版本"); c.Equal(2, p.Generation, "代数"); c.Equal(4, p.ProcessorCount, "处理器数");
            c.Equal(4096L, p.MemoryMiB, "启动内存 MiB"); c.Require(p.EnableDynamicMemory && p.EnableSecureBoot && p.EnableTpm, "默认动态内存、安全启动和 TPM 启用");
            c.Equal(0, p.DiskMode, "新盘模式"); c.Equal(128L, p.DiskSizeGiB, "磁盘 GiB"); c.Equal("Disabled", p.IsolationType, "隔离类型");
            c.Require(p.SwitchId is null && p.Path == "" && p.VhdPath == "" && p.IsoPath == "" && !p.CreateDifferencingDisk && !p.StoreNewDisksWithVm, "默认资源路径与选项");
        }]);
        add("VmCreateService.GetCreationError()", [c =>
        {
            VmCreationParameters Valid() => new() { Name = "配置校验", Path = c.DirectoryPath, DiskMode = 2, ProcessorCount = 1, MemoryMiB = 512 };
            c.Equal<string?>(null, c.Call(() => VmCreateService.GetCreationError(Valid())), "最小有效配置");
            foreach (Action<VmCreationParameters> change in new Action<VmCreationParameters>[] { p => p.Name = "", p => p.ProcessorCount = 0, p => p.MemoryMiB = 31, p => { p.DiskMode = 0; p.DiskSizeGiB = 0; }, p => { p.DiskMode = 1; p.VhdPath = Path.Combine(c.DirectoryPath, "missing.vhdx"); }, p => p.SwitchId = Guid.Empty })
            { var value = Valid(); change(value); c.Require(c.Call(() => VmCreateService.GetCreationError(value)) is not null, "非法创建参数返回说明"); }
            var emptyDisk = Valid(); emptyDisk.DiskMode = 0; c.Equal<string?>(null, c.Call(() => VmCreateService.GetCreationError(emptyDisk)), "新盘目录留空允许使用虚拟机目录");
        }]);
        add("DiskPartitionInfo.构造()", [c =>
        {
            var part = c.Call(() => new DiskPartitionInfo(2, 1073741824, OperatingSystemType.Windows, "固定说明"));
            c.Equal(2, part.PartitionNumber, "编号"); c.Equal(1073741824ul, part.SizeInBytes, "字节数"); c.Equal(OperatingSystemType.Windows, part.OsType, "系统分类"); c.Equal("固定说明", part.TypeDescription, "说明");
        }]);
        add("LinuxGpuDeploymentScript.ToString()", [c => { var script = new LinuxGpuDeploymentScript { Name = "Linux 驱动脚本", Description = "测试说明" }; c.Equal("Linux 驱动脚本", c.Call(script.ToString), "仅返回名称"); }]);
        add("SwitchUpstreamAdapter.ToString()", [c =>
        {
            foreach (var (name, expected) in new[] { ("网络连接", "网卡型号 (网络连接)"), ("", "网卡型号") })
            { var adapter = new SwitchUpstreamAdapter(Guid.NewGuid(), "device", "网卡型号", name, true); c.Equal(expected, c.Call(adapter.ToString), "显示名称"); }
        }]);
        add("VmInstance.构造()", [c => CheckVmConstruction(c), c => CheckVmConstruction(c)]);
        add("VmScreenshotImage.EncodeJpeg()", [c => ScreenshotCodec(c, true)]);
        add("VmScreenshotImage.FromEncoded()", [c => ScreenshotCodec(c, false)]);
    }

    private static void CheckVmConstruction(ApiCaseContext c)
    {
        var empty = c.Call(() => new VmInstance()); c.Equal(Guid.Empty, empty.Id, "默认身份");
        c.Require(empty.Disks.Count == 0 && empty.NetworkAdapters.Count == 0 && empty.StorageItems.Count == 0, "默认设备集合为空");
        var id = Guid.NewGuid(); var vm = c.Call(() => new VmInstance(id, "中文 VM")); c.Equal(id, vm.Id, "指定身份"); c.Equal("中文 VM", vm.Name, "指定名称");
        c.Require(!ReferenceEquals(empty.Disks, vm.Disks) && !ReferenceEquals(empty.NetworkAdapters, vm.NetworkAdapters), "实例集合彼此独立");
    }
    private static void SecurityMatrix(ApiCaseContext c, string api)
    {
        foreach (bool secure in new[] { false, true }) foreach (bool tpm in new[] { false, true }) foreach (bool shield in new[] { false, true })
        foreach (int generation in api == "interlocks" ? new[] { 2 } : new[] { 1, 2 })
        foreach (var state in api == "interlocks" ? new[] { VmState.PoweredOff } : Enum.GetValues<VmState>())
        {
            bool allowed = api == "interlocks" || generation == 2 && state == VmState.PoweredOff;
            bool[] expected = [allowed, allowed && !shield, allowed && !shield && secure, allowed && !shield, allowed && !shield && tpm, allowed];
            if (api == "error")
            {
                var settings = Enum.GetValues<VmSecurityService.Setting>();
                foreach (var setting in settings) c.Equal(expected[(int)setting + 1], c.Call(() => VmSecurityService.GetSettingError(generation, state, new(secure, "", tpm, false, shield), setting)) is null, $"{setting} / {generation} / {state} / {secure}/{tpm}/{shield}");
            }
            else
            {
                var actual = c.Call(() => api == "interlocks" ? VmSecurityService.GetInterlocks(secure, tpm, shield) : VmSecurityService.GetCapabilities(generation, state, secure, tpm, shield));
                c.Sequence(expected, new[] { actual.CanEdit, actual.SecureBoot, actual.SecureBootTemplate, actual.Tpm, actual.Encryption, actual.Shielding }, $"安全能力 {generation}/{state}/{secure}/{tpm}/{shield}");
            }
        }
    }

    private static void ScreenshotCodec(ApiCaseContext c, bool encode)
    {
        using var bitmap = new Bitmap(32, 16); using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(Color.Red); graphics.FillRectangle(Brushes.Blue, 16, 0, 16, 16); }
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg })
        {
            using var data = new MemoryStream(); bitmap.Save(data, format); byte[] source = data.ToArray();
            var image = encode ? VmScreenshotImage.FromEncoded(source) : c.Call(() => VmScreenshotImage.FromEncoded(source));
            c.Equal(32, image.Width, "图像宽"); c.Equal(16, image.Height, "图像高"); c.Require(image.IsEncoded && image.Stride == 0, "编码图像标志");
            c.Sequence(source, image.CopyBytes(), "原编码字节保留"); source[0] ^= 255; c.Require(!source.SequenceEqual(image.CopyBytes()), "输入数组复制隔离");
            if (encode)
            {
                var bytes = c.Call(image.EncodeJpeg); using var stream = new MemoryStream(bytes); using var decoded = new Bitmap(stream);
                c.Equal(ImageFormat.Jpeg.Guid, decoded.RawFormat.Guid, "实际 JPEG 编码"); c.Equal(32, decoded.Width, "解码宽"); c.Equal(16, decoded.Height, "解码高");
                var red = decoded.GetPixel(4, 8); var blue = decoded.GetPixel(28, 8); c.Require(red.R > 220 && red.B < 35 && blue.B > 220 && blue.R < 35, "JPEG 色块内容与容许误差");
            }
        }
        if (!encode) c.Throws<ArgumentException>(() => c.Call(() => VmScreenshotImage.FromEncoded([0, 1, 2, 3])), "损坏图像拒绝解码");
        else
        {
            byte[] rgb = Enumerable.Range(0, 32 * 16).SelectMany(i => i % 32 < 16 ? new byte[] { 0, 248 } : new byte[] { 31, 0 }).ToArray();
            byte[] encoded = c.Call(VmScreenshotImage.FromRgb565(rgb, 32, 16).EncodeJpeg); using var stream = new MemoryStream(encoded); using var decoded = new Bitmap(stream);
            c.Require(decoded.GetPixel(4, 8).R > 220 && decoded.GetPixel(28, 8).B > 220, "RGB565 转 JPEG 色块位置");
        }
    }
}
