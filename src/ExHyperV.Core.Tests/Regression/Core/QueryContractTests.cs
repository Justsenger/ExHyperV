using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class QueryContractTests
{
    [TestMethod]
    public void RequestCopiesAndDeduplicatesFieldsAndTargets()
    {
        var vmId = Guid.NewGuid();
        var fields = new List<VmQueryField> { VmIdentityFields.Name, VmMemoryFields.Startup, VmIdentityFields.Name };
        var targets = new List<Guid> { vmId, vmId };
        var request = new VmQueryRequest(fields, targets);
        fields.Clear();
        targets.Clear();
        CollectionAssert.AreEqual(new VmQueryField[] { VmIdentityFields.Name, VmMemoryFields.Startup }, request.Fields.ToArray());
        CollectionAssert.AreEqual(new[] { vmId }, request.VmIds!.ToArray());
        Assert.IsNull(new VmQueryRequest([]).VmIds);
        Assert.HasCount(0, new VmQueryRequest([], []).VmIds!);
    }

    [TestMethod]
    public void InvalidRequestIdentityAndForeignFieldsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new VmQueryRequest(null!));
        Assert.Throws<ArgumentException>(() => new VmQueryRequest([null!]));
        Assert.Throws<ArgumentException>(() => new VmQueryRequest([], [Guid.Empty]));
        var foreign = new VmQueryField<string>("Identity.Name", "仿造字段", "样例",
            VmQueryCollection.VirtualMachine, "Summary", "ElementName");
        Assert.Throws<ArgumentException>(() => new VmQueryRequest([foreign]));
    }

    [TestMethod]
    public void ValidZeroAndFalseRemainDistinctFromMissingOrFailedValues()
    {
        var time = DateTimeOffset.UtcNow;
        var values = new Dictionary<string, VmQueryValue>
        {
            [VmMemoryFields.Startup.Id] = VmMemoryFields.Startup.Make(VmQueryValueState.Available, 0L, time),
            [VmMemoryFields.DynamicMemoryEnabled.Id] = VmMemoryFields.DynamicMemoryEnabled.Make(VmQueryValueState.Available, false, time),
            [VmMemoryFields.Maximum.Id] = VmMemoryFields.Maximum.Make(VmQueryValueState.Failed, null, time, "读取失败")
        };
        var collections = new Dictionary<VmQueryCollection, VmQueryCollectionResult>
        {
            [VmQueryCollection.NetworkAdapters] = new(VmQueryValueState.Available, [], null, time)
        };
        var row = new VmQueryRow(Guid.NewGuid().ToString(), values, collections);
        values.Clear();
        collections.Clear();
        var zero = row.Get(VmMemoryFields.Startup);
        Assert.IsTrue(zero.HasValue);
        Assert.AreEqual(0L, zero.Value);
        Assert.AreEqual(time, zero.ObservedAtUtc);
        var disabled = row.Get(VmMemoryFields.DynamicMemoryEnabled);
        Assert.IsTrue(disabled.HasValue);
        Assert.IsFalse(disabled.Value);
        var failed = row.Get(VmMemoryFields.Maximum);
        Assert.AreEqual(VmQueryValueState.Failed, failed.State);
        Assert.IsFalse(failed.HasValue);
        Assert.IsNull(failed.UntypedValue);
        Assert.AreEqual("读取失败", failed.Error);
        Assert.AreEqual(VmQueryValueState.NotRequested, row.Get(VmMemoryFields.Minimum).State);
        Assert.AreEqual(VmQueryValueState.Available, row.GetCollection(VmQueryCollection.NetworkAdapters).State);
        Assert.HasCount(0, row.GetCollection(VmQueryCollection.NetworkAdapters).Rows);
        Assert.AreEqual(VmQueryValueState.NotRequested, row.GetCollection(VmQueryCollection.Storage).State);
    }
}
