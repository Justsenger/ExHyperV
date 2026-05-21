using System.Diagnostics;
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
                    var vmDeviceAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    // ── 1. VM列表 + 已分配设备（Get-VMAssignableDevice，原始逻辑）──────
                    string hostName = WmiApi.Escape(Environment.MachineName);
                    var vmResp = await WmiApi.QueryAsync(
                        $"SELECT ElementName FROM Msvm_ComputerSystem WHERE Name <> '{hostName}'",
                        obj => obj["ElementName"]?.ToString() ?? string.Empty,
                        WmiScope.HyperV);
                    if (vmResp.Success && vmResp.Data != null)
                        vmNameList.AddRange(vmResp.Data.Where(n => !string.IsNullOrEmpty(n)));
                    Debug.WriteLine($"[DDA] vmNames={string.Join(",", vmNameList)}");

                    // Get-VMAssignableDevice 返回 PCIP\* 格式的 InstanceID
                    foreach (var vmName in vmNameList)
                    {
                        var assigned = Utils.Run($"Get-VMAssignableDevice -VMName '{vmName}' | Select-Object InstanceID");
                        if (assigned == null) continue;
                        foreach (var dev in assigned)
                        {
                            var pureId = GetPureId(dev.Members["InstanceID"]?.Value?.ToString());
                            if (!string.IsNullOrEmpty(pureId))
                            {
                                vmDeviceAssignments[pureId] = vmName;
                                Debug.WriteLine($"[DDA] assigned: pureId='{pureId}' vm='{vmName}'");
                            }
                        }
                    }
                    Debug.WriteLine($"[DDA] vmDeviceAssignments count={vmDeviceAssignments.Count}");

                    // ── 2. 枚举所有 PCI 设备（Win32Api，替换 Get-PnpDevice）──────────
                    var allPciDevices = await Task.Run(() => Win32Api.GetAllDevices());
                    Debug.WriteLine($"[DDA] allPciDevices count={allPciDevices.Count}");

                    // PCIP（已卸除）且不在 vmDeviceAssignments → 标为 removed
                    // PCIP 设备只更新字典，不进 deviceList（与原始逻辑一致）
                    foreach (var d in allPciDevices.Where(d =>
                        d.InstanceId.StartsWith("PCIP\\", StringComparison.OrdinalIgnoreCase)))
                    {
                        string pureId = GetPureId(d.InstanceId);
                        if (!string.IsNullOrEmpty(pureId) && !vmDeviceAssignments.ContainsKey(pureId))
                        {
                            vmDeviceAssignments[pureId] = Resources.removed;
                            Debug.WriteLine($"[DDA] PCIP removed: pureId='{pureId}'");
                        }
                    }

                    // ── 3. 构建 DeviceInfo（只用 PCI\* 在线设备，与原始逻辑一致）────────
                    var sortedDevices = allPciDevices
                        .Where(d => !string.IsNullOrEmpty(d.Service)
                            && d.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                            && !d.InstanceId.StartsWith("PCIP\\", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(d => d.Service![0])
                        .ToList();

                    foreach (var pciDev in sortedDevices)
                    {
                        if (string.Equals(pciDev.Service, "pci", StringComparison.OrdinalIgnoreCase)) continue;

                        string pureId = GetPureId(pciDev.InstanceId);
                        vmDeviceAssignments.TryGetValue(pureId ?? string.Empty, out string? assignedVal);
                        bool inAssignments = !string.IsNullOrEmpty(pureId) && assignedVal != null;
                        assignedVal ??= string.Empty;

                        Debug.WriteLine($"[DDA] device '{pciDev.InstanceId}' status={pciDev.Status} inAssignments={inAssignments} assignedVal='{assignedVal}'");

                        string status;
                        if (pciDev.Status == "Unknown" && !string.IsNullOrEmpty(pureId))
                        {
                            if (!inAssignments)
                            {
                                Debug.WriteLine($"[DDA]   → SKIP (Unknown not in assignments)");
                                continue;
                            }
                            status = assignedVal;
                            Debug.WriteLine($"[DDA]   → status='{status}'");
                        }
                        else
                        {
                            status = Resources.Host;
                            Debug.WriteLine($"[DDA]   → Host");
                        }

                        string path = pciDev.FirstLocationPath ?? "";
                        string vendor = pciInfoProvider.GetVendorFromInstanceId(pciDev.InstanceId);
                        deviceList.Add(new DeviceInfo(pciDev.FriendlyName, status, pciDev.Class, pciDev.InstanceId, path, vendor));
                    }

                    Debug.WriteLine($"[DDA] deviceList final count={deviceList.Count}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DDA] EXCEPTION: {ex}");
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
                catch (Exception ex) { return (MmioCheckResultType.Error, ex.Message); }
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
            catch (Exception ex) { return (false, ex.Message); }
        }

        private async Task<List<string>> ExecuteCommandAsync(string psCommand, string instanceId, bool isPnpEnable, bool isPnpDisable)
        {
            string actualCommand = psCommand;
            if (isPnpEnable)
                actualCommand = $"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false";
            else if (isPnpDisable)
                actualCommand = $"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false";

            var logOutput = new List<string>();
            try
            {
                using var powerShell = PowerShell.Create();
                powerShell.AddScript(actualCommand);
                var results = await Task.Run(() => powerShell.Invoke());
                foreach (var item in results) logOutput.Add(item.ToString());
                foreach (var error in powerShell.Streams.Error.ReadAll()) logOutput.Add($"Error: {error}");
            }
            catch (Exception ex) { logOutput.Add($"Error: {ex.Message}"); }
            return logOutput;
        }

        private List<(string Command, string Message, bool IsPnpEnable, bool IsPnpDisable)> DDACommands(
            string Vmname, string instanceId, string path, string Nowname)
        {
            var operations = new List<(string Command, string Message, bool IsPnpEnable, bool IsPnpDisable)>();

            if (Nowname == Resources.removed && Vmname == Resources.Host)
            {
                operations.Add(($"Mount-VMHostAssignableDevice -LocationPath '{path}'", Resources.mounting, false, false));
                operations.Add((string.Empty, Resources.enabling, true, false));
            }
            else if (Nowname == Resources.removed && Vmname != Resources.Host)
            {
                operations.Add(($"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'", Resources.mounting, false, false));
            }
            else if (Nowname == Resources.Host)
            {
                operations.Add(($"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff", Resources.string5, false, false));
                operations.Add(($"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'", Resources.cpucache, false, false));
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath, false, false));
                operations.Add((string.Empty, Resources.Disabledevice, false, true));
                operations.Add(($"Dismount-VMHostAssignableDevice -Force -LocationPath '{path}'", Resources.Dismountdevice, false, false));
                operations.Add(($"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'", Resources.mounting, false, false));
            }
            else if (Vmname != Resources.Host && Nowname != Resources.Host)
            {
                operations.Add(($"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff", Resources.string5, false, false));
                operations.Add(($"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'", Resources.cpucache, false, false));
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath, false, false));
                operations.Add(($"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'", Resources.Dismountdevice, false, false));
                operations.Add(($"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'", Resources.mounting, false, false));
            }
            else if (Vmname == Resources.Host && Nowname != Resources.Host)
            {
                operations.Add(($"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]", Resources.getpath, false, false));
                operations.Add(($"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'", Resources.Dismountdevice, false, false));
                operations.Add(($"Mount-VMHostAssignableDevice -LocationPath '{path}'", Resources.mounting, false, false));
                operations.Add((string.Empty, Resources.enabling, true, false));
            }

            return operations;
        }
    }
}