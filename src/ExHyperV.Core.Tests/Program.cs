namespace ExHyperV.Core.Tests;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() is "--resource-probe" or "--inspect-resources" or "--api-test-worker"
            or "--run-api-suite" or "--resume-reboot-tests")
            ResourceInspection.TestNetwork.Initialize();
        // The configuration concurrency scenario starts isolated copies of this executable.
        // Workers never launch the test runner or touch the user's configuration.
        if (args.Length > 0 && args[0] == "--configuration-worker")
            return Task.FromResult(RunConfigurationWorker(args));

        if (args.Length > 0 && args[0] == "--resource-probe")
            return ResourceInspection.ResourceProbes.RunWorkerAsync(args);
        if (args.Length > 0 && args[0] == "--vm-query-test-worker")
            return TestExecution.VmQueryWorker.RunAsync();
        if (args.Length == 3 && args[0] == "--api-test-worker")
            return TestExecution.ApiCaseWorker.RunAsync(args);
        if (args.Length == 1 && args[0] == "--audit-catalog")
            return Task.FromResult(AuditCatalog());
        if (args.Length == 2 && args[0] == "--resume-reboot-tests")
            return TestExecution.RebootContinuation.RunAsync(args[1]);
        if (args.Length == 2 && args[0] == "--run-api-suite")
            return Automation.AutomatedRun.RunAsync(args[1]);
        if (args.Length > 0 && args[0] == "--inspect-resources")
            return ResourceInspection.InspectionHost.RunAsync(args[1..]);

        return MicrosoftTestingPlatformApplication.RunAsync(args);
    }

    // Do not load Core before the coverage extension can instrument it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static int RunConfigurationWorker(string[] args) => ConfigurationIsolationChecks.RunWorker(args) ?? 1;

    // Static inventory only: builds descriptors without executing any test or resource probe.
    private static int AuditCatalog()
    {
        var catalog = TestPlanning.TestPlanner.Catalog;
        var ready = new ResourceInspection.InspectionReport("static-audit", DateTimeOffset.Now, DateTimeOffset.Now, false, "静态检查，无资源读取", null,
            ResourceInspection.ResourceProbes.Checks.Select(c => new ResourceInspection.InspectionItem(c, new(ResourceInspection.InspectionStatus.Ready, "静态占位"))).ToArray());
        var plan = TestPlanning.TestPlanner.Analyze(ready);
        var items = catalog.Select(p => new { p.Id, p.Api, p.Name, p.Requirements,
            Implemented = TestPlanning.TestPlanner.HasExecutor(p.Id),
            Exclusive = TestExecution.ApiCaseRegistry.Supports(p.Id) ? TestExecution.ApiCaseRegistry.Get(p.Id).Exclusive : true,
            UsesHostResources = TestExecution.ApiCaseRegistry.Supports(p.Id) ? TestExecution.ApiCaseRegistry.Get(p.Id).UsesHostResources : true,
            PendingChecks = plan.Items.Single(i => i.Id == p.Id).PendingChecks }).ToArray();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(items, ResourceInspection.InspectionJson.Options));
        return items.All(p => p.Implemented && p.PendingChecks.Length == 0) ? 0 : 2;
    }
}
