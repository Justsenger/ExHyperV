using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices; // <--- 关键引用：用于调用 Windows API
using System.Text.RegularExpressions;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public enum CoreType
    {
        Performance, Efficient, Unknown
    }

    public class CachedCounter
    {
        public PerformanceCounter Counter { get; set; }
        public string VmName { get; set; }
        public int CoreId { get; set; }
        public string InstanceKey { get; set; }
        public bool IsValid { get; set; } = true;
    }

    public class CpuMonitorService
    {
        // ==========================================
        // 区域：核心类型检测 (API 级精准识别)
        // ==========================================
        #region Core Type Detection (Native API)

        // 缓存：逻辑处理器ID -> 核心类型
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

        /// <summary>
        /// 使用 Windows API (GetLogicalProcessorInformationEx) 获取每个核心的能效等级
        /// </summary>
        private static void InitializeCoreTypes()
        {
            try
            {
                // 1. 获取缓冲区大小
                uint returnLength = 0;
                GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref returnLength);

                if (returnLength == 0) return;

                // 2. 分配内存并读取数据
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

                        // 3. 遍历结构体链表
                        while (offset < returnLength)
                        {
                            var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(ptr);

                            // 只需要处理 Core 类型
                            if (info.Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                            {
                                byte efficiencyClass = info.Processor.EfficiencyClass;

                                // 记录最大和最小能效等级，用于判断是否是混合架构
                                if (efficiencyClass > maxClass) maxClass = efficiencyClass;
                                if (efficiencyClass < minClass) minClass = efficiencyClass;

                                // 解析 GroupMask 找出对应的逻辑处理器 ID
                                // 通常消费级 CPU 只有一个 Group (Group 0)
                                for (int i = 0; i < info.Processor.GroupCount; i++)
                                {
                                    var groupInfo = info.Processor.GroupMask[i];
                                    ulong mask = groupInfo.Mask.ToUInt64();

                                    // 遍历 64 位掩码
                                    for (int bit = 0; bit < 64; bit++)
                                    {
                                        if ((mask & (1UL << bit)) != 0)
                                        {
                                            // 这是一个逻辑处理器
                                            // 真正的逻辑ID可能需要结合 GroupID，但简单起见假设 Group 0
                                            // 对于绝大多数 PC，这足够了。
                                            tempInfo.Add((bit, efficiencyClass));
                                        }
                                    }
                                }
                            }

                            // 移动指针
                            offset += info.Size;
                            ptr = IntPtr.Add(ptr, (int)info.Size);
                        }

                        // 4. 判断是否混合架构
                        // 只有当存在不同的能效等级时，才认为是混合架构
                        // Arrow Lake: P核 Class 1, E核 Class 0 -> 混合
                        // 5950X: 全是 Class 0 (或相同) -> 非混合
                        if (maxClass > minClass)
                        {
                            _isHybrid = true;
                            foreach (var item in tempInfo)
                            {
                                // 最高等级的是 P 核，其余算 E 核
                                // (实际上可能有中间层，但目前Intel只有两层)
                                _coreTypeCache[item.Id] = (item.Class == maxClass) ? CoreType.Performance : CoreType.Efficient;
                            }
                        }
                        else
                        {
                            _isHybrid = false;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                _isHybrid = false;
            }
        }

        // --- P/Invoke 定义 ---
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType,
            IntPtr Buffer,
            ref uint ReturnedLength);

        private enum LOGICAL_PROCESSOR_RELATIONSHIP
        {
            RelationProcessorCore = 0,
            RelationNumaNode = 1,
            RelationCache = 2,
            RelationProcessorPackage = 3,
            RelationGroup = 4,
            RelationAll = 0xffff
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        {
            public LOGICAL_PROCESSOR_RELATIONSHIP Relationship;
            public uint Size;
            public PROCESSOR_RELATIONSHIP Processor; // 这是一个联合体，这里简化处理
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESSOR_RELATIONSHIP
        {
            public byte Flags;
            public byte EfficiencyClass; // <--- 我们需要这个！
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Reserved;
            public ushort GroupCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)] // 实际是变长的，但我们只读第一个通常够用
            public GROUP_AFFINITY[] GroupMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GROUP_AFFINITY
        {
            public UIntPtr Mask; // KAFFINITY
            public ushort Group;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public ushort[] Reserved;
        }

        #endregion

        // ==========================================
        // 区域：核心监控逻辑 (以下逻辑保持不变)
        // ==========================================

        private readonly List<CachedCounter> _cachedCounters = new List<CachedCounter>();
        private readonly Dictionary<string, int> _vmCoreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastSyncTime = DateTime.MinValue;
        private readonly TimeSpan _syncInterval = TimeSpan.FromSeconds(5);

        private const string CatVm = "Hyper-V Hypervisor Virtual Processor";
        private const string CounterVm = "% Total Run Time";
        private const string CatHost = "Processor";
        private const string CounterHost = "% Processor Time";

        private readonly Regex _vmRegex = new Regex(@"^(.+):Hv VP (\d+)$", RegexOptions.Compiled);
        private readonly Regex _hostRegex = new Regex(@"^(\d+)$", RegexOptions.Compiled);

        public CpuMonitorService()
        {
            SyncCounters();
        }

        public void SyncCounters()
        {
            try
            {
                UpdateVmListFromPowerShell();
                UpdateRunningCounters();

                _cachedCounters.Sort((a, b) =>
                {
                    int vmCompare = string.Compare(a.VmName, b.VmName, StringComparison.OrdinalIgnoreCase);
                    if (vmCompare != 0) return vmCompare;
                    return a.CoreId.CompareTo(b.CoreId);
                });
            }
            catch { }
        }

        private void UpdateVmListFromPowerShell()
        {
            if (!_vmCoreCounts.ContainsKey("Host")) _vmCoreCounts["Host"] = 0;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -WindowStyle Hidden -Command \"Get-VMProcessor * | Select-Object VMName, Count | ConvertTo-Csv -NoTypeInformation\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    while (!process.StandardOutput.EndOfStream)
                    {
                        string line = process.StandardOutput.ReadLine();
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("\"VMName\"")) continue;

                        var parts = line.Split(',');
                        if (parts.Length >= 2)
                        {
                            string name = parts[0].Trim('"');
                            if (int.TryParse(parts[1].Trim('"'), out int count))
                            {
                                _vmCoreCounts[name] = count;
                            }
                        }
                    }
                    process.WaitForExit(1000);
                }
            }
            catch { }
        }

        private void UpdateRunningCounters()
        {
            if (PerformanceCounterCategory.Exists(CatVm))
            {
                var cat = new PerformanceCounterCategory(CatVm);
                foreach (var name in cat.GetInstanceNames())
                {
                    if (name == "_Total") continue;
                    var match = _vmRegex.Match(name);
                    if (match.Success)
                    {
                        string vmName = match.Groups[1].Value;
                        int coreId = int.Parse(match.Groups[2].Value);
                        if (!_vmCoreCounts.ContainsKey(vmName)) _vmCoreCounts[vmName] = 1;
                        UpdateOrAddCounter($"{CatVm}|{name}", CatVm, CounterVm, name, vmName, coreId);
                    }
                }
            }

            if (PerformanceCounterCategory.Exists(CatHost))
            {
                var cat = new PerformanceCounterCategory(CatHost);
                foreach (var name in cat.GetInstanceNames())
                {
                    if (name == "_Total") continue;
                    var match = _hostRegex.Match(name);
                    if (match.Success)
                    {
                        int coreId = int.Parse(match.Groups[1].Value);
                        UpdateOrAddCounter($"{CatHost}|{name}", CatHost, CounterHost, name, "Host", coreId);
                    }
                }
            }
        }

        private void UpdateOrAddCounter(string key, string category, string counterName, string instance, string vmName, int coreId)
        {
            var existing = _cachedCounters.FirstOrDefault(x => x.InstanceKey == key);
            if (existing != null)
            {
                if (!existing.IsValid)
                {
                    try { existing.Counter = new PerformanceCounter(category, counterName, instance); existing.Counter.NextValue(); existing.IsValid = true; } catch { }
                }
                return;
            }
            try
            {
                var counter = new PerformanceCounter(category, counterName, instance); counter.NextValue();
                _cachedCounters.Add(new CachedCounter { Counter = counter, VmName = vmName, CoreId = coreId, InstanceKey = key, IsValid = true });
            }
            catch { }
        }

        public List<CpuCoreMetric> GetCpuUsage()
        {
            if ((DateTime.Now - _lastSyncTime) > _syncInterval) { SyncCounters(); _lastSyncTime = DateTime.Now; }

            var results = new List<CpuCoreMetric>();
            var runningVmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _cachedCounters)
            {
                float value = 0;
                bool isRunning = false;
                if (item.IsValid) { try { value = item.Counter.NextValue(); isRunning = true; } catch { item.IsValid = false; } }
                if (isRunning)
                {
                    runningVmNames.Add(item.VmName);
                    results.Add(new CpuCoreMetric { VmName = item.VmName, CoreId = item.CoreId, Usage = (float)Math.Round(value, 1), IsRunning = true });
                }
            }

            foreach (var kvp in _vmCoreCounts)
            {
                string vmName = kvp.Key;
                if (vmName == "Host") continue;

                if (!runningVmNames.Contains(vmName))
                {
                    int count = kvp.Value;
                    if (count <= 0) count = 1;

                    for (int i = 0; i < count; i++)
                    {
                        results.Add(new CpuCoreMetric { VmName = vmName, CoreId = i, Usage = 0, IsRunning = false });
                    }
                }
            }
            return results;
        }
    }
}