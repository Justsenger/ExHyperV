using ExHyperV.Models;

namespace ExHyperV.Services
{
    public interface IGpuPartitionService
    {
        Task<List<GPUInfo>> GetHostGpusAsync();
        Task<List<VMInfo>> GetVirtualMachinesAsync();
        Task<bool> RemoveGpuPartitionAsync(string vmName, string adapterId);
        Task<List<PartitionInfo>> GetPartitionsFromVmAsync(string vmName);
        Task<string> AddGpuPartitionAsync(string vmName, string gpuInstancePath, string gpuManu, PartitionInfo selectedPartition, string Id);
        Task<bool> IsHyperVModuleAvailableAsync();
        Task<string> GetVmStateAsync(string vmName);
        Task ShutdownVmAsync(string vmName);

    }
}