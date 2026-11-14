using System.Reflection;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmAddressSpaceApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmMmioService.ComputeMmioPlan()" or "VmMmioService.CheckMmioSpaceAsync()" or "VmMmioService.ConfigureMmioAsync()" or "VmMmioService.UpdateMmioSpaceAsync()"
        or "VmPcieService.GetSettingsAsync()" or "VmPcieService.SetBootPciExpressAsync()" or "VmPcieService.SetSystemSettingsAsync()";
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api.StartsWith("VmPcieService.")) { await PcieAsync(p, c); return; }
        await using var restore = new HostStateJournal(c);
        var cache = typeof(VmMmioService).GetField("_cachedCeilingMb", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingFieldException("MMIO cache fixture");
        cache.SetValue(null, null);
        if (p.Api.Contains("Compute"))
        {
            c.Equal<VmMmioService.MmioPlan?>(null, c.Call(VmMmioService.ComputeMmioPlan), "尚无进程缓存");
            foreach (ulong ceiling in new[] { 34816UL, 524288UL, 1048576UL })
            {
                cache.SetValue(null, ceiling); var result = c.Call(VmMmioService.ComputeMmioPlan); c.Require(result.HasValue, "已知上限产生方案");
                c.Equal(ceiling / 2, result!.Value.BaseMiB, "高位基址"); c.Equal(Math.Min(ceiling - ceiling / 2 - 1024, 262144UL), result.Value.HighSizeMiB, "高位间隙受上限约束"); c.Equal(3584UL, result.Value.LowSizeMiB, "低位间隙 MiB");
            }
            c.Evidence.Add("仅向独立工作进程的私有缓存注入固定值，公开计算接口负责产出结果。"); return;
        }
        await using var vm = await TemporaryVm.CreateAsync(c);
        if (p.Api.Contains("CheckMmio"))
        {
            foreach (ulong? ceiling in new ulong?[] { null, 34816, 1048576 })
            {
                cache.SetValue(null, ceiling); ulong needed = ceiling is ulong top ? Math.Min(top / 2 - 1024, 262144UL) : 262144;
                // Set-VM aligns the address window. 512 MiB samples keep the
                // below/equal/above cases distinct after the provider applies them.
                foreach (ulong size in new[] { needed - 512, needed, needed + 512 })
                {
                    await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Set-VM -HighMemoryMappedIoSpace ([uint64]$a.size*1MB); $true|ConvertTo-Json", new { id = vm.Id, size });
                    c.Equal(size, Convert.ToUInt64(SystemBaseline.Settings(vm.Id)["HighMmioGapSize"]), "系统实际 MMIO 样本（MiB）");
                    var result = await c.CallAsync(() => VmMmioService.CheckMmioSpaceAsync(vm.Id)); c.Require(result.HasData, result.Error); c.Equal(size < needed ? MmioCheckResultType.NeedsExpansion : MmioCheckResultType.Ok, result.Data, "空间边界检查");
                }
            }
            return;
        }
        restore.ConfigurationElement(AppDataPaths.ConfigFilePath, "MmioCeilingMb");
        bool probe = p.Id == "T-f0887b2e52e6a943-2";
        if (probe) AppConfigurationStore.Update(root => root.Element("MmioCeilingMb")?.Remove());
        else if (MmioSettingsStore.GetMmioCeilingMb() is null)
        {
            var prepared = await VmMmioService.ConfigureMmioAsync(vm.Id);
            c.RequireSettled(prepared.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, prepared.Error);
            c.Require(prepared.Success && MmioSettingsStore.GetMmioCeilingMb() > 0, "为缓存分支准备实际探测结果：" + prepared.Error);
            cache.SetValue(null, null);
        }
        if (p.Api.Contains("Update")) await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Start-VM; $true|ConvertTo-Json", new { id = vm.Id });
        var response = await c.CallAsync(() => p.Api.Contains("Update") ? VmMmioService.UpdateMmioSpaceAsync(vm.Id) : VmMmioService.ConfigureMmioAsync(vm.Id));
        c.RequireSettled(response.ErrorKind == ApiErrorKind.OutcomeUnknown, response.Error); c.Require(response.Success, response.Error);
        ulong? savedCeiling = MmioSettingsStore.GetMmioCeilingMb(); c.Require(savedCeiling > 0, "宿主检测上限已持久化");
        ulong expectedHigh = Math.Min(savedCeiling!.Value / 2 - 1024, 262144UL); var actual = SystemBaseline.Settings(vm.Id);
        c.Equal(savedCeiling.Value / 2, Convert.ToUInt64(actual["HighMmioGapBase"]), "实际高位基址"); c.Equal(expectedHigh, Convert.ToUInt64(actual["HighMmioGapSize"]), "实际高位间隙"); c.Equal(3584UL, Convert.ToUInt64(actual["LowMmioGapSize"]), "实际低位间隙"); c.Equal(true, Convert.ToBoolean(actual["GuestControlledCacheTypes"]), "缓存类型开关"); c.Equal((ushort)3, Convert.ToUInt16(SystemBaseline.Vm(vm.Id)["EnabledState"]), "结束后虚拟机关闭");
    }
    private static async Task PcieAsync(CatalogTest p, ApiCaseContext c)
    {
        await using var vm = await TemporaryVm.CreateAsync(c);
        var settings = SystemBaseline.Settings(vm.Id); string instance = Convert.ToString(settings["InstanceID"])!;
        var systems = SystemBaseline.Query("SELECT * FROM Msvm_VirtualSystemPciExpressSettingData").Where(v => Convert.ToString(v["InstanceID"])!.StartsWith(instance + "\\", StringComparison.OrdinalIgnoreCase)).ToArray();
        bool boot = p.Api.Contains("Boot");
        if (p.Api.Contains("GetSettings"))
        {
            var result = await c.CallAsync(() => VmPcieService.GetSettingsAsync(vm.Id)); c.Require(result.HasData, result.Error);
            c.Equal(settings.ContainsKey("BootPciExpress"), result.Data!.BootPciExpressAvailable, "Boot 属性存在性"); c.Equal(settings.TryGetValue("BootPciExpress", out var v) && Convert.ToBoolean(v), result.Data.BootPciExpress, "Boot 值");
            c.Equal(systems.Length > 0 && systems[0].ContainsKey("EmulationMode") && systems[0].ContainsKey("Topology"), result.Data.SystemSettingsAvailable, "系统配置能力");
            if (systems.Length > 0) { c.Equal(Convert.ToUInt16(systems[0]["EmulationMode"]) == 1, result.Data.EmulationEnabled, "模拟模式"); c.Equal(Convert.ToUInt16(systems[0]["Topology"]), result.Data.Topology, "拓扑"); }
            c.Equal(0, result.Data.Devices.Count, "专属空白虚拟机初始 PCIe 设备数"); return;
        }
        if (boot ? !settings.ContainsKey("BootPciExpress") : systems.Length == 0) throw new ApiCheckSkippedException("当前宿主配置提供程序尚无本项 PCIe 属性。");
        int number = int.Parse(p.Id[(p.Id.LastIndexOf('-') + 1)..]);
        if (number == 3) await vm.DisposeAsync();
        foreach (bool enabled in number == 1 ? new[] { true, false } : new[] { true, true })
        {
            var result = await c.CallAsync(() => boot ? VmPcieService.SetBootPciExpressAsync(vm.Id, enabled) : VmPcieService.SetSystemSettingsAsync(vm.Id, enabled, enabled ? (ushort)1 : (ushort)0));
            c.RequireSettled(result.ErrorKind == ApiErrorKind.OutcomeUnknown, result.Error);
            if (number == 3) { c.Require(!result.Success, "目标删除后返回失败"); continue; }
            c.Require(result.Success, result.Error);
            if (boot) c.Equal(enabled, Convert.ToBoolean(SystemBaseline.Settings(vm.Id)["BootPciExpress"]), "Boot 原始值");
            else { var actual = SystemBaseline.Query("SELECT * FROM Msvm_VirtualSystemPciExpressSettingData").Single(v => Convert.ToString(v["InstanceID"])!.StartsWith(instance + "\\", StringComparison.OrdinalIgnoreCase)); c.Equal((ushort)1, Convert.ToUInt16(actual["EmulationMode"]), "永久模拟保持开启"); c.Equal(enabled ? (ushort)1 : (ushort)0, Convert.ToUInt16(actual["Topology"]), "拓扑原始值"); }
        }
    }
}
