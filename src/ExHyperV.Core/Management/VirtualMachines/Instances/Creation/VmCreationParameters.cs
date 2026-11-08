namespace ExHyperV.Models
{
    /// <summary>创建虚拟机的可变输入。服务在开始时复制输入并按最终名称解析目录；设置此模型本身不会创建资源。</summary>
    public class VmCreationParameters
    {
        internal VmCreationParameters Copy() => (VmCreationParameters)MemberwiseClone();

        internal string? GetDiskPath(string actualName) => DiskMode switch
        {
            0 => System.IO.Path.GetFullPath(System.IO.Path.Combine(GetNewDiskDirectory(actualName), actualName + ".vhdx")),
            1 when CreateDifferencingDisk => System.IO.Path.GetFullPath(System.IO.Path.Combine(GetNewDiskDirectory(actualName), actualName + ".vhdx")),
            1 => System.IO.Path.GetFullPath(VhdPath),
            _ => null
        };

        /// <summary>调用方指定的虚拟机名称，默认 NewVM；名称或目录冲突时自动避让，实际名称和提示随创建结果返回。</summary>
        public string Name { get; set; } = "NewVM";

        /// <summary>虚拟机配置文件的根目录，服务在其下创建最终虚拟机名称目录。</summary>
        public string Path { get; set; } = string.Empty;
        // 仅新增磁盘跟随配置目录；已有磁盘不移动。服务按冲突避让后的实际名称解析。
        /// <summary>是否将新磁盘放入虚拟机目录下的 Virtual Hard Disks；不会移动已有磁盘。</summary>
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
        /// <summary>请求的 Hyper-V 配置版本字符串，默认 8.0。</summary>
        public string Version { get; set; } = "8.0";
        /// <summary>虚拟机代数，1 或 2；默认 2。</summary>
        public int Generation { get; set; } = 2;

        /// <summary>虚拟处理器数量，默认 4。</summary>
        public int ProcessorCount { get; set; } = 4;
        /// <summary>启动内存，单位为 MiB（1024² 字节），默认 4096。</summary>
        public long MemoryMiB { get; set; } = 4096;
        /// <summary>是否启用动态内存，默认 true。</summary>
        public bool EnableDynamicMemory { get; set; } = true;

        /// <summary>是否请求启用安全启动，默认 true，实际适用性取决于代数和隔离模式。</summary>
        public bool EnableSecureBoot { get; set; } = true;
        /// <summary>是否请求启用虚拟 TPM，默认 true。</summary>
        public bool EnableTpm { get; set; } = true;
        /// <summary>隔离模式标识：Disabled、TrustedLaunch、VBS、SNP、TDX、RME 或 OpenHCL；默认 Disabled。</summary>
        public string IsolationType { get; set; } = "Disabled"; // Disabled, TrustedLaunch, VBS, SNP, TDX, RME, OpenHCL
        /// <summary>OpenHCL 模式使用的 IGVM 文件路径；其他模式可为空。</summary>
        public string OpenHclIgvmPath { get; set; } = string.Empty;

        /// <summary>磁盘来源模式：0 新建、1 使用已有磁盘、2 稍后添加；默认 0。</summary>
        public int DiskMode { get; set; } = 0; // 0:新建, 1:现有, 2:稍后
        /// <summary>新建虚拟磁盘的容量，单位为 GiB（1024³ 字节），默认 128。</summary>
        public long DiskSizeGiB { get; set; } = 128;
        /// <summary>新建模式下为磁盘目录，可留空以使用虚拟机目录；已有磁盘模式下为磁盘文件路径。</summary>
        public string VhdPath { get; set; } = string.Empty; // 对应 NewVmNewDiskPath 或 NewVmExistingDiskPath
        /// <summary>使用已有磁盘时，是否基于该磁盘创建差分盘；由调用方明确选择，Core 不根据创建次数改变此选项。</summary>
        public bool CreateDifferencingDisk { get; set; }
        /// <summary>新差分盘的根目录；空值按虚拟机目录解析，最终还会追加虚拟机名称子目录。</summary>
        public string DifferencingDiskRoot { get; set; } = string.Empty;
        /// <summary>要挂载的 ISO 文件路径；空字符串表示不挂载安装介质。</summary>
        public string IsoPath { get; set; } = string.Empty;

        /// <summary>要连接的虚拟交换机 GUID；null 表示不连接，Guid.Empty 无效。名称只用于显示。</summary>
        public Guid? SwitchId { get; set; }
    }
}
