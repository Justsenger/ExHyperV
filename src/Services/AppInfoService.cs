using System.IO;
using System.Reflection;

namespace ExHyperV.Services
{
    /// <summary>
    /// App 元信息查询：版本 / 作者 / 构建时间。
    /// 版本与作者从 csproj 元数据自动生成的 assembly attribute 读取，避免硬编码。
    /// </summary>
    public static class AppInfoService
    {
        /// <summary>形如 "V1.5.0-Beta"，来自 csproj &lt;Version&gt;</summary>
        public static string Version =>
            $"V{Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0"}";

        /// <summary>作者名，来自 csproj &lt;Authors&gt;（SDK 默认会生成 AssemblyMetadata("Authors", ...)）</summary>
        public static string Author =>
            Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "Authors")?.Value ?? "Unknown";

        /// <summary>近似 build time —— 当前 exe 文件最后写入时间</summary>
        public static DateTime BuildTime
        {
            get
            {
                string filePath = Assembly.GetExecutingAssembly().Location;
                return new FileInfo(filePath).LastWriteTime;
            }
        }
    }
}
