// /Services/IGpuPartitionService.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public interface IGpuPartitionService
    {
        Task<List<GPUInfo>> GetHostGpusAsync();
        Task<List<VMInfo>> GetVirtualMachinesAsync();
        Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId);

        // <<<--- 这是修改过的一行 --->>>
        Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu);
    }
}