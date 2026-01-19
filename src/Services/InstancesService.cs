using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class InstancesService
    {
        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            var vmList = new List<VmInstanceInfo>();
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
                    [PSCustomObject]@{
                        Id = $_.Id
                        Name = $_.Name
                        State = $_.State
                        Notes = $_.Notes
                        Generation = $_.Generation
                        CPU = $_.ProcessorCount
                        RAM = [Math]::Round($_.MemoryStartup / 1GB, 1)
                        Disk = [Math]::Round($vhdSize / 1GB, 0)
                        UptimeTicks = $_.Uptime.Ticks
                    }
                }";

            try
            {
                var results = await Task.Run(() => Utils.Run(script));
                if (results != null)
                {
                    foreach (var vm in results)
                    {
                        if (vm == null || vm.Members == null) continue;
                        Guid id = Guid.TryParse(GetProp(vm, "Id"), out var g) ? g : Guid.Empty;
                        string name = GetProp(vm, "Name") ?? "Unknown";
                        long.TryParse(GetProp(vm, "UptimeTicks"), out long ticks);
                        var info = new VmInstanceInfo(id, name)
                        {
                            Generation = int.Parse(GetProp(vm, "Generation") ?? "0"),
                            Notes = GetProp(vm, "Notes") ?? "",
                            State = MapStateToText(GetProp(vm, "State")),
                            CpuCount = int.Parse(GetProp(vm, "CPU") ?? "0"),
                            MemoryGb = double.Parse(GetProp(vm, "RAM") ?? "0"),
                            DiskSize = GetProp(vm, "Disk") + "G",
                            RawUptime = TimeSpan.FromTicks(ticks)
                        };
                        vmList.Add(info);
                    }
                }
            }
            catch { }
            return vmList;
        }

        public async Task<string> GetVmSingleStateAsync(string vmName)
        {
            try
            {
                var res = await Task.Run(() => Utils.Run($"Get-VM -Name '{vmName.Replace("'", "''")}' | Select-Object -ExpandProperty State"));
                return MapStateToText(res?.FirstOrDefault()?.ToString());
            }
            catch { return "已关机"; }
        }

        public async Task ExecuteControlActionAsync(string vmName, string action)
        {
            var safeName = vmName.Replace("'", "''");
            string cmd = action switch
            {
                "Start" => $"Start-VM -Name '{safeName}'",
                "Stop" => $"Stop-VM -Name '{safeName}' -ErrorAction Stop",
                "TurnOff" => $"Stop-VM -Name '{safeName}' -TurnOff -Force",
                "Suspend" => $"Suspend-VM -Name '{safeName}'",
                "Save" => $"Save-VM -Name '{safeName}'",
                "Restart" => $"Restart-VM -Name '{safeName}' -Force -Confirm:$false",
                _ => ""
            };
            if (!string.IsNullOrEmpty(cmd)) await Task.Run(() => Utils.Run(cmd));
        }

        private string GetProp(System.Management.Automation.PSObject obj, string propName) => obj.Members[propName]?.Value?.ToString();

        private static string MapStateToText(string state) => state switch
        {
            "Running" => "运行中",
            "Off" => "已关机",
            "Paused" => "已暂停",
            "ShuttingDown" or "Stopping" => "正在关闭",
            "Starting" => "正在启动",
            "Saving" => "正在保存",
            "Saved" => "已保存",
            _ => state ?? "已关机"
        };
    }
}