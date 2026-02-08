using System;

namespace ExHyperV.Models // <--- ✅ 必须加上这一行
{
    public enum OperatingSystemType
    {
        Unknown,
        Windows,
        Linux,
        EFI,
        Other
    }

    public class PartitionInfo
    {
        public int PartitionNumber { get; }
        public ulong StartOffset { get; }
        public ulong SizeInBytes { get; }
        public OperatingSystemType OsType { get; }
        public string TypeDescription { get; }

        public PartitionInfo(int number, ulong startOffset, ulong size, OperatingSystemType osType, string typeDescription)
        {
            PartitionNumber = number;
            StartOffset = startOffset;
            SizeInBytes = size;
            OsType = osType;
            TypeDescription = typeDescription;
        }

        public string DisplayName => $"Partition {PartitionNumber} ({SizeInGb:F2} GB) - {TypeDescription}";
        public double SizeInGb => SizeInBytes / (1024.0 * 1024.0 * 1024.0);

        public string IconPath
        {
            get
            {
                switch (OsType)
                {
                    case OperatingSystemType.Windows:
                        return "pack://application:,,,/Assets/Microsoft.png";
                    case OperatingSystemType.Linux:
                        return "pack://application:,,,/Assets/Linux.png";
                    default:
                        return null;
                }
            }
        }
    }
}