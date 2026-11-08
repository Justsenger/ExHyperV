using System.Text.Json;
using System.Runtime.InteropServices;
using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Core.Tests.TestExecution;

// Each native submission has a durable phase. A process lost during submission is recovered,
// never replayed as if it had failed. The continuation owns restarting and resuming the host.
internal static class RebootApiCases
{
    internal sealed record BootValue(bool Exists, ulong Value);
    internal sealed record State(string Id, string Phase, string Boot, int Step, int[] Values, BootValue Launch,
        BootValue Scheduler, string ProductType, int Calls, List<string> Evidence, string? Failure = null);
    internal static bool Supports(CatalogTest p) => p.Api is "HostNvmeService.SetNativeNvmeEnabled()" or "HyperVSchedulerService.SetSchedulerTypeAsync()"
        or "HyperVHostService.SetHyperVEnabledAsync()" or "HostProductTypeService.ApplySwitch()" || p.Id == "T-eb7d67479cd7d470-2";

    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        int recordedCalls = 0, recordedEvidence = 0;
        State Capture(State value)
        {
            var captured = value with { Calls = value.Calls + c.Calls - recordedCalls, Evidence = [.. value.Evidence, .. c.Evidence.Skip(recordedEvidence)] };
            recordedCalls = c.Calls; recordedEvidence = c.Evidence.Count; return captured;
        }
        // Direct isolated workers cannot retain a parent Actions job across host restart.
        // The normal runner routes these cases to the durable continuation below.
        if (!File.Exists(Path.Combine(c.DirectoryPath, "reboot-owner.json"))) throw new InvalidOperationException("重启测试必须由持久续跑调度器执行。");
        var state = Read(c.DirectoryPath);
        if (state is null)
        {
            if (HyperVHostService.GetVmmsStatus() == 0) throw new ApiCheckSkippedException("重启测试使用预先安装的 Hyper-V 组件；当前宿主尚未安装。");
            var pending = HostProductTypeService.GetPendingSwitch(); c.Require(pending.Success, pending.Error);
            if (pending.HasData) throw new ApiCheckSkippedException("宿主已有系统类型切换或恢复线索，请先完成该项再启动重启测试。");
            string product = ProductType(); if (product is not ("WinNT" or "ServerNT")) throw new ApiCheckSkippedException("当前产品类型为 " + product + "，本轮支持工作站与普通服务器。");
            if (p.Api.StartsWith("HostNvme") && !HostNvmeService.IsNativeNvmeSupported()) throw new ApiCheckSkippedException("原生 NVMe 开关要求 Windows Build 26100 及以上。");
            if (p.Api.StartsWith("HostNvme"))
            {
                var hardware = await WindowsTestCommand.RunAsync("@(Get-Disk|Where-Object BusType -eq 'NVMe').Count|ConvertTo-Json", mayMutate: false);
                if (hardware.GetInt32() == 0) throw new ApiCheckSkippedException("宿主当前缺少 NVMe 总线磁盘。");
            }
            var journal = new HostStateJournal(c);
            journal.Registry(@"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides", "1176759950");
            journal.Registry(@"SOFTWARE\Policies\Microsoft\Windows NT\Reliability", "ShutdownReasonOn");
            File.Copy(Path.Combine(c.DirectoryPath, "host-state.json"), Path.Combine(c.DirectoryPath, "original-registry.json"), true);
            int[] values = p.Api.StartsWith("HyperVScheduler") ? RuntimeInformation.OSArchitecture == Architecture.Arm64 ? [2] : [0, 1, 2]
                : p.Api.StartsWith("HostProductType") ? [product == "WinNT" ? 1 : 0]
                : [p.Id is "T-16cd2ad445f82f43-1" or "T-42830780177c3ae6-1" ? 1 : 0];
            state = new(p.Id, "Ready", await BootStampAsync(), 0, values, ReadBoot(BcdSetting.HypervisorLaunchType), ReadBoot(BcdSetting.HypervisorSchedulerType), product, 0, []);
            Save(c.DirectoryPath, state);
        }
        if (state.Id != p.Id) throw new InvalidDataException("重启恢复记录与测试身份冲突。");
        if (state.Phase is "Applying" or "Restoring")
        {
            // Power loss or process failure interrupted the native submission. Its outcome is uncertain.
            File.WriteAllText(Path.Combine(c.DirectoryPath, "recovery-hold.txt"), "重启续跑检测到未确认的提交阶段：" + state.Phase);
            throw new IOException("上次进程在提交阶段结束，原生结果待核验；已保留原配置与恢复材料。");
        }
        if (state.Phase is "AwaitTestBoot" or "AwaitRestoreBoot")
        {
            string boot = await BootStampAsync();
            if (boot == state.Boot) throw new RebootNeededException();
            state.Evidence.Add("已跨越宿主启动边界：" + state.Boot + " → " + boot);
            if (state.Phase == "AwaitRestoreBoot")
            {
                try
                {
                    EqualBoot(state.Launch, BcdSetting.HypervisorLaunchType); EqualBoot(state.Scheduler, BcdSetting.HypervisorSchedulerType);
                    if (ProductType() != state.ProductType) throw new IOException("宿主产品类型尚未恢复原值。");
                    VerifyOriginalRegistry(c.DirectoryPath);
                    c.Require(true, "原 BCD、产品类型及注册表已恢复，且经过重启读回核验");
                    state = Capture(state with { Phase = "Complete", Boot = boot }); Save(c.DirectoryPath, state);
                }
                catch (Exception e) { File.WriteAllText(Path.Combine(c.DirectoryPath, "recovery-hold.txt"), e.Message); throw; }
                c.Evidence.AddRange(state.Evidence);
                // Previous process API calls remain counted in the persisted state/report.
                if (state.Failure is not null) throw new ApiCheckFailureException(state.Failure);
                return;
            }
            try
            {
                await VerifyAsync(p, c, state.Values[state.Step]); state = state with { Step = state.Step + 1, Boot = boot, Phase = "Ready" };
            }
            catch (Exception e) { state = state with { Failure = e.Message, Step = state.Values.Length, Boot = boot, Phase = "Ready" }; }
            state = Capture(state); Save(c.DirectoryPath, state);
        }
        if (state.Phase == "Complete") { c.Evidence.AddRange(state.Evidence); if (state.Failure is not null) throw new ApiCheckFailureException(state.Failure); return; }
        if (state.Step < state.Values.Length && state.Failure is null)
        {
            state = state with { Phase = "Applying", Boot = await BootStampAsync() }; Save(c.DirectoryPath, state);
            try { await ApplyAsync(p, c, state.Values[state.Step]); }
            catch (Exception e)
            {
                if (File.Exists(Path.Combine(c.DirectoryPath, "recovery-hold.txt"))) throw;
                state = Capture(state with { Failure = e.Message, Step = state.Values.Length, Phase = "Ready" }); Save(c.DirectoryPath, state);
            }
            if (state.Failure is null)
            {
                state = Capture(state with { Phase = "AwaitTestBoot" }); Save(c.DirectoryPath, state); throw new RebootNeededException();
            }
        }
        state = state with { Phase = "Restoring", Boot = await BootStampAsync() }; Save(c.DirectoryPath, state);
        try
        {
            if (ProductType() != state.ProductType)
            {
                var restored = c.Call(() => HostProductTypeService.ApplySwitch(state.ProductType == "ServerNT"));
                c.RequireSettled(restored.State == HostProductTypeSwitchState.CleanupRequired || restored.Response.ErrorKind == ApiErrorKind.OutcomeUnknown, restored.Response.Error);
                c.Require(restored.Success && restored.RequiresRestart, "提交原产品类型恢复：" + restored.Response.Error);
                c.Evidence.AddRange(restored.RecoveryPaths.Select(v => "产品类型恢复材料：" + v));
            }
            await RestoreBootAsync(BcdSetting.HypervisorLaunchType, state.Launch);
            await RestoreBootAsync(BcdSetting.HypervisorSchedulerType, state.Scheduler);
            await HostStateJournal.RecoverAsync(c.DirectoryPath);
            state = Capture(state with { Phase = "AwaitRestoreBoot" }); Save(c.DirectoryPath, state);
        }
        catch (Exception e) { File.WriteAllText(Path.Combine(c.DirectoryPath, "recovery-hold.txt"), "恢复配置失败：" + e.Message); throw; }
        throw new RebootNeededException();
    }
    private static async Task ApplyAsync(CatalogTest p, ApiCaseContext c, int value)
    {
        ApiResponse result;
        if (p.Api.StartsWith("HostNvme")) result = c.Call(() => HostNvmeService.SetNativeNvmeEnabled(value == 1));
        else if (p.Api.StartsWith("HyperVScheduler")) result = await c.CallAsync(() => HyperVSchedulerService.SetSchedulerTypeAsync(value switch { 0 => HyperVSchedulerType.Classic, 1 => HyperVSchedulerType.Core, _ => HyperVSchedulerType.Root }));
        else if (p.Api.StartsWith("HostProductType"))
        {
            var response = c.Call(() => HostProductTypeService.ApplySwitch(value == 1));
            c.RequireSettled(response.State == HostProductTypeSwitchState.CleanupRequired || response.Response.ErrorKind == ApiErrorKind.OutcomeUnknown, response.Response.Error);
            c.Require(response.Success && response.ReplacementSubmitted && response.RequiresRestart, "产品类型替换已提交且要求重启：" + response.Response.Error); c.Evidence.AddRange(response.RecoveryPaths.Select(v => "保留恢复材料：" + v));
            if (p.Id.EndsWith("-2", StringComparison.Ordinal))
            {
                var repeated = c.Call(() => HostProductTypeService.ApplySwitch(value == 1)); c.Equal(HostProductTypeSwitchState.AlreadyPending, repeated.State, "重复提交识别挂起状态"); c.Require(!repeated.ReplacementSubmitted, "挂起期间只报告已有提交"); c.Sequence(response.RecoveryPaths, repeated.RecoveryPaths, "恢复材料保持同一份");
            }
            return;
        }
        else result = await c.CallAsync(() => HyperVHostService.SetHyperVEnabledAsync(value == 1));
        c.RequireSettled(result.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired, result.Error); c.Require(result.Success, "配置提交与读回：" + result.Error);
    }
    private static async Task VerifyAsync(CatalogTest p, ApiCaseContext c, int value)
    {
        if (p.Api.StartsWith("HostNvme"))
        {
            var result = c.Call(HostNvmeService.IsNativeNvmeEnabled); c.Require(result.HasData, result.Error); c.Equal(value == 1, result.Data, "重启后 NVMe 配置");
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Policies\Microsoft\FeatureManagement\Overrides"); c.Require(value == 1 ? key?.GetValue("1176759950") is int v && v == 1 : key?.GetValue("1176759950") is null, "原始覆盖值存在性");
            var drivers = await WindowsTestCommand.RunAsync("@{drivers=@(Get-CimInstance Win32_SystemDriver|Where-Object {$_.Name -match 'nvme' -and $_.State -eq 'Running'}|Select-Object Name,PathName)}|ConvertTo-Json -Depth 4"); c.Evidence.Add("本次启动的 NVMe 驱动观察：" + drivers.GetRawText()); return;
        }
        if (p.Api.StartsWith("HyperVScheduler"))
        { var expected = value switch { 0 => HyperVSchedulerType.Classic, 1 => HyperVSchedulerType.Core, _ => HyperVSchedulerType.Root }; c.Equal(expected, c.Call(HyperVSchedulerService.GetSchedulerType), "重启后实际调度器"); c.Equal(expected, c.Call(HyperVSchedulerService.GetConfiguredSchedulerType), "重启后配置调度器"); return; }
        if (p.Api.StartsWith("HostProductType"))
        {
            c.Equal(value == 1 ? "ServerNT" : "WinNT", ProductType(), "系统 ProductOptions 原始产品类型"); c.Equal(value == 1, c.Call(HostProductTypeService.IsServerSystem), "Core 产品类型判断");
            var pending = c.Call(HostProductTypeService.GetPendingSwitch); c.Require(pending.Success && pending.IsEmpty, "重启后挂起替换已消费"); return;
        }
        var deadline = DateTime.UtcNow.AddMinutes(2); (bool IsReady, bool IsInstalled, string StatusText) status;
        do { status = await c.CallAsync(HyperVHostService.GetHyperVStatusAsync); if (status.IsReady == (value == 1)) break; await Task.Delay(1000, c.Token); } while (DateTime.UtcNow < deadline);
        c.Equal(value == 1, status.IsReady, "重启后的 Hyper-V 就绪状态");
        c.Equal(value == 1, status.IsInstalled, "兼容摘要只在本次确实检查到 VMMS 后报告已安装");
        c.Equal(1, SystemBaseline.Query("SELECT Name FROM Win32_Service WHERE Name='vmms'", "root/cimv2").Count,
            "独立服务清单确认关闭启动开关后仍保留 VMMS 组件");
        var launch = ReadBoot(BcdSetting.HypervisorLaunchType); c.Require(launch.Exists && launch.Value == (ulong)value, "独立 BCD 读回");
    }
    internal static State? Read(string directory) { string path = Path.Combine(directory, "reboot-case.json"); return File.Exists(path) ? DurableJson.Read<State>(path) : null; }
    internal static async Task<bool> UndoDisableBeforeRestartAsync(string directory, string id)
    {
        // Only disabling Hyper-V is a BCD-only operation. Enabling it may also install features.
        if (id is not ("T-eb7d67479cd7d470-2" or "T-8c0edf66ce3506bf-1")
            || File.Exists(Path.Combine(directory, "recovery-hold.txt"))) return false;
        var state = Read(directory);
        if (state is not { Phase: "AwaitTestBoot", Step: 0, Values: [0] } || state.Id != id
            || await BootStampAsync() != state.Boot) return false;
        var current = ReadBoot(BcdSetting.HypervisorLaunchType);
        if (current != state.Launch)
        {
            if (!current.Exists || current.Value != 0) return false;
            await RestoreBootAsync(BcdSetting.HypervisorLaunchType, state.Launch);
        }
        EqualBoot(state.Launch, BcdSetting.HypervisorLaunchType);
        return true;
    }
    private static void VerifyOriginalRegistry(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "original-registry.json")));
        foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            using var key = Registry.LocalMachine.OpenSubKey(entry.GetProperty("key").GetString()!);
            string name = entry.GetProperty("name").GetString()!; bool exists = key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
            if (exists != entry.GetProperty("exists").GetBoolean() || exists && (int)key!.GetValueKind(name) != entry.GetProperty("type").GetInt32()) throw new IOException("重启后原始注册表存在性或类型核验失败：" + name);
            if (exists && JsonSerializer.SerializeToElement(key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)).GetRawText() != entry.GetProperty("value").GetRawText()) throw new IOException("重启后原始注册表值核验失败：" + name);
        }
    }
    private static void Save(string directory, State state) => DurableJson.Write(Path.Combine(directory, "reboot-case.json"), state);
    internal static async Task<string> BootStampAsync() { var value = await WindowsTestCommand.RunAsync("(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToString('o')|ConvertTo-Json"); return value.GetString()!; }
    private static string ProductType() { using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions"); return key?.GetValue("ProductType") as string ?? throw new IOException("系统产品类型读取失败。"); }
    private static BootValue ReadBoot(BcdSetting setting) { var value = BcdStoreClient.Read(setting); if (!value.Success) throw new IOException(value.Error); return new(value.HasData, value.Data); }
    private static void EqualBoot(BootValue original, BcdSetting setting) { if (ReadBoot(setting) != original) throw new IOException("BCD 原值核验失败：" + setting); }
    private static async Task RestoreBootAsync(BcdSetting setting, BootValue value)
    {
        if (value.Exists) { var response = await BcdStoreClient.SetAsync(setting, value.Value); if (!response.Success) throw new IOException(response.Error); }
        else if (ReadBoot(setting).Exists) await WindowsTestCommand.RunAsync("$s=bcdedit /deletevalue '{current}' $a.name 2>&1;if($LASTEXITCODE){throw ($s -join ' ')};$true|ConvertTo-Json", new { name = setting == BcdSetting.HypervisorLaunchType ? "hypervisorlaunchtype" : "hypervisorschedulertype" });
        EqualBoot(value, setting);
    }
}

internal sealed class RebootNeededException : Exception;

internal static class DurableJson
{
    internal static T Read<T>(string path)
    {
        // Readers keep the previous snapshot while the writer publishes the next one.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(input, ResourceInspection.InspectionJson.Options)
            ?? throw new InvalidDataException("进度文件内容为空：" + path);
    }

    internal static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value, ResourceInspection.InspectionJson.Options);
                file.Flush(true);
            }
            // A separate report viewer may briefly hold the old file without delete sharing.
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, path, true); return; }
                catch (Exception e) when (attempt < 5 && (e is IOException or UnauthorizedAccessException)
                    && (e.HResult & 0xffff) is 5 or 32 or 33)
                { Thread.Sleep(25 << attempt); }
            }
        }
        finally { File.Delete(temp); }
    }
}
