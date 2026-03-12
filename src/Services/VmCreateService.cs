using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class VmCreateService
    {
        public async Task<List<string>> GetSupportedVersionsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var results = Utils.Run("Get-VMHostSupportedVersion | Select-Object -ExpandProperty Version");
                    if (results != null && results.Count > 0)
                    {
                        return results.Select(r => r.ToString()).OrderByDescending(v => double.Parse(v)).ToList();
                    }
                }
                catch { }
                return new List<string> { "11.0", "10.0", "9.0" };
            });
        }

        public async Task<(bool Supported, List<string> Types)> GetIsolationSupportAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var check = Utils.Run("(Get-Command New-VM).Parameters.ContainsKey('GuestStateIsolationType')");
                    if (check != null && check.Count > 0 && check[0].ToString().ToLower() == "true")
                    {
                        var types = Utils.Run("((Get-Command New-VM).Parameters['GuestStateIsolationType'].Attributes | Where-Object { $_.ValidValues }).ValidValues");
                        if (types != null && types.Count > 0) return (true, types.Select(t => t.ToString()).ToList());
                    }
                }
                catch { }
                return (false, new List<string> { "Disabled" });
            });
        }

        public async Task<(string DefaultVmPath, string DefaultVhdPath)> GetHostDefaultPathsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    var vmPath = Utils.Run("(Get-VMHost).VirtualMachinePath");
                    return (vmPath?.FirstOrDefault()?.ToString() ?? @"C:\ProgramData\Microsoft\Windows\Hyper-V", "");
                }
                catch { return (@"C:\ProgramData\Microsoft\Windows\Hyper-V", ""); }
            });
        }

        public async Task<(bool Success, string Message)> CreateVirtualMachineAsync(VmCreationParams p)
        {
            string finalVmName = p.Name;

            return await Task.Run(() =>
            {
                try
                {
                    string vmHomeFolder = Path.Combine(p.Path, finalVmName);
                    if (!Directory.Exists(vmHomeFolder)) Directory.CreateDirectory(vmHomeFolder);

                    if (p.DiskMode == 0) p.VhdPath = Path.Combine(vmHomeFolder, $"{finalVmName}.vhdx");

                    string switchParam = string.Empty;

                    if (!string.IsNullOrWhiteSpace(p.SwitchName) &&
                        p.SwitchName != ExHyperV.Properties.Resources.none)
                    {
                        switchParam = $"-SwitchName '{p.SwitchName}'";
                    }

                    long memoryBytes = p.MemoryMb * 1024 * 1024;
                    string diskParam = p.DiskMode switch
                    {
                        0 => $"-NewVHDPath '{p.VhdPath}' -NewVHDSizeBytes {p.DiskSizeGb}GB",
                        1 => $"-VHDPath '{p.VhdPath}'",
                        _ => "-NoVHD"
                    };
                    // 使用 -Force 参数来强制跳过预发行版本(如 255.0)的警告和确认提示
                    string createScript = $"New-VM -Name '{finalVmName}' -MemoryStartupBytes {memoryBytes} -Generation {p.Generation} -Path '{p.Path}' -Version {p.Version} {switchParam} {diskParam} -Force -ErrorAction Stop";
                    double.TryParse(p.Version, out double ver);
                    if (p.Generation == 2 && ver >= 10.0 && p.IsolationType != "Disabled")
                    {
                        createScript += $" -GuestStateIsolationType {p.IsolationType}";
                    }

                    Utils.Run(createScript);

                    Utils.Run($"Set-VMProcessor -VMName '{finalVmName}' -Count {p.ProcessorCount} -ErrorAction Stop");
                    Utils.Run($"Set-VMMemory -VMName '{finalVmName}' -DynamicMemoryEnabled {(p.EnableDynamicMemory ? "$true" : "$false")} -ErrorAction Stop");

                    if (p.Generation == 2)
                    {
                        if (p.EnableTpm)
                        {
                            Utils.Run($"Set-VMSecurity -VMName '{finalVmName}' -EncryptStateAndVmMigrationTraffic $true -ErrorAction Stop");
                            Utils.Run($"Set-VMKeyProtector -VMName '{finalVmName}' -NewLocalKeyProtector -ErrorAction Stop");
                            Utils.Run($"Enable-VMTPM -VMName '{finalVmName}' -ErrorAction Stop");
                        }
                        string secureBootState = p.EnableSecureBoot ? "On" : "Off";
                        Utils.Run($"Set-VMFirmware -VMName '{finalVmName}' -EnableSecureBoot {secureBootState} -ErrorAction Stop");
                    }

                    if (!string.IsNullOrWhiteSpace(p.IsoPath) && File.Exists(p.IsoPath))
                    {
                        Utils.Run($"if (!(Get-VMDvdDrive -VMName '{finalVmName}')) {{ Add-VMDvdDrive -VMName '{finalVmName}' }}");
                        Utils.Run($"Set-VMDvdDrive -VMName '{finalVmName}' -Path '{p.IsoPath}'");
                        if (p.Generation == 2) Utils.Run($"$d = Get-VMDvdDrive -VMName '{finalVmName}'; Set-VMFirmware -VMName '{finalVmName}' -FirstBootDevice $d");
                    }

                    if (p.StartAfterCreation) Utils.Run($"Start-VM -Name '{finalVmName}' -ErrorAction Stop");

                    return (true, finalVmName);
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            });
        }
    }
}