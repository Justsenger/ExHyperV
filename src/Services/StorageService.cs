using System;
using System.Collections.Generic;
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
        Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256, string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default", string blockSize = "Default");
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
                var resultList = new List<VmStorageControllerInfo>();
                string namespaceName = @"root\virtualization\v2";
                string hostNamespace = @"root\cimv2";
                Dictionary<string, int>? hvDiskMap = null;
                Dictionary<int, HostDiskInfoCache>? osDiskMap = null;
                try
                {
                    using (var session = CimSession.Create(null))
                    {
                        var vmQuery = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'";
                        var vm = session.QueryInstances(namespaceName, "WQL", vmQuery).FirstOrDefault();
                        if (vm == null) return resultList;
                        var settings = session.EnumerateAssociatedInstances(namespaceName, vm, "Msvm_SettingsDefineState", "Msvm_VirtualSystemSettingData", "ManagedElement", "SettingData").FirstOrDefault();
                        if (settings == null) return resultList;
                        var allResources = session.EnumerateAssociatedInstances(namespaceName, settings, "Msvm_VirtualSystemSettingDataComponent", "Msvm_ResourceAllocationSettingData", "GroupComponent", "PartComponent").ToList();
                        var allStorage = session.EnumerateAssociatedInstances(namespaceName, settings, "Msvm_VirtualSystemSettingDataComponent", "Msvm_StorageAllocationSettingData", "GroupComponent", "PartComponent").ToList();
                        var controllers = allResources.Where(res => { int rt = Convert.ToInt32(res.CimInstanceProperties["ResourceType"]?.Value ?? 0); return rt == 5 || rt == 6; }).ToList();
                        var childrenMap = new Dictionary<string, List<CimInstance>>();
                        var parentRegex = new Regex("InstanceID=\"([^\"]+)\"", RegexOptions.Compiled);
                        foreach (var res in allResources.Concat(allStorage))
                        {
                            var parentPath = res.CimInstanceProperties["Parent"]?.Value?.ToString();
                            if (!string.IsNullOrEmpty(parentPath))
                            {
                                var match = parentRegex.Match(parentPath);
                                if (match.Success)
                                {
                                    string parentId = match.Groups[1].Value.Replace("\\\\", "\\");
                                    if (!childrenMap.ContainsKey(parentId)) childrenMap[parentId] = new List<CimInstance>();
                                    childrenMap[parentId].Add(res);
                                }
                            }
                        }
                        int scsiCounter = 0; int ideCounter = 0;
                        foreach (var ctrl in controllers)
                        {
                            string ctrlId = ctrl.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                            int ctrlTypeVal = Convert.ToInt32(ctrl.CimInstanceProperties["ResourceType"]?.Value);
                            string ctrlType = ctrlTypeVal == 6 ? "SCSI" : "IDE";
                            int ctrlNum = (ctrlType == "SCSI") ? scsiCounter++ : ideCounter++;
                            var vmCtrlInfo = new VmStorageControllerInfo { VMName = vmName, ControllerType = ctrlType, ControllerNumber = ctrlNum, AttachedDrives = new List<AttachedDriveInfo>() };
                            if (childrenMap.ContainsKey(ctrlId))
                            {
                                foreach (var slot in childrenMap[ctrlId])
                                {
                                    int resType = Convert.ToInt32(slot.CimInstanceProperties["ResourceType"]?.Value);
                                    if (resType != 16 && resType != 17) continue;
                                    int location = int.Parse(slot.CimInstanceProperties["AddressOnParent"]?.Value?.ToString() ?? "0");
                                    CimInstance? media = null;
                                    string slotId = slot.CimInstanceProperties["InstanceID"]?.Value?.ToString() ?? "";
                                    if (childrenMap.ContainsKey(slotId)) media = childrenMap[slotId].FirstOrDefault(m => { int t = Convert.ToInt32(m.CimInstanceProperties["ResourceType"]?.Value); return t == 31 || t == 16 || t == 22; });
                                    if (media == null) media = slot;
                                    var driveInfo = new AttachedDriveInfo { ControllerLocation = location, DriveType = (resType == 16) ? "DvdDrive" : "HardDisk", DiskType = "Empty" };
                                    var hostRes = media.CimInstanceProperties["HostResource"]?.Value as string[];
                                    string rawPath = (hostRes != null && hostRes.Length > 0) ? hostRes[0] : "";
                                    if (!string.IsNullOrWhiteSpace(rawPath))
                                    {
                                        if (rawPath.Contains("PHYSICALDRIVE", StringComparison.OrdinalIgnoreCase) || rawPath.Contains("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase))
                                        {
                                            driveInfo.DiskType = "Physical"; driveInfo.PathOrDiskNumber = "Physical Drive";
                                            try
                                            {
                                                if (hvDiskMap == null)
                                                {
                                                    hvDiskMap = session.QueryInstances(namespaceName, "WQL", "SELECT DeviceID, DriveNumber FROM Msvm_DiskDrive").ToDictionary(d => d.CimInstanceProperties["DeviceID"].Value.ToString().Replace("\\\\", "\\"), d => Convert.ToInt32(d.CimInstanceProperties["DriveNumber"].Value));
                                                    osDiskMap = session.QueryInstances(hostNamespace, "WQL", "SELECT Index, Model, Size, SerialNumber FROM Win32_DiskDrive").ToDictionary(d => Convert.ToInt32(d.CimInstanceProperties["Index"].Value), d => new HostDiskInfoCache { Model = d.CimInstanceProperties["Model"].Value?.ToString(), SerialNumber = d.CimInstanceProperties["SerialNumber"].Value?.ToString()?.Trim(), SizeGB = Math.Round(Convert.ToInt64(d.CimInstanceProperties["Size"].Value) / 1073741824.0, 2) });
                                                }
                                                var match = Regex.Match(rawPath, "DeviceID=\"([^\"]+)\"");
                                                if (match.Success)
                                                {
                                                    string devId = match.Groups[1].Value.Replace("\\\\", "\\");
                                                    if (hvDiskMap.TryGetValue(devId, out int dNum))
                                                    {
                                                        driveInfo.DiskNumber = dNum; driveInfo.PathOrDiskNumber = $"PhysicalDisk{dNum}";
                                                        if (osDiskMap.TryGetValue(dNum, out var hi)) { driveInfo.DiskModel = hi.Model; driveInfo.SerialNumber = hi.SerialNumber; driveInfo.DiskSizeGB = hi.SizeGB; }
                                                    }
                                                }
                                            }
                                            catch { }
                                        }
                                        else if (rawPath.Contains("CDROM", StringComparison.OrdinalIgnoreCase)) { driveInfo.DiskType = "Physical"; driveInfo.PathOrDiskNumber = rawPath; }
                                        else
                                        {
                                            driveInfo.DiskType = "Virtual";
                                            string cleanPath = rawPath.Trim('"');
                                            driveInfo.PathOrDiskNumber = cleanPath;
                                            try { if (File.Exists(cleanPath)) driveInfo.DiskSizeGB = Math.Round(new FileInfo(cleanPath).Length / 1073741824.0, 2); } catch { }
                                        }
                                    }
                                    vmCtrlInfo.AttachedDrives.Add(driveInfo);
                                }
                            }
                            resultList.Add(vmCtrlInfo);
                        }
                    }
                }
                catch { }
                return resultList.OrderBy(c => c.ControllerType).ThenBy(c => c.ControllerNumber).ToList();
            });
        }

        private class HostDiskInfoCache { public string? Model { get; set; } public string? SerialNumber { get; set; } public double SizeGB { get; set; } }

        public async Task<List<HostDiskInfo>> GetHostDisksAsync()
        {
            string script = @"$used = (Get-VM | Get-VMHardDiskDrive).DiskNumber; Get-Disk | Where-Object { $_.BusType -ne 'USB' -and $_.MediaType -ne 'Removable' -and $_.IsSystem -eq $false -and $_.IsBoot -eq $false -and $used -notcontains $_.Number } | Select-Object Number, FriendlyName, @{N='SizeGB';E={[math]::round($_.Size/1GB, 2)}}, IsOffline, IsSystem, OperationalStatus | ConvertTo-Json -Compress";
            var json = await ExecutePowerShellAsync(script);
            if (string.IsNullOrEmpty(json)) return new List<HostDiskInfo>();
            return JsonSerializer.Deserialize<List<HostDiskInfo>>(json.StartsWith("{") ? "[" + json + "]" : json) ?? new List<HostDiskInfo>();
        }

        public async Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline) => await RunCommandAsync($"Set-Disk -Number {diskNumber} -IsOffline ${isOffline.ToString().ToLower()}");

        public async Task<(bool Success, string Message, string ActualType, int ActualNumber, int ActualLocation)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical, bool isNew = false, int sizeGb = 256, string vhdType = "Dynamic", string parentPath = "", string sectorFormat = "Default", string blockSize = "Default")
        {
            string psPath = string.IsNullOrWhiteSpace(pathOrNumber) ? "$null" : $"'{pathOrNumber}'";
            string script = $@"
    $ErrorActionPreference = 'Stop'
    if ('{controllerType}' -eq 'SCSI') {{
        $scsiCtrls = Get-VMScsiController -VMName '{vmName}' | Sort-Object ControllerNumber
        $max = if ($scsiCtrls) {{ ($scsiCtrls | Select-Object -Last 1).ControllerNumber }} else {{ -1 }}
        for ($i = $max + 1; $i -le {controllerNumber}; $i++) {{ Add-VMScsiController -VMName '{vmName}' -ErrorAction Stop }}
    }}
    $oldDisk = Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction SilentlyContinue
    $oldDvd = Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {location} | Where-Object {{ $_.ControllerType -eq '{controllerType}' }}
    if ('{driveType}' -eq 'HardDisk') {{
        if ($oldDisk) {{ Remove-VMHardDiskDrive -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction Stop }}
        if ($oldDvd) {{ Remove-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction Stop }}
        if ('{isNew.ToString().ToLower()}' -eq 'true') {{
            $vhdParams = @{{ Path = {psPath}; SizeBytes = {sizeGb}GB; {vhdType} = $true; ErrorAction = 'Stop' }}
            if ('{sectorFormat}' -eq '512n') {{ $vhdParams.LogicalSectorSizeBytes = 512; $vhdParams.PhysicalSectorSizeBytes = 512 }}
            elseif ('{sectorFormat}' -eq '512e') {{ $vhdParams.LogicalSectorSizeBytes = 512; $vhdParams.PhysicalSectorSizeBytes = 4096 }}
            elseif ('{sectorFormat}' -eq '4kn') {{ $vhdParams.LogicalSectorSizeBytes = 4096; $vhdParams.PhysicalSectorSizeBytes = 4096 }}
            if ('{blockSize}' -ne 'Default') {{ $vhdParams.BlockSizeBytes = '{blockSize}' }}
            if ('{vhdType}' -eq 'Differencing') {{ $vhdParams.Remove('SizeBytes'); $vhdParams.Remove('Dynamic'); $vhdParams.Remove('Fixed'); $vhdParams.ParentPath = '{parentPath}' }}
            New-VHD @vhdParams
        }}
        $p = @{{ VMName='{vmName}'; ControllerType='{controllerType}'; ControllerNumber={controllerNumber}; ControllerLocation={location}; ErrorAction='Stop' }}
        if ('{isPhysical.ToString().ToLower()}' -eq 'true') {{ $p.DiskNumber={psPath} }} else {{ $p.Path={psPath} }}
        Add-VMHardDiskDrive @p
    }} else {{
        if ($oldDisk) {{ Remove-VMHardDiskDrive -VMName '{vmName}' -ControllerType '{controllerType}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -ErrorAction Stop }}
        if ($oldDvd) {{ Set-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -Path {psPath} -ErrorAction Stop }}
        else {{ Add-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -Path {psPath} -ErrorAction Stop }}
    }}
    Write-Output ""RESULT:{controllerType},{controllerNumber},{location}""";
            try
            {
                var results = await Utils.Run2(script);
                string lastLine = results.LastOrDefault()?.ToString() ?? "";
                if (lastLine.StartsWith("RESULT:"))
                {
                    var parts = lastLine.Substring(7).Split(',');
                    return (true, "Storage_Msg_Success", parts[0], int.Parse(parts[1]), int.Parse(parts[2]));
                }
                return (true, "Storage_Msg_Success", controllerType, controllerNumber, location);
            }
            catch (Exception ex) { return (false, GetFriendlyErrorMessage(ex.Message), controllerType, controllerNumber, location); }
        }

        public async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, UiDriveModel drive)
        {
            string script = $@"
    $ErrorActionPreference = 'Stop'
    $vmName = '{vmName}'; $cnum = {drive.ControllerNumber}; $loc = {drive.ControllerLocation}; $ctype = '{drive.ControllerType}'
    $v = Get-VM -Name $vmName
    if ('{drive.DriveType}' -eq 'DvdDrive') {{
        $dvd = Get-VMDvdDrive -VMName $vmName | Where-Object {{ $_.ControllerType -eq $ctype -and $_.ControllerNumber -eq $cnum -and $_.ControllerLocation -eq $loc }}
        if (-not $dvd) {{ throw 'Storage_Error_DvdDriveNotFound' }}
        if ($v.State -eq 'Off' -or $ctype -eq 'SCSI') {{ $dvd | Remove-VMDvdDrive -ErrorAction Stop; return 'Storage_Msg_Removed' }}
        else {{ if ($dvd.Path) {{ $dvd | Set-VMDvdDrive -Path $null -ErrorAction Stop; return 'Storage_Msg_Ejected' }} else {{ throw 'Storage_Error_DvdHotRemoveNotSupported' }} }}
    }} else {{
        $disk = Get-VMHardDiskDrive -VMName $vmName | Where-Object {{ $_.ControllerType -eq $ctype -and $_.ControllerNumber -eq $cnum -and $_.ControllerLocation -eq $loc }}
        if (-not $disk) {{ throw 'Storage_Error_DiskNotFound' }}
        $disk | Remove-VMHardDiskDrive -ErrorAction Stop
        if ('{drive.DiskType}' -eq 'Physical' -and {drive.DiskNumber} -ne -1) {{ Start-Sleep -Milliseconds 500; Set-Disk -Number {drive.DiskNumber} -IsOffline $false -ErrorAction SilentlyContinue }}
        return 'Storage_Msg_Removed'
    }}";
            try
            {
                var results = await Utils.Run2(script);
                return (true, results.LastOrDefault()?.ToString() ?? "Storage_Msg_Removed");
            }
            catch (Exception ex) { return (false, GetFriendlyErrorMessage(ex.Message)); }
        }

        public async Task<(bool Success, string Message)> ModifyDvdDrivePathAsync(string vmName, string controllerType, int controllerNumber, int controllerLocation, string newIsoPath) => await RunCommandAsync($"Set-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {controllerLocation} -Path {(string.IsNullOrWhiteSpace(newIsoPath) ? "$null" : $"'{newIsoPath}'")} -ErrorAction Stop");

        private string GetFriendlyErrorMessage(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return "Storage_Error_Unknown";
            var match = Regex.Match(rawMessage, @"Storage_(Error|Msg)_[A-Za-z0-9_]+");
            if (match.Success) return match.Value;
            string cleanMsg = Regex.Replace(rawMessage.Trim(), @"[\(\（].*?ID\s+[a-fA-F0-9-]{36}.*?[\)\）]", "").Replace("\r", "").Replace("\n", " ");
            var parts = cleanMsg.Split(new[] { '。', '.' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            return (parts.Count >= 2 && parts.Last().Length > 2) ? parts.Last() + "。" : cleanMsg;
        }

        public async Task<double> GetVhdSizeGbAsync(string path) { try { var res = await Utils.Run2($"Get-VHD -Path '{path}' | Select-Object -ExpandProperty Size"); return Math.Round(Convert.ToInt64(res[0].ToString()) / 1073741824.0, 2); } catch { return 0; } }

        public async Task<(string ControllerType, int ControllerNumber, int Location)> GetNextAvailableSlotAsync(string vmName, string driveType)
        {
            string script = $@"
    $v = Get-VM -Name '{vmName}'; $ctype = 'IDE'; $cnum = 0; $loc = 0; $found = $false
    if ($v.Generation -eq 2 -or ($v.Generation -eq 1 -and $v.State -eq 'Running')) {{
        $ctype = 'SCSI'; $controllers = Get-VMScsiController -VMName '{vmName}' | Sort-Object ControllerNumber
        foreach ($ctrl in $controllers) {{
            $cn = $ctrl.ControllerNumber; $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType SCSI -ControllerNumber $cn).ControllerLocation + (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $cn).ControllerLocation
            for ($i=0; $i -lt 64; $i++) {{ if ($used -notcontains $i) {{ $cnum = $cn; $loc = $i; $found = $true; break }} }}
            if ($found) {{ break }}
        }}
        if (-not $found) {{ $cnum = if ($controllers) {{ ($controllers | Sort-Object ControllerNumber | Select-Object -Last 1).ControllerNumber + 1 }} else {{ 0 }}; $loc = 0; }}
    }} else {{
        $ctype = 'IDE'; for ($c=0; $c -lt 2; $c++) {{ $used = (Get-VMHardDiskDrive -VMName '{vmName}' -ControllerType IDE -ControllerNumber $c).ControllerLocation + (Get-VMDvdDrive -VMName '{vmName}' -ControllerNumber $c).ControllerLocation; for ($i=0; $i -lt 2; $i++) {{ if ($used -notcontains $i) {{ $cnum=$c; $loc=$i; $found=$true; break }} }} if ($found) {{ break }} }}
    }} ""$ctype,$cnum,$loc""";
            var res = await ExecutePowerShellAsync(script); var parts = res.Trim().Split(',');
            return parts.Length == 3 ? (parts[0], int.Parse(parts[1]), int.Parse(parts[2])) : ("SCSI", 0, 0);
        }

        private async Task<string> ExecutePowerShellAsync(string script) { try { var results = await Utils.Run2(script); return results == null ? "" : string.Join(Environment.NewLine, results.Select(r => r?.ToString() ?? "")); } catch { return ""; } }
        private async Task<(bool Success, string Message)> RunCommandAsync(string script) { try { await Utils.Run2(script); return (true, "Storage_Msg_Success"); } catch (Exception ex) { return (false, GetFriendlyErrorMessage(ex.Message)); } }
    }
}