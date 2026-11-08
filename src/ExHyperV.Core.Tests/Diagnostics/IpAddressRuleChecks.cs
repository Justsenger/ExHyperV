using ExHyperV.Models;
using ExHyperV.Services;

internal static class IpAddressRuleChecks
{
    public static void Run(Action<bool, string> check)
    {
        var reported = new List<string> { "192.0.2.10", "2001:db8::10" };
        var observed = VmIpService.ResolveAddresses(reported, "192.0.2.20", "192.0.2.30", true);
        check(observed.Addresses.SequenceEqual(new[] { "192.0.2.20" }) && !observed.RequiresLookup,
            "IP conflict uses host observation ahead of reported and fallback addresses");
        check(reported.SequenceEqual(new[] { "192.0.2.10", "2001:db8::10" }), "IP resolution leaves caller input unchanged");
        var integration = VmIpService.ResolveAddresses(reported, null, "192.0.2.30", true);
        check(integration.Addresses.SequenceEqual(reported) && !integration.RequiresLookup,
            "IP without observation preserves reported addresses including IPv6");
        check(VmIpService.ResolveAddresses(null, null, null, true).RequiresLookup,
            "Empty adapter with MAC requests asynchronous fallback");
        check(!VmIpService.ResolveAdapterAddresses(null, null).RequiresLookup,
            "Adapter without MAC does not schedule a lookup");
        var lookup = VmIpService.ResolveAddresses(null, null, "192.0.2.30, 192.0.2.31", true);
        check(lookup.Addresses.SequenceEqual(new[] { "192.0.2.30", "192.0.2.31" }) && !lookup.RequiresLookup,
            "Fallback returns each address as a separate value");
        var refreshed = VmIpService.ResolveAddresses(new[] { "192.0.2.99" }, null, "192.0.2.30", true);
        check(refreshed.Addresses.Single() == "192.0.2.99", "Late fallback cannot replace newer reported addresses");
        check(VmIpService.GetPrimaryAddress(new[] {
            new VmNetworkAdapter { IpAddresses = new List<string> { "2001:db8::10" } },
            new VmNetworkAdapter { IpAddresses = new List<string> { "192.0.2.20" } }
        }) == "192.0.2.20", "Primary address selection skips IPv6 and follows adapter order");
        check(VmIpService.GetPrimaryAddress(Array.Empty<VmNetworkAdapter>()) == string.Empty, "Absent addresses return empty data without presentation placeholders");

        var noMacWithAddresses = new VmNetworkAdapter
        {
            MacAddress = string.Empty,
            IpAddresses = new List<string> { "2001:db8::10", "192.0.2.10" }
        };
        var noMacWithoutAddresses = new VmNetworkAdapter { MacAddress = string.Empty, IpAddresses = null! };
        // Missing adapter identity is rejected before WMI or ARP access.
        var result = VmNetworkService.FillDynamicIpsAsync(Guid.NewGuid(), new[] { noMacWithAddresses, noMacWithoutAddresses })
            .GetAwaiter().GetResult();
        check(!result.Success && result.ErrorKind == ExHyperV.Tools.ApiErrorKind.InvalidArgument,
            "IP refresh requires adapter ownership even when the MAC is empty");
        check(noMacWithAddresses.IpAddresses.SequenceEqual(new[] { "2001:db8::10", "192.0.2.10" }),
            "Network page preserves known IPv4 and IPv6 entries when no observation is available");
        check(noMacWithoutAddresses.IpAddresses is null,
            "Rejected refresh preserves the caller snapshot");
    }
}
