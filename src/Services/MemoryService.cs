using System.Management.Automation;
using System.Text;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.ViewModels;

namespace ExHyperV.Services
{
    public class MemoryService : IMemoryService
    {
        public async Task<List<MemoryInfo>> GetHostMemoryAsync()
        {
            string script = @"
                Get-CimInstance -ClassName Win32_PhysicalMemory | ForEach-Object {
                    $rawManufacturer = if ($_.Manufacturer) { $_.Manufacturer.Trim() } else { 'Unknown' }; $partNumber = if ($_.PartNumber) { $_.PartNumber.Trim() } else { 'Unknown' }; $finalManufacturer = $rawManufacturer
                    if ($rawManufacturer -eq 'Unknown' -or [string]::IsNullOrEmpty($rawManufacturer) -or $rawManufacturer -match '^\s*0+\s*$') {
                        switch -Wildcard ($partNumber) {
                            'H9J*' { $finalManufacturer = 'SK Hynix' }; 'HMA*' { $finalManufacturer = 'SK Hynix' }; 'HMC*' { $finalManufacturer = 'SK Hynix' }; 'M3*' { $finalManufacturer = 'Samsung' }; 'M4*' { $finalManufacturer = 'Samsung' }; 'MTA*' { $finalManufacturer = 'Micron' }; 'MTF*' { $finalManufacturer = 'Micron' }; 'NT*' { $finalManufacturer = 'Nanya' }; 'CX*' { $finalManufacturer = 'CXMT' }; 'CM*' { $finalManufacturer = 'Corsair' }; 'CT*' { $finalManufacturer = 'Crucial' }; 'BL*' { $finalManufacturer = 'Crucial' }; 'KD*' { $finalManufacturer = 'Klevv' }; 'KM*' { $finalManufacturer = 'Klevv' }; 'KVR*' { $finalManufacturer = 'Kingston' }; 'KHX*' { $finalManufacturer = 'HyperX' }; 'KF?*C*'{ $finalManufacturer = 'Kingston' }; 'KSM*' { $finalManufacturer = 'Kingston' }; 'F?-*G*'{ $finalManufacturer = 'G.Skill' }; 'AX?U*' { $finalManufacturer = 'ADATA' }; 'TF*' { $finalManufacturer = 'Team Group' }; 'TP*' { $finalManufacturer = 'Team Group' }; 'PV*' { $finalManufacturer = 'Patriot' }; 'PSD*' { $finalManufacturer = 'Patriot' }; 'TS*' { $finalManufacturer = 'Transcend' }; 'SP*' { $finalManufacturer = 'Silicon Power' }; 'LD?*' { $finalManufacturer = 'Lexar' }; 'MD*' { $finalManufacturer = 'PNY' }; 'GS*' { $finalManufacturer = 'GeIL' }; 'OCZ*' { $finalManufacturer = 'OCZ' }; 'MRA*' { $finalManufacturer = 'Mushkin' }; 'MES*' { $finalManufacturer = 'Mushkin' }; 'TV*' { $finalManufacturer = 'V-Color' }; 'TC*' { $finalManufacturer = 'V-Color' }; 'KMVX*' { $finalManufacturer = 'Kingmax' }; 'AU*' { $finalManufacturer = 'Apacer' }; 'EL*' { $finalManufacturer = 'Apacer' }; 'GL*' { $finalManufacturer = 'Gloway' }; 'GW*' { $finalManufacturer = 'Gloway' }; 'AS*' { $finalManufacturer = 'Asgard' }; 'KP*' { $finalManufacturer = 'KingBank' }; 'CVN*' { $finalManufacturer = 'Colorful' }; 'GAL*' { $finalManufacturer = 'GALAX' }; 'GAM*' { $finalManufacturer = 'GALAX' }; 'HT.*' { $finalManufacturer = 'Acer Predator' }; 'ZA*' { $finalManufacturer = 'ZADAK' }; 'BW*' { $finalManufacturer = 'BIWIN' }; 'NTS*' { $finalManufacturer = 'Netac' }; 'TG*' { $finalManufacturer = 'Tigo' }; 'ZT*' { $finalManufacturer = 'Zotac' }; 'I3*' { $finalManufacturer = 'Inno3D' }; 'RM*' { $finalManufacturer = 'Ramaxel' }; 'D3*' { $finalManufacturer = 'Innodisk' }; '??????-???' { if ($partNumber -match '^\d{6}-\d{3}$') { $finalManufacturer = 'HP' } }; 'FRU*' { $finalManufacturer = 'Lenovo' }; 'A???????' { if ($partNumber -match '^A\d{7}$') { $finalManufacturer = 'Dell' } }; default { $finalManufacturer = 'Unknown' }
                        }
                    }
                    $memoryType = switch ($_.SMBIOSMemoryType) { 20 { 'DDR' }; 21 { 'DDR2' }; 22 { 'DDR2 FB-DIMM' }; 24 { 'DDR3' }; 26 { 'DDR4' }; 30 { 'LPDDR4' }; 31 { 'LPDDR4X' }; 34 { 'DDR5' }; 35 { 'LPDDR5' }; 36 { 'LPDDR5X' }; default { ""Unknown ($($_.SMBIOSMemoryType))"" } }
                    $isECC = if ($_.TotalWidth -gt $_.DataWidth) { '是' } else { '否' }
                    [PSCustomObject]@{
                        BankLabel       = $_.BankLabel.Trim()
                        DeviceLocator   = $_.DeviceLocator
                        Manufacturer    = $finalManufacturer
                        PartNumber      = $partNumber
                        Capacity        = ""$([math]::Round($_.Capacity / 1GB, 0)) GB""
                        DeclaredSpeed   = ""$($_.Speed) MT/s""
                        ConfiguredSpeed = ""$($_.ConfiguredClockSpeed) MT/s""
                        IsEcc           = $isECC
                        MemoryType      = $memoryType
                        SerialNumber    = $_.SerialNumber
                    }
                } | Sort-Object -Property BankLabel, DeviceLocator";

            var memoryList = new List<MemoryInfo>();
            try
            {
                var results = await Task.Run(() => Utils.Run(script));
                if (results == null) return memoryList;
                foreach (var result in results)
                {
                    memoryList.Add(new MemoryInfo
                    {
                        BankLabel = result.Properties["BankLabel"]?.Value?.ToString() ?? string.Empty,
                        DeviceLocator = result.Properties["DeviceLocator"]?.Value?.ToString() ?? string.Empty,
                        Manufacturer = result.Properties["Manufacturer"]?.Value?.ToString() ?? "Unknown",
                        PartNumber = result.Properties["PartNumber"]?.Value?.ToString() ?? "Unknown",
                        Capacity = result.Properties["Capacity"]?.Value?.ToString() ?? "0 GB",
                        DeclaredSpeed = result.Properties["DeclaredSpeed"]?.Value?.ToString() ?? "N/A",
                        ConfiguredSpeed = result.Properties["ConfiguredSpeed"]?.Value?.ToString() ?? "N/A",
                        IsEcc = result.Properties["IsEcc"]?.Value?.ToString() ?? "Unknown",
                        MemoryType = result.Properties["MemoryType"]?.Value?.ToString() ?? "Unknown",
                        SerialNumber = result.Properties["SerialNumber"]?.Value?.ToString() ?? "Unknown"
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get host memory info: {ex.Message}");
            }
            return memoryList;
        }

        public async Task<List<VirtualMachineMemoryInfo>> GetVirtualMachinesMemoryAsync()
        {
            string script = @"
        Get-VM | ForEach-Object {
            $vm = $_
            $memoryConfig = Get-VMMemory -VMName $vm.VMName
            
            [PSCustomObject]@{
                VMName               = $vm.VMName
                State                = $vm.State.ToString()
                DynamicMemoryEnabled = $memoryConfig.DynamicMemoryEnabled
                StartupMB            = [long]($memoryConfig.Startup / 1MB)
                MinimumMB            = [long]($memoryConfig.Minimum / 1MB)
                MaximumMB            = [long]($memoryConfig.Maximum / 1MB)
                AssignedMB           = [long]($vm.MemoryAssigned / 1MB)
                DemandMB             = [long]($vm.MemoryDemand / 1MB)
                Status               = $vm.MemoryStatus
                Buffer               = $memoryConfig.Buffer
                Priority             = $memoryConfig.Priority
            }
        } | ConvertTo-Json";

            var vmMemoryList = new List<VirtualMachineMemoryInfo>();
            try
            {
                var results = await Utils.Run2(script);
                if (results != null && results.Any() && results[0] != null)
                {
                    string json = results[0].BaseObject.ToString();
                    if (!json.Trim().StartsWith("["))
                    {
                        json = "[" + json + "]";
                    }
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var parsedList = JsonSerializer.Deserialize<List<VirtualMachineMemoryInfo>>(json, options);
                    if (parsedList != null)
                    {
                        vmMemoryList.AddRange(parsedList);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get VM memory info: {ex.Message}");
            }
            return vmMemoryList;
        }

        public async Task<List<VirtualMachineMemoryInfo>> GetVirtualMachinesMemoryQuickAsync()
        {
            // 这个脚本只获取实时数据，速度更快
            string script = @"
            Get-VM | Where-Object { $_.State -eq 'Running' } | ForEach-Object {
                $vm = $_
                [PSCustomObject]@{
                    VMName      = $vm.VMName
                    AssignedMB  = [long]($vm.MemoryAssigned / 1MB)
                    DemandMB    = [long]($vm.MemoryDemand / 1MB)
                }
            } | ConvertTo-Json -Depth 5 -Compress";

            var vmMemoryList = new List<VirtualMachineMemoryInfo>();
            try
            {
                var results = await Utils.Run2(script);
                if (results != null && results.Any() && results[0] != null)
                {
                    string json = results[0].BaseObject.ToString();
                    if (!json.Trim().StartsWith("["))
                    {
                        json = "[" + json + "]";
                    }
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var parsedList = JsonSerializer.Deserialize<List<VirtualMachineMemoryInfo>>(json, options);
                    if (parsedList != null)
                    {
                        vmMemoryList.AddRange(parsedList);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to get VM memory info: {ex.Message}");
            }
            return vmMemoryList;
        }

        public async Task<bool> SetVmMemoryAsync(VirtualMachineMemoryViewModel vmMemory)
        {
            var scriptBuilder = new StringBuilder();

            if (!vmMemory.DynamicMemoryEnabled)
            {
                long startupBytes = long.Parse(vmMemory.StartupMB) * 1024 * 1024;
                int priority = (int)vmMemory.Priority;
                scriptBuilder.AppendLine($"Set-VMMemory -VMName \"{vmMemory.VMName}\" -DynamicMemoryEnabled $false -StartupBytes {startupBytes} -Priority {priority}");
            }
            else
            {
                long startupBytes = long.Parse(vmMemory.StartupMB) * 1024 * 1024;
                long minimumBytes = long.Parse(vmMemory.MinimumMB) * 1024 * 1024;
                long maximumBytes = long.Parse(vmMemory.MaximumMB) * 1024 * 1024;
                // 关键修正：将 string 类型的 Buffer 解析为 int
                int.TryParse(vmMemory.Buffer, out int buffer);
                int priority = (int)vmMemory.Priority;

                scriptBuilder.AppendLine($"Set-VMMemory -VMName \"{vmMemory.VMName}\" -DynamicMemoryEnabled $true -StartupBytes {startupBytes} -MinimumBytes {minimumBytes} -MaximumBytes {maximumBytes} -Buffer {buffer} -Priority {priority}");
            }

            string script = scriptBuilder.ToString();

            return await Task.Run(() =>
            {
                using (PowerShell ps = PowerShell.Create())
                {
                    ps.AddScript(script);
                    ps.AddParameter("ErrorAction", "Stop");
                    try
                    {
                        ps.Invoke();
                        if (ps.HadErrors)
                        {
                            StringBuilder errorMessages = new StringBuilder();
                            foreach (var error in ps.Streams.Error)
                            {
                                errorMessages.AppendLine(error.ToString());
                            }
                            throw new Exception(errorMessages.ToString());
                        }
                        return true;
                    }
                    catch (Exception ex)
                    {
                        throw new Exception($"应用内存设置时出错: {ex.Message}");
                    }
                }
            });
        }
    }
}