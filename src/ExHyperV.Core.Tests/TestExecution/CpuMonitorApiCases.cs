using System.Diagnostics;
using System.Runtime.InteropServices;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class CpuMonitorApiCases
{
    internal static bool Supports(CatalogTest p) => p.Api.StartsWith("HyperVCpuMonitorService.", StringComparison.Ordinal);
    internal static async Task RunAsync(CatalogTest p, ApiCaseContext c)
    {
        if (p.Api.Contains("GetCoreType"))
        {
            var baseline = Topology(); byte min = baseline.Values.Min(), max = baseline.Values.Max();
            foreach (var (index, efficiency) in baseline) c.Equal(min == max ? CpuCoreType.Unknown : efficiency == max ? CpuCoreType.Performance : CpuCoreType.Efficient, c.Call(() => HyperVCpuMonitorService.GetCoreType(index)), "处理器效率分类：" + index);
            foreach (int invalid in new[] { -1, int.MaxValue }) c.Equal(CpuCoreType.Unknown, c.Call(() => HyperVCpuMonitorService.GetCoreType(invalid)), "范围外编号"); return;
        }
        if (p.Api.Contains("Dispose"))
        {
            var fixture = new CpuMonitorLifecycleChecks.Harness { HostInstances = ["LP 0"] };
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            bool active = p.Id.EndsWith("-2");
            if (active) fixture.OnSample = (_, n) => { if (n >= 2) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("受控样本释放超时"); } return 25; };
            await using var monitor = new HyperVCpuMonitorService(fixture.CreateBackend()); await fixture.NextDelayAsync(); monitor.GetCpuUsage();
            Task? sampling = active ? Task.Run(() => monitor.GetCpuUsage()) : null; Task? closing = null;
            try
            {
                if (active) c.Require(entered.Wait(TimeSpan.FromSeconds(5)), "采样进入受控阻塞点");
                closing = Task.Run(async () => { if (p.Api.Contains("DisposeAsync")) await c.CallAsync(async () => { await monitor.DisposeAsync(); return true; }); else c.Call(monitor.Dispose); });
                if (active) { await Task.Delay(50); c.Require(!closing.IsCompleted && fixture.Counters.All(v => v.Releases == 0), "释放等待在途采样"); }
            }
            finally { release.Set(); if (sampling is not null) await sampling; if (closing is not null) await closing; }
            if (p.Api.Contains("DisposeAsync")) await c.CallAsync(async () => { await monitor.DisposeAsync(); return true; }); else c.Call(monitor.Dispose);
            c.Require(fixture.Counters.All(v => v.Releases == 1), "重复释放后每个计数器恰好释放一次"); c.Throws<ObjectDisposedException>(() => monitor.GetCpuUsage(), "已释放实例拒绝采样");
            var emptyFixture = new CpuMonitorLifecycleChecks.Harness(); var empty = new HyperVCpuMonitorService(emptyFixture.CreateBackend());
            if (p.Api.Contains("DisposeAsync")) await c.CallAsync(async () => { await empty.DisposeAsync(); return true; }); else c.Call(empty.Dispose);
            c.Evidence.Add("在途采样通过内部计数器边界固定复现，调用公开释放接口。"); return;
        }
        const string category = "Hyper-V Hypervisor Logical Processor";
        if (!PerformanceCounterCategory.Exists(category)) throw new ApiCheckSkippedException("宿主缺少 Hyper-V 逻辑处理器性能计数器类别。");
        await using var service = p.Api.Contains("构造") ? c.Call(() => new HyperVCpuMonitorService()) : new HyperVCpuMonitorService();
        await Task.Delay(2300, c.Token);
        if (p.Api.Contains("构造")) { var samples = service.GetCpuUsage(); c.Require(samples.Any(s => s.IsHost), "构造后后台维护建立宿主计数器"); return; }
        var warmup = c.Call(service.GetCpuUsage); await Task.Delay(1100, c.Token); var idle = c.Call(service.GetCpuUsage);
        c.Require(idle.Where(v => v.IsHost).All(v => v.Sample.IsValid && v.Usage is >= 0 and <= 100), "预热后空闲样本有效且在范围内");
        using var stop = new CancellationTokenSource(); var load = Task.Run(() => { double total = 0; while (!stop.IsCancellationRequested) for (int n = 1; n < 10000; n++) total += Math.Sqrt(n); GC.KeepAlive(total); });
        try
        {
            await Task.Delay(1500, c.Token); var busy = c.Call(service.GetCpuUsage); c.Require(busy.Where(v => v.IsHost).All(v => v.Sample.IsValid && v.Usage is >= 0 and <= 100), "受控单线程负载样本有效"); c.Require(busy.Where(v => v.IsHost).Sum(v => v.Usage) > 0, "固定计算负载产生正的 CPU 使用率");
            var nativeIds = new PerformanceCounterCategory(category).GetInstanceNames()
                .Select(v => v.StartsWith("Hv ", StringComparison.Ordinal) ? v[3..] : v)
                .Where(v => v.StartsWith("LP ", StringComparison.Ordinal)).Select(v => int.Parse(v[3..])).Order();
            c.Sequence(nativeIds, busy.Where(v => v.IsHost).Select(v => v.CoreId).Order(), "逐核心身份与系统计数器一致");
        }
        finally { stop.Cancel(); await load; }
    }
    // Independent parsing of the native variable-length topology records. RelationProcessorCore=0.
    internal static Dictionary<int, byte> Topology()
    {
        uint size = 0; GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref size); if (size == 0) throw new IOException("系统处理器拓扑为空。");
        nint buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new Dictionary<int, byte>();
            for (int offset = 0; offset < size;)
            {
                nint record = buffer + offset; int length = Marshal.ReadInt32(record, 4); if (length < 32 || offset + length > size) throw new InvalidDataException("处理器拓扑记录长度无效。");
                byte efficiency = Marshal.ReadByte(record, 9); int groups = (ushort)Marshal.ReadInt16(record, 30);
                for (int g = 0; g < groups; g++)
                {
                    int at = 32 + g * 16; if (at + 16 > length) throw new InvalidDataException("处理器组记录越界。"); ulong mask = unchecked((ulong)Marshal.ReadInt64(record, at)); int group = (ushort)Marshal.ReadInt16(record, at + 8);
                    for (int bit = 0; bit < 64; bit++) if ((mask & (1UL << bit)) != 0) result.Add(group * 64 + bit, efficiency);
                }
                offset += length;
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLogicalProcessorInformationEx(int relationship, nint buffer, ref uint size);
}
