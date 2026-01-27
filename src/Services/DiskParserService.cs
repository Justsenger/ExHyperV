using System.IO;

namespace ExHyperV.Services
{
    public class DiskParserService
    {
        private static readonly Guid WindowsBasicDataGuid = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
        private static readonly Guid LinuxFileSystemGuid = new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
        private static readonly Guid LinuxLvmGuid = new Guid("E6D6D379-F507-44C2-A23C-238F2A3DF928");
        
        // btrfs 专用分区类型 GUID（Arch Linux 等常用）
        private static readonly Guid BtrfsGuid = new Guid("3B8F8425-20E0-4F3B-907F-1A25A76F98E9");
        
        // Linux root 分区 (x86-64) - systemd-boot/discoverable partitions spec
        private static readonly Guid LinuxRootX64Guid = new Guid("4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709");
        
        // Linux home 分区
        private static readonly Guid LinuxHomeGuid = new Guid("933AC7E1-2EB4-4F13-B844-0E14E2AEF915");
        
        // Linux /usr 分区 (x86-64)
        private static readonly Guid LinuxUsrX64Guid = new Guid("8484680C-9521-48C6-9C11-B0720656F69E");
        
        // Linux /var 分区
        private static readonly Guid LinuxVarGuid = new Guid("4D21B016-B534-45C2-A9FB-5C16E091FD2D");
        
        // Linux swap 分区
        private static readonly Guid LinuxSwapGuid = new Guid("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");

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
            if (typeGuid == LinuxLvmGuid) return (OperatingSystemType.Linux, "Linux LVM");
            
            // btrfs 专用分区（Arch Linux 等常用）
            if (typeGuid == BtrfsGuid) return (OperatingSystemType.Linux, "Linux (btrfs)");
            
            // Discoverable Partitions Specification (systemd-boot)
            if (typeGuid == LinuxRootX64Guid) return (OperatingSystemType.Linux, "Linux Root (x86-64)");
            if (typeGuid == LinuxHomeGuid) return (OperatingSystemType.Linux, "Linux Home");
            if (typeGuid == LinuxUsrX64Guid) return (OperatingSystemType.Linux, "Linux /usr (x86-64)");
            if (typeGuid == LinuxVarGuid) return (OperatingSystemType.Linux, "Linux /var");
            
            // Linux swap 分区标记为 Other，不用于驱动注入
            if (typeGuid == LinuxSwapGuid) return (OperatingSystemType.Other, "Linux Swap");

            return (OperatingSystemType.Other, "Other");
        }
    }
}