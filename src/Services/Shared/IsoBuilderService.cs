using System;
using System.IO;
using System.Runtime.InteropServices;
using IMAPI2FS;

namespace ExHyperV.Services
{
    /// <summary>
    /// 把一个目录构建成 ISO 文件（ISO9660 + UDF 兼容模式）。
    /// 内部使用 IMAPI2FS COM 组件——这里直接 P/Invoke 调用，因为只有一个方法不值得单独抽 Api。
    /// </summary>
    public static class IsoBuilderService
    {
        /// <summary>把文件夹名合法化成 IMAPI2FS 可用的卷标签:只留字母/数字/下划线/连字符,其余换下划线,合并并去首尾下划线,截断到 32,空则回退 NewISO。</summary>
        public static string SanitizeVolumeLabel(string name)
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
            using (var fs = File.OpenWrite(path))
            {
                byte[] buffer = new byte[blockSize];
                IntPtr pRead = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    for (int i = 0; i < totalBlocks; i++)
                    {
                        stream.Read(buffer, blockSize, pRead);
                        int bytesRead = Marshal.ReadInt32(pRead);
                        if (bytesRead <= 0) break;          // 流提前结束
                        fs.Write(buffer, 0, bytesRead);     // 按实际读取字节写，避免末块不足 blockSize 时写入上一块残留
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