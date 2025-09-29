// 文件路径: src/Services/MonitoringService.cs

using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class MonitoringService : IMonitoringService
    {
        public async Task<string> GetCpuNameAsync()
        {
            try
            {
                var result = await Utils.Run2("(Get-CimInstance -ClassName Win32_Processor).Name");
                return result?.FirstOrDefault()?.ToString().Trim() ?? "Unknown CPU";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取CPU名称失败: {ex.Message}");
                return "Unknown CPU";
            }
        }

        public async Task<HostCpuUsage> GetHostCpuUsageAsync()
        {
            var hostCpuUsage = new HostCpuUsage();
            string script = @"Get-Counter -Counter '\Processor(*)\% Processor Time' | Select-Object -ExpandProperty CounterSamples";

            try
            {
                var results = await Utils.Run2(script);
                var coreData = new Dictionary<int, double>();

                foreach (var counterSample in results)
                {
                    dynamic sample = counterSample;
                    string instanceName = sample.InstanceName?.ToString().Trim() ?? string.Empty;
                    double cookedValue = 0.0;
                    if (sample.CookedValue != null)
                    {
                        double.TryParse(sample.CookedValue.ToString(), out cookedValue);
                    }

                    if (instanceName == "_Total")
                    {
                        hostCpuUsage.TotalUsage = Math.Round(cookedValue, 2);
                    }
                    else if (int.TryParse(instanceName, out int coreId))
                    {
                        coreData[coreId] = Math.Round(cookedValue, 2);
                    }
                }

                hostCpuUsage.CoreUsages = coreData.OrderBy(kvp => kvp.Key)
                                                  .Select(kvp => kvp.Value)
                                                  .ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取宿主机CPU信息失败: {ex.Message}");
            }
            return hostCpuUsage;
        }

        public async Task<List<VmCpuUsage>> GetVmCpuUsagesAsync()
        {
            var vmUsages = new List<VmCpuUsage>();
            string script = @"
                $runningVMs = Get-VM | Where-Object { $_.State -eq 'Running' } | Select-Object -ExpandProperty VMName;
                if ($runningVMs) {
                    Get-Counter -Counter '\Hyper-V Hypervisor Virtual Processor(*)\% Guest Run Time' | 
                    Select-Object -ExpandProperty CounterSamples | 
                    Where-Object { $runningVMs -contains ($_.InstanceName -split ':')[0] }
                }";

            try
            {
                var results = await Utils.Run2(script);

                var groupedResults = results
                    .Select(psObject => (dynamic)psObject)
                    .GroupBy(sample => ((string)sample.InstanceName).Split(':')[0]);

                foreach (var group in groupedResults)
                {
                    var vmName = group.Key;
                    var vcpuUsages = group.Select(s => Math.Round((double)s.CookedValue, 2)).ToList();
                    var averageUsage = vcpuUsages.Any() ? Math.Round(vcpuUsages.Average(), 2) : 0;

                    vmUsages.Add(new VmCpuUsage
                    {
                        VmName = vmName,
                        AverageUsage = averageUsage,
                        VcpuUsages = vcpuUsages
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取虚拟机CPU信息失败: {ex.Message}");
            }
            return vmUsages.OrderBy(vm => vm.VmName).ToList();
        }
    }
}