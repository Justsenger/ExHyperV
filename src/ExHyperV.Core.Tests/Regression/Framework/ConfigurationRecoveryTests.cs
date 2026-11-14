using System.Xml;
using System.Xml.Linq;
using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class ConfigurationRecoveryTests
{
    [TestMethod]
    public void AvailabilityChecksTheDocumentInsteadOfDesktopProcesses()
    {
        Assert.AreEqual(InspectionStatus.Ready, ConfigurationAvailability.Inspect(() => new(new XElement("Config"))).Status);
        Assert.AreEqual(InspectionStatus.Unavailable, ConfigurationAvailability.Inspect(() => throw new IOException("fixture")).Status);
        Assert.AreEqual(InspectionStatus.Unavailable, ConfigurationAvailability.Inspect(() => XDocument.Parse("<Config>")).Status);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RecoveryRestoresOnlyTheOwnedFieldAndKeepsNewSettings(bool originalExists)
    {
        await InDirectory(async directory =>
        {
            string path = Path.Combine(directory, "Config.xml");
            var file = new XmlConfigurationFile(path);
            file.Update(root =>
            {
                root.SetAttributeValue("version", "1");
                root.SetElementValue("Language", "zh-CN");
                if (originalExists) root.SetElementValue("MmioCeilingMb", "34816");
            });
            var journal = new HostStateJournal(new(directory, CancellationToken.None));
            journal.ConfigurationElement(path, "MmioCeilingMb");
            file.Update(root => root.SetElementValue("MmioCeilingMb", "524288"));
            // A second configuration writer changes unrelated settings while the test is active.
            new XmlConfigurationFile(path).Update(root =>
            {
                root.SetElementValue("Language", "en-US");
                root.SetElementValue("Theme", "Dark");
                root.Add(new XComment("keep external settings"));
            });
            await HostStateJournal.RecoverAsync(directory);
            var actual = file.Load().Root!;
            Assert.AreEqual(originalExists ? "34816" : null, actual.Element("MmioCeilingMb")?.Value);
            Assert.AreEqual("en-US", actual.Element("Language")?.Value);
            Assert.AreEqual("Dark", actual.Element("Theme")?.Value);
            Assert.AreEqual("1", actual.Attribute("version")?.Value);
            Assert.HasCount(1, actual.Nodes().OfType<XComment>().ToArray());
            Assert.IsFalse(File.Exists(Path.Combine(directory, "host-state.json")));
            byte[] restored = File.ReadAllBytes(path);
            await journal.DisposeAsync();
            CollectionAssert.AreEqual(restored, File.ReadAllBytes(path));
        });
    }

    [TestMethod]
    public async Task ReadOnlyPreparationLeavesAnAbsentConfigAbsent()
    {
        await InDirectory(async directory =>
        {
            string path = Path.Combine(directory, "Config.xml");
            await using (var journal = new HostStateJournal(new(directory, CancellationToken.None)))
                journal.ConfigurationElement(path, "MmioCeilingMb");
            Assert.IsFalse(File.Exists(path));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "host-state.json")));
        });
    }

    [TestMethod]
    public async Task AChangedFileWithoutTheOwnedFieldKeepsItsExactBytes()
    {
        await InDirectory(async directory =>
        {
            string path = Path.Combine(directory, "Config.xml");
            File.WriteAllText(path, "<Config><Theme>Light</Theme></Config>");
            var journal = new HostStateJournal(new(directory, CancellationToken.None));
            journal.ConfigurationElement(path, "MmioCeilingMb");
            const string external = "<Config>\n  <Theme>Dark</Theme>\n</Config>";
            File.WriteAllText(path, external);
            await journal.DisposeAsync();
            Assert.AreEqual(external, File.ReadAllText(path));
        });
    }

    [TestMethod]
    public async Task MalformedConfigurationPreservesTheRecoveryRecord()
    {
        await InDirectory(async directory =>
        {
            string path = Path.Combine(directory, "Config.xml");
            File.WriteAllText(path, "<Config><MmioCeilingMb>34816</MmioCeilingMb></Config>");
            var journal = new HostStateJournal(new(directory, CancellationToken.None));
            journal.ConfigurationElement(path, "MmioCeilingMb");
            File.WriteAllText(path, "<Config>");
            await Assert.ThrowsExactlyAsync<XmlException>(() => HostStateJournal.RecoverAsync(directory));
            Assert.AreEqual("<Config>", File.ReadAllText(path));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "host-state.json")));
            File.WriteAllText(path, "<Config><Language>en-US</Language></Config>");
            await journal.DisposeAsync();
            Assert.AreEqual("en-US", XDocument.Load(path).Root!.Element("Language")?.Value);
            Assert.AreEqual("34816", XDocument.Load(path).Root!.Element("MmioCeilingMb")?.Value);
        });
    }

    [TestMethod]
    public async Task LegacyFileBackupsRemainRecoverable()
    {
        await InDirectory(async directory =>
        {
            string path = Path.Combine(directory, "legacy.txt");
            File.WriteAllText(path, "original");
            await using (var journal = new HostStateJournal(new(directory, CancellationToken.None)))
            {
                journal.File(path);
                File.WriteAllText(path, "test");
            }
            Assert.AreEqual("original", File.ReadAllText(path));
        });
    }

    private static async Task InDirectory(Func<string, Task> test)
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ExHyperV-ConfigRecovery-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { await test(root); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
