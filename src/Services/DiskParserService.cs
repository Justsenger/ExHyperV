using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ExHyperV.Services
{
    // 注意：这里删除了 PartitionInfo 和 OperatingSystemType 的定义
    // 程序会自动使用你项目中已存在的定义

    public class DiskParserService
    {
        // 常见 GUID 定义
        private static readonly Guid WindowsBasicDataGuid = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
        private static readonly Guid LinuxFileSystemGuid = new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
        private static readonly Guid LinuxLvmGuid = new Guid("E6D6D379-F507-44C2-A23C-238F2A3DF928");

        /// <summary>
        /// 解析磁盘分区
        /// </summary>
        public List<PartitionInfo> GetPartitions(string devicePath, int bytesPerSector = 512)
        {
            var partitions = new List<PartitionInfo>();

            try
            {
                using (var diskStream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var lba0Buffer = new byte[bytesPerSector];
                    diskStream.Read(lba0Buffer, 0, bytesPerSector);

                    // MBR 签名检查
                    if (BitConverter.ToUInt16(lba0Buffer, 510) != 0xAA55)
                    {
                        return partitions;
                    }

                    if (IsGptProtectiveMbr(lba0Buffer))
                    {
                        partitions.AddRange(ParseGptPartitions(diskStream, bytesPerSector));
                    }
                    else
                    {
                        partitions.AddRange(ParseMbrPartitions(diskStream, lba0Buffer, bytesPerSector));
                    }
                }
            }
            catch (Exception)
            {
                // 忽略读取错误，返回空列表
                return new List<PartitionInfo>();
            }

            // 过滤掉小于 1GB 的分区
            const long oneGbInBytes = 1024L * 1024 * 1024;

            return partitions
                .Where(p => p.SizeInBytes >= oneGbInBytes)
                .Where(p => p.OsType == OperatingSystemType.Windows || p.OsType == OperatingSystemType.Linux)
                .ToList();
        }

        private bool IsGptProtectiveMbr(byte[] mbrBuffer)
        {
            for (int i = 0; i < 4; i++)
            {
                int offset = 446 + (i * 16);
                if (mbrBuffer[offset + 4] == 0xEE) return true;
            }
            return false;
        }

        private IEnumerable<PartitionInfo> ParseGptPartitions(FileStream diskStream, int bytesPerSector)
        {
            diskStream.Seek(bytesPerSector, SeekOrigin.Begin);

            var gptHeaderBuffer = new byte[bytesPerSector];
            diskStream.Read(gptHeaderBuffer, 0, bytesPerSector);

            if (BitConverter.ToUInt64(gptHeaderBuffer, 0) != 0x5452415020494645)
            {
                yield break;
            }

            ulong partitionArrayStartLba = BitConverter.ToUInt64(gptHeaderBuffer, 72);
            uint partitionEntryCount = BitConverter.ToUInt32(gptHeaderBuffer, 80);
            uint partitionEntrySize = BitConverter.ToUInt32(gptHeaderBuffer, 84);

            long tableSize = partitionEntryCount * partitionEntrySize;
            byte[] tableBuffer = new byte[tableSize];

            diskStream.Seek((long)(partitionArrayStartLba * (ulong)bytesPerSector), SeekOrigin.Begin);
            diskStream.Read(tableBuffer, 0, (int)tableSize);

            using (var ms = new MemoryStream(tableBuffer))
            using (var reader = new BinaryReader(ms))
            {
                for (int i = 0; i < partitionEntryCount; i++)
                {
                    var entryBuffer = reader.ReadBytes((int)partitionEntrySize);
                    var typeGuid = new Guid(entryBuffer.Take(16).ToArray());

                    if (typeGuid == Guid.Empty) continue;

                    ulong firstLba = BitConverter.ToUInt64(entryBuffer, 32);
                    ulong lastLba = BitConverter.ToUInt64(entryBuffer, 40);

                    ulong startOffset = firstLba * (ulong)bytesPerSector;
                    ulong size = (lastLba - firstLba + 1) * (ulong)bytesPerSector;

                    var (osType, desc) = GetOsTypeFromGptGuid(typeGuid);

                    yield return new PartitionInfo(i + 1, startOffset, size, osType, desc);
                }
            }
        }

        private IEnumerable<PartitionInfo> ParseMbrPartitions(FileStream diskStream, byte[] mbrBuffer, int bytesPerSector)
        {
            var partitions = new List<PartitionInfo>();
            int logicalPartitionNumber = 5;
            ulong extendedPartitionStartOffset = 0;

            for (int i = 0; i < 4; i++)
            {
                int offset = 446 + (i * 16);
                byte systemId = mbrBuffer[offset + 4];

                if (systemId == 0x00) continue;

                uint startSector = BitConverter.ToUInt32(mbrBuffer, offset + 8);
                uint totalSectors = BitConverter.ToUInt32(mbrBuffer, offset + 12);

                ulong startOffset = (ulong)startSector * (ulong)bytesPerSector;
                ulong size = (ulong)totalSectors * (ulong)bytesPerSector;

                if (systemId == 0x05 || systemId == 0x0F)
                {
                    extendedPartitionStartOffset = startOffset;
                    continue;
                }

                var (osType, desc) = GetOsTypeFromMbrId(systemId);
                partitions.Add(new PartitionInfo(i + 1, startOffset, size, osType, desc));
            }

            if (extendedPartitionStartOffset > 0)
            {
                partitions.AddRange(ParseLogicalPartitions(diskStream, extendedPartitionStartOffset, bytesPerSector, ref logicalPartitionNumber));
            }

            return partitions;
        }

        private IEnumerable<PartitionInfo> ParseLogicalPartitions(FileStream diskStream, ulong currentEbrOffset, int bytesPerSector, ref int partitionNumber)
        {
            var partitions = new List<PartitionInfo>();
            var ebrBuffer = new byte[bytesPerSector];
            ulong extendedPartitionBaseOffset = currentEbrOffset;

            while (currentEbrOffset > 0)
            {
                diskStream.Seek((long)currentEbrOffset, SeekOrigin.Begin);
                diskStream.Read(ebrBuffer, 0, bytesPerSector);

                if (BitConverter.ToUInt16(ebrBuffer, 510) != 0xAA55) break;

                int entry1Offset = 446;
                byte systemId1 = ebrBuffer[entry1Offset + 4];
                if (systemId1 != 0x00)
                {
                    uint startSectorRelative = BitConverter.ToUInt32(ebrBuffer, entry1Offset + 8);
                    uint totalSectors = BitConverter.ToUInt32(ebrBuffer, entry1Offset + 12);

                    ulong startOffset = currentEbrOffset + ((ulong)startSectorRelative * (ulong)bytesPerSector);
                    ulong size = (ulong)totalSectors * (ulong)bytesPerSector;

                    var (osType, desc) = GetOsTypeFromMbrId(systemId1);
                    partitions.Add(new PartitionInfo(partitionNumber++, startOffset, size, osType, desc));
                }

                int entry2Offset = 446 + 16;
                byte systemId2 = ebrBuffer[entry2Offset + 4];
                if (systemId2 == 0x05 || systemId2 == 0x0F)
                {
                    uint nextEbrSectorRelative = BitConverter.ToUInt32(ebrBuffer, entry2Offset + 8);
                    currentEbrOffset = extendedPartitionBaseOffset + ((ulong)nextEbrSectorRelative * (ulong)bytesPerSector);
                }
                else
                {
                    currentEbrOffset = 0;
                }
            }
            return partitions;
        }

        private (OperatingSystemType, string) GetOsTypeFromMbrId(byte systemId)
        {
            switch (systemId)
            {
                case 0x07: return (OperatingSystemType.Windows, "Windows");
                case 0x0B:
                case 0x0C: return (OperatingSystemType.Windows, "Windows");
                case 0x83: return (OperatingSystemType.Linux, "Linux");
                case 0x8E: return (OperatingSystemType.Linux, "Linux LVM");
                default: return (OperatingSystemType.Other, "Other");
            }
        }

        private (OperatingSystemType, string) GetOsTypeFromGptGuid(Guid typeGuid)
        {
            if (typeGuid == WindowsBasicDataGuid) return (OperatingSystemType.Windows, "Windows");
            if (typeGuid == LinuxFileSystemGuid) return (OperatingSystemType.Linux, "Linux");
            if (typeGuid == LinuxLvmGuid) return (OperatingSystemType.Linux, "Linux");

            return (OperatingSystemType.Other, "Other");
        }
    }
}