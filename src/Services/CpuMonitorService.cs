using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
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

        private readonly ConcurrentDictionary<string, PerformanceCounter> _counters = new ConcurrentDictionary<string, PerformanceCounter>();
        private readonly ConcurrentDictionary<string, int> _vmCoreCounts = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public CpuMonitorService()
        {
            _vmCoreCounts["Host"] = Environment.ProcessorCount;
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

        private async Task MaintainCountersLoop(CancellationToken token)
        {
            int loopCount = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    UpdateCounterInstances();

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

                try { await Task.Delay(2000, token); } catch (TaskCanceledException) { break; }
            }
        }

        private void UpdateCounterInstances()
        {
            var detectedInstances = new HashSet<string>();

            if (PerformanceCounterCategory.Exists("Hyper-V Hypervisor Logical Processor"))
            {
                var cat = new PerformanceCounterCategory("Hyper-V Hypervisor Logical Processor");
                var instances = cat.GetInstanceNames().Where(i => i.StartsWith("LP ") || i.Contains("LP "));

                foreach (var instance in instances)
                {
                    string coreIdStr = instance.Split(' ').Last();
                    string key = $"Host_{coreIdStr}";
                    detectedInstances.Add(key);

                    if (!_counters.ContainsKey(key))
                    {
                        try
                        {
                            var pc = new PerformanceCounter("Hyper-V Hypervisor Logical Processor", "% Total Run Time", instance);
                            pc.NextValue();
                            _counters.TryAdd(key, pc);
                        }
                        catch { }
                    }
                }
            }

            if (PerformanceCounterCategory.Exists("Hyper-V Hypervisor Virtual Processor"))
            {
                var cat = new PerformanceCounterCategory("Hyper-V Hypervisor Virtual Processor");
                var instances = cat.GetInstanceNames().Where(i => i.Contains(":"));

                foreach (var instance in instances)
                {
                    detectedInstances.Add(instance);

                    if (!_counters.ContainsKey(instance))
                    {
                        try
                        {
                            var pc = new PerformanceCounter("Hyper-V Hypervisor Virtual Processor", "% Total Run Time", instance);
                            pc.NextValue();
                            _counters.TryAdd(instance, pc);
                        }
                        catch { }
                    }
                }
            }

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
        public List<CpuCoreMetric> GetCpuUsage()
        {
            var results = new List<CpuCoreMetric>();
            var activeVmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                        int colonIndex = instanceName.LastIndexOf(':');
                        if (colonIndex > 0)
                        {
                            string vmName = instanceName.Substring(0, colonIndex);
                            string suffix = instanceName.Substring(colonIndex + 1);

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
                }
            }
            foreach (var kvp in _vmCoreCounts)
            {
                string vmName = kvp.Key;
                if (vmName == "Host") continue;

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
    }
}