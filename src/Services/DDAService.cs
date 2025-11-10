using System.Management.Automation;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>
    /// 实现了IHyperVService接口，负责处理所有与PowerShell的实际交互。
    /// </summary>
    public class DDAService : IHyperVService
    {
        private const ulong RequiredMmioBytes = 64UL * 1024 * 1024 * 1024; // 64 GiB

        #region Public Methods (IHyperVService Implementation)

        public async Task<(List<DeviceInfo> Devices, List<string> VmNames)> GetDdaInfoAsync()
        {
            var deviceList = new List<DeviceInfo>();
            var vmNameList = new List<string>();

            await Task.Run(async () =>
            {
                var pciInfoProvider = new PciInfoProvider();
                await pciInfoProvider.EnsureInitializedAsync();

                try
                {
                    Dictionary<string, string> vmDeviceAssignments = new Dictionary<string, string>();

                    // 1. 获取虚拟机及已分配设备的信息
                    var hypervModule = Utils.Run("Get-Module -ListAvailable -Name Hyper-V");
                    if (hypervModule != null && hypervModule.Count != 0)
                    {
                        var vms = Utils.Run(@"Hyper-V\Get-VM | Select-Object Name");
                        if (vms != null)
                        {
                            foreach (var vm in vms)
                            {
                                var name = vm.Members["Name"]?.Value?.ToString();
                                if (string.IsNullOrEmpty(name)) continue;

                                vmNameList.Add(name);
                                var assignedDevices = Utils.Run($@"Get-VMAssignableDevice -VMName '{name}' | Select-Object InstanceID");
                                if (assignedDevices != null)
                                {
                                    foreach (var device in assignedDevices)
                                    {
                                        var instanceId = device.Members["InstanceID"]?.Value?.ToString()?.Substring(4);
                                        if (!string.IsNullOrEmpty(instanceId))
                                        {
                                            vmDeviceAssignments[instanceId] = name;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // 2. 检查已卸除(Dismounted)的设备状态
                    var pnpDevices = Utils.Run("Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCIP\\*' } | Select-Object InstanceId, Status");
                    if (pnpDevices != null)
                    {
                        foreach (var pnpDevice in pnpDevices)
                        {
                            var instanceId = pnpDevice.Members["InstanceId"]?.Value?.ToString()?.Substring(4);
                            var status = pnpDevice.Members["Status"]?.Value?.ToString();
                            if (!string.IsNullOrEmpty(instanceId) && status == "OK" && !vmDeviceAssignments.ContainsKey(instanceId))
                            {
                                vmDeviceAssignments[instanceId] = Resources.removed;
                            }
                        }
                    }

                    // 3. 获取所有PCI设备并确定其最终状态
                    string getPciDevicesScript = @"function Invoke-GetPathBatch {
                                                param($Ids, $Map, $Key)
                                                if ($Ids.Count -eq 0) { return }
                                                Get-PnpDeviceProperty -InstanceId $Ids -KeyName $Key -ErrorAction SilentlyContinue | ForEach-Object {
                                                    if ($_.Data -and $_.Data.Count -gt 0) { $Map[$_.InstanceId] = $_.Data[0] }
                                                }
                                            }

                                            $fastRetries = 3
                                            $slowRetries = 2
                                            $slowRetryIntervalSeconds = 1
                                            $maxRetries = $fastRetries + $slowRetries
                                            $KeyName = 'DEVPKEY_Device_LocationPaths'

                                            $pciDevices = Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCI\*' }
                                            if (-not $pciDevices) { exit }

                                            $pciDeviceCount = $pciDevices.Count
                                            if ($pciDeviceCount -gt 200) {
                                                $batchSize = 100
                                            } else {
                                                $batchSize = $pciDeviceCount
                                                if ($batchSize -lt 1) { $batchSize = 1 }
                                            }

                                            $allInstanceIds = $pciDevices.InstanceId
                                            $pathMap = @{}
                                            $idsNeedingPath = $allInstanceIds
                                            $attemptCount = 0

                                            while (($idsNeedingPath.Count -gt 0) -and ($attemptCount -lt $maxRetries)) {
                                                $attemptCount++
                                                if ($idsNeedingPath.Count -gt 0) {
                                                    $numBatches = [Math]::Ceiling($idsNeedingPath.Count / $batchSize)
                                                    for ($i = 0; $i -lt $numBatches; $i++) {
                                                        $batch = $idsNeedingPath[($i * $batchSize) .. ([Math]::Min((($i + 1) * $batchSize - 1), ($idsNeedingPath.Count - 1)))]
                                                        if ($batch.Count -gt 0) {
                                                            Invoke-GetPathBatch -Ids $batch -Map $pathMap -Key $KeyName
                                                        }
                                                    }
                                                }

                                                $idsNeedingPath = $allInstanceIds | Where-Object { -not $pathMap.ContainsKey($_) }

                                                if (($idsNeedingPath.Count -gt 0) -and ($attemptCount -ge $fastRetries) -and ($attemptCount -lt $maxRetries)) {
                                                    Start-Sleep -Seconds $slowRetryIntervalSeconds
                                                }
                                            }

                                            $pciDevices | Select-Object Class, InstanceId, FriendlyName, Status, Service | ForEach-Object {
                                                $val = $null; if ($pathMap.ContainsKey($_.InstanceId)) { $val = $pathMap[$_.InstanceId] }
                                                $_ | Add-Member -NotePropertyName 'Path' -NotePropertyValue $val -Force; $_
                                            }";

                    var pciData = Utils.Run(getPciDevicesScript);
                    if (pciData != null)
                    {
                        var sortedResults = pciData
                            .Where(r => r != null)
                            .OrderBy(r => r.Members["Service"]?.Value?.ToString()?[0])
                            .ToList();

                        foreach (var result in sortedResults)
                        {
                            var service = result.Members["Service"]?.Value?.ToString();
                            var classType = result.Members["Class"]?.Value?.ToString();
                            if (service == "pci" || string.IsNullOrEmpty(service)) continue;
                            if (classType == "System") continue;

                            var instanceId = result.Members["InstanceId"]?.Value?.ToString();
                            var status = result.Members["Status"]?.Value?.ToString();

                            if (status == "Unknown" && !string.IsNullOrEmpty(instanceId) && instanceId.Length > 3)
                            {
                                status = vmDeviceAssignments.GetValueOrDefault(instanceId.Substring(3));
                                if (status == null) continue;
                            }
                            else
                            {
                                status = Resources.Host;
                            }

                            var friendlyName = result.Members["FriendlyName"]?.Value?.ToString();
                            var path = result.Members["Path"]?.Value?.ToString();
                            string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId);

                            deviceList.Add(new DeviceInfo(friendlyName, status, classType, instanceId, path, vendor));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HyperVService] Error in GetDdaInfoAsync: {ex}");
                    deviceList.Clear();
                    vmNameList.Clear();
                }
            });

            return (deviceList, vmNameList);
        }

        public async Task<bool> IsServerOperatingSystemAsync()
        {
            return await Task.Run(() =>
            {
                var result = Utils.Run("(Get-CimInstance -Class Win32_OperatingSystem).ProductType");
                return result != null && result.Count > 0 && result[0].ToString() == "3";
            });
        }

        public async Task<(MmioCheckResultType Result, string Message)> CheckMmioSpaceAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var results = Utils.Run($"Hyper-V\\Get-VM -Name \"{vmName}\" | Select-Object HighMemoryMappedIoSpace");
                    if (results == null || results.Count == 0)
                        return (MmioCheckResultType.Error, Properties.Resources.Error_CannotGetVmInfo);

                    var mmioProperty = results[0].Properties["HighMemoryMappedIoSpace"];
                    if (mmioProperty == null || mmioProperty.Value == null)
                        return (MmioCheckResultType.Error, Properties.Resources.Error_CannotParseMmioSpace);

                    ulong currentMmioBytes = Convert.ToUInt64(mmioProperty.Value);
                    if (currentMmioBytes < RequiredMmioBytes)
                    {
                        long currentMmioGB = (long)(currentMmioBytes / (1024 * 1024 * 1024));
                        string message = string.Format(Properties.Resources.Warning_LowMmioSpace_ConfirmExpand, vmName, currentMmioGB);
                        return (MmioCheckResultType.NeedsConfirmation, message);
                    }
                    return (MmioCheckResultType.Ok, Properties.Resources.Info_MmioSpaceSufficient);
                }
                catch (Exception ex)
                {
                    return (MmioCheckResultType.Error, ex.Message);
                }
            });
        }

        public async Task<bool> UpdateMmioSpaceAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string script =
                        $"if ((Hyper-V\\Get-VM -Name '{vmName}').State -eq 'Running') {{ Stop-VM -Name '{vmName}' -Force; }};" +
                        $"\nSet-VM -VMName '{vmName}' -HighMemoryMappedIoSpace {RequiredMmioBytes};";
                    return Utils.Run(script) != null;
                }
                catch { return false; }
            });
        }

        public async Task<(bool Success, string? ErrorMessage)> ExecuteDdaOperationAsync(string targetVmName, string currentVmName, string instanceId, string path, IProgress<string>? progress = null)
        {
            try
            {
                var operations = DDACommands(targetVmName, instanceId, path, currentVmName);
                if (operations.Count == 0) return (true, null); 
                foreach (var operation in operations)
                {
                    progress?.Report(operation.Message);
                    var logOutput = await ExecutePowerShellCommandAsync(operation.Command);
                    var errorLogs = logOutput.Where(log => log.Contains("Error", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (errorLogs.Any())
                    {
                        var errorMessage = string.Join(Environment.NewLine, errorLogs);
                        return (false, errorMessage);
                    }
                }
                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        #endregion

        #region Private Helper Methods

        private async Task<List<string>> ExecutePowerShellCommandAsync(string psCommand)
        {
            var logOutput = new List<string>();
            try
            {
                using (var powerShell = PowerShell.Create())
                {
                    powerShell.AddScript(psCommand);
                    var results = await Task.Run(() => powerShell.Invoke());
                    foreach (var item in results)
                    {
                        logOutput.Add(item.ToString());
                    }
                    var errorStream = powerShell.Streams.Error.ReadAll();
                    if (errorStream.Any())
                    {
                        foreach (var error in errorStream)
                        {
                            logOutput.Add($"Error: {error}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logOutput.Add($"Error: {ex.Message}");
            }
            return logOutput;
        }

        private List<(string Command, string Message)> DDACommands(string Vmname, string instanceId, string path, string Nowname)
        {
            var operations = new List<(string Command, string Message)>();
            // 场景1: 设备已卸除，现在要分配给主机。
            if (Nowname == Resources.removed && Vmname == Resources.Host)
            {
                operations.Add((
                    Command: $"Mount-VMHostAssignableDevice -LocationPath '{path}'",
                    Message: Resources.mounting
                ));
            }
            // 场景2: 设备已卸除，现在要分配给某个虚拟机。
            else if (Nowname == Resources.removed && Vmname != Resources.Host)
            {
                operations.Add((
                    Command: $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'",
                    Message: Resources.mounting
                ));
            }
            // 场景3: 设备在主机上，现在要分配给某个虚拟机。
            else if (Nowname == Resources.Host)
            {
                operations.Add(($"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff", Resources.string5));
                operations.Add(($"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'", Resources.cpucache));
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath));
                operations.Add(($"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false", Resources.Disabledevice));
                operations.Add(($"Dismount-VMHostAssignableDevice -Force -LocationPath '{path}'", Resources.Dismountdevice));
                operations.Add(($"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'", Resources.mounting));
            }
            // 场景4: 设备从一个虚拟机移到另一个虚拟机。
            else if (Vmname != Resources.Host && Nowname != Resources.Host)
            {
                operations.Add(($"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff", Resources.string5));
                operations.Add(($"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'", Resources.cpucache));
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath));
                operations.Add(($"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'", Resources.Dismountdevice));
                operations.Add(($"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'", Resources.mounting));
            }
            // 场景5: 设备从一个虚拟机移回给主机。
            else if (Vmname == Resources.Host && Nowname != Resources.Host)
            {
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath));
                operations.Add(($"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'", Resources.Dismountdevice));
                operations.Add(($"Mount-VMHostAssignableDevice -LocationPath '{path}'", Resources.mounting));
                operations.Add(($"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false", Resources.enabling));
            }
            return operations;
        }
        #endregion
    }
}