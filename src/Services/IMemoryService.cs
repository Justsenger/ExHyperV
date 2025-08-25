using System.Collections.Generic;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.ViewModels;

namespace ExHyperV.Services
{
    public interface IMemoryService
    {
        Task<List<MemoryInfo>> GetHostMemoryAsync();
        // Task<List<VirtualMachineMemory>> GetVirtualMachinesMemoryAsync();
        Task<List<VirtualMachineMemoryInfo>> GetVirtualMachinesMemoryAsync();
        Task<bool> SetVmMemoryAsync(VirtualMachineMemoryViewModel vmMemory); // 添加新方法
    }
}