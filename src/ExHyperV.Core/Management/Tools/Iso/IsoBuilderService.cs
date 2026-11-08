using System;
using System.IO;
using System.Runtime.InteropServices;
using IMAPI2FS;

namespace ExHyperV.Services
{
    /// <summary>使用 Windows IMAPI 将目录构建为包含 ISO 9660 与 UDF 文件系统的光盘映像。</summary>
    public static class IsoBuilderService
    {
        /// <summary>删除卷标中不支持的字符并限制长度，为空时使用默认卷标。</summary>
        /// <returns>可传给本服务映像构建器的卷标。</returns>
        /// <param name="name">候选卷标文本。</param>
        internal static string SanitizeVolumeLabel(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "NewISO";
            char[] buf = name.ToCharArray();
            for (int i = 0; i < buf.Length; i++)
                if (!(char.IsLetterOrDigit(buf[i]) || buf[i] == '_' || buf[i] == '-')) buf[i] = '_';
            string s = new string(buf);
            while (s.Contains("__")) s = s.Replace("__", "_");
            s = s.Trim('_');
            if (s.Length > 32) s = s.Substring(0, 32);
            return s.Length == 0 ? "NewISO" : s;
        }

        /// <summary>将源目录内容写成 ISO 9660/UDF 映像，当前线程等待写入完成。</summary>
        /// <remarks>输出只构建文件，不挂载映像。已有目标文件会被覆盖并截断至新映像长度；调用方负责选择输出路径。数据流提前结束时抛出异常，失败时可能留下部分文件。源目录内容放在映像根目录下。</remarks>
        /// <param name="sourceDirectory">包含要写入光盘内容的宿主目录。</param>
        /// <param name="targetIsoPath">目标 ISO 文件路径。</param>
        /// <param name="volumeLabel">光盘卷标。Core 保留字母、数字、下划线和连字符，其他字符替换为下划线，最多 32 个字符；空值使用 NewISO。</param>
        public static void BuildUdfIso(string sourceDirectory, string targetIsoPath, string volumeLabel)
        {
            MsftFileSystemImage image = null;
            IFileSystemImageResult result = null;

            try
            {
                image = new MsftFileSystemImage();
                image.VolumeName = SanitizeVolumeLabel(volumeLabel);   // 合法化,避免非法/超长卷标签让 IMAPI2FS 抛错

                // 设置为兼容模式：ISO9660 + UDF
                image.FileSystemsToCreate = FsiFileSystems.FsiFileSystemISO9660 | FsiFileSystems.FsiFileSystemUDF;

                image.Root.AddTree(sourceDirectory, false);

                result = image.CreateResultImage();

                // 显式转换为系统标准 IStream 接口以解决类型转换错误和命名空间冲突
                var stream = (System.Runtime.InteropServices.ComTypes.IStream)result.ImageStream;
                WriteIStreamToFile(stream, targetIsoPath, result.BlockSize, result.TotalBlocks);
            }
            finally
            {
                // 释放 COM 对象
                if (result != null) Marshal.ReleaseComObject(result);
                if (image != null) Marshal.ReleaseComObject(image);
            }
        }

        private static void WriteIStreamToFile(System.Runtime.InteropServices.ComTypes.IStream stream, string path, int blockSize, int totalBlocks)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
            ArgumentOutOfRangeException.ThrowIfNegative(totalBlocks);
            long remaining = (long)blockSize * totalBlocks;
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[blockSize];
                IntPtr pRead = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    while (remaining > 0)
                    {
                        int requested = (int)Math.Min(buffer.Length, remaining);
                        Marshal.WriteInt32(pRead, 0);
                        stream.Read(buffer, requested, pRead);
                        int bytesRead = Marshal.ReadInt32(pRead);
                        if (bytesRead == 0) throw new EndOfStreamException($"光盘映像数据流提前结束，尚缺 {remaining} 字节。");
                        if (bytesRead < 0 || bytesRead > requested) throw new InvalidDataException("光盘映像数据流返回了无效的读取长度。");
                        fs.Write(buffer, 0, bytesRead);
                        remaining -= bytesRead;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pRead);
                }
            }
        }
    }
}
