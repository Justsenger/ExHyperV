using ExHyperV.Models;

namespace ExHyperV.Services
{
    public interface IGpuPartitionService
    {
        Task<List<GPUInfo>> GetHostGpusAsync();
        Task<List<VMInfo>> GetVirtualMachinesAsync();
        Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId);
        Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu);
        Task<bool> IsHyperVModuleAvailableAsync();
    }
}