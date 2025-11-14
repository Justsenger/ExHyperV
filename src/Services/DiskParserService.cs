using System.IO;

namespace ExHyperV.Services
{
    public class DiskParserService
    {
        private static readonly Guid WindowsBasicDataGuid = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
        private static readonly Guid LinuxFileSystemGuid = new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
        private static readonly Guid EfiSystemGuid = new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

        public List<PartitionInfo> GetPartitions(string devicePath)
        {
            var partitions = new List<PartitionInfo>();

            using (var diskStream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var mbrBuffer = new byte[512];
                diskStream.Read(mbrBuffer, 0, 512);

                if (BitConverter.ToUInt16(mbrBuffer, 510) != 0xAA55)
                {
                    return partitions;
                }

                if (IsGptProtectiveMbr(mbrBuffer))
                {
                    partitions.AddRange(ParseGptPartitions(diskStream));
                }
                else
                {
                    partitions.AddRange(ParseMbrPartitions(diskStream, mbrBuffer));
                }
            }

            const long oneGbInBytes = 1024L * 1024 * 1024;
            var filteredPartitions = partitions
                .Where(p => p.SizeInBytes >= oneGbInBytes)
                .ToList();

            return filteredPartitions;
        }

        private bool IsGptProtectiveMbr(byte[] mbrBuffer)
        {
            for (int i = 0; i < 4; i++)
            {
                int offset = 446 + (i * 16);
                if (mbrBuffer[offset + 4] == 0xEE)
                {
                    return true;
                }
            }
            return false;
        }

        private IEnumerable<PartitionInfo> ParseMbrPartitions(FileStream diskStream, byte[] mbrBuffer)
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
                ulong startOffset = (ulong)startSector * 512;
                ulong size = (ulong)totalSectors * 512;

                if (systemId == 0x05 || systemId == 0x0F)
                {
                    extendedPartitionStartOffset = startOffset;
                    continue;
                }

                var (osType, typeDescription) = GetOsTypeFromMbrId(systemId);
                partitions.Add(new PartitionInfo(i + 1, startOffset, size, osType, typeDescription));
            }

            if (extendedPartitionStartOffset > 0)
            {
                partitions.AddRange(ParseLogicalPartitions(diskStream, extendedPartitionStartOffset, ref logicalPartitionNumber));
            }

            return partitions;
        }

        private IEnumerable<PartitionInfo> ParseLogicalPartitions(FileStream diskStream, ulong currentEbrOffset, ref int partitionNumber)
        {
            var partitions = new List<PartitionInfo>();
            var ebrBuffer = new byte[512];
            ulong extendedPartitionBaseOffset = currentEbrOffset;

            while (currentEbrOffset > 0)
            {
                diskStream.Seek((long)currentEbrOffset, SeekOrigin.Begin);
                diskStream.Read(ebrBuffer, 0, 512);

                if (BitConverter.ToUInt16(ebrBuffer, 510) != 0xAA55)
                {
                    break;
                }

                int entry1Offset = 446;
                byte systemId1 = ebrBuffer[entry1Offset + 4];
                if (systemId1 != 0x00)
                {
                    uint startSectorRelative = BitConverter.ToUInt32(ebrBuffer, entry1Offset + 8);
                    uint totalSectors = BitConverter.ToUInt32(ebrBuffer, entry1Offset + 12);

                    ulong startOffset = currentEbrOffset + ((ulong)startSectorRelative * 512);
                    ulong size = (ulong)totalSectors * 512;

                    var (osType, typeDescription) = GetOsTypeFromMbrId(systemId1);
                    partitions.Add(new PartitionInfo(partitionNumber++, startOffset, size, osType, typeDescription));
                }

                int entry2Offset = 446 + 16;
                byte systemId2 = ebrBuffer[entry2Offset + 4];
                if (systemId2 == 0x05 || systemId2 == 0x0F)
                {
                    uint nextEbrSectorRelative = BitConverter.ToUInt32(ebrBuffer, entry2Offset + 8);
                    currentEbrOffset = extendedPartitionBaseOffset + ((ulong)nextEbrSectorRelative * 512);
                }
                else
                {
                    currentEbrOffset = 0;
                }
            }
            return partitions;
        }

        private IEnumerable<PartitionInfo> ParseGptPartitions(FileStream diskStream)
        {
            diskStream.Seek(512, SeekOrigin.Begin);
            var gptHeaderBuffer = new byte[512];
            diskStream.Read(gptHeaderBuffer, 0, 512);

            if (BitConverter.ToUInt64(gptHeaderBuffer, 0) != 0x5452415020494645)
            {
                yield break;
            }

            ulong partitionArrayStartLba = BitConverter.ToUInt64(gptHeaderBuffer, 72);
            uint partitionEntryCount = BitConverter.ToUInt32(gptHeaderBuffer, 80);
            uint partitionEntrySize = BitConverter.ToUInt32(gptHeaderBuffer, 84);

            diskStream.Seek((long)(partitionArrayStartLba * 512), SeekOrigin.Begin);

            for (int i = 0; i < partitionEntryCount; i++)
            {
                var entryBuffer = new byte[partitionEntrySize];
                diskStream.Read(entryBuffer, 0, (int)partitionEntrySize);

                var typeGuid = new Guid(entryBuffer.Take(16).ToArray());

                if (typeGuid == Guid.Empty) continue;

                ulong firstLba = BitConverter.ToUInt64(entryBuffer, 32);
                ulong lastLba = BitConverter.ToUInt64(entryBuffer, 40);
                ulong startOffset = firstLba * 512;
                ulong size = (lastLba - firstLba + 1) * 512;

                var (osType, typeDescription) = GetOsTypeFromGptGuid(typeGuid);
                yield return new PartitionInfo(i + 1, startOffset, size, osType, typeDescription);
            }
        }

        private (OperatingSystemType, string) GetOsTypeFromMbrId(byte systemId)
        {
            switch (systemId)
            {
                case 0x07:
                case 0x0C:
                case 0x0B:
                case 0x06:
                case 0x04:
                case 0x01:
                    return (OperatingSystemType.Windows, "Windows");
                case 0x83:
                    return (OperatingSystemType.Linux, "Linux");
                case 0x82:
                    return (OperatingSystemType.Linux, "Linux Swap");
                case 0xEF:
                    return (OperatingSystemType.EFI, "EFI System Partition");
                case 0x05:
                case 0x0F:
                    return (OperatingSystemType.Other, "Extended Partition");
                default:
                    return (OperatingSystemType.Unknown, $"Unknown (Type 0x{systemId:X2})");
            }
        }

        private (OperatingSystemType, string) GetOsTypeFromGptGuid(Guid typeGuid)
        {
            if (typeGuid == WindowsBasicDataGuid)
                return (OperatingSystemType.Windows, "Windows (Basic Data)");
            if (typeGuid == LinuxFileSystemGuid)
                return (OperatingSystemType.Linux, "Linux Filesystem");
            if (typeGuid == EfiSystemGuid)
                return (OperatingSystemType.EFI, "EFI System Partition");

            return (OperatingSystemType.Unknown, "Unknown Partition Type");
        }
    }
}