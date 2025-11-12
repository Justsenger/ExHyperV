using ExHyperV.Models;

namespace ExHyperV.Services
{
    public interface IGpuPartitionService
    {
        Task<List<GPUInfo>> GetHostGpusAsync();
        Task<List<VMInfo>> GetVirtualMachinesAsync();
        Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId);
        Task<List<PartitionInfo>> GetPartitionsFromVmAsync(string vmName);
        Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu, PartitionInfo selectedPartition);
        Task<bool> IsHyperVModuleAvailableAsync();
    }
}