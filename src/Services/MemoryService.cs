using System.Management.Automation;
using System.Text;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.ViewModels;

namespace ExHyperV.Services
{
    public class MemoryService : IMemoryService
    {
        // ▼▼▼▼▼ 【已删除】GetHostMemoryAsync() 方法 ▼▼▼▼▼
        // 整个获取宿主机物理内存的方法已被移除。
        // ▲▲▲▲▲ 【已删除】GetHostMemoryAsync() 方法 ▲▲▲▲▲

        public async Task<List<VirtualMachineMemoryInfo>> GetVirtualMachinesMemoryAsync()
        {
            string script = @"
        Hyper-V\Get-VM | ForEach-Object {
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
        }";

            var vmMemoryList = new List<VirtualMachineMemoryInfo>();
            try
            {
                // 优化：直接使用更高效的 Run2
                var results = await Utils.Run2(script);
                if (results == null) return vmMemoryList;

                foreach (var result in results)
                {
                    vmMemoryList.Add(new VirtualMachineMemoryInfo
                    {
                        VMName = result.Properties["VMName"]?.Value?.ToString() ?? string.Empty,
                        State = result.Properties["State"]?.Value?.ToString() ?? string.Empty,
                        DynamicMemoryEnabled = (bool)(result.Properties["DynamicMemoryEnabled"]?.Value ?? false),
                        StartupMB = (long)(result.Properties["StartupMB"]?.Value ?? 0L),
                        MinimumMB = (long)(result.Properties["MinimumMB"]?.Value ?? 0L),
                        MaximumMB = (long)(result.Properties["MaximumMB"]?.Value ?? 0L),
                        AssignedMB = (long)(result.Properties["AssignedMB"]?.Value ?? 0L),
                        DemandMB = (long)(result.Properties["DemandMB"]?.Value ?? 0L),
                        Status = result.Properties["Status"]?.Value?.ToString() ?? string.Empty,
                        Buffer = (int)(result.Properties["Buffer"]?.Value ?? 0),
                        Priority = (int)(result.Properties["Priority"]?.Value ?? 0)
                    });
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
            string script = @"
            Hyper-V\Get-VM | Where-Object { $_.State -eq 'Running' } | ForEach-Object {
                $vm = $_
                [PSCustomObject]@{
                    VMName      = $vm.VMName
                    AssignedMB  = [long]($vm.MemoryAssigned / 1MB)
                    DemandMB    = [long]($vm.MemoryDemand / 1MB)
                }
            }";

            var vmMemoryList = new List<VirtualMachineMemoryInfo>();
            var results = await Utils.Run2(script);
            if (results == null) return vmMemoryList;
            foreach (var result in results)
            {
                vmMemoryList.Add(new VirtualMachineMemoryInfo
                {
                    VMName = result.Properties["VMName"]?.Value?.ToString() ?? string.Empty,
                    AssignedMB = (long)(result.Properties["AssignedMB"]?.Value ?? 0L),
                    DemandMB = (long)(result.Properties["DemandMB"]?.Value ?? 0L)
                });
            }
            return vmMemoryList;
        }


        public async Task<bool> SetVmMemoryAsync(VirtualMachineMemoryViewModel vmMemory)
        {
            var scriptBuilder = new StringBuilder();

            // 将字符串解析移到这里，增加健壮性
            if (!long.TryParse(vmMemory.StartupMB, out long startupMB) ||
                !long.TryParse(vmMemory.MinimumMB, out long minimumMB) ||
                !long.TryParse(vmMemory.MaximumMB, out long maximumMB) ||
                !int.TryParse(vmMemory.Buffer, out int buffer))
            {
                // 如果解析失败，可以抛出异常或返回 false
                throw new ArgumentException("Invalid memory value format.");
            }

            long startupBytes = startupMB * 1024 * 1024;
            int priority = (int)vmMemory.Priority;

            if (!vmMemory.DynamicMemoryEnabled)
            {
                scriptBuilder.AppendLine($"Set-VMMemory -VMName \"{vmMemory.VMName}\" -DynamicMemoryEnabled $false -StartupBytes {startupBytes} -Priority {priority}");
            }
            else
            {
                long minimumBytes = minimumMB * 1024 * 1024;
                long maximumBytes = maximumMB * 1024 * 1024;
                scriptBuilder.AppendLine($"Set-VMMemory -VMName \"{vmMemory.VMName}\" -DynamicMemoryEnabled $true -StartupBytes {startupBytes} -MinimumBytes {minimumBytes} -MaximumBytes {maximumBytes} -Buffer {buffer} -Priority {priority}");
            }

            string script = scriptBuilder.ToString();

            // 优化：使用统一的异步执行器 Run2
            try
            {
                await Utils.Run2(script);
                return true;
            }
            catch (Exception ex)
            {
                // 可以记录日志或向上层抛出更具体的异常
                Console.WriteLine($"Failed to set VM memory: {ex.Message}");
                // throw; // 如果希望上层能捕获到详细的 PowerShell 错误，可以重新抛出
                return false;
            }
        }
    }
}