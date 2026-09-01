namespace ExHyperV.Models
{
    public class VmCreationParams
    {
        public string Name { get; set; } = "NewVM";

        public string Path { get; set; } = string.Empty;
        public string Version { get; set; } = "8.0";
        public int Generation { get; set; } = 2;

        public int ProcessorCount { get; set; } = 4;
        public long MemoryMb { get; set; } = 4096;
        public bool EnableDynamicMemory { get; set; } = true;

        public bool EnableSecureBoot { get; set; } = true;
        public bool EnableTpm { get; set; } = true;
        public string IsolationType { get; set; } = "Disabled"; // Disabled, TrustedLaunch, VBS, SNP, TDX, RME, OpenHCL
        public string OpenHclIgvmPath { get; set; } = string.Empty;

        public int DiskMode { get; set; } = 0; // 0:新建, 1:现有, 2:稍后
        public long DiskSizeGb { get; set; } = 128;
        public string VhdPath { get; set; } = string.Empty; // 对应 NewVmNewDiskPath 或 NewVmExistingDiskPath
        public bool CreateDifferencingDisk { get; set; }
        public string DifferencingDiskRoot { get; set; } = string.Empty;
        public string IsoPath { get; set; } = string.Empty;

        public string SwitchName { get; set; } = string.Empty;
        public bool StartAfterCreation { get; set; } = true;
    }
}
