namespace ExHyperV.Models
{
    public class VmCreationParams
    {
        // --- 常规 ---
        public string Name { get; set; } = "NewVM";

        // 标记用户是否手动修改了名称
        public bool IsManualName { get; set; }
        public string Path { get; set; } = string.Empty;
        public string Version { get; set; } = "8.0";
        public int Generation { get; set; } = 2;

        // --- 计算资源 ---
        public int ProcessorCount { get; set; } = 4;
        public long MemoryMb { get; set; } = 4096;
        public bool EnableDynamicMemory { get; set; } = true;

        // --- 安全 (仅第 2 代) ---
        public bool EnableSecureBoot { get; set; } = true;
        public bool EnableTpm { get; set; } = true;
        public string IsolationType { get; set; } = "Disabled"; // Disabled, TrustedLaunch, VBS, SNP, TDX

        // --- 存储 ---
        public int DiskMode { get; set; } = 0; // 0:新建, 1:现有, 2:稍后
        public long DiskSizeGb { get; set; } = 128;
        public string VhdPath { get; set; } = string.Empty; // 对应 NewVmNewDiskPath 或 NewVmExistingDiskPath
        public string IsoPath { get; set; } = string.Empty;

        // --- 网络 ---
        public string SwitchName { get; set; } = string.Empty;
        public bool StartAfterCreation { get; set; } = true;
    }
}