using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ExHyperV.Models;

namespace ExHyperV.Tools
{
    public class DiskParser
    {
        private static readonly Guid WindowsBasicDataGuid = new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
        private static readonly Guid LinuxFileSystemGuid = new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
        private static readonly Guid LinuxLvmGuid = new Guid("E6D6D379-F507-44C2-A23C-238F2A3DF928");
        private static readonly Guid BtrfsGuid = new Guid("3B8F8425-20E0-4F3B-907F-1A25A76F98E9");
        private static readonly Guid LinuxRootX64Guid = new Guid("4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709");

        /// <summary>
        /// 解析磁盘分区（自动探测扇区大小，标记 BitLocker 加密分区）。
        /// 打开设备失败（盘刚联机未就绪/被独占）向上抛出——调用方须与"确实没有符合条件的分区"区分；
        /// 分区表内容损坏则容忍，返回已解析出的部分。
        /// </summary>
        public List<PartitionInfo> GetPartitions(string devicePath)
        {
            var partitions = new List<PartitionInfo>();

            using (var diskStream = new FileStream(devicePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // 物理设备直接问系统真实扇区大小（4Kn 盘 MBR/GPT 都不再靠猜）；
                // 普通文件（测试镜像）IOCTL 必然失败 → 默认 512 + GPT 盲测兜底。
                int bytesPerSector = QuerySectorSize(diskStream) ?? 512;

                try
                {
                    byte[] lba0 = new byte[512];
                    if (diskStream.Read(lba0, 0, 512) < 512) return Filter(partitions);
                    if (lba0[510] != 0x55 || lba0[511] != 0xAA) return Filter(partitions);

                    if (IsGptProtectiveMbr(lba0))
                    {
                        if (!CheckGptSignatureAt(diskStream, bytesPerSector))
                        {
                            // IOCTL 不可用（文件镜像）时按 512/4096 盲测 GPT 头位置
                            if (CheckGptSignatureAt(diskStream, 512)) bytesPerSector = 512;
                            else if (CheckGptSignatureAt(diskStream, 4096)) bytesPerSector = 4096;
                            else
                            {
                                Debug.WriteLine(Properties.Resources.DiskParser_LogErrGptNotFound);
                                return Filter(partitions);
                            }
                        }
                        partitions.AddRange(ParseGptPartitions(diskStream, bytesPerSector));
                    }
                    else
                    {
                        partitions.AddRange(ParseMbrPartitions(diskStream, lba0, bytesPerSector));
                    }
                }
                catch (Exception ex)
                {
                    // 分区表内容损坏：容忍，带着已解析出的部分返回（设备打开失败已在 using 处向上抛）
                    Debug.WriteLine(string.Format(Properties.Resources.DiskParser_LogErrReadFailed, ex.Message));
                }
            }

            return Filter(partitions);
        }

        // 统一出口过滤：>=1GB 且 Windows/Linux 类型（BitLocker 分区类型为 Windows，自然通过并携带加密标记）
        private static List<PartitionInfo> Filter(List<PartitionInfo> partitions)
        {
            const long oneGbInBytes = 1024L * 1024 * 1024;
            return partitions
                .Where(p => p.SizeInBytes >= (ulong)oneGbInBytes)
                .Where(p => p.OsType == OperatingSystemType.Windows || p.OsType == OperatingSystemType.Linux)
                .ToList();
        }

        // 物理设备经 IOCTL_DISK_GET_DRIVE_GEOMETRY 取真实 BytesPerSector；非设备句柄返回 null
        private static int? QuerySectorSize(FileStream disk)
        {
            try
            {
                byte[] geometry = new byte[24];   // DISK_GEOMETRY：BytesPerSector 在偏移 20
                if (!DeviceIoControl(disk.SafeFileHandle, 0x00070000, IntPtr.Zero, 0,
                        geometry, geometry.Length, out _, IntPtr.Zero))
                    return null;
                int bps = BitConverter.ToInt32(geometry, 20);
                return (bps == 512 || bps == 4096) ? bps : (int?)null;
            }
            catch { return null; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        private bool CheckGptSignatureAt(Stream stream, long offset)
        {
            try
            {
                byte[] sig = new byte[8];
                stream.Position = offset;
                if (stream.Read(sig, 0, 8) < 8) return false;
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

        // BitLocker 加密卷的引导扇区 OEM ID 为 "-FVE-FS-"(偏移 3..10)。分区类型 GUID 层面
        // 加密卷与普通基本数据分区无差别、挂载后卷的 FileSystem 字段随加密模式表现不一，
        // 只有首扇区签名可靠——识别后上层可明确提示"先解锁"而非误导性的"不是有效分区"。
        private static bool HasBitLockerSignature(FileStream disk, long partitionStartBytes)
        {
            try
            {
                byte[] boot = new byte[512];
                disk.Position = partitionStartBytes;
                if (disk.Read(boot, 0, 512) < 512) return false;
                return boot[3] == (byte)'-' && boot[4] == (byte)'F' && boot[5] == (byte)'V' && boot[6] == (byte)'E'
                    && boot[7] == (byte)'-' && boot[8] == (byte)'F' && boot[9] == (byte)'S' && boot[10] == (byte)'-';
            }
            catch { return false; }
        }

        private List<PartitionInfo> ParseGptPartitions(FileStream diskStream, int sectorSize)
        {
            var list = new List<PartitionInfo>();

            diskStream.Position = sectorSize;
            byte[] header = new byte[512];
            if (diskStream.Read(header, 0, 512) < 512) return list;

            ulong arrayLba = BitConverter.ToUInt64(header, 72);
            uint entryCount = BitConverter.ToUInt32(header, 80);
            uint entrySize = BitConverter.ToUInt32(header, 84);

            // 表头合法性：规范条目 128 字节/128 条。超出合理界限=表头损坏，放弃解析而非
            // 按垃圾值分配巨额内存或空转（损坏文件是修复场景的常态输入）。
            if (arrayLba == 0 || entryCount == 0 || entryCount > 1024 || entrySize < 128 || entrySize > 4096)
                return list;

            // 条目表一次读入内存（上限 1024*4096=4MB，常规 16KB），随后可自由 seek 各分区首扇区判 BitLocker
            byte[] table = new byte[entryCount * entrySize];
            diskStream.Position = (long)arrayLba * sectorSize;
            int got = diskStream.Read(table, 0, table.Length);

            for (int i = 0; i < entryCount; i++)
            {
                int off = i * (int)entrySize;
                if (off + 128 > got) break;

                byte[] guidBytes = new byte[16];
                Buffer.BlockCopy(table, off, guidBytes, 0, 16);
                var typeGuid = new Guid(guidBytes);
                if (typeGuid == Guid.Empty) continue;

                ulong firstLba = BitConverter.ToUInt64(table, off + 32);
                ulong lastLba = BitConverter.ToUInt64(table, off + 40);

                var (osType, desc) = GetOsTypeFromGptGuid(typeGuid);
                var p = new PartitionInfo(i + 1, (lastLba - firstLba + 1) * (ulong)sectorSize, osType, desc);
                if (osType == OperatingSystemType.Windows)
                    p.IsBitLocker = HasBitLockerSignature(diskStream, (long)firstLba * sectorSize);
                list.Add(p);
            }
            return list;
        }

        private List<PartitionInfo> ParseMbrPartitions(FileStream diskStream, byte[] mbr, int sectorSize)
        {
            var list = new List<PartitionInfo>();
            var extendedBases = new List<uint>();   // 扩展分区起始 LBA（逻辑分区链的基址）

            // 主分区：编号=槽位号(1..4)，与系统分区号语义一致
            for (int i = 0; i < 4; i++)
            {
                int offset = 446 + (i * 16);
                byte sysId = mbr[offset + 4];
                if (sysId == 0x00) continue;
                uint relLba = BitConverter.ToUInt32(mbr, offset + 8);
                if (sysId == 0x05 || sysId == 0x0F) { extendedBases.Add(relLba); continue; }

                uint totalSectors = BitConverter.ToUInt32(mbr, offset + 12);
                var (osType, desc) = GetOsTypeFromMbrId(sysId);
                var p = new PartitionInfo(i + 1, (ulong)totalSectors * (ulong)sectorSize, osType, desc);
                if (osType == OperatingSystemType.Windows)
                    p.IsBitLocker = HasBitLockerSignature(diskStream, (long)relLba * sectorSize);
                list.Add(p);
            }

            // 逻辑分区：遍历 EBR 链（老式布局的 Linux 常装在这里）。编号从 5 起，与系统语义一致。
            // EBR 槽1=本逻辑分区（起始相对本 EBR），槽2=下一 EBR（起始相对扩展分区基址）。
            int logicalNumber = 5;
            foreach (uint extBase in extendedBases)
            {
                ulong ebr = extBase;
                for (int guard = 0; guard < 128 && ebr != 0; guard++)
                {
                    byte[] sector = new byte[512];
                    diskStream.Position = (long)ebr * sectorSize;
                    if (diskStream.Read(sector, 0, 512) < 512) break;
                    if (sector[510] != 0x55 || sector[511] != 0xAA) break;

                    byte id1 = sector[446 + 4];
                    uint rel1 = BitConverter.ToUInt32(sector, 446 + 8);
                    uint total1 = BitConverter.ToUInt32(sector, 446 + 12);
                    if (id1 != 0x00 && total1 > 0)
                    {
                        var (osType, desc) = GetOsTypeFromMbrId(id1);
                        var p = new PartitionInfo(logicalNumber, (ulong)total1 * (ulong)sectorSize, osType, desc);
                        if (osType == OperatingSystemType.Windows)
                            p.IsBitLocker = HasBitLockerSignature(diskStream, (long)(ebr + rel1) * sectorSize);
                        list.Add(p);
                        logicalNumber++;
                    }

                    byte id2 = sector[462 + 4];
                    uint rel2 = BitConverter.ToUInt32(sector, 462 + 8);
                    ebr = (id2 == 0x05 || id2 == 0x0F) && rel2 > 0 ? (ulong)extBase + rel2 : 0;
                }
            }
            return list;
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
