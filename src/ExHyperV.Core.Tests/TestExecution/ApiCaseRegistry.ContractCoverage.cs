using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddCurrentContractCases(Dictionary<string, ApiCaseDefinition> cases)
    {
        void Register(Func<string, ApiCaseContext, Task> run, bool exclusive, bool hostResources, params string[] apis)
        {
            foreach (string api in apis)
            {
                var plan = TestPlanner.Catalog.Single(item => item.Api == api);
                cases.Add(plan.Id, new(plan.Id, api, c => run(api, c), Exclusive: exclusive,
                    UsesHostResources: hostResources, TimeoutSeconds: exclusive ? 360 : 60));
            }
        }

        Register(HostTelemetryContractCases.RunAsync, exclusive: false, hostResources: true,
            "HostDeviceTelemetryService.Read()",
            "HostDeviceTelemetryService.ReadAdapterAddresses()",
            "HostNetworkTelemetryService.GetAdapterThroughput()",
            "HostNetworkTelemetryService.GetSwitchThroughput()",
            "HostNetworkTelemetryService.Reset()",
            "HostSystemInfoService.TryReadHostMemoryMiB()",
            "HyperVSwitchService.GetAllUpstreamAdaptersAsync()",
            "VmNetworkService.GetSwitchIovCapacitiesAsync()");

        Register(HostTelemetryContractCases.RunAsync, exclusive: false, hostResources: false,
            "HostDeviceTelemetryService.Dispose()",
            "HostNetworkTelemetryService.Dispose()",
            "VmNetworkAdapter.Clone()",
            "VmNetworkService.NormalizeStaticMac()");

        Register(VmSecurityContractCases.RunAsync, exclusive: true, hostResources: true,
            "VmProcessorService.GetHostMaxVirtualCpuCountAsync()",
            "VmSerialPortService.SetPortSettingsAsync()",
            "VmSecurityService.GetVtpmStateAsync()",
            "VmSecurityService.ExportLocalGuardianAsync()",
            "VmSecurityService.GrantHostAccessAsync()",
            "VmSecurityService.ReissueLocalKeyProtectorAsync()",
            "VmSecurityService.RestorePreviousKeyProtectorAsync()",
            "VmSecurityService.RevokeHostAccessAsync()",
            "VmSecurityService.SetVtpmPolicyAsync()");

        Register(VmDiskMaintenanceApiCases.RunAsync, exclusive: true, hostResources: true,
            "VmStorageService.GetPassthroughCandidatesAsync()",
            "VmStorageService.InspectDiskAsync()",
            "VmStorageService.InspectDifferencingChainAsync()",
            "VmStorageService.InspectResizeSafetyAsync()",
            "VmStorageService.ResizeVirtualDiskAsync()",
            "VmStorageService.ModifyDriveSlotAsync()",
            "VmStorageService.DeleteAttachedMediaFileAsync()",
            "VmStorageService.ConvertVirtualDiskTypeAsync()",
            "VmStorageService.UpgradeVhdToVhdxAsync()",
            "VmStorageService.ReconnectParentDiskAsync()",
            "VmStorageService.ExportDifferencingDiskAsync()",
            "VmStorageService.MergeDifferencingDiskAsync()");
    }
}
