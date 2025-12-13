using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public enum CoreType
    {
        Performance, Efficient, Unknown
    }

    public class CpuMonitorService : IDisposable
    {
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

        #region P/Invoke for Core Type Detection
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

        private readonly List<PerformanceCounter> _hostTotalRunTimeCounters = new List<PerformanceCounter>();
        private readonly List<PerformanceCounter> _vmCpuCounters = new List<PerformanceCounter>();
        private readonly Dictionary<string, int> _vmCoreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastSyncTime = DateTime.MinValue;
        private readonly TimeSpan _syncInterval = TimeSpan.FromSeconds(5);

        public CpuMonitorService()
        {
            SyncCounters();
        }

        public void Dispose()
        {
            _hostTotalRunTimeCounters.ForEach(c => c?.Dispose());
            _vmCpuCounters.ForEach(c => c?.Dispose());
        }

        public void SyncCounters()
        {
            try
            {
                UpdateVmListFromPowerShell();
                Dispose();
                _hostTotalRunTimeCounters.Clear();
                _vmCpuCounters.Clear();

                if (PerformanceCounterCategory.Exists("Hyper-V Hypervisor Logical Processor"))
                {
                    var cat = new PerformanceCounterCategory("Hyper-V Hypervisor Logical Processor");
                    foreach (var instance in cat.GetInstanceNames().Where(i => i.Contains("LP ")))
                    {
                        _hostTotalRunTimeCounters.Add(new PerformanceCounter("Hyper-V Hypervisor Logical Processor", "% Total Run Time", instance));
                    }
                }

                if (PerformanceCounterCategory.Exists("Hyper-V Hypervisor Virtual Processor"))
                {
                    var cat = new PerformanceCounterCategory("Hyper-V Hypervisor Virtual Processor");
                    foreach (var instance in cat.GetInstanceNames().Where(i => i.Contains(":")))
                    {
                        _vmCpuCounters.Add(new PerformanceCounter("Hyper-V Hypervisor Virtual Processor", "% Total Run Time", instance));
                    }
                }

                _hostTotalRunTimeCounters.ForEach(c => c.NextValue());
                _vmCpuCounters.ForEach(c => c.NextValue());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CpuMonitorService] SyncCounters failed: {ex.Message}");
            }
        }

        public List<CpuCoreMetric> GetCpuUsage()
        {
            if ((DateTime.Now - _lastSyncTime) > _syncInterval)
            {
                SyncCounters();
                _lastSyncTime = DateTime.Now;
            }

            var results = new List<CpuCoreMetric>();
            var runningVmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 步骤 1: 获取 Host 的真实物理核心负载
            foreach (var counter in _hostTotalRunTimeCounters)
            {
                try
                {
                    if (int.TryParse(counter.InstanceName.Split(' ').Last(), out int coreId))
                    {
                        results.Add(new CpuCoreMetric { VmName = "Host", CoreId = coreId, Usage = counter.NextValue(), IsRunning = true });
                    }
                }
                catch { /* Counter might become invalid */ }
            }

            // 步骤 2: 获取每个运行中VM的 vCPU 占用率
            var vmRegex = new Regex(@"^(.+?):Hv VP (\d+)$");
            foreach (var counter in _vmCpuCounters)
            {
                try
                {
                    var match = vmRegex.Match(counter.InstanceName);
                    if (match.Success)
                    {
                        string vmName = match.Groups[1].Value;
                        int vCpuId = int.Parse(match.Groups[2].Value);
                        runningVmNames.Add(vmName);

                        results.Add(new CpuCoreMetric
                        {
                            VmName = vmName,
                            CoreId = vCpuId,
                            Usage = counter.NextValue(),
                            IsRunning = true
                        });
                    }
                }
                catch { /* Counter might become invalid */ }
            }

            // 步骤 3: 为已关闭的VM添加占位条目
            foreach (var kvp in _vmCoreCounts)
            {
                if (kvp.Key != "Host" && !runningVmNames.Contains(kvp.Key))
                {
                    // 为已关闭的VM的每个vCPU都生成一个条目
                    for (int i = 0; i < kvp.Value; i++)
                    {
                        results.Add(new CpuCoreMetric { VmName = kvp.Key, CoreId = i, IsRunning = false });
                    }
                }
            }

            return results;
        }

        private void UpdateVmListFromPowerShell()
        {
            if (!_vmCoreCounts.ContainsKey("Host")) _vmCoreCounts["Host"] = Environment.ProcessorCount;
            try
            {
                string script = "Get-VMProcessor * | Select-Object VMName, Count";
                var results = Utils.Run(script);
                var vmsFromPs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (results != null)
                {
                    foreach (var pso in results)
                    {
                        if (pso?.Properties["VMName"]?.Value is string vmName && pso.Properties["Count"]?.Value != null)
                        {
                            try
                            {
                                int coreCount = Convert.ToInt32(pso.Properties["Count"].Value);
                                _vmCoreCounts[vmName] = coreCount;
                                vmsFromPs.Add(vmName);
                            }
                            catch (FormatException) { }
                        }
                    }
                }

                var vmsToRemove = _vmCoreCounts.Keys.Where(k => k != "Host" && !vmsFromPs.Contains(k)).ToList();
                foreach (var oldVm in vmsToRemove)
                {
                    _vmCoreCounts.Remove(oldVm);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update VM list from PowerShell: {ex.Message}");
            }
        }
    }
}