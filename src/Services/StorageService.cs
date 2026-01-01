using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Tools;
using Microsoft.Management.Infrastructure;

namespace ExHyperV.Services
{
    public interface IStorageService
    {
        Task<List<VmStorageControllerInfo>> GetVmStorageInfoAsync(string vmName);
        Task<List<HostDiskInfo>> GetHostDisksAsync();
        Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline);
        Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256, string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default", string blockSize = "默认");
        Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, UiDriveModel drive);
        Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, string controllerType, int controllerNumber, int controllerLocation, string newIsoPath);
        Task<double> GetVhdSizeGbAsync(string path);
        Task<(string ControllerType, int ControllerNumber, int Location)> GetNextAvailableSlotAsync(string vmName, string driveType);
    }

    public class StorageService : IStorageService
    {
        public async Task<List<VmStorageControllerInfo>> GetVmStorageInfoAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                Debug.WriteLine($"[ExHyperV] === 开始查询虚拟机: {vmName} ===");
                var resultList = new List<VmStorageControllerInfo>();
                string namespaceName = @"root\virtualization\v2";

                try
                {
                    using (var session = CimSession.Create(null))
                    {
                        // 1. 获取虚拟机
                        var vmQuery = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'";
                        var vm = session.QueryInstances(namespaceName, "WQL", vmQuery).FirstOrDefault();
                        if (vm == null) return resultList;

                        // 2. 获取 Settings
                        var settings = session.EnumerateAssociatedInstances(
                            namespaceName, vm,
                            "Msvm_SettingsDefineState",
                            "Msvm_VirtualSystemSettingData",
                            "ManagedElement",
                            "SettingData").FirstOrDefault();

                        if (settings == null) return resultList;

                        // 3. 获取所有资源 (合并 RASD 和 SASD)
                        var rasd = session.EnumerateAssociatedInstances(
                            namespaceName, settings,
                            "Msvm_VirtualSystemSettingDataComponent",
                            "Msvm_ResourceAllocationSettingData",
                            "GroupComponent",
                            "PartComponent").ToList();

                        var sasd = session.EnumerateAssociatedInstances(
                            namespaceName, settings,
                            "Msvm_VirtualSystemSettingDataComponent",
                            "Msvm_StorageAllocationSettingData",
                            "GroupComponent",
                            "PartComponent").ToList();

                        var allResources = new List<CimInstance>(rasd.Count + sasd.Count);
                        allResources.AddRange(rasd);
                        allResources.AddRange(sasd);

                        // 4. 提取控制器 (5=IDE, 6=SCSI)
                        var controllers = allResources
                            .Where(r => {
                                var rt = Convert.ToInt32(r.CimInstanceProperties["ResourceType"]?.Value);
                                return rt == 5 || rt == 6;
                            })
                            .OrderBy(c => c.CimInstanceProperties["ResourceType"]?.Value)
                            .ToList();

                        // 5. 建立 [InstanceID -> List<Resource>] 的子设备映射表 (预处理，提升性能)
                        // 这一步完全模拟 PowerShell 脚本中的 childrenMap 逻辑
                        var childrenMap = new Dictionary<string, List<CimInstance>>();

                        // 正则表达式：用于从 Parent 属性中提取 InstanceID
                        // 匹配模式：InstanceID="任意字符"
                        var regex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);

                        foreach (var res in allResources)
                        {
                            var parentPath = res.CimInstanceProperties["Parent"]?.Value?.ToString();
                            if (string.IsNullOrEmpty(parentPath)) continue;

                            var match = regex.Match(parentPath);
                            if (match.Success)
                            {
                                // 关键修复：WMI 路径里的 \\ 必须转回 \ 才能跟控制器的 ID 匹配
                                string parentId = match.Groups[1].Value.Replace("\\\\", "\\");

                                if (!childrenMap.ContainsKey(parentId))
                                {
                                    childrenMap[parentId] = new List<CimInstance>();
                                }
                                childrenMap[parentId].Add(res);
                            }
                        }

                        // 6. 遍历控制器并关联设备
                        foreach (var ctrl in controllers)
                        {
                            string ctrlId = ctrl.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                            int ctrlTypeVal = Convert.ToInt32(ctrl.CimInstanceProperties["ResourceType"]?.Value);
                            string ctrlType = ctrlTypeVal == 6 ? "SCSI" : "IDE";
                            int ctrlNum = int.Parse(ctrl.CimInstanceProperties["Address"]?.Value?.ToString() ?? "0");

                            var vmCtrlInfo = new VmStorageControllerInfo
                            {
                                VMName = vmName,
                                ControllerType = ctrlType,
                                ControllerNumber = ctrlNum,
                                AttachedDrives = new List<AttachedDriveInfo>()
                            };

                            // 从 Map 中直接查找该控制器的子设备
                            if (childrenMap.ContainsKey(ctrlId))
                            {
                                var slots = childrenMap[ctrlId];
                                foreach (var slot in slots)
                                {
                                    // 确定插槽位置 (AddressOnParent)
                                    string address = slot.CimInstanceProperties["AddressOnParent"]?.Value?.ToString() ?? "0";
                                    int location = int.TryParse(address, out int loc) ? loc : 0;

                                    // 寻找真正的媒介 (Media)
                                    // 逻辑：插槽 (Slot) 本身可能就是媒介，也可能指向另一个 Image 资源
                                    // 检查 Slot 是否有子节点 (Media)
                                    string slotId = slot.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                                    CimInstance media = null;

                                    // 看看这个 slot 下面有没有挂着 ISO 或 VHD (递归找一层)
                                    if (childrenMap.ContainsKey(slotId))
                                    {
                                        var childMedias = childrenMap[slotId];
                                        media = childMedias.FirstOrDefault(m => {
                                            int t = Convert.ToInt32(m.CimInstanceProperties["ResourceType"]?.Value);
                                            return t == 31 || t == 16 || t == 22; // 31=DiskImage, 16=DVDImage
                                        });
                                    }

                                    // 如果没有子媒介，且 Slot 本身有 HostResource (HostResource[] 包含路径)，则 Slot 就是媒介
                                    var slotHostRes = slot.CimInstanceProperties["HostResource"]?.Value as string[];
                                    if (media == null && slotHostRes != null && slotHostRes.Length > 0)
                                    {
                                        media = slot;
                                    }

                                    // 准备数据
                                    string path = "";
                                    int resType = Convert.ToInt32(slot.CimInstanceProperties["ResourceType"]?.Value);

                                    // 最终判断设备类型 (根据 Slot 类型)
                                    // 16=DVD Drive, 17=Disk Drive
                                    if (resType != 16 && resType != 17) continue;

                                    if (media != null)
                                    {
                                        var hostRes = media.CimInstanceProperties["HostResource"]?.Value as string[];
                                        if (hostRes != null && hostRes.Length > 0)
                                        {
                                            path = hostRes[0];
                                        }
                                    }

                                    var driveInfo = new AttachedDriveInfo
                                    {
                                        ControllerLocation = location,
                                        DriveType = (resType == 16) ? "DvdDrive" : "HardDisk",
                                        DiskType = "Empty",
                                        PathOrDiskNumber = path,
                                        DiskNumber = -1,
                                        DiskModel = "",
                                        DiskSizeGB = 0,
                                        SerialNumber = ""
                                    };

                                    // 解析路径信息
                                    if (!string.IsNullOrEmpty(path))
                                    {
                                        if (path.IndexOf("PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            driveInfo.DiskType = "Physical";
                                            var parts = path.Split(new[] { "PHYSICALDRIVE" }, StringSplitOptions.None);
                                            if (parts.Length > 1)
                                            {
                                                string numStr = parts[1].Replace("\"", "").Replace("\\", "").Trim();
                                                if (int.TryParse(numStr, out int dNum))
                                                {
                                                    driveInfo.DiskNumber = dNum;
                                                    driveInfo.PathOrDiskNumber = $"PhysicalDisk{dNum}";
                                                }
                                            }
                                        }
                                        else
                                        {
                                            driveInfo.DiskType = "Virtual";
                                            try
                                            {
                                                if (File.Exists(path))
                                                {
                                                    var fi = new FileInfo(path);
                                                    driveInfo.DiskSizeGB = Math.Round(fi.Length / (1024.0 * 1024.0 * 1024.0), 2);
                                                }
                                            }
                                            catch { }
                                        }
                                    }

                                    vmCtrlInfo.AttachedDrives.Add(driveInfo);
                                }
                            }

                            vmCtrlInfo.AttachedDrives = vmCtrlInfo.AttachedDrives.OrderBy(d => d.ControllerLocation).ToList();
                            resultList.Add(vmCtrlInfo);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExHyperV] WMI Error: {ex.Message}");
                }

                return resultList;
            });
        }
        public async Task<List<HostDiskInfo>> GetHostDisksAsync()
        {
            string script = @"
            $used = (Get-VM | Get-VMHardDiskDrive).DiskNumber
            $bootDiskNum = (Get-Partition | Where-Object { $_.IsBoot -eq $true }).DiskNumber | Select-Object -First 1
            Get-Disk | Where-Object {
                $_.BusType -ne 'USB' -and $_.MediaType -ne 'Removable' -and
                $_.IsSystem -eq $false -and $_.IsBoot -eq $false -and
                $used -notcontains $_.Number
            } | Select-Object Number, FriendlyName, @{N='SizeGB';E={[math]::round($_.Size/1GB, 2)}}, IsOffline, IsSystem, OperationalStatus |
            ConvertTo-Json -Compress";

            var json = await ExecutePowerShellAsync(script);
            if (string.IsNullOrEmpty(json)) return new List<HostDiskInfo>();
            if (json.StartsWith("{") && !json.StartsWith("[")) json = "[" + json + "]";
            return JsonSerializer.Deserialize<List<HostDiskInfo>>(json) ?? new List<HostDiskInfo>();
        }

        public async Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
        {
            string status = isOffline ? "$true" : "$false";
            return await RunCommandAsync($"Set-Disk -Number {diskNumber} -IsOffline {status}");
        }

        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256, string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default", string blockSize = "默认")
        {
            string psPath = string.IsNullOrWhiteSpace(pathOrNumber) ? "$null" : $"'{pathOrNumber}'";

            string script = $@"
            $ErrorActionPreference = 'Stop'
            $vmName = '{vmName}'
            $ctype = '{controllerType}'; $cnum = {controllerNumber}; $loc = {location}
            $driveType = '{driveType}'
            $path = {psPath}

            $v = Get-VM -Name $vmName
            if (-not $v) {{ throw ""找不到虚拟机 $vmName"" }}
            $state = [int]$v.State

            $oldDisk = Get-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc -ErrorAction SilentlyContinue
            $oldDvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc | Where-Object {{ $_.ControllerType -eq $ctype }}

            if ($driveType -eq 'HardDisk') {{
                if ($state -eq 2 -and $ctype -eq 'IDE') {{
                    throw ""IDE 控制器不支持在虚拟机运行状态下更换硬盘。""
                }}

                if ($oldDisk) {{ Remove-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc }}
                if ($oldDvd) {{ Remove-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc }}

                if ('{isNew.ToString().ToLower()}' -eq 'true') {{
                    $vhdParams = @{{ Path = $path; SizeBytes = {sizeGb}GB; {vhdType} = $true }}
                    if ('{sectorFormat}' -eq '512n') {{ $vhdParams.LogicalSectorSizeBytes = 512; $vhdParams.PhysicalSectorSizeBytes = 512 }}
                    elseif ('{sectorFormat}' -eq '512e') {{ $vhdParams.LogicalSectorSizeBytes = 512; $vhdParams.PhysicalSectorSizeBytes = 4096 }}
                    elseif ('{sectorFormat}' -eq '4kn') {{ $vhdParams.LogicalSectorSizeBytes = 4096; $vhdParams.PhysicalSectorSizeBytes = 4096 }}
                    if ('{blockSize}' -ne '默认') {{ $vhdParams.BlockSizeBytes = '{blockSize}' }}
                    if ('{vhdType}' -eq 'Differencing') {{
                        $vhdParams.Remove('SizeBytes'); $vhdParams.Remove('Dynamic'); $vhdParams.Remove('Fixed')
                        $vhdParams.ParentPath = '{parentPath}'
                    }}
                    New-VHD @vhdParams
                }}

                $p = @{{ VMName=$vmName; ControllerType=$ctype; ControllerNumber=$cnum; ControllerLocation=$loc }}
                if ('{isPhysical.ToString().ToLower()}' -eq 'true') {{ $p.DiskNumber=$path }} else {{ $p.Path=$path }}
                Add-VMHardDiskDrive @p
                
            }} else {{
                if ($oldDisk) {{ 
                    if ($state -eq 2) {{ throw ""运行状态下无法将硬盘位更换为光驱位。"" }}
                    Remove-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc 
                }}

                if ($oldDvd) {{
                    Set-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -Path $path
                }} else {{
                    Add-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -Path $path
                }}
            }}
            
            Write-Output ""RESULT:$ctype,$cnum,$loc""";

            try
            {
                var results = await Utils.Run2(script);
                string lastLine = results.LastOrDefault()?.ToString() ?? "";
                if (lastLine.StartsWith("RESULT:"))
                {
                    var parts = lastLine.Substring(7).Split(',');
                    return (true, "Success", parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
                }
                return (true, "Success", controllerType, controllerNumber, location);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, controllerType, controllerNumber, location);
            }
        }

        public async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, UiDriveModel drive)
        {
            string script = $@"
            $ErrorActionPreference = 'Stop'
            $vmName = '{vmName}'
            $cnum = {drive.ControllerNumber}
            $loc = {drive.ControllerLocation}

            $vmWmi = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter ""ElementName = '$vmName'""
            $state = [int]$vmWmi.EnabledState

            if ('{drive.DriveType}' -eq 'DvdDrive') {{
                $dvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -ErrorAction SilentlyContinue
                
                if (-not $dvd) {{ return ""SUCCESS:AlreadyRemoved"" }}

                if ($state -eq 3) {{
                    Remove-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc
                    return ""SUCCESS:Removed""
                }} 
                else {{
                    if (-not [string]::IsNullOrWhiteSpace($dvd.Path)) {{
                        $dvd | Set-VMDvdDrive -Path $null
                        return ""SUCCESS:Ejected""
                    }} else {{
                        throw ""虚拟机运行中，无法移除空光驱硬件。""
                    }}
                }}
            }} else {{
                Remove-VMHardDiskDrive -VMName $vmName -ControllerType '{drive.ControllerType}' -ControllerNumber $cnum -ControllerLocation $loc
                if ('{drive.DiskType}' -eq 'Physical' -and {drive.DiskNumber} -ne -1) {{
                    Start-Sleep -Milliseconds 500
                    Set-Disk -Number {drive.DiskNumber} -IsOffline $false -ErrorAction SilentlyContinue
                }}
            }}";

            return await RunCommandAsync(script);
        }

        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, string controllerType, int controllerNumber, int controllerLocation, string newIsoPath)
        {
            string pathArgument = string.IsNullOrWhiteSpace(newIsoPath) ? "$null" : $"'{newIsoPath}'";
            string script = $@"
            $ErrorActionPreference = 'Stop'
            $dvd = Get-VMDvdDrive -VMName '{vmName}' | Where-Object {{ 
                $_.ControllerNumber -eq {controllerNumber} -and 
                $_.ControllerLocation -eq {controllerLocation}
            }} | Select-Object -First 1

            if ($dvd) {{
                $dvd | Set-VMDvdDrive -Path {pathArgument}
            }} else {{
                throw '找不到指定位置的光驱设备'
            }}";
            return await RunCommandAsync(script);
        }

        public async Task<double> GetVhdSizeGbAsync(string path)
        {
            string script = $"Get-VHD -Path '{path}' | Select-Object -ExpandProperty Size";
            try
            {
                var res = await Utils.Run2(script);
                if (res != null && res.Count > 0 && long.TryParse(res[0].ToString().Trim(), out long bytes))
                {
                    return Math.Round(bytes / 1073741824.0, 2);
                }
            }
            catch { }
            return 0;
        }

        public async Task<(string ControllerType, int ControllerNumber, int Location)> GetNextAvailableSlotAsync(string vmName, string driveType)
        {
            string script = $@"
            $v = Get-VM -Name '{vmName}'
            $ctype = 'IDE'; $cnum = 0; $loc = 0;
            if ($v.Generation -eq 2 -or ($v.Generation -eq 1 -and $v.State -eq 'Running')) {{
                $ctype = 'SCSI'
                $ctrl = Get-VMScsiController -VMName '{vmName}' | Select-Object -First 1
                if (-not $ctrl) {{ $cnum = 0 }} else {{ $cnum = $ctrl.ControllerNumber }}
                $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType SCSI -ControllerNumber $cnum).ControllerLocation + `
                        (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $cnum).ControllerLocation
                for ($i=0; $i -lt 64; $i++) {{ if ($used -notcontains $i) {{ $loc = $i; break }} }}
            }} else {{
                $ctype = 'IDE'
                for ($c=0; $c -lt 2; $c++) {{
                    $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType IDE -ControllerNumber $c).ControllerLocation + `
                            (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $c).ControllerLocation
                    for ($i=0; $i -lt 2; $i++) {{ if ($used -notcontains $i) {{ $cnum=$c; $loc=$i; break }} }}
                    if ($loc -ne 0 -or $used.Count -lt 2) {{ break }}
                }}
            }}
            ""$ctype,$cnum,$loc""";

            var res = await ExecutePowerShellAsync(script);
            var parts = res.Trim().Split(',');
            if (parts.Length == 3) return (parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
            return ("SCSI", 0, 0);
        }

        private async Task<string> ExecutePowerShellAsync(string script)
        {
            try
            {
                var results = await Utils.Run2(script);
                if (results == null || results.Count == 0) return string.Empty;
                return string.Join(Environment.NewLine, results.Select(r => r?.ToString() ?? ""));
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<(bool Success, string Message)> RunCommandAsync(string script)
        {
            try
            {
                await Utils.Run2(script);
                return (true, "Success");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}