using ExHyperV.Models;
using ExHyperV.Services;
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
}
