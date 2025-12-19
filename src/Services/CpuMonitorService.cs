using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Tools; // 引用你的 Utils 所在的命名空间

namespace ExHyperV.Services
{
    public enum CoreType
    {
        Performance, Efficient, Unknown
    }

    public class CpuMonitorService : IDisposable
    {
        #region P/Invoke for Core Type Detection (保持不变)
        private static readonly Dictionary<int, CoreType> _coreTypeCache = new Dictionary<int, CoreType>();
        private static bool _isHybrid = false;

        static CpuMonitorService()
        {
            InitializeCoreTypes();
        }

        public static CoreType GetCoreType(int coreId)
        {
            if (!_isHybrid) return CoreType.Unknown;
            return _coreTypeCache.TryGetValue(coreId, out var type) ? type : CoreType.Unknown;
        }

        private static void InitializeCoreTypes()
        {
            try
            {
                uint returnLength = 0;
                GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref returnLength);
                if (returnLength == 0) return;
                IntPtr buffer = Marshal.AllocHGlobal((int)returnLength);
                try
                {
                    if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, buffer, ref returnLength))
                    {
                        var ptr = buffer;
                        long offset = 0;
                        byte maxClass = 0;
                        byte minClass = 255;
                        var tempInfo = new List<(int Id, byte Class)>();
                        while (offset < returnLength)
                        {
                            var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(ptr);
                            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                            {
                                byte efficiencyClass = info.Processor.EfficiencyClass;
                                if (efficiencyClass > maxClass) maxClass = efficiencyClass;
                                if (efficiencyClass < minClass) minClass = efficiencyClass;
                                for (int i = 0; i < info.Processor.GroupCount; i++)
                                {
                                    IntPtr groupMaskPtr = ptr + (int)Marshal.OffsetOf<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>("Processor")
                                                          + (int)Marshal.OffsetOf<PROCESSOR_RELATIONSHIP>("GroupMask")
                                                          + i * Marshal.SizeOf<GROUP_AFFINITY>();
                                    var groupInfo = Marshal.PtrToStructure<GROUP_AFFINITY>(groupMaskPtr);
                                    ulong mask = (ulong)groupInfo.Mask;
                                    for (int bit = 0; bit < 64; bit++)
                                    {
                                        if ((mask & (1UL << bit)) != 0)
                                        {
                                            tempInfo.Add((bit + groupInfo.Group * 64, efficiencyClass));
                                        }
                                    }
                                }
                            }
                            offset += info.Size;
                            ptr = IntPtr.Add(ptr, (int)info.Size);
                        }
                        if (maxClass > minClass)
                        {
                            _isHybrid = true;
                            foreach (var item in tempInfo)
                            {
                                _coreTypeCache[item.Id] = (item.Class == maxClass) ? CoreType.Performance : CoreType.Efficient;
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch { _isHybrid = false; }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType, IntPtr Buffer, ref uint ReturnedLength);
        private enum LOGICAL_PROCESSOR_RELATIONSHIP { RelationProcessorCore = 0 }
        [StructLayout(LayoutKind.Sequential)] private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX { public LOGICAL_PROCESSOR_RELATIONSHIP Relationship; public uint Size; public PROCESSOR_RELATIONSHIP Processor; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESSOR_RELATIONSHIP { public byte Flags; public byte EfficiencyClass; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)] public byte[] Reserved; public ushort GroupCount; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] public GROUP_AFFINITY[] GroupMask; }
        [StructLayout(LayoutKind.Sequential)] private struct GROUP_AFFINITY { public UIntPtr Mask; public ushort Group; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public ushort[] Reserved; }
        #endregion

        // --- 核心逻辑重构 ---

        // 使用线程安全的字典存储活跃的计数器，Key 为 Instance Name (例如 "Ubuntu:Hv VP 0")
        private readonly ConcurrentDictionary<string, PerformanceCounter> _counters = new ConcurrentDictionary<string, PerformanceCounter>();

        // 存储所有 VM 的核心数配置 (用于显示已关闭的 VM)，Key 为 VM Name
        private readonly ConcurrentDictionary<string, int> _vmCoreCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public CpuMonitorService()
        {
            // 初始化宿主机核心数
            _vmCoreCounts["Host"] = Environment.ProcessorCount;

            // 启动后台维护线程：负责处理计数器的增删和 PowerShell 信息的获取
            Task.Run(() => MaintainCountersLoop(_cts.Token));
        }

        public void Dispose()
        {
            _cts.Cancel();
            foreach (var counter in _counters.Values)
            {
                counter.Dispose();
            }
            _counters.Clear();
        }

        /// <summary>
        /// 获取当前 CPU 用量。
        /// 此方法只读取内存中的计数器，不进行 I/O 操作，因此速度极快，适合 UI 定时调用。
        /// </summary>
        public List<CpuCoreMetric> GetCpuUsage()
        {
            var results = new List<CpuCoreMetric>();
            var activeVmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 读取所有现存计数器的值
            // ToArray() 创建当前时刻的快照，保证遍历安全
            var currentCounters = _counters.ToArray();

            foreach (var kvp in currentCounters)
            {
                string instanceName = kvp.Key;
                PerformanceCounter counter = kvp.Value;

                try
                {
                    float value = counter.NextValue();

                    if (instanceName.StartsWith("Host_"))
                    {
                        // 处理宿主机 Host_0, Host_1 等
                        if (int.TryParse(instanceName.Substring(5), out int coreId))
                        {
                            results.Add(new CpuCoreMetric
                            {
                                VmName = "Host",
                                CoreId = coreId,
                                Usage = value,
                                IsRunning = true
                            });
                        }
                    }
                    else
                    {
                        // 处理虚拟机
                        // 格式通常为 "VmName:Hv VP X" 
                        int colonIndex = instanceName.LastIndexOf(':');
                        if (colonIndex > 0)
                        {
                            string vmName = instanceName.Substring(0, colonIndex);
                            string suffix = instanceName.Substring(colonIndex + 1); // "Hv VP 0"

                            // 简单解析 ID
                            var match = Regex.Match(suffix, @"Hv VP (\d+)");
                            if (match.Success)
                            {
                                int vCpuId = int.Parse(match.Groups[1].Value);
                                activeVmNames.Add(vmName);
                                results.Add(new CpuCoreMetric
                                {
                                    VmName = vmName,
                                    CoreId = vCpuId,
                                    Usage = value,
                                    IsRunning = true
                                });
                            }
                        }
                    }
                }
                catch
                {
                    // 计数器可能在读取瞬间失效（VM刚关闭），忽略本次错误，后台线程会清理它
                }
            }

            // 2. 补全已关闭 VM 的显示条目
            // 基于后台 PowerShell 获取的静态配置
            foreach (var kvp in _vmCoreCounts)
            {
                string vmName = kvp.Key;
                if (vmName == "Host") continue;

                // 如果该 VM 没有任何活跃的计数器，说明它已关闭
                if (!activeVmNames.Contains(vmName))
                {
                    int count = kvp.Value;
                    for (int i = 0; i < count; i++)
                    {
                        results.Add(new CpuCoreMetric { VmName = vmName, CoreId = i, IsRunning = false });
                    }
                }
            }

            return results;
        }

        // --- 后台维护循环 ---

        private async Task MaintainCountersLoop(CancellationToken token)
        {
            int loopCount = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 1. 快速更新：基于 PerformanceCounter 类别扫描实例 (每 2 秒一次)
                    UpdateCounterInstances();

                    // 2. 慢速更新：PowerShell 获取静态信息 (每 5 个循环，即约 10 秒一次)
                    if (loopCount % 5 == 0)
                    {
                        await UpdateVmInfoFromPowerShellAsync(token);
                    }

                    loopCount++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[CpuMonitor] Maintain loop error: {ex.Message}");
                }

                // 等待 2 秒再次检查实例变动
                try { await Task.Delay(2000, token); } catch (TaskCanceledException) { break; }
            }
        }

        private void UpdateCounterInstances()
        {
            var detectedInstances = new HashSet<string>();

            // A. 宿主机计数器 (Host)
            if (PerformanceCounterCategory.Exists("Hyper-V Hypervisor Logical Processor"))
            {
                var cat = new PerformanceCounterCategory("Hyper-V Hypervisor Logical Processor");
                // 过滤得到 "LP 0", "LP 1" 这样的实例
                var instances = cat.GetInstanceNames().Where(i => i.StartsWith("LP ") || i.Contains("LP "));

                foreach (var instance in instances)
                {
                    string coreIdStr = instance.Split(' ').Last(); // "LP 0" -> "0"
                    string key = $"Host_{coreIdStr}";
                    detectedInstances.Add(key);

                    // 如果不存在则添加
                    if (!_counters.ContainsKey(key))
                    {
                        try
                        {
                            var pc = new PerformanceCounter("Hyper-V Hypervisor Logical Processor", "% Total Run Time", instance);
                            pc.NextValue(); // 第一次读取作为基准，防止初次返回 0
                            _counters.TryAdd(key, pc);
                        }
                        catch { /* 忽略创建失败 */ }
                    }
                }
            }

            // B. 虚拟机计数器 (VM)
            if (PerformanceCounterCategory.Exists("Hyper-V Hypervisor Virtual Processor"))
            {
                var cat = new PerformanceCounterCategory("Hyper-V Hypervisor Virtual Processor");
                // 实例名通常是 "Ubuntu:Hv VP 0"
                var instances = cat.GetInstanceNames().Where(i => i.Contains(":"));

                foreach (var instance in instances)
                {
                    detectedInstances.Add(instance);

                    if (!_counters.ContainsKey(instance))
                    {
                        try
                        {
                            var pc = new PerformanceCounter("Hyper-V Hypervisor Virtual Processor", "% Total Run Time", instance);
                            pc.NextValue(); // 第一次读取作为基准
                            _counters.TryAdd(instance, pc);
                        }
                        catch { /* 忽略创建失败 */ }
                    }
                }
            }

            // C. 清理已经消失的计数器 (VM 关闭)
            // 找出所有在 _counters 中但不在本次 detectedInstances 中的 Key
            var deadKeys = _counters.Keys.Where(k => !detectedInstances.Contains(k)).ToList();
            foreach (var key in deadKeys)
            {
                if (_counters.TryRemove(key, out var pc))
                {
                    pc.Dispose();
                }
            }
        }

        private async Task UpdateVmInfoFromPowerShellAsync(CancellationToken token)
        {
            try
            {
                // 获取 VM 列表和核心数，用于显示"已关闭"的 VM
                // 使用 Utils.Run2 (异步)
                string script = "Get-VMProcessor * | Select-Object VMName, Count";
                var results = await Utils.Run2(script, token);

                if (results == null) return;

                var activeConfigNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var pso in results)
                {
                    if (pso == null) continue;
                    var vmName = pso.Properties["VMName"]?.Value as string;
                    var countVal = pso.Properties["Count"]?.Value;

                    if (!string.IsNullOrEmpty(vmName) && countVal != null)
                    {
                        try
                        {
                            int count = Convert.ToInt32(countVal);
                            _vmCoreCounts[vmName] = count;
                            activeConfigNames.Add(vmName);
                        }
                        catch { }
                    }
                }

                // 移除已被彻底删除的 VM 配置
                var keysToRemove = _vmCoreCounts.Keys
                    .Where(k => k != "Host" && !activeConfigNames.Contains(k))
                    .ToList();

                foreach (var key in keysToRemove)
                {
                    _vmCoreCounts.TryRemove(key, out _);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CpuMonitor] PowerShell update failed: {ex.Message}");
            }
        }
    }
}