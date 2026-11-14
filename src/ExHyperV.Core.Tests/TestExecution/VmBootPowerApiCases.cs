using System.Management;
using System.Runtime.InteropServices;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class VmBootPowerApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api is "VmBootService.GetBootOrderAsync()" or "VmBootService.SetBootOrderAsync()" or "VmBootService.SetIsoFirstAsync()"
        || p.Api.StartsWith("VmPowerService.ExecuteControlActionAsync(", StringComparison.Ordinal);

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api.StartsWith("VmPowerService.", StringComparison.Ordinal)) { await PowerAsync(p, c); return; }
        var generations = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? new[] { 2 } : new[] { 1, 2 };
        foreach (int generation in generations)
        {
            await using var vm = await TemporaryVm.CreateAsync(c, generation);
            await WindowsTestCommand.RunAsync("$v=Get-VM -Id ([guid]$a.id); if(@($v|Get-VMDvdDrive).Count -eq 0){$v|Add-VMDvdDrive}; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
            var read = await VmBootService.GetBootOrderAsync(vm.Id);
            c.Require(read.HasData && read.Data!.Count > 0, "准备已存在的启动项：" + read.Error);
            var order = read.Data!;
            if (p.Api == "VmBootService.GetBootOrderAsync()")
            {
                var result = await c.CallAsync(() => VmBootService.GetBootOrderAsync(vm.Id));
                c.Require(result.HasData, "读取启动项：" + result.Error);
                c.Sequence(BootKeys(vm.Id, generation), result.Data!.Select(item => generation == 1 ? Convert.ToString(item.Reference)! : item.FwPath ?? ""), "启动项身份及顺序");
                c.Require(result.Data!.All(item => item.OwnerVmId == vm.Id && !string.IsNullOrWhiteSpace(item.Name)), "每个启动项具有正确归属及显示名称");
            }
            else if (p.Api == "VmBootService.SetIsoFirstAsync()")
            {
                var before = BootKeys(vm.Id, generation);
                Require(c, await c.CallAsync(() => VmBootService.SetIsoFirstAsync(vm.Id)), "空光驱调用");
                c.Sequence(before, BootKeys(vm.Id, generation), "空光驱保持顺序");
                string source = Path.Combine(c.DirectoryPath, "iso-source-" + generation), iso = Path.Combine(vm.Root, "boot.iso");
                Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "marker.txt"), "optical fixture");
                IsoApiCases.OnSta(() => IsoBuilderService.BuildUdfIso(source, iso, "BOOT_TEST"));
                await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Get-VMDvdDrive|Select-Object -First 1|Set-VMDvdDrive -Path $a.iso; @{ok=$true}|ConvertTo-Json", new { id = vm.Id, iso });
                Require(c, await c.CallAsync(() => VmBootService.SetIsoFirstAsync(vm.Id)), "已有 ISO 移至首位");
                // Hyper-V UEFI paths may use a hardware address rather than CDROM text.
                if (generation == 2)
                {
                    var native = await WindowsTestCommand.RunAsync("$v=Get-VM -Id ([guid]$a.id); $first=($v|Get-VMFirmware).BootOrder[0].Device; $dvd=@($v|Get-VMDvdDrive)[0]; @{id=$first.Id;dvd=$dvd.Id}|ConvertTo-Json", new { id = vm.Id });
                    c.Require(!string.IsNullOrEmpty(native.GetProperty("id").GetString()), "系统首位启动设备具备身份");
                    c.Equal(native.GetProperty("dvd").GetString(), native.GetProperty("id").GetString(), "系统首位设备为已连接 ISO 的光驱");
                }
                else c.Equal("1", BootKeys(vm.Id, generation)[0], "BIOS 首位光驱代码");
                var first = BootKeys(vm.Id, generation);
                Require(c, await c.CallAsync(() => VmBootService.SetIsoFirstAsync(vm.Id)), "重复设置 ISO 首位");
                c.Sequence(first, BootKeys(vm.Id, generation), "重复调用保持顺序");
            }
            else if (p.Id.EndsWith("-1", StringComparison.Ordinal))
            {
                var reversed = order.AsEnumerable().Reverse().ToList();
                var identities = BootKeys(vm.Id, generation).Order(StringComparer.Ordinal).ToArray();
                Require(c, await c.CallAsync(() => VmBootService.SetBootOrderAsync(vm.Id, reversed)), "重排启动设备");
                c.Sequence(reversed.Select(item => generation == 1 ? Convert.ToString(item.Reference)! : item.FwPath ?? ""), BootKeys(vm.Id, generation), "系统实际启动顺序");
                c.Sequence(identities, BootKeys(vm.Id, generation).Order(StringComparer.Ordinal), "启动设备集合保留");
            }
            else
            {
                var before = BootKeys(vm.Id, generation);
                var fabricated = new BootOrderItem { Name = "失效项", OwnerVmId = vm.Id, Reference = generation == 1 ? (object)65535 : "missing", FwPath = "missing" };
                var invalid = await c.CallAsync(() => VmBootService.SetBootOrderAsync(vm.Id, [fabricated]));
                c.Require(!invalid.Success, "失效启动项被拒绝：" + invalid.Error);
                c.Sequence(before, BootKeys(vm.Id, generation), "拒绝后原启动顺序保留");
                if (generation == 1)
                {
                    await WindowsTestCommand.RunAsync("Get-VM -Id ([guid]$a.id)|Start-VM; @{ok=$true}|ConvertTo-Json", new { id = vm.Id });
                    var active = await c.CallAsync(() => VmBootService.SetBootOrderAsync(vm.Id, order.AsEnumerable().Reverse().ToList()));
                    c.Require(!active.Success, "一代运行时拒绝重排");
                    c.Sequence(before, BootKeys(vm.Id, generation), "运行时拒绝后顺序保留");
                }
            }
            c.Evidence.Add($"已核验第 {generation} 代启动配置。");
        }
        if (generations.Length == 1) c.Evidence.Add("本机 ARM64 覆盖第二代 UEFI；第一代 BIOS 分支由 x64 主机覆盖。");
    }

    private static List<Dictionary<string, object?>> NativeBootSources(Guid id)
    {
        var paths = (string[]?)SystemBaseline.Settings(id)["BootSourceOrder"] ?? [];
        return paths.Select(path =>
        {
            using var row = new ManagementObject(path); row.Get();
            return row.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase);
        }).ToList();
    }

    private static string[] BootKeys(Guid id, int generation) => generation == 1
        ? ((ushort[]?)SystemBaseline.Settings(id)["BootOrder"] ?? []).Select(value => value.ToString()).ToArray()
        : NativeBootSources(id).Select(row => Convert.ToString(row["FirmwareDevicePath"]) ?? "").ToArray();

    private static async Task PowerAsync(CatalogTest p, ApiCaseContext c)
    {
        int scenario = int.Parse(p.Id[(p.Id.LastIndexOf('-') + 1)..]);
        if (scenario == 2) VmTestAccess.RequireCredentials(c);
        await using var vm = scenario == 2 ? await TemporaryVm.CopyTemplateAsync(c) : await TemporaryVm.CreateAsync(c);
        bool byName = p.Api.Contains("string vmName", StringComparison.Ordinal);
        Task<ApiResponse> Call(string action) => c.CallAsync(() => byName ? VmPowerService.ExecuteControlActionAsync(vm.Name, action) : VmPowerService.ExecuteControlActionAsync(vm.Id, action));
        async Task Act(string action, string expected, int seconds = 60)
        {
            if (action == "Start") await vm.EnsureStartCapacityAsync();
            Require(c, await Call(action), action);
            var actual = await WindowsTestCommand.RunAsync("""
                $end=[DateTime]::UtcNow.AddSeconds($a.seconds)
                do {
                    $state=(Get-VM -Id ([guid]$a.id)).State.ToString()
                    if($state -eq $a.expected){break}
                    Start-Sleep -Milliseconds 200
                } while([DateTime]::UtcNow -lt $end)
                $state|ConvertTo-Json
                """, new { id = vm.Id, expected, seconds }, seconds + 15, mayMutate: false);
            c.Equal(expected, actual.GetString(), action + " 系统状态");
        }
        if (scenario == 3)
        {
            foreach (string action in new[] { "unknown", "start", "", "2" })
            { var invalid = await Call(action); c.Equal(ApiErrorKind.InvalidArgument, invalid.ErrorKind, "动作校验 " + action); c.Equal((ushort)3, Convert.ToUInt16(SystemBaseline.Vm(vm.Id)["EnabledState"]), "原关机状态保留"); }
            var conflict = await Call("Suspend");
            c.Require(!conflict.Success, "关机虚拟机的暂停请求返回失败");
            await vm.DisposeAsync(); var gone = await Call("Start");
            c.Require(!gone.Success, "已删除目标返回失败"); return;
        }
        await Act("Start", "Running");
        if (scenario == 1) { await Act("Suspend", "Paused"); await Act("Start", "Running"); await Act("TurnOff", "Off"); return; }
        var ready = await WindowsTestCommand.RunAsync("""
            $end=(Get-Date).AddSeconds(180)
            do {$v=Get-VM -Id ([guid]$a.id); $s=@($v|Get-VMIntegrationService|Where-Object {$_.Id.ToString() -match '0e0b6031-5213-4934-818b-38d90ced39db'}); if($s.Count -eq 1 -and $s[0].Enabled -and $s[0].PrimaryStatusDescription -and $s[0].PrimaryOperationalStatus -eq 'Ok'){break}; Start-Sleep -Milliseconds 500}while((Get-Date) -lt $end)
            @{ready=($s.Count -eq 1 -and $s[0].Enabled -and $s[0].PrimaryOperationalStatus -eq 'Ok')}|ConvertTo-Json
            """, new { id = vm.Id }, 200);
        if (!ready.GetProperty("ready").GetBoolean()) throw new ApiCheckSkippedException("模板副本的正常关闭集成服务尚未就绪，保留正常关机语义；本项结束后清理专属副本。");
        await VmTestAccess.ReadyWindowsAsync(c, vm);
        await Act("Save", "Saved"); await Act("Start", "Running");
        const string bootTimeScript = "@{boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().Ticks}";
        long originalBoot = (await VmTestAccess.WindowsAsync(c, vm, bootTimeScript, mayMutate: false)).GetProperty("boot").GetInt64();
        Require(c, await Call("Restart"), "正常重新启动");
        await SystemBaseline.WaitStateAsync(vm.Id, 2, 180);
        bool restarted = false; var deadline = DateTime.UtcNow.AddMinutes(3); string? lastRead = null;
        do
        {
            try
            {
                long current = (await VmTestAccess.WindowsAsync(c, vm, bootTimeScript, seconds: 15, mayMutate: false)).GetProperty("boot").GetInt64();
                if (current > originalBoot) { restarted = true; c.Evidence.Add($"虚拟机系统启动时间已更新：{originalBoot} → {current}"); break; }
            }
            catch (Exception error) { lastRead = error.Message; }
            await Task.Delay(1000, c.Token);
        } while (DateTime.UtcNow < deadline);
        c.Require(restarted, "重启后虚拟机系统启动时间更新：" + lastRead);
        await Act("Stop", "Off", 180); await Act("Start", "Running"); await Act("TurnOff", "Off");
    }

    private static void Require(ApiCaseContext c, ApiResponse r, string step) => c.Require(r.Success, step + "：" + r.Error);
}
