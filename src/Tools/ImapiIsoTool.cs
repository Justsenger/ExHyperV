using System;
using System.IO;
using System.Runtime.InteropServices;
using IMAPI2FS;

namespace ExHyperV.Tools
{
    public static class ImapiIsoTool
    {
        public static void BuildUdfIso(string sourceDirectory, string targetIsoPath, string volumeLabel)
        {
            MsftFileSystemImage image = null;
            IFileSystemImageResult result = null;

            try
            {
                image = new MsftFileSystemImage();
                image.VolumeName = volumeLabel;

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
                        fs.Write(buffer, 0, blockSize);
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