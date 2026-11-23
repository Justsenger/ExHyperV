using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class NetworkPolicyTests
{
    [TestMethod]
    [DataRow(0, 0, VlanOperationMode.Off)]
    [DataRow(1, 0, VlanOperationMode.Off)]
    [DataRow(1, 4094, VlanOperationMode.Access)]
    [DataRow(2, 0, VlanOperationMode.Trunk)]
    [DataRow(3, 0, VlanOperationMode.Private)]
    [DataRow(4, 0, VlanOperationMode.Unknown)]
    public void ReadModeDistinguishesUntaggedAndUnknown(int mode, int accessId, VlanOperationMode expected)
        => Assert.AreEqual(expected, VmNetworkService.ReadVlanMode((uint)mode, accessId));

    private static Dictionary<string, object?> VlanFeature() => new()
    {
        ["OperationMode"] = 2u, ["AccessVlanId"] = (ushort)10, ["NativeVlanId"] = (ushort)1,
        ["TrunkVlanIdArray"] = new ushort[] { 1, 42 }, ["PruneVlanIdArray"] = new ushort[] { 11 },
        ["PvlanMode"] = 3u, ["PrimaryVlanId"] = (ushort)100, ["SecondaryVlanId"] = (ushort)101,
        ["SecondaryVlanIdArray"] = new ushort[] { 101, 103 }
    };

    private static Dictionary<string, object?> Writes(VmNetworkAdapter target, Dictionary<string, object?> raw, params string[] fields)
        => VmNetworkService.BuildNetworkWrites("Msvm_EthernetSwitchPortVlanSettingData", raw,
            SettingsSelection<VmNetworkAdapter>.Capture(target, fields));

    [TestMethod]
    public void TurningVlanOffClearsAllIncompatibleFieldsAndRoundTrips()
    {
        var target = new VmNetworkAdapter { VlanMode = VlanOperationMode.Off };
        var writes = Writes(target, VlanFeature(), nameof(target.VlanMode));
        Assert.AreEqual(1u, writes["OperationMode"]);
        foreach (string key in new[] { "AccessVlanId", "NativeVlanId", "PrimaryVlanId", "SecondaryVlanId" })
            Assert.AreEqual((ushort)0, writes[key]);
        Assert.AreEqual(0u, writes["PvlanMode"]);
        foreach (string key in new[] { "PruneVlanIdArray", "TrunkVlanIdArray", "SecondaryVlanIdArray" })
            Assert.IsNull(writes[key]);
        Assert.AreEqual(VlanOperationMode.Off, VmNetworkService.ReadVlanMode((uint)writes["OperationMode"]!, (ushort)writes["AccessVlanId"]!));
        Assert.AreEqual(0, Writes(target, writes, nameof(target.VlanMode)).Count);
        target.AccessVlanId = 4094;
        Assert.AreEqual((ushort)4094, Writes(target, writes, nameof(target.AccessVlanId))["AccessVlanId"]);
    }

    [TestMethod]
    public void TrunkRangesAreNormalizedAndOnlySelectedFieldsAreWritten()
    {
        var saved = new VmNetworkAdapter { VlanMode = VlanOperationMode.Trunk, NativeVlanId = 1, TrunkAllowedVlanIds = [10, 20, 21, 22] };
        var draft = new VmNetworkPolicyDraft(saved) { AllowedVlans = "22, 20-22, 10" };
        Assert.IsFalse(draft.IsDirty);
        draft.AllowedVlans = "4094, 1-3, 2";
        Assert.IsNull(draft.PrepareVlan());
        CollectionAssert.AreEqual(new[] { nameof(saved.TrunkAllowedVlanIds) }, draft.VlanChanges());
        var writes = Writes(draft.Values, VlanFeature(), draft.VlanChanges().ToArray());
        Assert.AreEqual(1, writes.Count);
        CollectionAssert.AreEqual(new ushort[] { 1, 2, 3, 4094 }, (ushort[])writes["TrunkVlanIdArray"]!);
        CollectionAssert.AreEqual(new[] { 10, 20, 21, 22 }, saved.TrunkAllowedVlanIds);

        var untaggedNative = new VmNetworkAdapter { VlanMode = VlanOperationMode.Trunk, NativeVlanId = 0, TrunkAllowedVlanIds = [10] };
        var untaggedDraft = new VmNetworkPolicyDraft(untaggedNative) { AllowedVlans = "10,20" };
        Assert.IsNull(untaggedDraft.PrepareVlan());
        CollectionAssert.AreEqual(new[] { nameof(untaggedNative.TrunkAllowedVlanIds) }, untaggedDraft.VlanChanges());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("10,,20")]
    [DataRow("1,")]
    [DataRow(",1")]
    [DataRow("0-3")]
    [DataRow("4-3")]
    [DataRow("1-4095")]
    [DataRow("+1")]
    [DataRow("1.5")]
    [DataRow("2-3-4")]
    public void RejectsMalformedOrOutOfRangeTrunkLists(string text)
        => Assert.IsFalse(VmNetworkPolicyDraft.TryParseVlanList(text, out _));

    [TestMethod]
    public void PromiscuousUsesPrimaryOnlyInDraftAndPreservesExistingSecondaryArray()
    {
        var saved = new VmNetworkAdapter { VlanMode = VlanOperationMode.Private, PvlanMode = PvlanMode.Isolated, PvlanPrimaryId = 100, PvlanSecondaryId = 101 };
        var draft = new VmNetworkPolicyDraft(saved);
        draft.Values.PvlanMode = PvlanMode.Promiscuous;
        Assert.IsNull(draft.PrepareVlan());
        CollectionAssert.AreEqual(new[] { nameof(saved.PvlanMode) }, draft.VlanChanges());
        var raw = VlanFeature();
        raw["OperationMode"] = 3u;
        raw["PvlanMode"] = 1u;
        raw["SecondaryVlanIdArray"] = null;
        var writes = Writes(draft.Values, raw, draft.VlanChanges().ToArray());
        CollectionAssert.AreEqual(new ushort[] { 100 }, (ushort[])writes["SecondaryVlanIdArray"]!);
        Assert.AreEqual((ushort)0, writes["SecondaryVlanId"]);
        raw["PvlanMode"] = 3u;
        raw["SecondaryVlanIdArray"] = new ushort[] { 101, 102, 103 };
        draft.Values.PvlanPrimaryId = 200;
        Assert.IsFalse(Writes(draft.Values, raw, nameof(saved.PvlanPrimaryId)).ContainsKey("SecondaryVlanIdArray"));
    }

    [TestMethod]
    public void RevertingVisibleChangesRestoresCleanDraftAndPreservesOffScaleValues()
    {
        var saved = new VmNetworkAdapter { VlanMode = VlanOperationMode.Off, BandwidthLimitBitsPerSecond = 123456789,
            StormLimit = 123, MacAddress = "00:15:5D:01:2A:0E", IsStaticMac = true };
        var draft = new VmNetworkPolicyDraft(saved);
        draft.Values.VlanMode = VlanOperationMode.Access;
        draft.Values.AccessVlanId = 20;
        draft.Values.DhcpGuardEnabled = true;
        Assert.IsTrue(draft.IsDirty);
        draft.Values.VlanMode = VlanOperationMode.Off;
        draft.Values.DhcpGuardEnabled = false;
        draft.Values.MacAddress = "00-15-5d-01-2a-0e";
        Assert.IsFalse(draft.IsDirty);
        Assert.AreEqual(123456789ul, draft.Values.BandwidthLimitBitsPerSecond);
        Assert.AreEqual(123u, draft.Values.StormLimit);
        Assert.IsNull(VmNetworkService.NormalizeStaticMac("00155d012a0e!"));
        Assert.AreEqual("00155D012A0E", VmNetworkService.NormalizeStaticMac("00155d012a0e"));
    }

    [TestMethod]
    public void AddMacExampleAdvancesAfterCreationAndSkipsOccupiedFormats()
    {
        List<VmNetworkAdapter> adapters = [];
        Assert.AreEqual("00:15:5D:01:2A:0E", VmNetworkPolicyDraft.SuggestStaticMac(adapters));
        adapters.Add(new() { MacAddress = "00:15:5D:01:2A:0E", IsStaticMac = true });
        Assert.AreEqual("00:15:5D:01:2A:0F", VmNetworkPolicyDraft.SuggestStaticMac(adapters));
        adapters.Add(new() { MacAddress = "00-15-5d-01-2a-11" });
        adapters.Add(new() { MacAddress = "00155d012a12", IsStaticMac = true });
        Assert.AreEqual("00:15:5D:01:2A:13", VmNetworkPolicyDraft.SuggestStaticMac(adapters));
    }

    [TestMethod]
    public void AddMacExampleWrapsAndStopsWhenPrefixIsExhausted()
    {
        var adapters = Enumerable.Range(0, 256).Where(tail => tail != 10)
            .Select(tail => new VmNetworkAdapter { MacAddress = $"00:15:5D:01:2A:{tail:X2}" }).ToList();
        Assert.AreEqual("00:15:5D:01:2A:0A", VmNetworkPolicyDraft.SuggestStaticMac(adapters));
        adapters.Add(new() { MacAddress = "00:15:5D:01:2A:0A" });
        Assert.IsNull(VmNetworkPolicyDraft.SuggestStaticMac(adapters));
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void RestoringLockedMacPreservesOtherDraftEdits(bool wasStatic, bool wasReported)
    {
        var saved = new VmNetworkAdapter { IsStaticMac = wasStatic, HasReportedMac = wasReported, MacAddress = "00:15:5D:01:2A:55" };
        var draft = new VmNetworkPolicyDraft(saved) { AllowedVlans = "10, 20-" };
        draft.Values.VlanMode = VlanOperationMode.Trunk;
        draft.Values.TeamingAllowed = true;
        draft.Values.IsStaticMac = !wasStatic;
        draft.Values.MacAddress = "00:15:5D:01:2A:56";
        Assert.IsTrue(draft.MacChanged);
        draft.RestoreMac();
        Assert.AreEqual(wasStatic, draft.Values.IsStaticMac);
        Assert.AreEqual(wasReported ? saved.MacAddress : "", draft.Values.MacAddress);
        Assert.IsFalse(draft.MacChanged);
        Assert.IsTrue(draft.Values.TeamingAllowed);
        Assert.AreEqual(VlanOperationMode.Trunk, draft.Values.VlanMode);
        Assert.AreEqual("10, 20-", draft.AllowedVlans);
    }

    [TestMethod]
    public void SwitchingToDynamicHasNoCopyableAddressUntilReadBack()
    {
        var adapter = new VmNetworkAdapter { IsStaticMac = true, MacAddress = "00:15:5D:01:2A:51" };
        var draft = new VmNetworkPolicyDraft(adapter);
        draft.Values.IsStaticMac = false;
        Assert.IsNull(draft.ReportedDynamicMac);

        var readBack = new VmNetworkAdapter { IsStaticMac = false, HasReportedMac = false };
        Assert.IsNull(new VmNetworkPolicyDraft(readBack).ReportedDynamicMac);
        readBack.HasReportedMac = true;
        readBack.MacAddress = "00:15:5D:01:2A:52";
        Assert.AreEqual(readBack.MacAddress, new VmNetworkPolicyDraft(readBack).ReportedDynamicMac);
    }

    [TestMethod]
    public void CardExpansionRestoresEachVmIncludingAllCollapsedAndNewlyAdded()
    {
        var cache = new VmNetworkCardExpansion();
        Guid vm = Guid.NewGuid(), otherVm = Guid.NewGuid();
        VmNetworkAdapter Adapter() => new() { Id = $@"Microsoft:{vm}\{Guid.NewGuid()}" };
        var first = Adapter();
        var second = Adapter();
        var added = Adapter();
        VmNetworkAdapter[] adapters = [first, second];
        Assert.IsEmpty(cache.GetExpanded(vm, []));
        Assert.IsTrue(cache.GetExpanded(vm, adapters).SetEquals([first.DeviceId]));
        cache.SetExpanded(vm, second.DeviceId, true);
        Assert.IsTrue(cache.GetExpanded(vm, adapters).SetEquals([first.DeviceId, second.DeviceId]));
        cache.SetExpanded(vm, first.DeviceId, false);
        cache.SetExpanded(vm, second.DeviceId, false);
        Assert.IsEmpty(cache.GetExpanded(vm, adapters));
        Assert.IsTrue(cache.GetExpanded(otherVm, adapters).SetEquals([first.DeviceId]));
        cache.SetExpanded(vm, second.DeviceId, true);
        cache.ExpandNew(vm, added.DeviceId);
        Assert.IsTrue(cache.GetExpanded(vm, [first, second, added]).SetEquals([added.DeviceId]));
        Assert.IsTrue(cache.GetExpanded(otherVm, adapters).SetEquals([first.DeviceId]));
    }

    [TestMethod]
    public void BandwidthRoundTripsAtBitPrecisionBeyondSliderStops()
    {
        foreach (ulong bits in new ulong[] { 0, 1, 999999, 123456789, 10000000000, ulong.MaxValue })
        {
            string mbps = VmNetworkPolicyDraft.FormatBandwidthMbps(bits);
            Assert.IsTrue(VmNetworkPolicyDraft.TryParseBandwidthMbps(mbps, out ulong parsed));
            Assert.AreEqual(bits, parsed);
        }
        foreach (string invalid in new[] { "-1", "0.0000001", "18446744073709.551616", "1,2", "" })
            Assert.IsFalse(VmNetworkPolicyDraft.TryParseBandwidthMbps(invalid, out _));
    }

    [TestMethod]
    public async Task UnavailableIovProviderLeavesCapabilityUnknown()
    {
        using var fault = WmiApi.UseQuerySource((_, _) => new(new ApiFailure(ApiErrorKind.AccessDenied, 5, ApiErrorSource.Wmi, "denied")));
        Assert.AreEqual(0, (await VmNetworkService.GetSwitchIovCapacitiesAsync()).Count);
    }

    [TestMethod]
    public void PromiscuousRequiresNonEmptySecondaryListExcludingPrimary()
    {
        var raw = VlanFeature();
        raw["OperationMode"] = 3u;
        raw["PvlanMode"] = 1u;
        raw["SecondaryVlanIdArray"] = null;

        var emptyTarget = new VmNetworkAdapter
        {
            VlanMode = VlanOperationMode.Private,
            PvlanMode = PvlanMode.Promiscuous,
            PvlanPrimaryId = 100
        };
        try
        {
            Writes(emptyTarget, raw, nameof(emptyTarget.PvlanMode), nameof(emptyTarget.PvlanSecondaryIds));
            Assert.Fail("Expected ArgumentException when Promiscuous secondary list is empty.");
        }
        catch (ArgumentException) { }

        var overlapTarget = new VmNetworkAdapter
        {
            VlanMode = VlanOperationMode.Private,
            PvlanMode = PvlanMode.Promiscuous,
            PvlanPrimaryId = 100,
            PvlanSecondaryIds = [100]
        };
        try
        {
            Writes(overlapTarget, raw, nameof(overlapTarget.PvlanMode), nameof(overlapTarget.PvlanSecondaryIds));
            Assert.Fail("Expected ArgumentException when Promiscuous secondary list contains primary VLAN ID.");
        }
        catch (ArgumentException) { }
    }

    [TestMethod]
    public async Task CreationRejectsPunctuatedMacConsistentlyWithUi()
    {
        var result = await VmNetworkService.AddNetworkAdapterAsync(
            Guid.NewGuid(), Guid.NewGuid(), isConnected: true, staticMacAddress: "00155D012A0E!");
        Assert.IsFalse(result.Success);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, result.ErrorKind);
    }
}
