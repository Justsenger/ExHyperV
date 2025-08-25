// In Services/MemoryService.cs
using ExHyperV.Models;
using ExHyperV.Tools; // 假设您的 Utils 类在这个命名空间下
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class MemoryService : IMemoryService
    {
        public async Task<List<MemoryInfo>> GetHostMemoryAsync()
        {
            // PowerShell脚本已更新，移除了末尾的 ConvertTo-Json
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
                // 使用您项目中的 Utils.Run 方法执行脚本
                var results = await Task.Run(() => Utils.Run(script));

                if (results == null)
                {
                    return memoryList; // 如果执行失败或没有结果，返回空列表
                }

                foreach (var result in results)
                {
                    var memoryInfo = new MemoryInfo
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
                    };
                    memoryList.Add(memoryInfo);
                }
            }
            catch (Exception ex)
            {
                // 可以添加日志记录等错误处理
                Console.WriteLine($"Failed to get host memory info: {ex.Message}");
            }

            return memoryList;
        }
    }
}