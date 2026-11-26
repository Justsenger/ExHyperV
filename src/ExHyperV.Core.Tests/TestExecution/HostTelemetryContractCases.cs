using System.Net.NetworkInformation;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class HostTelemetryContractCases
{
    internal static async Task RunAsync(string api, ApiCaseContext c)
    {
        switch (api)
        {
            case "HostDeviceTelemetryService.Read()":
                using (var service = new HostDeviceTelemetryService())
                {
                    var sample = c.Call(() => service.Read(Array.Empty<int>()));
                    c.Equal(0, sample.Disks.Count, "未请求物理盘时磁盘清单为空");
                    c.Require(sample.CpuGHz == null || sample.CpuGHz > 0, "频率为正值或缺测");
                    foreach (var (id, gpu) in sample.Gpus)
                        c.Require(id.Length > 0 && new[] { gpu.Pct3D, gpu.PctCopy, gpu.PctEnc, gpu.PctDec }
                            .All(p => p == null || p is >= 0 and <= 100), "设备身份及已报告的 GPU 百分比有效");
                }
                break;
            case "HostDeviceTelemetryService.Dispose()":
                var devices = new HostDeviceTelemetryService();
                c.Call(devices.Dispose);
                c.Call(devices.Dispose);
                c.Throws<ObjectDisposedException>(() => devices.Read([]), "释放后关闭采样入口");
                break;
            case "HostDeviceTelemetryService.ReadAdapterAddresses()":
                var addresses = c.Call(HostDeviceTelemetryService.ReadAdapterAddresses);
                var interfaces = NetworkInterface.GetAllNetworkInterfaces().Where(n => Guid.TryParse(n.Id, out _))
                    .ToDictionary(n => Guid.Parse(n.Id));
                c.Require(addresses.Keys.All(interfaces.ContainsKey), "返回的接口 GUID 属于系统清单");
                foreach (var (id, address) in addresses)
                    c.Equal(interfaces[id].GetPhysicalAddress().ToString(), address.Mac, "物理地址一致");
                break;
            case "HostNetworkTelemetryService.GetSwitchThroughput()":
            case "HostNetworkTelemetryService.GetAdapterThroughput()":
                using (var service = new HostNetworkTelemetryService())
                {
                    string absent = Guid.NewGuid().ToString("N");
                    var reading = api.Contains("GetSwitch", StringComparison.Ordinal)
                        ? c.Call(() => service.GetSwitchThroughput(absent)) : c.Call(() => service.GetAdapterThroughput(absent));
                    c.Require(!reading.Sample.IsValid && reading.Throughput == null && reading.Sample.ObservedAtUtc != default,
                        "缺失实例有观测时间且不发布有效流量");
                }
                break;
            case "HostNetworkTelemetryService.Dispose()":
                var counters = new HostNetworkTelemetryService();
                c.Call(counters.Dispose);
                c.Throws<ObjectDisposedException>(() => counters.GetAdapterThroughput("missing"), "已释放实例拒绝新采样");
                break;
            case "HostNetworkTelemetryService.Reset()":
                using (var service = new HostNetworkTelemetryService())
                {
                    string category = "Network Interface";
                    var instances = new System.Diagnostics.PerformanceCounterCategory(category).GetInstanceNames();
                    var instance = instances.FirstOrDefault(name => !name.Contains('#') &&
                        !instances.Any(other => other.StartsWith(name + "#", StringComparison.OrdinalIgnoreCase)));
                    if (instance == null) throw new ApiCheckSkippedException("找不到唯一网络计数器实例。");
                    var initial = service.GetAdapterThroughput(instance);
                    if (initial.Sample.State != PerformanceSampleState.WarmingUp)
                        throw new ApiCheckSkippedException("所选网络接口的性能计数器不可读。");
                    c.Call(service.Reset);
                    var first = service.GetAdapterThroughput(instance);
                    c.Equal(PerformanceSampleState.WarmingUp, first.Sample.State, "重置后重新预热");
                    c.Require(first.Throughput == null, "重置后首样本没有速率");
                }
                break;
            case "HostSystemInfoService.TryReadHostMemoryMiB()":
                var memory = c.Call(HostSystemInfoService.TryReadHostMemoryMiB);
                c.Require(memory is { TotalMiB: > 0, AvailableMiB: >= 0 } && memory.Value.AvailableMiB <= memory.Value.TotalMiB,
                    "物理内存总量和可用量的 MiB 范围有效");
                break;
            case "HyperVSwitchService.GetAllUpstreamAdaptersAsync()":
                var adapters = await c.CallAsync(HyperVSwitchService.GetAllUpstreamAdaptersAsync);
                c.Require(adapters.All(a => a.ConnectionId != Guid.Empty) &&
                    adapters.Select(a => a.ConnectionId).Distinct().Count() == adapters.Count, "上游连接身份唯一");
                var bridgeable = await HyperVSwitchService.GetBridgeableAdaptersAsync();
                c.Require(bridgeable.All(b => adapters.Any(a => a.ConnectionId == b.ConnectionId && a.CanBridge)), "全清单包含可桥接候选");
                break;
            case "VmNetworkService.GetSwitchIovCapacitiesAsync()":
                var capacities = await c.CallAsync(VmNetworkService.GetSwitchIovCapacitiesAsync);
                var rows = SystemBaseline.Query("SELECT SystemName, IovVfCapacity FROM Msvm_EthernetSwitchHardwareOffloadData");
                var expected = rows.Where(r => Guid.TryParse(r["SystemName"]?.ToString(), out _) && r["IovVfCapacity"] != null)
                    .ToDictionary(r => Guid.Parse((string)r["SystemName"]!), r => Convert.ToUInt32(r["IovVfCapacity"]));
                c.Equal(expected.Count, capacities.Count, "已报告容量的交换机数量");
                foreach (var (id, value) in expected) c.Equal(value, capacities[id], "VF 容量");
                break;
            case "VmNetworkAdapter.Clone()":
                var original = new VmNetworkAdapter { MacAddress = "00155D123456", TrunkAllowedVlanIds = [10], PvlanSecondaryIds = [101], IpAddresses = ["192.0.2.1"] };
                var copy = c.Call(original.Clone);
                c.Equal(original.MacAddress, copy.MacAddress, "克隆包含当前配置");
                copy.TrunkAllowedVlanIds.Clear(); copy.PvlanSecondaryIds.Clear(); copy.IpAddresses.Clear();
                c.Require(original.TrunkAllowedVlanIds.Count == 1 && original.PvlanSecondaryIds.Count == 1 && original.IpAddresses.Count == 1, "三个集合独立");
                break;
            case "VmNetworkService.NormalizeStaticMac()":
                c.Equal("00155D123456", c.Call(() => VmNetworkService.NormalizeStaticMac("00:15:5d:12:34:56")), "合法地址规范化");
                c.Require(c.Call(() => VmNetworkService.NormalizeStaticMac("00:15:5D:12:34:XX")) == null, "拒绝非法字符");
                break;
            default: throw new InvalidDataException(api);
        }
    }
}
