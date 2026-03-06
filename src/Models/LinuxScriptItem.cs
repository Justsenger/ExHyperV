namespace ExHyperV.Models
{
    public class LinuxScriptItem
    {
        public string Name { get; set; } = "Unknown"; //脚本名称
        public string Description { get; set; } = string.Empty; //脚本描述
        public string Author { get; set; } = "Unknown"; //作者名称
        public string Version { get; set; } = "1.0.0"; //脚本版本
        public bool IsLocal { get; set; } //是否属于本地脚本
        public string SourcePathOrUrl { get; set; } = string.Empty; //路径
        public string FileName { get; set; } = string.Empty; //文件名

        public override string ToString() => Name;
    }
}