using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

if (ConfigurationIsolationChecks.RunWorker(args) is int workerExitCode)
{
    Environment.ExitCode = workerExitCode;
    return;
}

if (args.Contains("--console-native-selftest"))
{
    uint result = FreeRdpNative.RunSelfTest();
    Console.WriteLine(JsonSerializer.Serialize(new { mode = "console-native-offline", result, vmConnectionsAttempted = 0 }));
    Environment.ExitCode = result == 0 ? 0 : 1;
    return;
}

if (args.Contains("--console-checks"))
{
    int consoleChecks = 0;
    await ConsoleConnectionChecks.RunAsync((value, name) =>
    {
        if (!value) throw new InvalidOperationException(name);
        consoleChecks++;
        Console.WriteLine("PASS " + name);
    });
    Console.WriteLine(JsonSerializer.Serialize(new { mode = "console-fake-native", passed = consoleChecks, vmConnectionsAttempted = 0 }));
    return;
}

if (args.Contains("--console-probe"))
{
    await ConsoleProbe.RunAsync(args);
    return;
}

int checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

var core = typeof(HostPowerPlanService).Assembly;
Check(core.GetReferencedAssemblies().All(a => a.Name is not ("PresentationFramework" or "PresentationCore" or "WindowsBase" or "System.Windows.Forms")), "Core has no desktop UI dependency");
Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "PresentationFramework" or "PresentationCore"), "Headless consumer does not load WPF");

var staSamples = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
    ComApi.RunOnSta(() => (Thread.CurrentThread.ManagedThreadId, Thread.CurrentThread.GetApartmentState())))));
Check(staSamples.Select(x => x.ManagedThreadId).Distinct().Count() == 1 && staSamples.All(x => x.Item2 == ApartmentState.STA), "COM calls share one STA without WPF");
Check(ComApi.RunOnSta(() => ComApi.RunOnSta(() => 42)) == 42, "Nested COM execution cannot deadlock");
try { ComApi.RunOnSta<int>(() => throw new InvalidOperationException("expected")); throw new Exception("Exception lost"); }
catch (InvalidOperationException ex) when (ex.Message == "expected") { Check(true, "COM failure reaches its caller"); }

var pixels = new byte[] { 0, 248, 224, 7, 31, 0, 255, 255 };
var frame = VmScreenshotImage.FromRgb565(pixels, 2, 2);
pixels[0] = 255;
Check(frame.CopyBytes()[0] == 0 && frame.Stride == 4, "Thumbnail owns its RGB565 buffer");
var jpeg = frame.EncodeJpeg();
var decoded = VmScreenshotImage.FromEncoded(jpeg);
Check(decoded.Width == 2 && decoded.Height == 2 && decoded.IsEncoded, "Headless thumbnails preserve dimensions and JPEG compatibility");
var pci = new PciIds();
await pci.EnsureInitializedAsync();
Check(core.GetManifestResourceNames().Contains("ExHyperV.Assets.pci.ids"), "PCI database is available without Application resources");

string scratch = Path.Combine(Path.GetTempPath(), "ExHyperV-CoreChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    string file = Path.Combine(scratch, "owned.vhdx");
    File.WriteAllBytes(file, new byte[123]);
    var sizes = await VmStorageService.GetVirtualDiskSizesAsync(new[] { file, file, Path.Combine(scratch, "missing.vhdx") });
    Check(sizes.Count == 1 && sizes[file] == 123, "Storage size queries return detached results and tolerate missing files");
    string iso = Path.Combine(scratch, "owned.iso");
    ComApi.RunOnSta(() => { IsoBuilderService.BuildUdfIso(scratch, iso, "ExHyperV 1.X"); return true; });
    using var isoStream = File.OpenRead(iso);
    isoStream.Position = 16 * 2048 + 1;
    byte[] signature = new byte[5];
    isoStream.ReadExactly(signature);
    Check(System.Text.Encoding.ASCII.GetString(signature) == "CD001", "Core creates a valid ISO through native COM without a WPF application");
}
finally { File.Delete(Path.Combine(scratch, "owned.vhdx")); File.Delete(Path.Combine(scratch, "owned.iso")); Directory.Delete(scratch); }

await GpuWorkflowChecks.Run(Check);
SwitchRuleChecks.Run(Check);
HostBusinessRuleChecks.Run(Check);
await VmBusinessRuleChecks.Run(Check);
await QueryNetworkRuleChecks.Run(Check);
MemoryBackingRuleChecks.Run(Check);
IpAddressRuleChecks.Run(Check);
await SpacetimeWorkflowChecks.Run(Check);
await PowerWorkflowChecks.Run(Check);
await DeviceWorkflowChecks.RunAsync(Check);
ModelIsolationChecks.Run(Check);
await ResourceCultureChecks.RunAsync(Check);
await CacheOwnershipChecks.RunAsync(Check);
await ConfigurationIsolationChecks.RunAsync(Check);
await CpuMonitorLifecycleChecks.RunAsync(Check);
await VmQueryMonitorLifecycleChecks.RunAsync(Check);
NativeLifetimeChecks.Run(Check);
await ConsoleConnectionChecks.RunAsync(Check);
await ConfigurationRuleChecks.Run(Check);
await ExportPlanChecks.RunAsync(Check);
await StorageWorkflowChecks.Run(Check);
await ConfigurationInteractionChecks.RunAsync(Check);

var memory = new VmMemorySettings();
memory.ConfigurePageSizeSupport(false);
memory.ConfigurePageSizeSupport(true);
memory.SgxEnabled = true;
Check(memory.SgxSize == 2, "SGX retains the original default EPC size");
memory.Startup = 1025;
memory.MemoryEncryptionPolicy = 2;
memory.EnableColdHint = false;
memory.EnableHotHint = null;
memory.MaxMemoryBlocksPerNumaNode = 33;
var saved = memory.Clone();
memory.Startup = 4096;
memory.MemoryEncryptionPolicy = null;
memory.Restore(saved);
Check(memory.Startup == 1025 && memory.MemoryEncryptionPolicy == 2 && memory.EnableHotHint == null && memory.MaxMemoryBlocksPerNumaNode == 33, "Edit restore preserves values and unsupported fields");

var baselineArg = Array.IndexOf(args, "--baseline");
if (baselineArg >= 0)
{
    var baseline = Assembly.LoadFrom(Path.GetFullPath(args[baselineArg + 1]));
    var baselineMemoryType = baseline.GetType("ExHyperV.Models.VmMemorySettings", true)!;
    var baselineResources = baseline.GetType("ExHyperV.Properties.Resources", true)!;
    foreach (string culture in new[] { "en-US", "zh-CN" })
    {
        ExHyperV.Properties.Resources.Culture = CultureInfo.GetCultureInfo(culture);
        baselineResources.GetProperty("Culture")!.SetValue(null, CultureInfo.GetCultureInfo(culture));
        object original = Activator.CreateInstance(baselineMemoryType)!;
        var extracted = new VmMemorySettings();
        Check(JsonSerializer.Serialize(original, baselineMemoryType) == JsonSerializer.Serialize(extracted), "Original memory defaults and text: " + culture);
        baselineMemoryType.GetMethod("ConfigurePageSizeSupport")!.Invoke(original, new object[] { false });
        extracted.ConfigurePageSizeSupport(false);
        baselineMemoryType.GetProperty("SgxEnabled")!.SetValue(original, true);
        extracted.SgxEnabled = true;
        Check(JsonSerializer.Serialize(original, baselineMemoryType) == JsonSerializer.Serialize(extracted), "Original compatibility and SGX behavior: " + culture);
    }
    ExHyperV.Properties.Resources.Culture = null;
    var baselinePower = baseline.GetType("ExHyperV.Services.HostPowerPlanService", true)!;
    var beforePlans = baselinePower.GetMethod("GetPowerPlans")!.Invoke(null, null)!;
    Check(JsonSerializer.Serialize(beforePlans, beforePlans.GetType()) == JsonSerializer.Serialize(HostPowerPlanService.GetPowerPlans()), "Native power-plan query agrees with 1.5.2");
    Check(Equals(baselinePower.GetMethod("GetActivePowerPlanId")!.Invoke(null, null), HostPowerPlanService.GetActivePowerPlanId()), "Native active power plan agrees with 1.5.2");
}

int ownedArg = Array.IndexOf(args, "--owned-native-root");
int networkArg = Array.IndexOf(args, "--host-network-report");
if (networkArg >= 0) await HostNetworkChecks.Run(Check, args[networkArg + 1]);
if (ownedArg >= 0)
{
    if (baselineArg < 0) throw new ArgumentException("Native comparison requires --baseline");
    var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
    if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
    {
        string output = Path.GetFullPath(args[ownedArg + 1]);
        Directory.CreateDirectory(output);
        string report = JsonSerializer.Serialize(new { status = "blocked", reason = "Hyper-V integration checks require an elevated process.", fixturesCreated = 0 });
        File.WriteAllText(Path.Combine(output, "native-report.json"), report);
        Console.Error.WriteLine(report);
        Environment.ExitCode = 2;
    }
    else await NativeChecks.Run(args[ownedArg + 1], args[baselineArg + 1], Check);
}
Console.WriteLine(JsonSerializer.Serialize(new { passed = checks, mode = ownedArg >= 0 ? (Environment.ExitCode == 2 ? "native-blocked" : "owned-native-fixtures") : "headless-readonly", runtime = Environment.Version.ToString() }));
