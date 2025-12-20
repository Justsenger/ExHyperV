using ExHyperV.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public interface IMemoryService
    {
        Task<List<VirtualMachineMemoryInfo>> GetVirtualMachinesMemoryConfigurationAsync();
        Task<List<VirtualMachineMemoryInfo>> GetVirtualMachinesMemoryUsageAsync();
        Task<(bool Success, string Message)> SetVmMemoryAsync(VirtualMachineMemoryInfo vmMemory);
    }
}