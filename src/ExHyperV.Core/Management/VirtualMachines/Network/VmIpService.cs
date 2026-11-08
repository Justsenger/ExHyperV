using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ExHyperV.Tools;
using ExHyperV.Models;

namespace ExHyperV.Services
{
    /// <summary>合并已有地址的结果及是否仍需发起独立查询。</summary>
    /// <param name="Addresses">按来源优先级选取的地址文本列表。</param>
    /// <param name="RequiresLookup">现有来源没有地址且存在 MAC，调用者可继续执行独立查询。</param>
    public sealed record VmIpAddressResolution(List<string> Addresses, bool RequiresLookup);

    /// <summary>按宿主被动嗅探、来宾上报和宿主邻居缓存合并地址来源；宿主已观察到的地址优先。</summary>
    /// <remarks>合并各数据源的观测值或缓存地址。ARP 持续嗅探由调用方启动。</remarks>
    public static class VmIpService
    {
        /// <summary>从已有来源中选择地址：宿主已嗅探到的值优先，其次来宾上报列表，再次独立查询文本。</summary>
        /// <param name="macAddress">网卡 MAC；null 或空字符串时不尝试宿主按 MAC 查找。</param>
        /// <param name="reportedAddresses">来宾已上报的地址集合；null 表示没有上报数据。</param>
        /// <param name="lookupResult">此前独立查询得到的字符串；null 或空表示暂无查询结果。</param>
        /// <returns>所选来源中去重后的有效 IP 地址及是否仍需查询的标记。逗号分隔的结果会拆成独立地址，空白和无效项会被忽略。</returns>
        public static VmIpAddressResolution ResolveAdapterAddresses(
            string? macAddress, IEnumerable<string>? reportedAddresses, string? lookupResult = null)
        {
            string? observed = null;
            bool hasMac = !string.IsNullOrEmpty(macAddress);
            if (hasMac && ArpSnoopService.Instance.TryGetIp(macAddress!, out var address))
                observed = address;
            return ResolveAddresses(reportedAddresses, observed, lookupResult, hasMac);
        }

        internal static VmIpAddressResolution ResolveAddresses(
            IEnumerable<string>? reportedAddresses, string? observed, string? lookupResult, bool hasMac)
        {
            var observedAddresses = ParseAddresses([observed]);
            if (observedAddresses.Count > 0)
                return new(observedAddresses, false);
            var reported = ParseAddresses(reportedAddresses ?? []);
            if (reported.Count > 0)
                return new(reported, false);
            var lookedUp = ParseAddresses([lookupResult]);
            if (lookedUp.Count > 0)
                return new(lookedUp, false);
            return new(reported, hasMac);
        }

        private static List<string> ParseAddresses(IEnumerable<string?> values)
        {
            var addresses = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? value in values)
                foreach (string item in (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    if (IPAddress.TryParse(item, out var address) && seen.Add(address.ToString()))
                        addresses.Add(address.ToString());
            return addresses;
        }

        /// <summary>从网卡地址列表中选择首个有效 IPv4 地址用于简要显示或连接。</summary>
        /// <param name="adapters">网卡对象集合。</param>
        /// <param name="fallback">没有可显示地址时返回的文本，默认为空字符串。</param>
        /// <returns>单个 IPv4 地址或 fallback；逗号分隔的地址先拆分，无效文本及 IPv6 不作为 IPv4 返回。</returns>
        public static string GetPrimaryAddress(IEnumerable<VmNetworkAdapter> adapters, string fallback = "") =>
            ParseAddresses(adapters.SelectMany(a => a.IpAddresses ?? Enumerable.Empty<string>()))
                .FirstOrDefault(ip => IPAddress.TryParse(ip, out var parsed)
                    && parsed.AddressFamily == AddressFamily.InterNetwork) ?? fallback;

        internal static async Task<string> LookupAsync(string vmName, string macAddressWithColons)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) throw new InvalidOperationException(identity.Error);
            var response = await LookupAsync(identity.Data, macAddressWithColons);
            if (!response.Success) throw new InvalidOperationException(response.Error);
            return string.Join(", ", (response.Data ?? []).Select(item => item.Address));
        }

        /// <summary>查找 IPv4，返回独立地址及网卡关联、来源和时间；宿主嗅探优先。</summary>
        /// <param name="vmId">目标虚拟机 GUID，用于来宾上报查询。</param>
        /// <param name="macAddressWithColons">目标 MAC，允许冒号、横线或连续 12 位十六进制；空值返回空集合。</param>
        /// <returns>按优先级选取的地址观测集合。若只能取得整台 VM 的来宾地址，Association 为 VirtualMachine，不能当成该 MAC 的确切地址。来源均无地址且查询失败时返回结构化错误。</returns>
        /// <remarks>ARP 和邻居缓存仅按 MAC 关联，不证明地址仍属于目标 VM，也不替代 SSH 等服务器身份验证。来宾网卡通过系统关联查询；缓存无可靠观测时间时 ObservedAtUtc 为 null。</remarks>
        public static async Task<ApiResponse<IReadOnlyList<VmIpAddressObservation>>> LookupAsync(Guid vmId, string macAddressWithColons)
        {
            if (vmId == Guid.Empty) return ApiResponse<IReadOnlyList<VmIpAddressObservation>>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            if (string.IsNullOrWhiteSpace(macAddressWithColons)) return ApiResponse<IReadOnlyList<VmIpAddressObservation>>.Ok([]);
            string mac = NormalizeMac(macAddressWithColons);
            if (mac.Length != 12) return ApiResponse<IReadOnlyList<VmIpAddressObservation>>.Fail("MAC 格式无效。", kind: ApiErrorKind.InvalidArgument);
            if (ArpSnoopService.Instance.TryGetObservation(mac, out var snoopIp, out var observed))
                return Observations([snoopIp], null, mac, VmIpAddressSource.ArpCapture, VmIpAddressAssociation.MacAddress, observed);

            // Query only the requested VM's current adapter records. Native paths
            // come from these records, never from caller-provided WMI text.
            var failures = new List<ApiFailure>();
            foreach (string className in new[] { "Msvm_SyntheticEthernetPortSettingData", "Msvm_EmulatedEthernetPortSettingData" })
            {
                var ports = await WmiApi.QueryAsync($"SELECT InstanceID, Address FROM {className} WHERE InstanceID LIKE 'Microsoft:{vmId:D}%' ",
                    o => new { Id = o["InstanceID"]?.ToString() ?? "", Mac = NormalizeMac(o["Address"]?.ToString()), Path = o.Path.RelativePath });
                // Prefix selection is narrowed again by the exact VM and MAC below.
                if (!ports.Success) { failures.Add(ApiFailure.From(ports)!); continue; }
                foreach (var port in ports.Data ?? [])
                {
                    if (!port.Id.StartsWith("Microsoft:" + vmId.ToString("D") + "\\", StringComparison.OrdinalIgnoreCase) || port.Mac != mac) continue;
                    var guest = await WmiApi.QueryAsync($"ASSOCIATORS OF {{{port.Path}}} WHERE AssocClass=Msvm_SettingDataComponent ResultClass=Msvm_GuestNetworkAdapterConfiguration",
                        o => o["IPAddresses"] as string[] ?? []);
                    if (!guest.Success) { failures.Add(ApiFailure.From(guest)!); continue; }
                    var matched = Observations((guest.Data ?? []).SelectMany(a => a), port.Id, mac,
                        VmIpAddressSource.GuestIntegration, VmIpAddressAssociation.GuestAdapter);
                    if (matched.Data!.Count > 0) return matched;
                }
            }
            var neighbor = await FromArpCache(mac);
            if (neighbor.HasData && neighbor.Data.Count > 0) return neighbor;
            if (!neighbor.Success) failures.Add(ApiFailure.From(neighbor)!);
            // Preserve VM-wide fallback, but never label another adapter's IP as a MAC match.
            var all = await WmiApi.QueryAsync($"SELECT IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration WHERE InstanceID LIKE '%{vmId:D}%'",
                o => o["IPAddresses"] as string[] ?? []);
            if (all.Success)
            {
                var fallback = Observations((all.Data ?? []).SelectMany(a => a), null, null,
                    VmIpAddressSource.GuestIntegration, VmIpAddressAssociation.VirtualMachine);
                if (fallback.Data!.Count > 0) return fallback;
            }
            else failures.Add(ApiFailure.From(all)!);
            return failures.Count == 0 ? ApiResponse<IReadOnlyList<VmIpAddressObservation>>.Ok([])
                : ApiResponse<IReadOnlyList<VmIpAddressObservation>>.Fail(failures[0].Message, failures[0].Code, failures[0].Source, kind: failures[0].Kind);
        }

        internal static string NormalizeMac(string? value)
        {
            string cleaned = (value ?? "").Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
            return cleaned.Length == 12 && cleaned.All(Uri.IsHexDigit) ? cleaned : "";
        }

        internal static ApiResponse<IReadOnlyList<VmIpAddressObservation>> Observations(IEnumerable<string> addresses,
            string? adapterId, string? mac, VmIpAddressSource source, VmIpAddressAssociation association, DateTimeOffset? observed = null)
        {
            DateTimeOffset read = DateTimeOffset.UtcNow;
            return ApiResponse<IReadOnlyList<VmIpAddressObservation>>.Ok(Array.AsReadOnly(ParseAddresses(addresses)
                .Where(ip => IPAddress.Parse(ip).AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => new VmIpAddressObservation(ip, adapterId, mac, source, association, observed, read)).ToArray()));
        }

        private static async Task<ApiResponse<IReadOnlyList<VmIpAddressObservation>>> FromArpCache(string mac)
        {
            string formatted = Regex.Replace(mac, ".{2}", "$0-").TrimEnd('-');
            var response = await WmiApi.QueryCimAsync(
                $"SELECT IPAddress FROM MSFT_NetNeighbor WHERE LinkLayerAddress = '{WmiApi.Escape(formatted)}' AND AddressFamily = 2 AND State <> 0",
                obj => obj["IPAddress"]?.ToString() ?? string.Empty, WmiScope.StdCimV2);
            if (!response.Success) return ApiResponse<IReadOnlyList<VmIpAddressObservation>>.Fail(response.Error, response.Code, response.ErrorSource,
                response.Exception, response.ErrorKind);
            return Observations(response.Data ?? [], null, mac, VmIpAddressSource.NeighborCache, VmIpAddressAssociation.MacAddress);
        }
    }
}
