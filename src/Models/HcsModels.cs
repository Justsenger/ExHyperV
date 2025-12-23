using System.Text.Json.Serialization;

namespace ExHyperV.Models
{
    public class VmCpuGroupInfo
    {
        [JsonPropertyName("CpuGroupId")]
        public Guid CpuGroupId { get; set; }
    }
    public class HcsQueryResult
    {
        [JsonPropertyName("Properties")]
        public List<HcsProperty> Properties { get; set; }
    }
    public class HcsProperty
    {
        [JsonPropertyName("CpuGroups")]
        public List<HcsCpuGroupDetail> CpuGroups { get; set; }
    }
    public class HcsCpuGroupDetail
    {
        [JsonPropertyName("GroupId")]
        public Guid GroupId { get; set; }

        [JsonPropertyName("Affinity")]
        public HcsCpuAffinity Affinity { get; set; }
    }
    public class HcsCpuAffinity
    {
        [JsonPropertyName("LogicalProcessorCount")]
        public uint LogicalProcessorCount { get; set; }

        [JsonPropertyName("LogicalProcessors")]
        public List<uint> LogicalProcessors { get; set; }
    }
}