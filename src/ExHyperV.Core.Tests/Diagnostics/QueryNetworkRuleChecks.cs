using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using R = ExHyperV.Properties.Resources;

internal static class QueryNetworkRuleChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var first = new HostGpuInfo
        {
            InstanceId = @"PCI\VEN_10DE&DEV_1234\4&ABC",
            Name = "First GPU", Manu = "NVIDIA", Vendor = "Board maker",
            DriverVersion = "1.2.3", MemoryBytes = 8UL * 1024 * 1024 * 1024,
            PartitionableGpuPath = "partition-first"
        };
        var duplicate = new HostGpuInfo { InstanceId = first.InstanceId, Name = "Second match" };
        var empty = new HostGpuInfo { InstanceId = "", Name = "Must not match" };
        var assignments = VmGpuService.BuildGpuAssignments(
            [
                ("normalized", @"\\?\pci#ven_10de&dev_1234#4&abc#{11111111-1111-1111-1111-111111111111}"),
                ("contained", @"prefix-PCI\VEN_10DE&DEV_1234\4&abc-suffix"),
                ("unknown", @"PCI\VEN_9999&DEV_0000\1"),
                ("empty", "")
            ], [empty, first, duplicate]);
        check(assignments.Select(a => a.AdapterId).SequenceEqual(["normalized", "contained", "unknown", "empty"]),
            "GPU assignments preserve adapter order and identity");
        check(assignments.Take(2).All(a => a.Name == first.Name && a.Manu == first.Manu &&
                a.Vendor == first.Vendor && a.DriverVersion == first.DriverVersion &&
                a.MemoryBytes == first.MemoryBytes && a.PartitionableGpuPath == first.PartitionableGpuPath),
            "GPU assignments preserve first-match metadata for normalized and contained IDs");
        check(assignments.Skip(2).All(a => a.Name == "Unknown Device" && a.Manu == "Default" &&
                a.MemoryBytes == null && a.PartitionableGpuPath == string.Empty),
            "Unmatched and empty GPU paths keep the original fallback");
        assignments[0].Name = "Edited result";
        check(first.Name == "First GPU" && assignments[1].Name == "First GPU",
            "GPU assignment results do not share host or other assignment state");

        var originalCulture = R.Culture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN" })
            {
                R.Culture = CultureInfo.GetCultureInfo(culture);
                void Verify(bool result, string name) => check(result, "Network/video rules: " + name + " (" + culture + ")");
                Verify(VmNetworkService.GetConnectionError(true, 0) == R.Msg_Net_CreateSwitchFirst,
                    "connecting without switches gives the original guidance");
                Verify(VmNetworkService.GetConnectionError(false, 0) == null &&
                    VmNetworkService.GetConnectionError(true, 1) == null,
                    "disconnecting needs no switch and existing switches allow connection");

                var failedSwitchQuery = ApiResponse<List<string?>>.Fail("switch query failed");
                Verify(VmNetworkService.GetConnectionError(true, failedSwitchQuery) == failedSwitchQuery.Error,
                    "failed switch query preserves its error instead of claiming no switch exists");
                Verify(VmNetworkService.GetConnectionError(true, ApiResponse<List<string?>>.Empty()) == R.Msg_Net_CreateSwitchFirst,
                    "only a successful empty switch query gives create-switch guidance");
                Verify(VmNetworkService.GetConnectionError(true, ApiResponse<List<string?>>.Ok([null, "", "existing switch"])) == null &&
                    VmNetworkService.GetConnectionError(false, failedSwitchQuery) == null,
                    "valid switch data allows connection and disconnection does not depend on switch queries");

                var steps = new List<string>();
                Task<ApiResponse> Security(bool success)
                {
                    steps.Add("security");
                    return Task.FromResult(success ? ApiResponse.Ok() : ApiResponse.Fail("security-detail"));
                }
                Task<ApiResponse> Offload(bool success)
                {
                    steps.Add("offload");
                    return Task.FromResult(success ? ApiResponse.Ok() : ApiResponse.Fail("offload-detail"));
                }
                var securityFailure = await VmNetworkService.ApplySecurityAndOffloadSettingsAsync(
                    () => Security(false), () => Offload(true));
                Verify(!securityFailure.Success && securityFailure.Error == string.Format(R.Error_Net_Security, "security-detail") &&
                    steps.SequenceEqual(["security"]), "security failure stops before offload with the original error");

                steps.Clear();
                var offloadFailure = await VmNetworkService.ApplySecurityAndOffloadSettingsAsync(
                    () => Security(true), () => Offload(false));
                Verify(!offloadFailure.Success && offloadFailure.Error == string.Format(R.Error_Net_Offload, "offload-detail") &&
                    steps.SequenceEqual(["security", "offload"]), "offload failure preserves security-first partial application");

                steps.Clear();
                var success = await VmNetworkService.ApplySecurityAndOffloadSettingsAsync(
                    () => Security(true), () => Offload(true));
                Verify(success.Success && success.Error == string.Empty && steps.SequenceEqual(["security", "offload"]),
                    "combined network settings apply in the original order");

                Verify(VmDisplaySettingsService.GetResolutionError(3, 200, 200) == null &&
                    VmDisplaySettingsService.GetResolutionError(3, 7680, 4320) == null &&
                    VmDisplaySettingsService.GetResolutionError(3, 201, 201) == null,
                    "fixed video limits are inclusive and odd values remain eligible for existing rounding");
                foreach (var (width, height) in new[] { (199, 1080), (7681, 1080), (1920, 199), (1920, 4321) })
                {
                    var invalid = await VmDisplaySettingsService.SetResolutionAsync(Guid.NewGuid(), 3, width, height);
                    Verify(!invalid.Success && invalid.Error == R.VmAdvanced_ResolutionInvalid,
                        $"public video setter rejects {width}x{height} before querying the VM");
                }
                Verify(VmDisplaySettingsService.GetResolutionError(2, 0, 0) == null &&
                    VmDisplaySettingsService.GetResolutionError(4, 0, 0) == null,
                    "maximum and automatic video modes continue to ignore fixed dimensions");
            }
        }
        finally { R.Culture = originalCulture; }
    }
}
