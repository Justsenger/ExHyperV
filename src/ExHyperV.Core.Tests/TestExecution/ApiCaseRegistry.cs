using ExHyperV.Core.Tests.TestPlanning;

namespace ExHyperV.Core.Tests.TestExecution;

// Explicit, reviewed tests. API discovery only audits the catalog; it never executes a method.
internal static partial class ApiCaseRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<string, ApiCaseDefinition>> Cases = new(Build);
    internal static bool Supports(string id) => Cases.Value.ContainsKey(id);
    internal static ApiCaseDefinition Get(string id) => Cases.Value.TryGetValue(id, out var value)
        ? value : throw new ArgumentException("此检查点的执行代码尚待接入。", nameof(id));
    internal static IReadOnlyCollection<ApiCaseDefinition> All => Cases.Value.Values.ToArray();
    internal static bool NeedsAzureBackup(string api) => api is "VmCreateService.CreateVirtualMachineAsync()" or "VmProcessorService.SetVmProcessorAsync()"
        or "VmPcieService.SetSystemSettingsAsync()" or "VmPcieService.SetDeviceModeAsync()"
        || api.StartsWith("VmPowerService.ExecuteControlActionAsync(", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, ApiCaseDefinition> Build()
    {
        var cases = new Dictionary<string, ApiCaseDefinition>(StringComparer.Ordinal);
        void Add(string api, Action<ApiCaseContext>[] handlers)
        {
            var plans = TestPlanner.Catalog.Where(item => item.Api == api).OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
            if (plans.Length != handlers.Length) throw new InvalidDataException($"执行器与检查点数量待同步：{api}");
            for (int i = 0; i < plans.Length; i++)
            {
                var handler = handlers[i];
                cases.Add(plans[i].Id, new(plans[i].Id, api, context => { handler(context); return Task.CompletedTask; }));
            }
        }
        AddValueCases(Add);
        AddRuleCases(Add);
        AddQueryCases(Add);
        AddAdditionalRuleCases(Add);
        AddSwitchCases(Add);
        AddFrameCases(Add);
        AddSnapshotCases(Add);
        AddConfigurationRuleCases(Add);
        AddStorageRuleCases(Add);
        AddConditionCases(Add);
        foreach (var plan in TestPlanner.Catalog.Where(item => HostReadApiCases.Supports(item.Api)))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => { HostReadApiCases.Run(plan.Api, context); return Task.CompletedTask; }, UsesHostResources: true));
        foreach (var plan in TestPlanner.Catalog.Where(item => item.Api == "IsoBuilderService.BuildUdfIso()"))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => IsoApiCases.RunAsync(plan.Id, context), Exclusive: true));
        foreach (var plan in TestPlanner.Catalog.Where(item => VmProjectionApiCases.Supports(item.Id)))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmProjectionApiCases.RunAsync(plan.Id, context), UsesHostResources: true));
        foreach (var plan in TestPlanner.Catalog.Where(VmSettingsApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmSettingsApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 180));
        foreach (var plan in TestPlanner.Catalog.Where(VmDeviceApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmDeviceApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 240));
        foreach (var plan in TestPlanner.Catalog.Where(p => p.Api is "VmMemoryService.GetVmMemorySettingsAsync()" or "VmProcessorService.GetVmProcessorAsync()"))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmConfigurationReadApiCases.RunAsync(plan.Api, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 180));
        foreach (var plan in TestPlanner.Catalog.Where(p => p.Api == "VmExportPackagingService.CreatePackageAsync()"))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => FileApiCases.PackageAsync(plan.Id, context), UsesHostResources: true, TimeoutSeconds: 90));
        foreach (var plan in TestPlanner.Catalog.Where(p => p.Api == "DiskParser.GetPartitions()"))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => { FileApiCases.Partitions(plan.Id, context); return Task.CompletedTask; }, UsesHostResources: true));
        foreach (var plan in TestPlanner.Catalog.Where(VmPlatformApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmPlatformApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 240));
        foreach (var plan in TestPlanner.Catalog.Where(VmNetworkFeatureApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmNetworkFeatureApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 300));
        foreach (var plan in TestPlanner.Catalog.Where(VmBootPowerApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmBootPowerApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 2400));
        foreach (var plan in TestPlanner.Catalog.Where(VmGroupApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmGroupApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 300));
        foreach (var plan in TestPlanner.Catalog.Where(VmCheckpointApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmCheckpointApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 2400));
        foreach (var plan in TestPlanner.Catalog.Where(VmcxApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmcxApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 300));
        foreach (var plan in TestPlanner.Catalog.Where(VmCreationApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmCreationApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 1800));
        foreach (var plan in TestPlanner.Catalog.Where(VmDeletionApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmDeletionApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 600));
        foreach (var plan in TestPlanner.Catalog.Where(VmMetadataApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmMetadataApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 240));
        foreach (var plan in TestPlanner.Catalog.Where(VmStorageApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmStorageApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 360));
        foreach (var plan in TestPlanner.Catalog.Where(VmQueryContractApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmQueryContractApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 300));
        foreach (var plan in TestPlanner.Catalog.Where(HostSettingsApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => HostSettingsApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 180));
        foreach (var plan in TestPlanner.Catalog.Where(VmTransferApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmTransferApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 900));
        foreach (var plan in TestPlanner.Catalog.Where(WorkflowSessionApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => WorkflowSessionApiCases.RunAsync(plan, context), TimeoutSeconds: 60));
        foreach (var plan in TestPlanner.Catalog.Where(VmAddressSpaceApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmAddressSpaceApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 900));
        foreach (var plan in TestPlanner.Catalog.Where(HostDiskApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => HostDiskApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 360));
        foreach (var plan in TestPlanner.Catalog.Where(ConsoleApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => ConsoleApiCases.RunAsync(plan, context), Exclusive: ConsoleApiCases.IsLive(plan), UsesHostResources: ConsoleApiCases.IsLive(plan), TimeoutSeconds: ConsoleApiCases.IsLive(plan) ? 2400 : 60));
        foreach (var plan in TestPlanner.Catalog.Where(CpuMonitorApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => CpuMonitorApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 60));
        foreach (var plan in TestPlanner.Catalog.Where(HostInventoryApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => HostInventoryApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 300));
        foreach (var plan in TestPlanner.Catalog.Where(CpuAffinityApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => CpuAffinityApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 300));
        foreach (var plan in TestPlanner.Catalog.Where(PhysicalStorageApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => PhysicalStorageApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 360));
        foreach (var plan in TestPlanner.Catalog.Where(VmGpuApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmGpuApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 3600));
        foreach (var plan in TestPlanner.Catalog.Where(IpCaptureApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => IpCaptureApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: IpCaptureApiCases.Live(plan), TimeoutSeconds: 180));
        foreach (var plan in TestPlanner.Catalog.Where(VmPerformanceApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => VmPerformanceApiCases.RunAsync(plan, context), Exclusive: true,
                UsesHostResources: plan.Requirements.Contains("counters"), TimeoutSeconds: plan.Id.EndsWith("-1", StringComparison.Ordinal) ? 2700 : 180));
        foreach (var plan in TestPlanner.Catalog.Where(PcieApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => PcieApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 900));
        foreach (var plan in TestPlanner.Catalog.Where(SwitchApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => SwitchApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 600));
        foreach (var plan in TestPlanner.Catalog.Where(RebootApiCases.Supports))
            cases.Add(plan.Id, new(plan.Id, plan.Api, context => RebootApiCases.RunAsync(plan, context), Exclusive: true, UsesHostResources: true, TimeoutSeconds: 3600));
        return cases;
    }
}
