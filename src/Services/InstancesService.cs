using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class InstancesService
    {
        // Hyper-V WMI 命名空间
        private const string ScopePath = @"\\.\root\virtualization\v2";

        /// <summary>
        /// 获取虚拟机列表：使用 WMI 极速模式
        /// </summary>
        public async Task<List<VmInstanceInfo>> GetVmListAsync()
        {
            return await Task.Run(() =>
            {
                var vmList = new List<VmInstanceInfo>();
                try
                {
                    var scope = new ManagementScope(ScopePath);
                    scope.Connect();

                    // 查询摘要信息 (速度极快)
                    var query = new ObjectQuery("SELECT Name, ElementName, EnabledState, UpTime, NumberOfProcessors, MemoryUsage, Notes FROM Msvm_SummaryInformation");
                    using var searcher = new ManagementObjectSearcher(scope, query);
                    using var collection = searcher.Get();

                    foreach (ManagementObject obj in collection)
                    {
                        string idStr = obj["Name"]?.ToString();
                        Guid id = Guid.TryParse(idStr, out var g) ? g : Guid.Empty;
                        string name = obj["ElementName"]?.ToString() ?? "Unknown";

                        ushort stateCode = obj["EnabledState"] != null ? (ushort)obj["EnabledState"] : (ushort)0;
                        string stateText = MapStateCodeToText(stateCode);

                        int cpu = obj["NumberOfProcessors"] != null ? Convert.ToInt32(obj["NumberOfProcessors"]) : 1;

                        double ram = 0;
                        if (obj["MemoryUsage"] != null)
                        {
                            double mb = Convert.ToDouble(obj["MemoryUsage"]);
                            ram = Math.Round(mb / 1024.0, 1);
                        }

                        ulong uptimeMs = obj["UpTime"] != null ? (ulong)obj["UpTime"] : 0;
                        TimeSpan uptime = TimeSpan.FromMilliseconds(uptimeMs);

                        string notes = "";
                        if (obj["Notes"] is string[] notesArr && notesArr.Length > 0)
                            notes = string.Join(" ", notesArr);
                        else if (obj["Notes"] is string s)
                            notes = s;

                        string osType = notes.Contains("linux", StringComparison.OrdinalIgnoreCase) ? "linux" : "windows";
                        string disk = "N/A";

                        var info = new VmInstanceInfo(id, name, stateText, osType, cpu, ram, disk, uptime)
                        {
                            Notes = notes,
                            Generation = 0
                        };

                        vmList.Add(info);
                    }
                }
                catch
                {
                    // 忽略 WMI 错误，返回空列表或处理异常
                }

                return vmList.OrderByDescending(x => x.State == "运行中" || x.State == "Running").ThenBy(x => x.Name).ToList();
            });
        }

        /// <summary>
        /// 执行操作：混合模式
        /// 开机/拔电源 -> WMI (快)
        /// 其他 -> PowerShell (稳)
        /// </summary>
        public async Task ExecuteControlActionAsync(string vmName, string action)
        {
            // 1. 复杂操作：走 PowerShell
            // -------------------------------------------------------------
            // Stop (软关机): 需要与 Guest OS 交互
            // Restart (重启): 需要处理 Soft/Hard 逻辑，无 OS 时必须用 -Force 强制重置
            // Save (保存): 涉及磁盘 I/O，WMI 会返回 Job 需要等待
            // Suspend (暂停): PowerShell 更稳妥
            if (action == "Stop" || action == "Restart" || action == "Save" || action == "Suspend")
            {
                string cmd = action switch
                {
                    // 关键：Restart 使用 -Force 确保无 OS 时也能硬重启
                    "Restart" => $"Restart-VM -Name '{vmName.Replace("'", "''")}' -Force -Confirm:$false",
                    "Stop" => $"Stop-VM -Name '{vmName.Replace("'", "''")}' -ErrorAction Stop",
                    "Save" => $"Save-VM -Name '{vmName.Replace("'", "''")}'",
                    "Suspend" => $"Suspend-VM -Name '{vmName.Replace("'", "''")}'",
                    _ => ""
                };

                if (!string.IsNullOrEmpty(cmd))
                {
                    await Task.Run(() => Utils.Run(cmd));
                }
                return;
            }

            // 2. 瞬时操作：走 WMI
            // -------------------------------------------------------------
            // Start (开机)
            // TurnOff (强制断电/拔电源)
            await Task.Run(() =>
            {
                try
                {
                    var scope = new ManagementScope(ScopePath);
                    scope.Connect();

                    string queryStr = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "\\'")}'";
                    using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(queryStr));
                    using var instances = searcher.Get();

                    foreach (ManagementObject vm in instances)
                    {
                        int targetState = -1;

                        switch (action)
                        {
                            case "Start": targetState = 2; break;    // Enabled
                            case "TurnOff": targetState = 3; break;  // Disabled (Hard Power Off)
                        }

                        if (targetState != -1)
                        {
                            var inParams = vm.GetMethodParameters("RequestStateChange");
                            inParams["RequestedState"] = targetState;
                            vm.InvokeMethod("RequestStateChange", inParams, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"WMI Action Failed: {ex.Message}");
                }
            });
        }

        private static string MapStateCodeToText(ushort code)
        {
            return code switch
            {
                2 => "运行中",       // Enabled
                3 => "已关机",       // Disabled

                // --- 关键修改：Code 6 (Enabled but Offline) 通常代表 Saved 状态 ---
                6 => "已保存",

                9 => "已暂停",       // Quiesce
                32768 => "已暂停",   // Paused
                32769 => "已保存",   // Saved (Suspended)

                32770 => "正在启动",
                32771 => "正在快照",
                32773 => "正在保存",
                32774 => "正在停止",
                32776 => "正在暂停",
                32777 => "正在恢复",
                _ => $"未知状态({code})"
            };
        }
    }
}