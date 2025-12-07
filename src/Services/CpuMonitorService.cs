using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public class CachedCounter
    {
        public PerformanceCounter Counter { get; set; }
        public string VmName { get; set; }
        public int CoreId { get; set; }
    }

    public class CpuMonitorService
    {
        private readonly List<CachedCounter> _cachedCounters = new List<CachedCounter>();

        private const string CatVm = "Hyper-V Hypervisor Virtual Processor";
        private const string CounterVm = "% Total Run Time";

        private const string CatHost = "Processor";
        private const string CounterHost = "% Processor Time";

        private readonly Regex _vmRegex = new Regex(@"^(.+):Hv VP (\d+)$", RegexOptions.Compiled);
        private readonly Regex _hostRegex = new Regex(@"^(\d+)$", RegexOptions.Compiled);

        public CpuMonitorService()
        {
            RefreshCounters();
        }

        public void RefreshCounters()
        {
            _cachedCounters.Clear();
            try
            {
                // 1. 扫描虚拟机 vCPU
                if (PerformanceCounterCategory.Exists(CatVm))
                {
                    var cat = new PerformanceCounterCategory(CatVm);
                    foreach (var name in cat.GetInstanceNames())
                    {
                        if (name == "_Total") continue;
                        var match = _vmRegex.Match(name);
                        if (match.Success)
                        {
                            AddCounter(CatVm, CounterVm, name, match.Groups[1].Value, int.Parse(match.Groups[2].Value));
                        }
                    }
                }

                // 2. 扫描宿主机物理核心
                if (PerformanceCounterCategory.Exists(CatHost))
                {
                    var cat = new PerformanceCounterCategory(CatHost);
                    foreach (var name in cat.GetInstanceNames())
                    {
                        if (name == "_Total") continue;
                        var match = _hostRegex.Match(name);
                        if (match.Success)
                        {
                            AddCounter(CatHost, CounterHost, name, "Host", int.Parse(match.Groups[1].Value));
                        }
                    }
                }

                // 【核心修复】对缓存列表进行一次性排序
                // 规则：先按 VM 名字母序，再按 Core ID 数字序 (0, 1, 2... 10, 11...)
                _cachedCounters.Sort((a, b) =>
                {
                    int vmCompare = string.Compare(a.VmName, b.VmName, StringComparison.OrdinalIgnoreCase);
                    if (vmCompare != 0) return vmCompare;

                    return a.CoreId.CompareTo(b.CoreId);
                });
            }
            catch { }
        }

        private void AddCounter(string category, string counterName, string instance, string vmName, int coreId)
        {
            try
            {
                var counter = new PerformanceCounter(category, counterName, instance);
                try { counter.NextValue(); } catch { } // 预热

                _cachedCounters.Add(new CachedCounter
                {
                    Counter = counter,
                    VmName = vmName,
                    CoreId = coreId
                });
            }
            catch { }
        }

        public List<CpuCoreMetric> GetCpuUsage()
        {
            var results = new List<CpuCoreMetric>(_cachedCounters.Count);

            if (_cachedCounters.Count == 0)
            {
                RefreshCounters();
                if (_cachedCounters.Count == 0) return results;
            }

            // 因为 _cachedCounters 已经排好序了，这里顺序读取即可
            foreach (var item in _cachedCounters)
            {
                try
                {
                    float value = item.Counter.NextValue();

                    results.Add(new CpuCoreMetric
                    {
                        VmName = item.VmName,
                        CoreId = item.CoreId,
                        Usage = (float)Math.Round(value, 1)
                    });
                }
                catch { }
            }

            return results;
        }
    }
}