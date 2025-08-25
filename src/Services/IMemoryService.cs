using ExHyperV.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public interface IMemoryService
    {
        Task<List<MemoryInfo>> GetHostMemoryAsync();
        // Task<List<VirtualMachineMemory>> GetVirtualMachinesMemoryAsync();
    }
}