namespace ExHyperV.Models
{
    /// <summary>Linux GPU 部署脚本的可变描述。部署服务按 SourceUrl 下载 FileName 并在来宾执行；名称和说明只用于展示。</summary>
    public class LinuxGpuDeploymentScript
    {
        /// <summary>脚本显示名称，未指定时为 Unknown。</summary>
        public string Name { get; set; } = "Unknown"; //脚本名称
        /// <summary>脚本适用系统和用途的显示说明。</summary>
        public string Description { get; set; } = string.Empty; //脚本描述
        /// <summary>脚本作者的显示名称。</summary>
        public string Author { get; set; } = "Unknown"; //作者名称
        /// <summary>脚本版本字符串。</summary>
        public string Version { get; set; } = "1.0.0"; //脚本版本
        /// <summary>来宾下载部署脚本所用的 URL；部署时必须非空。</summary>
        public string SourceUrl { get; set; } = string.Empty; //下载地址
        /// <summary>下载后执行的脚本文件名；部署时必须非空。</summary>
        public string FileName { get; set; } = string.Empty; //文件名

        /// <summary>取得脚本的显示名称。</summary>
        /// <returns>当前 Name。</returns>
        public override string ToString() => Name;
    }
}
