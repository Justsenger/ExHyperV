namespace ExHyperV.Models
{
    public class LinuxScriptItem
    {
        public string Name { get; set; } = "Unknown";
        public string Description { get; set; } = string.Empty;
        public string Author { get; set; } = "Unknown";
        public string Version { get; set; } = "1.0.0";
        public bool IsLocal { get; set; }
        public string SourcePathOrUrl { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;

        public override string ToString() => Name;
    }
}