using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
[TestCategory("OperationQuery")]
public sealed class MemorySettingsTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PageSizeCapability_IsPreservedByClone(bool supported)
    {
        var settings = new VmMemorySettings();
        settings.ConfigurePageSizeSupport(supported);
        Assert.AreEqual(supported, settings.Clone().SupportsBackingPageSize);
    }

    [TestMethod]
    [DataRow(null, 2d)]
    [DataRow(0d, 2d)]
    [DataRow(1d, 2d)]
    [DataRow(2d, 2d)]
    [DataRow(32d, 32d)]
    public void EnablingSgx_EnsuresMinimumEpcWithoutOverwritingLargerValues(double? initial, double expected)
    {
        var settings = new VmMemorySettings { SgxSize = initial, SgxEnabled = true };
        Assert.AreEqual(initial, settings.SgxSize, "编辑值保持调用方输入");
        var target = VmMemoryService.MergeMemorySelection(new VmMemorySettings { SgxSize = initial },
            SettingsSelection<VmMemorySettings>.Capture(settings, [nameof(VmMemorySettings.SgxEnabled)]));
        Assert.AreEqual(expected, target.SgxSize);
    }

    [TestMethod]
    public void CancelEdit_RestoresValuesAndUnsupportedFields()
    {
        var settings = new VmMemorySettings
        {
            Startup = 1025, MemoryEncryptionPolicy = 2, EnableHotHint = null, MaxMemoryBlocksPerNumaNode = 33
        };
        var saved = settings.Clone();
        settings.Startup = 4096;
        settings.MemoryEncryptionPolicy = null;
        settings.EnableHotHint = true;
        settings.MaxMemoryBlocksPerNumaNode = 64;
        settings.Restore(saved);

        Assert.AreEqual(1025L, settings.Startup);
        Assert.AreEqual((byte)2, settings.MemoryEncryptionPolicy);
        Assert.IsNull(settings.EnableHotHint);
        Assert.AreEqual(33UL, settings.MaxMemoryBlocksPerNumaNode);
    }

    [TestMethod]
    public void PhysicalBacking_DisablesIncompatibleFeaturesAndEnforces1GbPageRule()
    {
        var current = new VmMemorySettings
        {
            BackingType = 1,
            BackingPageSize = 1,
            EnableColdHint = true,
            EnableHotHint = true,
            EnableEpf = true,
            EnablePrivateCompressionStore = true
        };
        var reqPhysical = new VmMemorySettings { BackingType = 0 };
        var merged = VmMemoryService.MergeMemorySelection(current,
            SettingsSelection<VmMemorySettings>.Capture(reqPhysical, [nameof(VmMemorySettings.BackingType)]));

        Assert.AreEqual((byte)0, merged.BackingType);
        Assert.AreEqual(false, merged.EnableColdHint);
        Assert.AreEqual(false, merged.EnableHotHint);
        Assert.AreEqual(false, merged.EnableEpf);
        Assert.AreEqual(false, merged.EnablePrivateCompressionStore);

        var reqVirtual = new VmMemorySettings { BackingType = 1 };
        var currentHuge = new VmMemorySettings { BackingType = 0, BackingPageSize = 2 };
        var downgraded = VmMemoryService.MergeMemorySelection(currentHuge,
            SettingsSelection<VmMemorySettings>.Capture(reqVirtual, [nameof(VmMemorySettings.BackingType)]));
        Assert.AreEqual((byte)1, downgraded.BackingType);
        Assert.AreEqual((byte)1, downgraded.BackingPageSize);
    }

    [TestMethod]
    public void ValidateMemoryChange_RejectsInvalidDynamicBoundsAndPerNodeModes()
    {
        var current = new VmMemorySettings { Startup = 2048, Minimum = 512, Maximum = 16384, Buffer = 20, Weight = 5000 };

        var badStartup = current.Clone();
        badStartup.DynamicMemoryEnabled = true;
        badStartup.Startup = 256; // < Minimum
        var resBounds = VmMemoryService.ValidateMemoryChange(current, badStartup, VmState.PoweredOff);
        Assert.IsFalse(resBounds.Success);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, resBounds.ErrorKind);

        var badHybrid = current.Clone();
        badHybrid.BackingType = 2;
        var resHybrid = VmMemoryService.ValidateMemoryChange(current, badHybrid, VmState.PoweredOff);
        Assert.IsFalse(resHybrid.Success);
        Assert.AreEqual(ApiErrorKind.Unsupported, resHybrid.ErrorKind);

        var badTracking = current.Clone();
        badTracking.MemoryAccessTrackingState = 2;
        var resTracking = VmMemoryService.ValidateMemoryChange(current, badTracking, VmState.PoweredOff);
        Assert.IsFalse(resTracking.Success);
        Assert.AreEqual(ApiErrorKind.Unsupported, resTracking.ErrorKind);
    }

    [TestMethod]
    public void VmmsServiceRecovery_StartsStoppedServiceClearsCacheAndProbesWmi()
    {
        var states = new Queue<VmmsServiceState>([
            VmmsServiceState.Stopped,
            VmmsServiceState.StartPending,
            VmmsServiceState.Running
        ]);
        bool startCalled = false;
        bool cacheCleared = false;
        int probeCount = 0;

        var backend = new VmmsServiceRecovery.Backend
        {
            QueryState = () => states.Count > 1 ? states.Dequeue() : states.Peek(),
            StartService = () => { startCalled = true; return 0; },
            ClearWmiCache = () => { cacheCleared = true; },
            ProbeHyperVWmi = () => ++probeCount >= 2,
            SleepMs = _ => { }
        };

        var result = VmmsServiceRecovery.TryRecoverCore(backend, CancellationToken.None);
        Assert.IsTrue(result.Attempted);
        Assert.IsTrue(result.Recovered);
        Assert.IsNull(result.Error);
        Assert.IsTrue(startCalled);
        Assert.IsTrue(cacheCleared);
        Assert.AreEqual(2, probeCount);

        bool startCalledWhenRunning = false;
        var runningResult = VmmsServiceRecovery.TryRecoverCore(new VmmsServiceRecovery.Backend
        {
            QueryState = () => VmmsServiceState.Running,
            StartService = () => { startCalledWhenRunning = true; return 0; },
            ClearWmiCache = () => { },
            ProbeHyperVWmi = () => true,
            SleepMs = _ => { }
        }, CancellationToken.None);
        Assert.IsFalse(runningResult.Attempted);
        Assert.IsTrue(runningResult.Recovered);
        Assert.IsFalse(startCalledWhenRunning);
    }
}
