using ExHyperV.Core.Tests.TestExecution;
using ExHyperV.Core.Tests.TestPlanning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class ApiCaseTests
{
    public static IEnumerable<object[]> FixedCases() => ApiCaseRegistry.All.Where(c => !c.Exclusive && !c.UsesHostResources)
        .Select(c => new object[] { c.Id });

    [TestMethod]
    [DynamicData(nameof(FixedCases))]
    public async Task FixedInputCasesExerciseTheirDeclaredCoreApi(string id)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ExHyperV-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var context = new ApiCaseContext(directory, CancellationToken.None);
            await ApiCaseRegistry.Get(id).Run(context);
            Assert.IsTrue(context.Calls > 0, "目标 API 调用记录缺失");
            Assert.IsNotEmpty(context.Evidence);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void EveryRegisteredCaseHasOneCatalogEntryAndIsoHasSpecificConditions()
    {
        foreach (var definition in ApiCaseRegistry.All)
            Assert.AreEqual(definition.Api, TestPlanner.Catalog.Single(c => c.Id == definition.Id).Api);
        var iso = TestPlanner.Catalog.Where(c => c.Api == "IsoBuilderService.BuildUdfIso()").ToArray();
        Assert.HasCount(2, iso);
        Assert.IsTrue(iso.All(c => c.Requirements.SequenceEqual(new[] { "files", "iso" }) && ApiCaseRegistry.Get(c.Id).Exclusive));
    }
}
