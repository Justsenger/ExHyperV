using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class FileApiCases
{
    internal static async Task PackageAsync(string id, ApiCaseContext c)
    {
        var content = new Dictionary<string, byte[]>
        {
            ["中文目录/配置.txt"] = Encoding.UTF8.GetBytes("配置内容\r\n第二行"),
            ["empty.bin"] = [], ["binary.dat"] = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray()
        };
        if (id.EndsWith("-1", StringComparison.Ordinal))
        {
            foreach (var mode in new[] { VmExportPackageMode.Store, VmExportPackageMode.Compress })
            {
                string source = Path.Combine(c.DirectoryPath, mode.ToString()), zip = source + ".zip";
                WriteSource(source, content);
                var response = await c.CallAsync(() => VmExportPackagingService.CreatePackageAsync(source, zip, mode));
                c.Require(response.HasData, "归档成功：" + response.Error);
                c.Equal(zip, response.Data!.ArchivePath, "最终归档路径");
                using var archive = ZipFile.OpenRead(zip);
                var entries = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToArray();
                c.Sequence(content.Keys.Order(), entries.Select(e => e.FullName.Replace('\\', '/')).Order(), "归档文件清单");
                foreach (var entry in entries)
                {
                    using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output);
                    c.Sequence(content[entry.FullName.Replace('\\', '/')], output.ToArray(), "归档逐字节核验：" + entry.FullName);
                }
                c.Require(response.Data.SourceDirectoryRemoved && !Directory.Exists(source), "校验后清理专属源目录");
                c.Require(!File.Exists(zip + ".partial"), "归档暂存文件已清理");
            }
            return;
        }
        string kept = Path.Combine(c.DirectoryPath, "source"), blocked = Path.Combine(c.DirectoryPath, "occupied");
        WriteSource(kept, content); File.WriteAllText(blocked, "保留的目标文件");
        var failed = await c.CallAsync(() => VmExportPackagingService.CreatePackageAsync(kept, Path.Combine(blocked, "output.zip"), VmExportPackageMode.Store));
        c.Require(!failed.Success, "输出父路径已由文件占用，返回写入失败：" + failed.Error);
        c.Equal("保留的目标文件", File.ReadAllText(blocked), "原有目标文件保持");
        var cancelled = await c.CallAsync(() => VmExportPackagingService.CreatePackageAsync(kept, Path.Combine(c.DirectoryPath, "cancelled.zip"), VmExportPackageMode.Store, cancellationToken: new CancellationToken(true)));
        c.Equal(ApiErrorKind.Cancelled, cancelled.ErrorKind, "调用前取消的分类");
        foreach (var item in content) c.Sequence(item.Value, File.ReadAllBytes(Path.Combine(kept, item.Key)), "失败与取消后保留源文件：" + item.Key);
        c.Require(!Directory.EnumerateFiles(c.DirectoryPath, "*.partial", SearchOption.AllDirectories).Any(), "失败与取消后暂存归档已清理");
    }

    private static void WriteSource(string root, Dictionary<string, byte[]> content)
    {
        foreach (var item in content)
        {
            string file = Path.Combine(root, item.Key); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, item.Value);
        }
    }

    internal static void Partitions(string id, ApiCaseContext c)
    {
        var parser = new DiskParser();
        if (id.EndsWith("-1", StringComparison.Ordinal))
        {
            foreach (int sector in new[] { 512, 4096 })
            {
                string path = Path.Combine(c.DirectoryPath, $"gpt-{sector}.img");
                WriteDisk(path, sector, true);
                var rows = c.Call(() => parser.GetPartitions(path));
                CheckPartitions(rows, c, $"GPT {sector} 字节扇区");
            }
            string mbr = Path.Combine(c.DirectoryPath, "mbr.img"); WriteDisk(mbr, 512, false);
            CheckPartitions(c.Call(() => parser.GetPartitions(mbr)), c, "MBR");
            return;
        }
        foreach (int length in new[] { 0, 17, 511, 512 })
        {
            string path = Path.Combine(c.DirectoryPath, $"short-{length}.img"); File.WriteAllBytes(path, new byte[length]);
            c.Equal(0, c.Call(() => parser.GetPartitions(path)).Count, $"{length} 字节空表返回空清单");
        }
        string invalid = Path.Combine(c.DirectoryPath, "invalid-gpt.img"); WriteDisk(invalid, 512, true);
        using (var stream = new FileStream(invalid, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 512 + 80; stream.Write(BitConverter.GetBytes(uint.MaxValue));
        }
        c.Equal(0, c.Call(() => parser.GetPartitions(invalid)).Count, "超界条目数被拒绝");
        WriteDisk(invalid, 512, true);
        using (var stream = new FileStream(invalid, FileMode.Open, FileAccess.Write))
        {
            // A complete table with a reversed range must never manufacture a huge partition.
            stream.Position = 2 * 512 + 40; stream.Write(BitConverter.GetBytes(1UL));
        }
        var malformed = c.Call(() => parser.GetPartitions(invalid));
        c.Require(malformed.All(row => row.PartitionNumber != 1), "起始 LBA 大于终止 LBA 的分区应被排除；实际：" + string.Join("；", malformed.Select(p => $"分区 {p.PartitionNumber}，{p.SizeInBytes} 字节")));
        foreach (var (first, last) in new[] { (0UL, 2048UL), (1UL, ulong.MaxValue), (ulong.MaxValue - 1, ulong.MaxValue) })
        {
            WriteDisk(invalid, 512, true);
            using (var stream = new FileStream(invalid, FileMode.Open, FileAccess.Write))
            {
                stream.Position = 2 * 512 + 32; stream.Write(BitConverter.GetBytes(first)); stream.Write(BitConverter.GetBytes(last));
            }
            var rows = c.Call(() => parser.GetPartitions(invalid));
            c.Require(rows.All(row => row.PartitionNumber != 1), $"排除无效范围 {first}..{last}");
            c.Require(rows.Any(row => row.PartitionNumber == 2), "保留同表中的有效分区");
        }
        WriteDisk(invalid, 512, true);
        using (var stream = new FileStream(invalid, FileMode.Open, FileAccess.Write))
        { stream.Position = 512 + 72; stream.Write(BitConverter.GetBytes(ulong.MaxValue)); }
        c.Equal(0, c.Call(() => parser.GetPartitions(invalid)).Count, "拒绝溢出的分区表位置");
    }

    private static void CheckPartitions(List<DiskPartitionInfo> rows, ApiCaseContext c, string format)
    {
        c.Sequence(new[] { 1, 2 }, rows.Select(p => p.PartitionNumber), format + "：筛选 Windows/Linux，排除小分区");
        c.Sequence(new[] { OperatingSystemType.Windows, OperatingSystemType.Linux }, rows.Select(p => p.OsType), "类型");
        c.Sequence(new[] { 2UL << 30, 1UL << 30 }, rows.Select(p => p.SizeInBytes), "容量字节");
        c.Require(rows[0].IsBitLocker && !rows[1].IsBitLocker, "Windows 引导扇区 BitLocker 签名");
    }

    private static void WriteDisk(string path, int sector, bool gpt)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        if (!DeviceIoControl(file.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new ApiCheckSkippedException("测试缓存所在文件系统未接受稀疏文件，无法创建轻量磁盘样本。");
        file.SetLength(4L << 30);
        byte[] mbr = new byte[512]; mbr[510] = 0x55; mbr[511] = 0xAA;
        long[] starts = [1L << 20, (1L << 20) + (2L << 30), (1L << 20) + (3L << 30)];
        long[] sizes = [2L << 30, 1L << 30, 16L << 20];
        if (gpt)
        {
            mbr[450] = 0xEE; BitConverter.GetBytes(1u).CopyTo(mbr, 454);
            BitConverter.GetBytes((uint)(file.Length / sector - 1)).CopyTo(mbr, 458);
            byte[] header = new byte[sector], table = new byte[128 * 128];
            Encoding.ASCII.GetBytes("EFI PART").CopyTo(header, 0);
            BitConverter.GetBytes(0x10000u).CopyTo(header, 8); BitConverter.GetBytes(92u).CopyTo(header, 12);
            BitConverter.GetBytes(1UL).CopyTo(header, 24); BitConverter.GetBytes((ulong)(file.Length / sector - 1)).CopyTo(header, 32);
            BitConverter.GetBytes(34UL).CopyTo(header, 40); BitConverter.GetBytes((ulong)(file.Length / sector - 34)).CopyTo(header, 48);
            Guid.Parse("fd17e9e5-94b5-491c-a124-25f0345f288f").ToByteArray().CopyTo(header, 56);
            BitConverter.GetBytes(2UL).CopyTo(header, 72); BitConverter.GetBytes(128u).CopyTo(header, 80); BitConverter.GetBytes(128u).CopyTo(header, 84);
            for (int i = 0; i < 3; i++)
            {
                Guid.Parse(i == 1 ? "0FC63DAF-8483-4772-8E79-3D69D8477DE4" : "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7").ToByteArray().CopyTo(table, i * 128);
                Guid.NewGuid().ToByteArray().CopyTo(table, i * 128 + 16);
                BitConverter.GetBytes((ulong)(starts[i] / sector)).CopyTo(table, i * 128 + 32);
                BitConverter.GetBytes((ulong)((starts[i] + sizes[i]) / sector - 1)).CopyTo(table, i * 128 + 40);
            }
            BitConverter.GetBytes(Crc(table)).CopyTo(header, 88); BitConverter.GetBytes(Crc(header.AsSpan(0, 92))).CopyTo(header, 16);
            file.Position = sector; file.Write(header); file.Position = 2L * sector; file.Write(table);
        }
        else for (int i = 0; i < 3; i++)
        {
            int offset = 446 + i * 16; mbr[offset + 4] = (byte)(i == 1 ? 0x83 : 0x07);
            BitConverter.GetBytes((uint)(starts[i] / 512)).CopyTo(mbr, offset + 8);
            BitConverter.GetBytes((uint)(sizes[i] / 512)).CopyTo(mbr, offset + 12);
        }
        file.Position = 0; file.Write(mbr);
        file.Position = starts[0] + 3; file.Write(Encoding.ASCII.GetBytes("-FVE-FS-"));
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte item in bytes) { value ^= item; for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ ((value & 1) == 0 ? 0u : 0xEDB88320u); }
        return ~value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
