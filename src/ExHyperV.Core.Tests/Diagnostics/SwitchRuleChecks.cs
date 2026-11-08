using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using R = ExHyperV.Properties.Resources;

internal static class SwitchRuleChecks
{
    public static void Run(Action<bool, string> check)
    {
        var originalCulture = R.Culture;
        try
        {
            foreach (var culture in new[] { "en-US", "zh-CN" })
            {
                R.Culture = CultureInfo.GetCultureInfo(culture);
                void Verify(bool result, string name) => check(result, "Switch rules: " + name + " (" + culture + ")");
                var wired = new SwitchUpstreamAdapter(Guid.Parse("10000000-0000-0000-0000-000000000001"), "wired", "Ethernet", "LAN", "external-wired", true);
                var cellular = new SwitchUpstreamAdapter(Guid.Parse("10000000-0000-0000-0000-000000000002"), "cellular", "WWAN", "Mobile", "", true);
                var spare = wired with { ConnectionId = Guid.Parse("10000000-0000-0000-0000-000000000003"), DeviceId = "spare", ExternalPortPath = "external-spare" };
                SwitchUpstreamAdapter[] physical = [wired, cellular, spare], bridgeable = [wired, spare];
                var systemDefault = new SwitchInfo { Id = "{C08CB7B8-9B3C-408E-8E30-5E16A3AEB444}", SwitchName = "Localized default name", SwitchType = SwitchMode.NAT };
                var owned = new SwitchInfo { Id = "20000000-0000-0000-0000-000000000001", SwitchName = "Existing NAT", SwitchType = SwitchMode.NAT, Upstream = wired };
                string? Create(string name, SwitchMode mode, SwitchUpstreamAdapter? upstream, params SwitchInfo[] existing) =>
                    SwitchConfigurationRules.GetCreationError(name, mode, upstream, existing, physical, bridgeable);

                Verify(Create(" ", SwitchMode.Isolated, null) == R.AddSwitch_Validation_NameCannotBeEmpty, "empty name keeps original message");
                Verify(Create("existing nat", SwitchMode.Isolated, null, owned) == string.Format(R.AddSwitch_Validation_NameExists, "existing nat"), "duplicate names ignore case");
                Verify(SwitchConfigurationRules.GetCreationError("new", SwitchMode.Bridge, null, [], [], []) == R.AddSwitch_Validation_NoAdaptersForExternalOrNat,
                    "no eligible adapters keeps original message");
                Verify(Create("new", SwitchMode.Bridge, null) == R.AddSwitch_Validation_AdapterRequiredForExternalOrNat, "unselected upstream keeps original message");
                Verify(Create("new", SwitchMode.Isolated, null, owned) == null, "isolated creation needs no upstream");
                Verify(Create("new", SwitchMode.NAT, spare, systemDefault) == null, "system default is identified by ID and excluded from custom NAT limit");
                Verify(Create("new", SwitchMode.NAT, spare, owned) == R.AddSwitch_Validation_OnlyOneNatAllowed, "second custom NAT creation is rejected");
                Verify(SwitchConfigurationRules.GetAvailableCreationAdapters(SwitchMode.Bridge, [owned], physical, bridgeable).SequenceEqual([spare]),
                    "bridge choices exclude occupied and nonbridgeable adapters");
                Verify(SwitchConfigurationRules.GetAvailableCreationAdapters(SwitchMode.NAT, [owned], physical, bridgeable).SequenceEqual([cellular, spare]),
                    "NAT choices retain unoccupied cellular adapters");
                Verify(Create("new", SwitchMode.Bridge, wired, owned) == R.Network_UpstreamMissing, "stale creation selection cannot reuse an occupied upstream");
                Verify(SwitchConfigurationRules.GetUpdateConflict("{" + owned.Id.ToUpperInvariant() + "}", SwitchMode.NAT, wired, [owned, systemDefault]) == null,
                    "editing a switch keeps its own NAT and upstream");
                Verify(SwitchConfigurationRules.GetUpdateConflict("other", SwitchMode.NAT, null, [owned]) == string.Format(R.Error_OnlyOneNatNetworkAllowed, owned.SwitchName),
                    "NAT conflict is reported before waiting for an upstream selection");
                var renamed = wired with { Name = "Renamed in another UI", Description = "New display text" };
                Verify(SwitchConfigurationRules.GetUpdateConflict("other", SwitchMode.Bridge, renamed, [owned]) == string.Format(R.Error_PhysicalAdapterInUse, renamed, owned.SwitchName),
                    "upstream conflict uses connection identity rather than display text");
                Verify(SwitchConfigurationRules.GetUpdateConflict("other", SwitchMode.Isolated, wired, [owned]) == null, "isolated mode does not claim an upstream");
                Verify(SwitchConfigurationRules.GetUpdateConflict(systemDefault.Id, SwitchMode.Isolated, null, [systemDefault]) == R.Warning_CannotModifyDefaultSwitch,
                    "service rules protect the system default switch");
                Verify(!SwitchConfigurationRules.IsConfigurationComplete(SwitchMode.NAT, null) && SwitchConfigurationRules.IsConfigurationComplete(SwitchMode.Isolated, null),
                    "form completeness comes from Core");
            }
        }
        finally { R.Culture = originalCulture; }
    }
}
