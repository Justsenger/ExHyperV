using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ExHyperV.Models
{
    // =======================================================
    // 全新的、与真实JSON结构精确匹配的模型类
    // =======================================================

    public class VmCpuGroupInfo
    {
        [JsonPropertyName("CpuGroupId")]
        public Guid CpuGroupId { get; set; }
    }
    // 最外层结构: {"Properties": [...]}
    public class HcsQueryResult
    {
        [JsonPropertyName("Properties")]
        public List<HcsProperty> Properties { get; set; }
    }

    // Properties数组中的元素: {"CpuGroups": [...]}
    public class HcsProperty
    {
        [JsonPropertyName("CpuGroups")]
        public List<HcsCpuGroupDetail> CpuGroups { get; set; }
    }

    // CpuGroups数组中的元素 (单个CPU组的详细信息)
    public class HcsCpuGroupDetail
    {
        [JsonPropertyName("GroupId")]
        public Guid GroupId { get; set; }

        [JsonPropertyName("Affinity")]
        public HcsCpuAffinity Affinity { get; set; }

        // 其他我们暂时用不到的属性
        // [JsonPropertyName("GroupProperties")]
        // public List<object> GroupProperties { get; set; }
        // [JsonPropertyName("HypervisorGroupId")]
        // public int HypervisorGroupId { get; set; }
    }

    // Affinity对象: {"LogicalProcessorCount": 4, "LogicalProcessors": [0,1,8,9]}
    public class HcsCpuAffinity
    {
        [JsonPropertyName("LogicalProcessorCount")]
        public uint LogicalProcessorCount { get; set; }

        [JsonPropertyName("LogicalProcessors")]
        public List<uint> LogicalProcessors { get; set; }
    }
}