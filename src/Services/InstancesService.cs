using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class InstancesService
    {
        public Task<List<VMInfo>> GetVmListAsync()
        {
            return Task.Run(() =>
            {
                var vmList = new List<VMInfo>();
                string script = @"
                    Get-VM | ForEach-Object {
                        $vhd = Get-VMHardDiskDrive -VMName $_.Name | Select-Object -First 1
                        $vhdSize = 0
                        if ($vhd -and $vhd.Path) {
                            try {
                                $vhdInfo = Get-VHD -Path $vhd.Path -ErrorAction SilentlyContinue
                                if ($vhdInfo) { $vhdSize = $vhdInfo.Size }
                            } catch {}
                        }
                        # 修改：直接获取 Ticks
                        $upTicks = $_.Uptime.Ticks
                        
                        [PSCustomObject]@{
                            Name = $_.Name
                            State = $_.State
                            Notes = $_.Notes
                            Generation = $_.Generation
                            CPU = $_.ProcessorCount
                            RAM = [Math]::Round($_.MemoryStartup / 1GB, 1)
                            Disk = [Math]::Round($vhdSize / 1GB, 0)
                            UptimeTicks = $upTicks
                        }
                    }";

                var results = Utils.Run(script);
                if (results != null)
                {
                    foreach (var vm in results)
                    {
                        string name = vm.Members["Name"]?.Value?.ToString() ?? "Unknown";
                        string stateRaw = vm.Members["State"]?.Value?.ToString() ?? "Off";
                        string notes = vm.Members["Notes"]?.Value?.ToString() ?? "";
                        int gen = int.Parse(vm.Members["Generation"]?.Value?.ToString() ?? "0");
                        int cpu = int.Parse(vm.Members["CPU"]?.Value?.ToString() ?? "0");
                        double ram = double.Parse(vm.Members["RAM"]?.Value?.ToString() ?? "0");
                        double diskSize = double.Parse(vm.Members["Disk"]?.Value?.ToString() ?? "0");

                        // 修改：解析 Ticks
                        long ticks = 0;
                        if (vm.Members["UptimeTicks"]?.Value != null)
                        {
                            long.TryParse(vm.Members["UptimeTicks"].Value.ToString(), out ticks);
                        }
                        TimeSpan uptime = TimeSpan.FromTicks(ticks);

                        var info = new VMInfo(name, "", "", "", null, gen, stateRaw == "Running", notes, MapStateToText(stateRaw), cpu, ram, $"{diskSize}G", uptime);
                        vmList.Add(info);
                    }
                }
                return vmList;
            });
        }

        // 修改：返回值元组改为 (string State, TimeSpan Uptime)
        public Task<(string State, TimeSpan Uptime)> GetVmDynamicInfoAsync(string vmName)
        {
            return Task.Run(() =>
            {
                // 获取 Uptime.Ticks
                var result = Utils.Run($"(Get-VM -Name '{vmName}') | Select-Object State, @{{N='UpTicks';E={{$_.Uptime.Ticks}}}}");
                if (result != null && result.Count > 0)
                {
                    string state = MapStateToText(result[0].Members["State"]?.Value?.ToString());

                    long ticks = 0;
                    if (result[0].Members["UpTicks"]?.Value != null)
                    {
                        long.TryParse(result[0].Members["UpTicks"].Value.ToString(), out ticks);
                    }
                    return (state, TimeSpan.FromTicks(ticks));
                }
                return ("未知", TimeSpan.Zero);
            });
        }

        private static string MapStateToText(string state) => state switch
        {
            "Running" => "运行中",
            "Off" => "已关机",
            "Paused" => "已暂停",
            "ShuttingDown" => "正在关闭",
            "Stopping" => "正在关闭",
            "Starting" => "正在启动",
            "Saving" => "正在保存",
            "Saved" => "已保存",
            _ => state
        };

        public Task ExecuteControlActionAsync(string vmName, string action)
        {
            return Task.Run(() =>
            {
                string cmd = action switch
                {
                    "Start" => $"Start-VM -Name '{vmName}'",
                    "Stop" => $"Stop-VM -Name '{vmName}'",
                    "TurnOff" => $"Stop-VM -Name '{vmName}' -TurnOff",
                    "Suspend" => $"Suspend-VM -Name '{vmName}'",
                    "Save" => $"Save-VM -Name '{vmName}'",
                    "Restart" => $"Restart-VM -Name '{vmName}' -Force",
                    _ => null
                };
                if (!string.IsNullOrEmpty(cmd)) Utils.Run(cmd);
            });
        }

        public Task UpdateOsTypeNoteAsync(string vmName, string osType)
        {
            return Task.Run(() =>
            {
                string script = $@"
                    $vm = Get-VM -Name '{vmName}';
                    $cleaned = $vm.Notes -replace '\[OSType:[^\]]+\]', '';
                    Set-VM -VM $vm -Notes ($cleaned.Trim() + ' [OSType:{osType.ToLower()}]').Trim()";
                Utils.Run(script);
            });
        }
    }
}