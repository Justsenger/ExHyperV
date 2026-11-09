using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Core.Tests.TestExecution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class VmCredentialStoreTests
{
    [TestMethod]
    public async Task PasswordIsEncryptedAndExistingCredentialSurvivesInvalidInput()
    {
        string root = Path.Combine(Path.GetTempPath(), "ExHyperV-CredentialTest-" + Guid.NewGuid().ToString("N"));
        var store = new VmCredentialStore(root);var id = Guid.NewGuid();
        string password = "fixture-测试-'$`\"-" + Guid.NewGuid().ToString("N");
        try
        {
            await store.SaveAsync(id, "fixture\\测试用户", password);
            string saved = File.ReadAllText(store.PathFor(id));
            Assert.IsFalse(saved.Contains(password, StringComparison.Ordinal));
            var result = await WindowsTestCommand.RunAsync("$c=Import-Clixml -LiteralPath $a.path;@{user=$c.UserName;valid=($c -is [PSCredential] -and $c.Password.Length -gt 0)}|ConvertTo-Json", new { path = store.PathFor(id) }, 20, false);
            Assert.IsTrue(result.GetProperty("valid").GetBoolean());
            Assert.AreEqual("fixture\\测试用户", result.GetProperty("user").GetString());
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.SaveAsync(id, "", ""));
            Assert.AreEqual(saved, File.ReadAllText(store.PathFor(id)));
            Assert.AreEqual(1, Directory.GetFiles(root).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CredentialsAreBoundToSavedTemplateAndResourceRevision()
    {
        string id = Guid.NewGuid().ToString("D");
        var registration = new RegistrationSnapshot("v1", "fixture", new() { TemplateVmId = id });
        var report = new InspectionReport("r1", DateTimeOffset.Now, DateTimeOffset.Now, false, "fixture", null, []);
        SaveVmCredentialsRequest Request(string revision, string template) => new() { Revision = revision, TemplateVmId = template, ResourceRunId = "r1", UserName = "user", Password = "fixture" };
        Assert.AreEqual(Guid.Parse(id), VmCredentialStore.Validate(Request("v1", id), registration, report));
        Assert.ThrowsExactly<InvalidOperationException>(() => VmCredentialStore.Validate(Request("old", id), registration, report));
        Assert.ThrowsExactly<ArgumentException>(() => VmCredentialStore.Validate(Request("v1", Guid.NewGuid().ToString()), registration, report));
    }
}
