using System.Text.Json;
using ExHyperV.Services;
using ExHyperV.Tools;

internal static class HostNetworkChecks
{
    // Read-only integration coverage for the real startup/preloaded switch page.
    public static async Task Run(Action<bool, string> check, string reportPath)
    {
        var native = await WmiApi.QueryAsync("SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch",
            item => (Id: item["Name"]?.ToString() ?? "", Name: item["ElementName"]?.ToString() ?? ""));
        check(native.Success, "Read native switch inventory with the current token");
        var (switches, physical) = await HyperVSwitchService.GetNetworkInfoAsync();
        check(switches.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(native.Data!.Select(s => s.Id)), "Network page retains every native switch, including unreadable entries");
        check(switches.All(s => native.Data!.Any(n => n.Id.Equals(s.Id, StringComparison.OrdinalIgnoreCase) && n.Name == s.SwitchName)),
            "Network inventory preserves switch identities and names");
        var bridgeable = await HyperVSwitchService.GetBridgeableAdaptersAsync();
        check(bridgeable.All(a => physical.Any(p => p.ConnectionId == a.ConnectionId)), "Bridgeable adapters remain part of the physical inventory");
        var unknown = switches.Where(s => s.StateError != null).ToArray();
        check(unknown.All(s => !s.StateError!.Contains("Hyper-V模块") && !s.StateError.Contains("administrat", StringComparison.OrdinalIgnoreCase)),
            "Per-switch configuration failures do not masquerade as missing administrator privileges");
        var repeat = await HyperVSwitchService.GetNetworkInfoAsync();
        check(repeat.Switches.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(switches.Select(s => s.Id)),
            "Refreshing network information succeeds after an unreadable switch");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            switches = switches.Select(s => new { s.Id, s.SwitchName, s.StateError }),
            physicalAdapters = physical.Count,
            bridgeableAdapters = bridgeable.Count,
            unreadableSwitches = unknown.Length
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
