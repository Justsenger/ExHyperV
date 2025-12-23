using ExHyperV.Models;
using ExHyperV.Tools;
using System.Management.Automation;
using System.Text;
using System.Text.RegularExpressions;

namespace ExHyperV.Services
{
    public class MemoryService : IMemoryService
    {
        public async Task<List<VirtualMachineMemoryInfo>> GetVirtualMachinesMemoryConfigurationAsync()
        {
            string script = @"
                Get-VM | ForEach-Object {
                    $vm = $_
                    $memoryConfig = Get-VMMemory -VMName $vm.VMName
                    
                    [PSCustomObject]@{
                        VMName               = $vm.VMName
                        State                = $vm.State.ToString()
                        DynamicMemoryEnabled = [bool]$memoryConfig.DynamicMemoryEnabled
                        StartupMB            = [long]($memoryConfig.Startup / 1MB)
                        MinimumMB            = [long]($memoryConfig.Minimum / 1MB)
                        MaximumMB            = [long]($memoryConfig.Maximum / 1MB)
                        AssignedMB           = [long]($vm.MemoryAssigned / 1MB)
                        DemandMB             = [long]($vm.MemoryDemand / 1MB)
                        Status               = $vm.MemoryStatus
                        Buffer               = [int]$memoryConfig.Buffer
                        Priority             = [int]$memoryConfig.Priority
                    }
                }";

            var results = await Utils.Run2(script);
            return results?.Select(ParsePsoToModel).ToList() ?? new List<VirtualMachineMemoryInfo>();
        }

        public async Task<List<VirtualMachineMemoryInfo>> GetVirtualMachinesMemoryUsageAsync()
        {
            string script = @"
                Get-VM | Where-Object { $_.State -eq 'Running' } | ForEach-Object {
                    $vm = $_
                    [PSCustomObject]@{
                        VMName      = $vm.VMName
                        State       = $vm.State.ToString()
                        AssignedMB  = [long]($vm.MemoryAssigned / 1MB)
                        DemandMB    = [long]($vm.MemoryDemand / 1MB)
                        Status      = $vm.MemoryStatus
                    }
                }";

            var results = await Utils.Run2(script);
            return results?.Select(ParsePsoToModel).ToList() ?? new List<VirtualMachineMemoryInfo>();
        }

        public async Task<(bool Success, string Message)> SetVmMemoryAsync(VirtualMachineMemoryInfo vmMemory)
        {
            try
            {
                var sb = new StringBuilder();
                string escapedName = vmMemory.VMName.Replace("'", "''");
                long startupBytes = vmMemory.StartupMB * 1024 * 1024;

                if (!vmMemory.DynamicMemoryEnabled)
                {
                    sb.Append($"Set-VMMemory -VMName '{escapedName}' -DynamicMemoryEnabled $false -StartupBytes {startupBytes} -Priority {vmMemory.Priority} -ErrorAction Stop");
                }
                else
                {
                    long minBytes = vmMemory.MinimumMB * 1024 * 1024;
                    long maxBytes = vmMemory.MaximumMB * 1024 * 1024;
                    sb.Append($"Set-VMMemory -VMName '{escapedName}' -DynamicMemoryEnabled $true -StartupBytes {startupBytes} -MinimumBytes {minBytes} -MaximumBytes {maxBytes} -Buffer {vmMemory.Buffer} -Priority {vmMemory.Priority} -ErrorAction Stop");
                }

                await Utils.Run2(sb.ToString());
                return (true, ExHyperV.Properties.Resources.SettingsSavedSuccessfully);
            }
            catch (Exception ex)
            {
                return (false, GetFriendlyErrorMessage(ex.Message));
            }
        }

        private string GetFriendlyErrorMessage(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return ExHyperV.Properties.Resources.UnknownError;
            string cleanMsg = rawMessage.Trim();
            cleanMsg = Regex.Replace(cleanMsg, @"[\(\（].*?ID\s+[a-fA-F0-9-]{36}.*?[\)\）]", "");
            cleanMsg = cleanMsg.Replace("\r", "").Replace("\n", " ");
            var parts = cleanMsg.Split(new[] { '。', '.' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim())
                                .Where(s => !string.IsNullOrWhiteSpace(s))
                                .ToList();
            if (parts.Count >= 2)
            {
                var lastPart = parts.Last();
                if (lastPart.Length > 2) return lastPart + "。";
            }
            return cleanMsg;
        }

        private VirtualMachineMemoryInfo ParsePsoToModel(PSObject pso)
        {
            if (pso == null) return null;
            var model = new VirtualMachineMemoryInfo();
            model.VMName = pso.Properties["VMName"]?.Value?.ToString() ?? string.Empty;
            model.State = pso.Properties["State"]?.Value?.ToString() ?? string.Empty;
            model.Status = pso.Properties["Status"]?.Value?.ToString() ?? string.Empty;
            if (pso.Properties["DynamicMemoryEnabled"]?.Value != null)
                model.DynamicMemoryEnabled = Convert.ToBoolean(pso.Properties["DynamicMemoryEnabled"].Value);
            long GetLong(string name) => pso.Properties[name]?.Value != null ? Convert.ToInt64(pso.Properties[name].Value) : 0L;
            int GetInt(string name) => pso.Properties[name]?.Value != null ? Convert.ToInt32(pso.Properties[name].Value) : 0;
            model.StartupMB = GetLong("StartupMB");
            model.MinimumMB = GetLong("MinimumMB");
            model.MaximumMB = GetLong("MaximumMB");
            model.AssignedMB = GetLong("AssignedMB");
            model.DemandMB = GetLong("DemandMB");
            model.Buffer = GetInt("Buffer");
            model.Priority = GetInt("Priority");
            return model;
        }
    }
}