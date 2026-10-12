namespace ExHyperV.Models
{
    public class VmCreationParams
    {
        public string Name { get; set; } = "NewVM";

        public string Path { get; set; } = string.Empty;
        // 仅新增磁盘跟随配置目录；已有磁盘不移动。服务按最终 VM 名解析，兼容批量及重名避让。
        public bool StoreNewDisksWithVm { get; set; }

        internal string GetNewDiskDirectory(string finalVmName)
        {
            string vmFolder = System.IO.Path.Combine(Path, finalVmName);
            if (StoreNewDisksWithVm)
                return System.IO.Path.Combine(vmFolder, "Virtual Hard Disks");
            if (DiskMode == 0)
                return string.IsNullOrWhiteSpace(VhdPath) ? vmFolder : VhdPath;
            string root = string.IsNullOrWhiteSpace(DifferencingDiskRoot) ? vmFolder : DifferencingDiskRoot;
            return System.IO.Path.Combine(root, finalVmName);
        }
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
