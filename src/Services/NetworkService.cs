using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Management.Automation;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class NetworkService : INetworkService
    {
        public async Task<(List<SwitchInfo> Switches, List<PhysicalAdapterInfo> PhysicalAdapters)> GetNetworkInfoAsync()
        {
            return await Task.Run(() =>
            {
                var switchList = new List<SwitchInfo>();
                var physicalAdapterList = new List<PhysicalAdapterInfo>();

                try
                {
                    Utils.Run("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");

                    if (Utils.Run("Get-Module -ListAvailable -Name Hyper-V").Count == 0)
                    {
                        return (switchList, physicalAdapterList);
                    }

                    var phydata = Utils.Run(@"Get-NetAdapter -Physical | select Name, InterfaceDescription");
                    if (phydata != null)
                    {
                        foreach (var result in phydata)
                        {
                            var phyDesc = result.Properties["InterfaceDescription"]?.Value?.ToString();
                            if (!string.IsNullOrEmpty(phyDesc))
                            {
                                physicalAdapterList.Add(new PhysicalAdapterInfo(phyDesc));
                            }
                        }
                    }

                    var switchdata = Utils.Run(@"Get-VMSwitch | Select-Object Name, Id, SwitchType, AllowManagementOS, NetAdapterInterfaceDescription");
                    if (switchdata != null)
                    {
                        foreach (var result in switchdata)
                        {
                            var switchName = result.Properties["Name"]?.Value?.ToString() ?? string.Empty;
                            var switchType = result.Properties["SwitchType"]?.Value?.ToString() ?? string.Empty;
                            var host = result.Properties["AllowManagementOS"]?.Value?.ToString() ?? string.Empty;
                            var id = result.Properties["Id"]?.Value?.ToString() ?? string.Empty;
                            var phydesc = result.Properties["NetAdapterInterfaceDescription"]?.Value?.ToString() ?? string.Empty;

                            string? icsAdapter = GetIcsSourceAdapterName(switchName);
                            if (icsAdapter != null)
                            {
                                switchType = "NAT";
                                phydesc = icsAdapter;
                            }

                            if (!string.IsNullOrEmpty(switchName))
                            {
                                switchList.Add(new SwitchInfo(switchName, switchType, host, id, phydesc));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in GetNetworkInfoAsync: {ex}");
                    throw new InvalidOperationException(Properties.Resources.Error_GetNetworkInfoFailed, ex);
                }

                return (switchList, physicalAdapterList);
            });
        }

        public async Task<List<AdapterInfo>> GetFullSwitchNetworkStateAsync(string switchName)
        {

            return await Task.Run(async () =>
            {
                string vmAdaptersScript =
                    $"Get-VMNetworkAdapter -VMName * | Where-Object {{ $_.SwitchName -eq '{switchName}' }} | " +
                    "Select-Object VMName, " +
                    "@{Name='MacAddress'; Expression={($_.MacAddress).Insert(2, ':').Insert(5, ':').Insert(8, ':').Insert(11, ':').Insert(14, ':')}}, " +
                    "@{Name='AdapterStatus'; Expression={($_.Status | Out-String).Trim()}}";

                string hostAdapterScript =
                    $"$vmAdapter = Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}';" +
                    "if ($vmAdapter) {" +
                    "    $netAdapter = Get-NetAdapter | Where-Object { ($_.MacAddress -replace '-') -eq ($vmAdapter.MacAddress -replace '-') -and ($_.InterfaceDescription -like '*Hyper-V*') };" +
                    "    if ($netAdapter) {" +
                    "        $ipAddressObjects = Get-NetIPAddress -InterfaceIndex $netAdapter.InterfaceIndex -ErrorAction SilentlyContinue;" +
                    "        if ($ipAddressObjects) {" +
                    "            $ipAddresses = ($ipAddressObjects.IPAddress) -join ',';" +
                    "        } else {" +
                    "            $ipAddresses = '';" +
                    "        }" +
                    "        [PSCustomObject]@{" +
                    "            VMName      = '(ManagementOS)';" +
                    "            MacAddress  = ($netAdapter.MacAddress -replace '-', ':');" +
                    "            Status      = $netAdapter.Status.ToString();" +
                    "            IPAddresses = $ipAddresses;" +
                    "        };" +
                    "    }" +
                    "}";

                try
                {
                    var allAdapters = new List<AdapterInfo>();

                    var vmResults = Utils.Run(vmAdaptersScript);
                    if (vmResults != null)
                    {
                        var tasks = vmResults.Select(async pso =>
                        {
                            var vmName = pso.Properties["VMName"]?.Value?.ToString() ?? "";
                            var macAddress = pso.Properties["MacAddress"]?.Value?.ToString() ?? "";
                            var adapterStatus = pso.Properties["AdapterStatus"]?.Value?.ToString() ?? "";

                            var ipAddresses = await Utils.GetVmIpAddressAsync(vmName, macAddress);

                            return new AdapterInfo(vmName, macAddress, adapterStatus, ipAddresses);
                        });

                        allAdapters.AddRange(await Task.WhenAll(tasks));
                    }

                    var stopwatch = Stopwatch.StartNew();
                    Collection<PSObject>? hostResults = null;
                    while (stopwatch.ElapsedMilliseconds < 2000)
                    {
                        hostResults = Utils.Run(hostAdapterScript);
                        if (hostResults != null && hostResults.Count > 0)
                        {
                            var ipValue = hostResults[0].Properties["IPAddresses"]?.Value?.ToString();
                            if (!string.IsNullOrEmpty(ipValue))
                            {
                                break;
                            }
                        }
                        await Task.Delay(200);
                    }
                    stopwatch.Stop();

                    if (hostResults != null)
                    {
                        foreach (var pso in hostResults)
                        {
                            string rawVmName = pso.Properties["VMName"]?.Value?.ToString() ?? "";
                            allAdapters.Add(new AdapterInfo(
                                rawVmName == "(ManagementOS)" ? ExHyperV.Properties.Resources.DisplayName_HostManagementOS : rawVmName,
                                pso.Properties["MacAddress"]?.Value?.ToString() ?? "",
                                pso.Properties["Status"]?.Value?.ToString() ?? "",
                                pso.Properties["IPAddresses"]?.Value?.ToString() ?? ""
                            ));
                        }
                    }

                    return allAdapters;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error getting full network state for switch '{switchName}': {ex.Message}");
                    return new List<AdapterInfo>();
                }
            });
        }

        public async Task UpdateSwitchConfigurationAsync(string switchName, string mode, string? adapterDescription, bool allowManagementOS, bool enableDhcp)
        {
            await Utils.UpdateSwitchConfigurationAsync(switchName, mode, adapterDescription, allowManagementOS, enableDhcp);
        }

        public async Task CreateSwitchAsync(string name, string type, string? adapterDescription)
        {
            try
            {
                string script;
                switch (type.ToUpper())
                {
                    case "EXTERNAL":
                        if (string.IsNullOrEmpty(adapterDescription))
                        {
                            throw new ArgumentException(Properties.Resources.Error_ExternalSwitchRequiresPhysicalAdapter);
                        }
                        script = $"New-VMSwitch -Name '{name}' -NetAdapterInterfaceDescription '{adapterDescription}' -AllowManagementOS $true";
                        await Task.Run(() => Utils.Run(script));
                        break;
                    case "NAT":
                        script = $"New-VMSwitch -Name '{name}' -SwitchType Internal";
                        await Task.Run(() => Utils.Run(script));
                        await Task.Delay(3000);
                        await UpdateSwitchConfigurationAsync(name, "NAT", adapterDescription, true, true);
                        break;
                    case "INTERNAL":
                    default:
                        script = $"New-VMSwitch -Name '{name}' -SwitchType Internal";
                        await Task.Run(() => Utils.Run(script));
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error in CreateSwitchAsync: {ex}");
                throw new InvalidOperationException(string.Format(Properties.Resources.Error_CreateSwitchFailed, name, ex.Message), ex);
            }
        }

        public async Task DeleteSwitchAsync(string switchName)
        {
            await Task.Run(() =>
            {
                try
                {
                    ClearAllIcsSettings();
                    string script = $"Remove-VMSwitch -Name '{switchName}' -Force";
                    Utils.Run(script);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error in DeleteSwitchAsync: {ex}");
                    throw new InvalidOperationException(string.Format(Properties.Resources.Error_DeleteSwitchFailed, switchName), ex);
                }
            });
        }

        private void ClearAllIcsSettings()
        {
            try
            {
                string script = @"
                    $netShareManager = New-Object -ComObject HNetCfg.HNetShare;
                    foreach ($connection in $netShareManager.EnumEveryConnection) {
                        $config = $netShareManager.INetSharingConfigurationForINetConnection.Invoke($connection);
                        if ($config.SharingEnabled) {
                            $config.DisableSharing();
                        }
                    }";
                Utils.Run(script);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in ClearAllIcsSettings: {ex}");
            }
        }

        private string? GetIcsSourceAdapterName(string switchName)
        {
            string script = @"
            param([string]$switchName)
            try {
                $PublicAdapterNameToFind = ""vEthernet ({0})"" -f $switchName
                $netShareManager = New-Object -ComObject HNetCfg.HNetShare
                $icsSourceAdapterName = $null
                $icsGatewayIsCorrect = $false
                foreach ($connection in $netShareManager.EnumEveryConnection) {
                    $config = $netShareManager.INetSharingConfigurationForINetConnection.Invoke($connection)
                    if ($config.SharingEnabled) {
                        $props = $netShareManager.NetConnectionProps.Invoke($connection)
                        if (($config.SharingConnectionType -eq 1) -and ($props.Name -eq $PublicAdapterNameToFind)) {
                            $icsGatewayIsCorrect = $true
                        }
                        elseif ($config.SharingConnectionType -eq 0) {
                            $icsSourceAdapterName = $props.Name
                        }
                    }
                }
                if (($icsGatewayIsCorrect) -and ($null -ne $icsSourceAdapterName)) {
                    try {
                        $adapterDetails = Get-NetAdapter -Name $icsSourceAdapterName -ErrorAction Stop
                        return $adapterDetails.InterfaceDescription
                    }
                    catch {
                        return $icsSourceAdapterName
                    }
                }
                return $null
            }
            catch {
                return $null
            }";

            try
            {
                using (var ps = PowerShell.Create())
                {
                    ps.AddScript(script);
                    ps.AddParameter("switchName", switchName);
                    var results = ps.Invoke();
                    if (ps.Streams.Error.Count > 0) return null;
                    if (results.Count > 0 && results[0] != null) return results[0].BaseObject?.ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Exception in GetIcsSourceAdapterName: {ex}");
                return null;
            }

            return null;
        }
    }
}