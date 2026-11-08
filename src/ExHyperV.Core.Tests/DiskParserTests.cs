using ExHyperV.Core.Tests.TestExecution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class DiskParserTests
{
    [TestMethod]
    [DataRow("T-cc0ae6d5227b0f65-1")]
    [DataRow("T-cc0ae6d5227b0f65-2")]
    public void ValidAndDamagedPartitionTablesUseBoundedRanges(string id)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ExHyperV.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { FileApiCases.Partitions(id, new ApiCaseContext(directory, CancellationToken.None)); }
        catch (ApiCheckSkippedException error) { Assert.Inconclusive(error.Message); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
