using System.ComponentModel;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class SettingsSnapshotTests
{
    [TestMethod]
    public void MemoryDistinguishesZeroMissingValueAndUnsupportedProperty()
    {
        var raw = new Dictionary<string, object?> { ["VirtualQuantity"] = 0L, ["Weight"] = null, ["HugePagesEnabled"] = false };
        var snapshot = VmMemoryService.MapMemorySnapshot(new VmMemorySettings { Weight = 5000 }, raw);
        Assert.AreEqual(VmQueryValueState.Available, snapshot.States[nameof(VmMemorySettings.Startup)]);
        Assert.AreEqual(VmQueryValueState.Unavailable, snapshot.States[nameof(VmMemorySettings.Weight)]);
        Assert.AreEqual(VmQueryValueState.Unsupported, snapshot.States[nameof(VmMemorySettings.CxlEnabled)]);
        Assert.AreEqual(VmQueryValueState.Available, snapshot.States[nameof(VmMemorySettings.BackingPageSize)]);
        raw["VirtualQuantity"] = null;
        Assert.AreEqual(VmQueryValueState.Available, snapshot.States[nameof(VmMemorySettings.Startup)]);
    }

    [TestMethod]
    public void ProcessorSentinelRemainsAvailableWhileNullNativeValueIsUnavailable()
    {
        var snapshot = VmProcessorService.MapProcessorSnapshot(new VmProcessorSettings(),
            new Dictionary<string, object?> { ["PerfCpuFreqCapMhz"] = uint.MaxValue, ["VirtualQuantity"] = null, ["Limit"] = 0UL });
        Assert.AreEqual(VmQueryValueState.Available, snapshot.States[nameof(VmProcessorSettings.PerfCpuFreqCapMhz)]);
        Assert.IsNull(snapshot.Values.PerfCpuFreqCapMhz);
        Assert.AreEqual(VmQueryValueState.Unavailable, snapshot.States[nameof(VmProcessorSettings.Count)]);
        Assert.AreEqual(VmQueryValueState.Available, snapshot.States[nameof(VmProcessorSettings.Limit)]);
        Assert.AreEqual(VmQueryValueState.Unsupported, snapshot.States[nameof(VmProcessorSettings.LpiMode)]);
    }

    [TestMethod]
    public void DraftsAndSourceCannotChangeCapturedConfiguration()
    {
        var source = new VmMemorySettings { Startup = 1024, SupportedProps = ["VirtualQuantity"] };
        var snapshot = VmMemoryService.MapMemorySnapshot(source, new Dictionary<string, object?> { ["VirtualQuantity"] = 1024L });
        source.Startup = 4096;
        var draft = snapshot.Values;
        draft.Startup = 8192;
        draft.SupportedProps!.Clear();
        Assert.AreEqual(1024L, snapshot.Values.Startup);
        Assert.IsTrue(snapshot.Values.SupportedProps!.Contains("VirtualQuantity"));
        Assert.AreEqual(TimeSpan.Zero, snapshot.ObservedAtUtc.Offset);
    }

    [TestMethod]
    public void CoreModelsExposeDataWithoutUiNotificationContracts()
    {
        foreach (var type in typeof(VmMemorySettings).Assembly.GetExportedTypes().Where(type => type.Namespace == "ExHyperV.Models"))
            Assert.IsFalse(typeof(INotifyPropertyChanged).IsAssignableFrom(type), type.FullName);
        Assert.IsFalse(typeof(VmMemorySettings).Assembly.GetReferencedAssemblies().Any(name => name.Name == "CommunityToolkit.Mvvm"));
    }
}
