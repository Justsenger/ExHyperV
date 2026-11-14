using System.Text.Json;
using System.Text.Json.Nodes;
using ExHyperV.Core.Tests.TestExecution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class VmTestMemoryTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    [TestMethod]
    [DataRow(256, 256)]
    [DataRow(512, 512)]
    [DataRow(2048, 2048)]
    [DataRow(4096, 4096)]
    [DataRow(8192, 4096)]
    [DataRow(16384, 4096)]
    public void TemplateCopiesHaveTheirOwnBoundedStartupMemory(int originalMiB, int expectedMiB)
        => Assert.AreEqual(expectedMiB * MiB, VmTestMemory.TemplateStartupBytes(originalMiB * MiB));

    [TestMethod]
    public void InvalidTemplateMemoryIsReported()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => VmTestMemory.TemplateStartupBytes(0));
        Assert.ThrowsExactly<InvalidDataException>(() => VmTestMemory.TemplateStartupBytes(-1));
    }

    [TestMethod]
    [DataRow("Off", 5120, true, 0, true)]
    [DataRow("Off", 6144, true, 1, false)]
    [DataRow("Off", 6144, false, 0, false)]
    [DataRow("Saved", 5120, true, 0, true)]
    [DataRow("Saved", 6144, true, 1, false)]
    [DataRow("Running", 1, true, 0, false)]
    [DataRow("Paused", 1, false, 0, false)]
    public async Task ActualPowerShellPreparationHonorsCapacityStateAndReadOnlyMode(string state, int freeMiB, bool start, int calls, bool skip)
    {
        await InDirectory(async directory =>
        {
            var context = new ApiCaseContext(directory, CancellationToken.None);
            var vm = TemporaryVm.Reserve(context); vm.RecordIdentity(Guid.NewGuid());
            var action = () => VmTestMemory.PrepareStartAsync(context, vm, start, Backend(state, freeMiB * MiB, 4 * GiB, calls));
            if (skip) await Assert.ThrowsExactlyAsync<ApiCheckSkippedException>(action);
            else await action();
            Assert.IsTrue(context.Evidence.Single().Contains("宿主预留 2.0 GiB"));
        });
    }

    [TestMethod]
    public async Task CapacityUsesActualFixtureSettingsRatherThanTheDefaultCap()
    {
        await InDirectory(async directory =>
        {
            var context = new ApiCaseContext(directory, CancellationToken.None);
            var vm = TemporaryVm.Reserve(context); vm.RecordIdentity(Guid.NewGuid());
            await Assert.ThrowsExactlyAsync<ApiCheckSkippedException>(() => VmTestMemory.PrepareStartAsync(context, vm, true,
                Backend("Off", 9 * GiB, 8 * GiB, 0)));
        });
    }

    [TestMethod]
    public async Task KnownOutOfMemoryAfterTheCheckIsSkippedWithoutRetry()
    {
        await InDirectory(async directory =>
        {
            var context = new ApiCaseContext(directory, CancellationToken.None);
            var vm = TemporaryVm.Reserve(context); vm.RecordIdentity(Guid.NewGuid());
            await Assert.ThrowsExactlyAsync<ApiCheckSkippedException>(() => VmTestMemory.PrepareStartAsync(context, vm, true,
                Backend("Off", 8 * GiB, 4 * GiB, 1, "memory")));
        });
    }

    [TestMethod]
    [DataRow("permission")]
    [DataRow("unknown")]
    [DataRow("memory-running")]
    [DataRow("ownership")]
    public async Task OtherFailuresAndUnsettledStatesRemainFailures(string failure)
    {
        await InDirectory(async directory =>
        {
            var context = new ApiCaseContext(directory, CancellationToken.None);
            var vm = TemporaryVm.Reserve(context); vm.RecordIdentity(Guid.NewGuid());
            await Assert.ThrowsExactlyAsync<IOException>(() => VmTestMemory.PrepareStartAsync(context, vm, true,
                Backend("Off", 8 * GiB, 4 * GiB, 1, failure)));
        });
    }

    private static Func<string, object?, int, bool, Task<JsonElement>> Backend(string state, long available, long startup, int expectedCalls, string failure = "")
        => async (script, args, timeout, mayMutate) =>
        {
            var data = JsonSerializer.SerializeToNode(args)!.AsObject();
            Assert.AreEqual(data["start"]!.GetValue<bool>(), mayMutate);
            data["fixtureState"] = state; data["fixtureAvailable"] = available; data["fixtureStartup"] = startup;
            data["expectedCalls"] = expectedCalls; data["fixtureFailure"] = failure;
            // Run the production script in Windows PowerShell 5.1 with local fake
            // cmdlets. No Hyper-V provider or real VM is accessed by this regression.
            return await WindowsTestCommand.RunAsync("""
                $script:starts=0
                $script:vm=[pscustomobject]@{Id=[guid]$a.id;Name=$a.name;ConfigurationLocation=(Join-Path $a.root 'instance');MemoryStartup=[long]$a.fixtureStartup;State=$a.fixtureState}
                if($a.fixtureFailure -eq 'ownership'){$script:vm.Name='another-vm'}
                function Get-VM {param($Id) $script:vm}
                function Get-CimInstance {param($ClassName) [pscustomobject]@{FreePhysicalMemory=([long]$a.fixtureAvailable/1KB)}}
                function Start-VM {
                    [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$VM)
                    process{
                        $script:starts++
                        switch($a.fixtureFailure){
                            'memory' {throw [OutOfMemoryException]::new('fixture memory allocation')}
                            'memory-running' {$script:vm.State='Running';throw [OutOfMemoryException]::new('fixture ambiguous outcome')}
                            'permission' {throw [UnauthorizedAccessException]::new('fixture permission')}
                            'unknown' {throw [InvalidOperationException]::new('fixture unknown failure')}
                        }
                        $script:vm.State='Running'
                    }
                }
                $raw=& {
                """ + "\n" + script + "\n}\n" + """
                if($script:starts -ne $a.expectedCalls){throw ('Unexpected start calls: '+$script:starts)}
                $raw
                """, data, timeout, mayMutate: false);
        };

    private static async Task InDirectory(Func<string, Task> test)
    {
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ExHyperV-MemoryBudget-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try { await test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
