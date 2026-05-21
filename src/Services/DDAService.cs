using System.Management.Automation;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Tools;
using ExHyperV.Tools.Api;

namespace ExHyperV.Services
{
    public enum MmioCheckResultType { Ok, NeedsConfirmation, Error }

    public class DDAService
    {
        private const ulong RequiredMmioBytes = 64UL * 1024 * 1024 * 1024; // 64 GiB

        private string GetPureId(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return string.Empty;
            int idx = instanceId.IndexOf(@"\VEN_", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? instanceId.Substring(idx) : instanceId;
        }

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

                    // 1. 获取虚拟机列表（WMI 替换 Get-VM）
                    string hostName = WmiApi.Escape(Environment.MachineName);
                    var vmResp = await WmiApi.QueryAsync(
                        $"SELECT ElementName FROM Msvm_ComputerSystem WHERE Name <> '{hostName}'",
                        obj => obj["ElementName"]?.ToString() ?? string.Empty,
                        WmiScope.HyperV);

                    if (vmResp.Success && vmResp.Data != null)
                        vmNameList.AddRange(vmResp.Data.Where(n => !string.IsNullOrEmpty(n)));

                    // 用 Msvm_PciExpressSettingData 替换 Get-VMAssignableDevice
                    // InstanceID 格式：Microsoft:VM_GUID\device_GUID（排除 Definition/Minimum/Maximum/Increment）
                    // HostResource[0] 的 DeviceID 里包含 PNP InstanceId
                    var pciSettingResp = await WmiApi.QueryAsync(
                        "SELECT InstanceID, HostResource FROM Msvm_PciExpressSettingData",
                        obj => obj,
                        WmiScope.HyperV);

                    if (pciSettingResp.Success && pciSettingResp.Data != null)
                    {
                        // 建立 VM GUID → VM 名称的映射
                        var vmGuidToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var vmGuidResp = await WmiApi.QueryAsync(
                            $"SELECT Name, ElementName FROM Msvm_ComputerSystem WHERE Name <> '{hostName}'",
                            obj => (Guid: obj["Name"]?.ToString() ?? "", Name: obj["ElementName"]?.ToString() ?? ""),
                            WmiScope.HyperV);
                        if (vmGuidResp.Success && vmGuidResp.Data != null)
                            foreach (var item in vmGuidResp.Data.Where(x => !string.IsNullOrEmpty(x.Guid)))
                                vmGuidToName[item.Guid] = item.Name;

                        foreach (var obj in pciSettingResp.Data)
                        {
                            using (obj)
                            {
                                string instanceId = obj["InstanceID"]?.ToString() ?? string.Empty;
                                // 排除 Definition/Minimum/Maximum/Increment 记录
                                if (!instanceId.StartsWith("Microsoft:", StringComparison.OrdinalIgnoreCase)) continue;
                                string withoutPrefix = instanceId.Substring("Microsoft:".Length);
                                // 格式：VMGUID\deviceGUID，VMGUID 不含 "Definition" 等关键字
                                int backslash = withoutPrefix.IndexOf('\\');
                                if (backslash < 0) continue;
                                string vmGuid = withoutPrefix.Substring(0, backslash);
                                if (vmGuid.IndexOf("Definition", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                                if (!vmGuidToName.TryGetValue(vmGuid, out string vmName) || string.IsNullOrEmpty(vmName)) continue;

                                // 从 HostResource 提取 PNP InstanceId
                                if (!(obj["HostResource"] is string[] hostResources) || hostResources.Length == 0) continue;
                                string hostResStr = hostResources[0];
                                var match = System.Text.RegularExpressions.Regex.Match(
                                    hostResStr, @"DeviceID=""Microsoft:[^\\]+\\\\(.+?)""");
                                if (!match.Success) continue;
                                string pnpInstanceId = match.Groups[1].Value.Replace("\\\\", "\\");
                                string pureId = GetPureId(pnpInstanceId);
                                if (!string.IsNullOrEmpty(pureId))
                                    vmDeviceAssignments[pureId] = vmName;
                            }
                        }
                    }

                    // 2. 检查已卸除设备（Get-PnpDevice 无法替换，保留 PS）
                    var pnpDevices = Utils.Run("Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCIP\\*' } | Select-Object InstanceId, Status");
                    if (pnpDevices != null)
                    {
                        foreach (var pnpDevice in pnpDevices)
                        {
                            var pureId = GetPureId(pnpDevice.Members["InstanceId"]?.Value?.ToString());
                            var status = pnpDevice.Members["Status"]?.Value?.ToString();
                            if (!string.IsNullOrEmpty(pureId) && status == "OK" && !vmDeviceAssignments.ContainsKey(pureId))
                                vmDeviceAssignments[pureId] = Resources.removed;
                        }
                    }

                    // 3. 获取所有PCI设备（Get-PnpDeviceProperty DEVPKEY_Device_LocationPaths 无法替换，保留 PS）
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

                            var instanceId = result.Members["InstanceId"]?.Value?.ToString();
                            var status = result.Members["Status"]?.Value?.ToString();
                            var pureId = GetPureId(instanceId);

                            if (status == "Unknown" && !string.IsNullOrEmpty(pureId))
                            {
                                if (vmDeviceAssignments.TryGetValue(pureId, out var assignedStatus))
                                    status = assignedStatus;
                                else
                                    continue;
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
                    Console.WriteLine($"[DDAService] Error in GetDdaInfoAsync: {ex}");
                    deviceList.Clear();
                    vmNameList.Clear();
                }
            });

            return (deviceList, vmNameList);
        }

        public Task<bool> IsServerOperatingSystemAsync()
            => Task.FromResult(HyperVEnvironmentService.IsServerSystem());

        public async Task<(MmioCheckResultType Result, string Message)> CheckMmioSpaceAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var results = Utils.Run($"Get-VM -Name \"{vmName}\" | Select-Object HighMemoryMappedIoSpace");
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
                        $"if ((Get-VM -Name '{vmName}').State -eq 'Running') {{ Stop-VM -Name '{vmName}' -Force; }};" +
                        $"\nSet-VM -VMName '{vmName}' -HighMemoryMappedIoSpace {RequiredMmioBytes};";
                    return Utils.Run(script) != null;
                }
                catch { return false; }
            });
        }

        public async Task<(bool Success, string? ErrorMessage)> ExecuteDdaOperationAsync(
            string targetVmName, string currentVmName, string instanceId, string path,
            IProgress<string>? progress = null)
        {
            try
            {
                var operations = DDACommands(targetVmName, instanceId, path, currentVmName);
                if (operations.Count == 0) return (true, null);

                foreach (var operation in operations)
                {
                    progress?.Report(operation.Message);
                    var logOutput = await ExecuteCommandAsync(operation.Command, instanceId, operation.IsPnpEnable, operation.IsPnpDisable);
                    var errorLogs = logOutput.Where(log => log.Contains("Error", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (errorLogs.Any())
                    {
                        var errorMessage = string.Join(Environment.NewLine, errorLogs);
                        if (targetVmName == Resources.Host)
                            Win32Api.EnablePnpDevice(instanceId);
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

        private async Task<List<string>> ExecuteCommandAsync(string psCommand, string instanceId, bool isPnpEnable, bool isPnpDisable)
        {
            if (isPnpEnable)
            {
                var result = Win32Api.EnablePnpDevice(instanceId);
                return result.Success ? new List<string>() : new List<string> { $"Error: {result.Error}" };
            }
            if (isPnpDisable)
            {
                var result = Win32Api.DisablePnpDevice(instanceId);
                return result.Success ? new List<string>() : new List<string> { $"Error: {result.Error}" };
            }

            var logOutput = new List<string>();
            try
            {
                using var powerShell = PowerShell.Create();
                powerShell.AddScript(psCommand);
                var results = await Task.Run(() => powerShell.Invoke());
                foreach (var item in results) logOutput.Add(item.ToString());
                var errorStream = powerShell.Streams.Error.ReadAll();
                foreach (var error in errorStream) logOutput.Add($"Error: {error}");
            }
            catch (Exception ex)
            {
                logOutput.Add($"Error: {ex.Message}");
            }
            return logOutput;
        }

        private List<(string Command, string Message, bool IsPnpEnable, bool IsPnpDisable)> DDACommands(
            string Vmname, string instanceId, string path, string Nowname)
        {
            var operations = new List<(string Command, string Message, bool IsPnpEnable, bool IsPnpDisable)>();

            // 场景1: 设备已卸除，现在要分配给主机
            if (Nowname == Resources.removed && Vmname == Resources.Host)
            {
                operations.Add(($"Mount-VMHostAssignableDevice -LocationPath '{path}'", Resources.mounting, false, false));
                operations.Add((string.Empty, Resources.enabling, true, false)); // Win32Api.EnablePnpDevice
            }
            // 场景2: 设备已卸除，现在要分配给某个虚拟机
            else if (Nowname == Resources.removed && Vmname != Resources.Host)
            {
                operations.Add(($"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'", Resources.mounting, false, false));
            }
            // 场景3: 设备在主机上，现在要分配给某个虚拟机
            else if (Nowname == Resources.Host)
            {
                operations.Add(($"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff", Resources.string5, false, false));
                operations.Add(($"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'", Resources.cpucache, false, false));
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath, false, false));
                operations.Add((string.Empty, Resources.Disabledevice, false, true)); // Win32Api.DisablePnpDevice
                operations.Add(($"Dismount-VMHostAssignableDevice -Force -LocationPath '{path}'", Resources.Dismountdevice, false, false));
                operations.Add(($"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'", Resources.mounting, false, false));
            }
            // 场景4: 设备从一个虚拟机移到另一个虚拟机
            else if (Vmname != Resources.Host && Nowname != Resources.Host)
            {
                operations.Add(($"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff", Resources.string5, false, false));
                operations.Add(($"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'", Resources.cpucache, false, false));
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath, false, false));
                operations.Add(($"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'", Resources.Dismountdevice, false, false));
                operations.Add(($"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'", Resources.mounting, false, false));
            }
            // 场景5: 设备从一个虚拟机移回给主机
            else if (Vmname == Resources.Host && Nowname != Resources.Host)
            {
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath, false, false));
                operations.Add(($"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'", Resources.Dismountdevice, false, false));
                operations.Add(($"Mount-VMHostAssignableDevice -LocationPath '{path}'", Resources.mounting, false, false));
                operations.Add((string.Empty, Resources.enabling, true, false)); // Win32Api.EnablePnpDevice
            }

            return operations;
        }
    }
}