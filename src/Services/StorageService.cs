using System.Management.Automation;
using System.Text.Json;
using ExHyperV.Models;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Diagnostics;

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
            string script = $@"
            $ErrorActionPreference = 'Stop'
            $vm = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter ""ElementName = '{vmName}'""
            if (-not $vm) {{ return ""[]"" }}
            $vmSettings = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData -Association Msvm_SettingsDefineState
            if (-not $vmSettings) {{ return ""[]"" }}
            $rasd = Get-CimAssociatedInstance -InputObject $vmSettings -ResultClassName Msvm_ResourceAllocationSettingData
            $sasd = Get-CimAssociatedInstance -InputObject $vmSettings -ResultClassName Msvm_StorageAllocationSettingData
            $allObjects = @{{}}
            foreach ($item in ($rasd + $sasd)) {{ if ($item.InstanceID) {{ $allObjects[$item.InstanceID] = $item }} }}
            $allList = $allObjects.Values
            $childrenMap = @{{}}
            foreach ($obj in $allList) {{
                if ($obj.Parent -match 'InstanceID=""([^""]+)""') {{
                    $parentId = $matches[1].Replace('\\', '\')
                    if (-not $childrenMap.ContainsKey($parentId)) {{ $childrenMap[$parentId] = @() }}
                    $childrenMap[$parentId] += $obj
                }}
            }}
            $controllers = $allList | Where-Object {{ $_.ResourceType -eq 5 -or $_.ResourceType -eq 6 }}
            $result = foreach ($ctrl in $controllers) {{
                $drivesFound = @()
                $slots = $childrenMap[$ctrl.InstanceID]
                if ($slots) {{
                    foreach ($slot in $slots) {{
                        $mediaList = $childrenMap[$slot.InstanceID]
                        $media = $mediaList | Where-Object {{ $_.ResourceType -eq 31 -or $_.ResourceType -eq 16 -or $_.ResourceType -eq 22 }} | Select-Object -First 1
                        if (-not $media -and $slot.HostResource) {{ $media = $slot }}
                        $location = 0
                        if ($slot.AddressOnParent) {{ $location = [int]$slot.AddressOnParent }}
                        elseif ($slot.InstanceID -match ""(\d+)\\\w+$"") {{ $location = [int]$matches[1] }}
                        $dType = ""Empty""; $path = """"; $dModel = """"; $dSize = 0; $sn = """"; $dNum = -1
                        if ($media) {{
                            $rawPath = if ($media.HostResource) {{ [string]$media.HostResource[0] }} else {{ """" }}
                            $path = $rawPath; $dType = ""Virtual""
                            if ([string]::IsNullOrWhiteSpace($path)) {{ $dType = ""Empty"" }}
                            elseif ($path -match ""Msvm_DiskDrive|PHYSICALDRIVE"") {{
                                $dType = ""Physical""
                                if ($path -match 'DeviceID=""([^""]+)""') {{
                                    try {{
                                        $devID = $matches[1].Replace('\\\\', '\\'); $hvDisk = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_DiskDrive -Filter ""DeviceID = '$devID'""
                                        if ($hvDisk) {{
                                            $dNum = $hvDisk.DriveNumber; $path = ""PhysicalDisk$dNum""
                                            $osDisk = Get-CimInstance -Namespace root\cimv2 -ClassName Win32_DiskDrive -Filter ""Index = $dNum""
                                            if ($osDisk) {{ $dModel = $osDisk.Model; $dSize = [math]::Round($osDisk.Size / 1GB, 2); $sn = if ($osDisk.SerialNumber) {{ $osDisk.SerialNumber.Trim() }} else {{ """" }} }}
                                        }}
                                    }} catch {{}}
                                }}
                            }} else {{
                                if (Test-Path $path) {{
                                    try {{
                                        if ($path.EndsWith("".iso"", [System.StringComparison]::OrdinalIgnoreCase)) {{ $f = Get-Item $path; $dSize = [math]::Round($f.Length / 1GB, 2) }}
                                        else {{ $v = Get-VHD -Path $path; if ($v) {{ $dSize = [math]::Round($v.Size / 1GB, 2) }} }}
                                    }} catch {{}}
                                }}
                            }}
                        }}
                        $drvType = if ($slot.ResourceType -eq 16 -or ($media -and $media.ResourceType -eq 16)) {{ ""DvdDrive"" }} else {{ ""HardDisk"" }}
                        if ($slot.ResourceType -eq 17 -or $slot.ResourceType -eq 16 -or $slot.ResourceType -eq 31 -or $slot.ResourceType -eq 22) {{
                            $drivesFound += [PSCustomObject]@{{
                                ControllerLocation = $location; DriveType = $drvType; DiskType = $dType; PathOrDiskNumber = $path;
                                DiskNumber = $dNum; DiskModel = $dModel; DiskSizeGB = $dSize; SerialNumber = $sn
                            }}
                        }}
                    }}
                }}
                $cNum = 0
                if ($ctrl.InstanceID -match ""(\d+)$"") {{ $cNum = [int]$matches[1] }}
                [PSCustomObject]@{{
                    VMName = '{vmName}'
                    Generation = if ($controllers | Where-Object {{ $_.ResourceType -eq 5 }}) {{ 1 }} else {{ 2 }}
                    ControllerType = if ($ctrl.ResourceType -eq 6) {{ ""SCSI"" }} else {{ ""IDE"" }}
                    ControllerNumber = $cNum
                    AttachedDrives = @($drivesFound)
                }}
            }}
            $result | ConvertTo-Json -Depth 10 -Compress";

            var json = await ExecutePowerShellAsync(script);
            if (string.IsNullOrWhiteSpace(json) || json == "null") return new List<VmStorageControllerInfo>();
            json = json.Trim();
            if (json.StartsWith("{")) json = $"[{json}]";

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var list = JsonSerializer.Deserialize<List<VmStorageControllerInfo>>(json, options) ?? new List<VmStorageControllerInfo>();
                return list.OrderBy(c => c.ControllerType == "IDE" ? 0 : 1).ThenBy(c => c.ControllerNumber).ToList();
            }
            catch { return new List<VmStorageControllerInfo>(); }
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

    # 1. 探测该位置现有的硬件驱动器
    $oldDisk = Get-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc -ErrorAction SilentlyContinue
    $oldDvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc | Where-Object {{ $_.ControllerType -eq $ctype }}

    # 2. 核心逻辑：针对硬盘 (VHDX) 的“更换”处理
    if ($driveType -eq 'HardDisk') {{
        # 如果是 IDE 且虚拟机在运行，Hyper-V 严禁热插拔硬盘
        if ($state -eq 2 -and $ctype -eq 'IDE') {{
            throw ""IDE 控制器不支持在虚拟机运行状态下更换硬盘。""
        }}

        # 如果原位置有东西（不管是硬盘还是光驱），先执行物理删除
        # 这是“更换” VHDX 的唯一可靠方法：先拔掉旧的，再插上新的
        if ($oldDisk) {{ Remove-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc }}
        if ($oldDvd) {{ Remove-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc }}

        # 3. 处理新建 VHD 逻辑 (仅在添加物理硬盘之外的情况下)
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

        # 4. 执行 Add 操作（此时插槽已干净）
        $p = @{{ VMName=$vmName; ControllerType=$ctype; ControllerNumber=$cnum; ControllerLocation=$loc }}
        if ('{isPhysical.ToString().ToLower()}' -eq 'true') {{ $p.DiskNumber=$path }} else {{ $p.Path=$path }}
        Add-VMHardDiskDrive @p
        
    }} else {{
        # --- 光驱处理逻辑 (光驱支持 Set-VMDvdDrive 换碟) ---
        if ($oldDisk) {{ 
            if ($state -eq 2) {{ throw ""运行状态下无法将硬盘位更换为光驱位。"" }}
            Remove-VMHardDiskDrive -VMName $vmName -ControllerType $ctype -ControllerNumber $cnum -ControllerLocation $loc 
        }}

        if ($oldDvd) {{
            # 光驱硬件已存在，直接 Set 路径即可
            Set-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -Path $path
        }} else {{
            # 光驱硬件不存在，执行物理添加
            Add-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -Path $path
        }}
    }}
    
    Write-Output ""RESULT:$ctype,$cnum,$loc""";

            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddScript(script);
                var results = await Task.Run(() => ps.Invoke());
                if (ps.HadErrors) return (false, ps.Streams.Error.FirstOrDefault()?.ToString() ?? "Unknown Error", controllerType, controllerNumber, location);
                string lastLine = results.LastOrDefault()?.ToString() ?? "";
                if (lastLine.StartsWith("RESULT:"))
                {
                    var parts = lastLine.Substring(7).Split(',');
                    return (true, "Success", parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
                }
                return (true, "Success", controllerType, controllerNumber, location);
            }
        }

        public async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, UiDriveModel drive)
        {
            string script = $@"
    $ErrorActionPreference = 'Stop'
    $vmName = '{vmName}'
    $cnum = {drive.ControllerNumber}
    $loc = {drive.ControllerLocation}

    # 1. 强制获取 WMI 原始状态 (3 = Off)
    $vmWmi = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter ""ElementName = '$vmName'""
    $state = [int]$vmWmi.EnabledState

    if ('{drive.DriveType}' -eq 'DvdDrive') {{
        # 实时获取最新驱动器对象
        $dvd = Get-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc -ErrorAction SilentlyContinue
        
        if (-not $dvd) {{ return ""SUCCESS:AlreadyRemoved"" }}

        if ($state -eq 3) {{
            # --- 情况 A: 虚拟机已关机 (State 3) ---
            # 别废话，直接一记物理删除，管它有没有盘，一步到位
            Remove-VMDvdDrive -VMName $vmName -ControllerNumber $cnum -ControllerLocation $loc
            return ""SUCCESS:Removed""
        }} 
        else {{
            # --- 情况 B: 虚拟机运行中 (State 2) ---
            # 运行中删不掉硬件，只能弹盘
            if (-not [string]::IsNullOrWhiteSpace($dvd.Path)) {{
                $dvd | Set-VMDvdDrive -Path $null
                return ""SUCCESS:Ejected""
            }} else {{
                throw ""虚拟机运行中，无法移除空光驱硬件。""
            }}
        }}
    }} else {{
        # 硬盘移除 (关机状态下也建议直接坐标删除，不留活口)
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
            var res = await ExecutePowerShellAsync(script);
            if (long.TryParse(res.Trim(), out long bytes)) return Math.Round(bytes / 1073741824.0, 2);
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
            return await Task.Run(() => {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript(script);
                    var results = ps.Invoke();
                    return string.Join(Environment.NewLine, results.Select(r => r.ToString()));
                }
            });
        }

        private async Task<(bool Success, string Message)> RunCommandAsync(string script)
        {
            return await Task.Run(() => {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript(script);
                    ps.Invoke();
                    if (ps.HadErrors) return (false, ps.Streams.Error.FirstOrDefault()?.ToString() ?? "Execution Error");
                    return (true, "Success");
                }
            });
        }
    }
}