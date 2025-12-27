using System.Management.Automation;
using System.Text.Json;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public interface IStorageService
    {
        Task<List<VmStorageControllerInfo>> GetVmStorageInfoAsync(string vmName);
        Task<List<HostDiskInfo>> GetHostDisksAsync();
        Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline);
        Task<(bool Success, string Message)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical);
        Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType);
    }

    public class StorageService : IStorageService
    {
        public async Task<List<VmStorageControllerInfo>> GetVmStorageInfoAsync(string vmName)
        {
            string script = $@"
            $vm = Get-VM -Name '{vmName}';
            $allHardDrives = Get-VMHardDiskDrive -VM $vm;
            $allDvdDrives = Get-VMDvdDrive -VM $vm;
            $result = @();
            if ($vm.Generation -eq 1) {{
                $ideControllers = Get-VMIdeController -VM $vm;
                foreach ($controller in $ideControllers) {{
                    $hddsOnController = @($allHardDrives | Where-Object {{ $_.ControllerType -eq 'IDE' -and $_.ControllerNumber -eq $controller.ControllerNumber }} | Select-Object ControllerLocation, @{{N='DriveType';E={{'HardDisk'}}}}, @{{N='DiskType';E={{if($_.DiskNumber -ne $null){{'Physical'}}else{{'Virtual'}}}}}}, @{{N='PathOrDiskNumber';E={{if($_.DiskNumber -ne $null){{$_.DiskNumber}}else{{$_.Path}}}}}});
                    $dvdsOnController = @($allDvdDrives | Where-Object {{ $_.ControllerType -eq 'IDE' -and $_.ControllerNumber -eq $controller.ControllerNumber }} | Select-Object ControllerLocation, @{{N='DriveType';E={{'DvdDrive'}}}}, @{{N='DiskType';E={{'Virtual'}}}}, @{{N='PathOrDiskNumber';E={{$_.Path}}}});
                    $result += [PSCustomObject]@{{ VMName = $vm.Name; Generation = $vm.Generation; ControllerType = 'IDE'; ControllerNumber = $controller.ControllerNumber; AttachedDrives = $hddsOnController + $dvdsOnController }}
                }}
            }};
            $scsiControllers = Get-VMScsiController -VM $vm;
            foreach ($controller in $scsiControllers) {{
                $hddsOnController = @($allHardDrives | Where-Object {{ $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq $controller.ControllerNumber }} | Select-Object ControllerLocation, @{{N='DriveType';E={{'HardDisk'}}}}, @{{N='DiskType';E={{if($_.DiskNumber -ne $null){{'Physical'}}else{{'Virtual'}}}}}}, @{{N='PathOrDiskNumber';E={{if($_.DiskNumber -ne $null){{$_.DiskNumber}}else{{$_.Path}}}}}});
                $dvdsOnController = @($allDvdDrives | Where-Object {{ $_.ControllerType -eq 'SCSI' -and $_.ControllerNumber -eq $controller.ControllerNumber }} | Select-Object ControllerLocation, @{{N='DriveType';E={{'DvdDrive'}}}}, @{{N='DiskType';E={{'Virtual'}}}}, @{{N='PathOrDiskNumber';E={{$_.Path}}}});
                $result += [PSCustomObject]@{{ VMName = $vm.Name; Generation = $vm.Generation; ControllerType = 'SCSI'; ControllerNumber = $controller.ControllerNumber; AttachedDrives = $hddsOnController + $dvdsOnController }}
            }}
            $result | ConvertTo-Json -Depth 5 -Compress";

            var json = await ExecutePowerShellAsync(script);
            if (string.IsNullOrEmpty(json)) return new List<VmStorageControllerInfo>();
            if (json.StartsWith("{")) json = "[" + json + "]";
            return JsonSerializer.Deserialize<List<VmStorageControllerInfo>>(json) ?? new List<VmStorageControllerInfo>();
        }

        public async Task<List<HostDiskInfo>> GetHostDisksAsync()
        {
            string script = "Get-Disk | Select-Object Number, FriendlyName, @{N='SizeGB';E={[math]::round($_.Size/1GB, 2)}}, IsOffline, IsSystem, OperationalStatus | ConvertTo-Json -Compress";
            var json = await ExecutePowerShellAsync(script);
            if (string.IsNullOrEmpty(json)) return new List<HostDiskInfo>();
            if (json.StartsWith("{")) json = "[" + json + "]";
            return JsonSerializer.Deserialize<List<HostDiskInfo>>(json) ?? new List<HostDiskInfo>();
        }

        public async Task<(bool Success, string Message)> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
        {
            string status = isOffline ? "$true" : "$false";
            string script = $"Set-Disk -Number {diskNumber} -IsOffline {status}";
            return await RunCommandAsync(script);
        }

        public async Task<(bool Success, string Message)> AddDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType, string pathOrNumber, bool isPhysical)
        {
            string script;
            if (driveType == "HardDisk")
            {
                if (isPhysical)
                    script = $"Add-VMHardDiskDrive -VMName '{vmName}' -ControllerType {controllerType} -ControllerNumber {controllerNumber} -ControllerLocation {location} -DiskNumber {pathOrNumber}";
                else
                    script = $"Add-VMHardDiskDrive -VMName '{vmName}' -ControllerType {controllerType} -ControllerNumber {controllerNumber} -ControllerLocation {location} -Path '{pathOrNumber}'";
            }
            else
            {
                script = $"Add-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {location} -Path '{pathOrNumber}'";
            }
            return await RunCommandAsync(script);
        }

        public async Task<(bool Success, string Message)> RemoveDriveAsync(string vmName, string controllerType, int controllerNumber, int location, string driveType)
        {
            string script = driveType == "HardDisk"
                ? $"Remove-VMHardDiskDrive -VMName '{vmName}' -ControllerType {controllerType} -ControllerNumber {controllerNumber} -ControllerLocation {location}"
                : $"Remove-VMDvdDrive -VMName '{vmName}' -ControllerNumber {controllerNumber} -ControllerLocation {location}";
            return await RunCommandAsync(script);
        }

        private async Task<string> ExecutePowerShellAsync(string script)
        {
            return await Task.Run(() =>
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript(script);
                    var results = ps.Invoke();
                    return results.FirstOrDefault()?.ToString() ?? string.Empty;
                }
            });
        }

        private async Task<(bool Success, string Message)> RunCommandAsync(string script)
        {
            return await Task.Run(() =>
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript(script);
                    ps.Invoke();
                    if (ps.HadErrors)
                    {
                        return (false, ps.Streams.Error.FirstOrDefault()?.ToString() ?? "Execution Error");
                    }
                    return (true, "Success");
                }
            });
        }
    }
}