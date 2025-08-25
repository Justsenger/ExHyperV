namespace ExHyperV.Models
{
    public class MemoryInfo
    {
        public string BankLabel { get; set; }
        public string DeviceLocator { get; set; }
        public string Manufacturer { get; set; }
        public string PartNumber { get; set; }
        public string Capacity { get; set; }
        public string DeclaredSpeed { get; set; }
        public string ConfiguredSpeed { get; set; }
        public string IsEcc { get; set; }
        public string MemoryType { get; set; }
        public string SerialNumber { get; set; }
    }
}