using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    public class DiskParserService
    {
        private static readonly Guid WindowsBasicDataGuid = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
        private static readonly Guid LinuxFileSystemGuid = new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
        private static readonly Guid LinuxLvmGuid = new Guid("E6D6D379-F507-44C2-A23C-238F2A3DF928");
        private static readonly Guid BtrfsGuid = new Guid("3B8F8425-20E0-4F3B-907F-1A25A76F98E9");
        private static readonly Guid LinuxRootX64Guid = new Guid("4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709");

        /// <summary>
        /// 解析磁盘分区（自动探测扇区大小）
        /// </summary>
        public List<PartitionInfo> GetPartitions(string devicePath)
        {
            var partitions = new List<PartitionInfo>();
            int bytesPerSector = 512; // 默认值

            try
            {
                using (var diskStream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // 1. 读取 LBA 0 (无论 512 还是 4Kn，前 512 字节始终包含 MBR)
                    byte[] lba0 = new byte[512];
                    diskStream.Read(lba0, 0, 512);

                    if (lba0[510] != 0x55 || lba0[511] != 0xAA) return partitions;

                    // 2. 如果检测到 GPT 标识，进行扇区大小“盲测”
                    if (IsGptProtectiveMbr(lba0))
                    {
                        // 探测 LBA 1 的位置
                        if (CheckGptSignatureAt(diskStream, 512))
                        {
                            bytesPerSector = 512;
                            Debug.WriteLine(Properties.Resources.DiskParserService_1);
                        }
                        else if (CheckGptSignatureAt(diskStream, 4096))
                        {
                            bytesPerSector = 4096;
                            Debug.WriteLine(Properties.Resources.DiskParserService_2);
                        }
                        else
                        {
                            Debug.WriteLine(Properties.Resources.DiskParserService_3);
                            return partitions;
                        }

                        partitions.AddRange(ParseGptPartitions(diskStream, bytesPerSector));
                    }
                    else
                    {
                        // 纯 MBR 磁盘
                        partitions.AddRange(ParseMbrPartitions(diskStream, lba0, bytesPerSector));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format(Properties.Resources.DiskParserService_4, ex.Message));
            }

            const long oneGbInBytes = 1024L * 1024 * 1024;
            return partitions
                .Where(p => p.SizeInBytes >= oneGbInBytes)
                .Where(p => p.OsType == OperatingSystemType.Windows || p.OsType == OperatingSystemType.Linux)
                .ToList();
        }

        private bool CheckGptSignatureAt(Stream stream, long offset)
        {
            try
            {
                byte[] sig = new byte[8];
                stream.Position = offset;
                stream.Read(sig, 0, 8);
                // "EFI PART" 的十六进制
                return BitConverter.ToUInt64(sig, 0) == 0x5452415020494645;
            }
            catch { return false; }
        }

        private bool IsGptProtectiveMbr(byte[] mbrBuffer)
        {
            for (int i = 0; i < 4; i++)
                if (mbrBuffer[446 + (i * 16) + 4] == 0xEE) return true;
            return false;
        }

        private IEnumerable<PartitionInfo> ParseGptPartitions(FileStream diskStream, int sectorSize)
        {
            // 在正确的扇区位置读取 Header
            diskStream.Position = sectorSize;
            byte[] header = new byte[512];
            diskStream.Read(header, 0, 512);

            ulong arrayLba = BitConverter.ToUInt64(header, 72);
            uint entryCount = BitConverter.ToUInt32(header, 80);
            uint entrySize = BitConverter.ToUInt32(header, 84);

            diskStream.Position = (long)arrayLba * sectorSize;

            for (int i = 0; i < entryCount; i++)
            {
                byte[] entry = new byte[entrySize];
                if (diskStream.Read(entry, 0, (int)entrySize) < entrySize) break;

                byte[] guidBytes = new byte[16];
                Buffer.BlockCopy(entry, 0, guidBytes, 0, 16);
                var typeGuid = new Guid(guidBytes);
                if (typeGuid == Guid.Empty) continue;

                ulong firstLba = BitConverter.ToUInt64(entry, 32);
                ulong lastLba = BitConverter.ToUInt64(entry, 40);

                var (osType, desc) = GetOsTypeFromGptGuid(typeGuid);
                yield return new PartitionInfo(
                    i + 1,
                    firstLba * (ulong)sectorSize,
                    (lastLba - firstLba + 1) * (ulong)sectorSize,
                    osType,
                    desc);
            }
        }

        private IEnumerable<PartitionInfo> ParseMbrPartitions(FileStream diskStream, byte[] mbr, int sectorSize)
        {
            for (int i = 0; i < 4; i++)
            {
                int offset = 446 + (i * 16);
                byte sysId = mbr[offset + 4];
                if (sysId == 0x00 || sysId == 0x05 || sysId == 0x0F) continue;

                uint startLba = BitConverter.ToUInt32(mbr, offset + 8);
                uint totalSectors = BitConverter.ToUInt32(mbr, offset + 12);

                var (osType, desc) = GetOsTypeFromMbrId(sysId);
                yield return new PartitionInfo(
                    i + 1,
                    (ulong)startLba * (ulong)sectorSize,
                    (ulong)totalSectors * (ulong)sectorSize,
                    osType,
                    desc);
            }
        }

        private (OperatingSystemType, string) GetOsTypeFromMbrId(byte id)
        {
            if (id == 0x07) return (OperatingSystemType.Windows, "Windows");
            if (id == 0x83 || id == 0x8E) return (OperatingSystemType.Linux, "Linux");
            return (OperatingSystemType.Other, "Other");
        }

        private (OperatingSystemType, string) GetOsTypeFromGptGuid(Guid guid)
        {
            if (guid == WindowsBasicDataGuid) return (OperatingSystemType.Windows, "Windows");
            if (guid == LinuxFileSystemGuid || guid == LinuxLvmGuid || guid == BtrfsGuid || guid == LinuxRootX64Guid)
                return (OperatingSystemType.Linux, "Linux");
            return (OperatingSystemType.Other, "Other");
        }
    }
}