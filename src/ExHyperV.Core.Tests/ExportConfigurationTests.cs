using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class ExportConfigurationTests
{
    [TestMethod]
    public void OnlyPrimaryConfigurationsCanIdentifyAnExport()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var vm = Guid.NewGuid();
            var checkpoint = Guid.NewGuid();
            string Add(string folder, Guid id)
            {
                string path = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "export", folder)).FullName, $"{id:D}.vmcx");
                File.WriteAllBytes(path, []);
                return path;
            }
            Add("Snapshots", vm);
            string primary = Add("Virtual Machines", checkpoint);
            Assert.IsNull(VmExportService.FindCurrentConfigurationFile(root, vm));
            Assert.AreEqual(primary, VmExportService.FindCurrentConfigurationFile(root, checkpoint));
            Assert.AreEqual(primary, VmExportService.FindCurrentConfigurationFile(root, null));
            Add("Virtual Machines", Guid.NewGuid());
            Assert.IsNull(VmExportService.FindCurrentConfigurationFile(root, null));
        }
        finally { Directory.Delete(root, true); }
    }
}
