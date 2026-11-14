namespace ExHyperV.Core.Tests.TestExecution;

internal static partial class ApiCaseRegistry
{
    private static void AddConditionCases(Action<string, Action<ApiCaseContext>[]> add)
    {
        add("VmProcessorService.GetModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().OfflineProcessorConditionMatchesSubmissionForEveryPowerState()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmProcessorSettings.Clone()", [c => { c.Call(() => new ModificationConditionTests().RuntimeAndUnsupportedProcessorSettingsAreDistinct()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmMemoryService.GetModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().MemoryConditionsMatchWritesForStaticAndDynamicMemory()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmPcieService.GetModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().PcieAndCompactionStayDisabledBeforeSubmission()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmPcieService.GetDeviceModeModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().PcieAndCompactionStayDisabledBeforeSubmission()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmStorageService.GetCompactionCondition()", [c => { c.Call(() => new ModificationConditionTests().PcieAndCompactionStayDisabledBeforeSubmission()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmStorageService.GetMediaModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().PcieAndCompactionStayDisabledBeforeSubmission()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmStorageService.GetRemovalCondition()", [c => { c.Call(() => new ModificationConditionTests().PcieAndCompactionStayDisabledBeforeSubmission()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmBootService.GetBootOrderModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().GenerationAndSecurityDependenciesHaveActionableReasons()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmSecurityService.GetModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().GenerationAndSecurityDependenciesHaveActionableReasons()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmBootService.GetNumLockModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().SingleStateConditionsCoverEveryPowerState()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmConsoleSupportService.GetModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().SingleStateConditionsCoverEveryPowerState()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmDisplaySettingsService.GetModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().SingleStateConditionsCoverEveryPowerState()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmMmioService.GetModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().SingleStateConditionsCoverEveryPowerState()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmNetworkService.GetMacModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().SingleStateConditionsCoverEveryPowerState()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
        add("VmPowerService.GetHibernationModificationCondition()", [c => { c.Call(() => new ModificationConditionTests().SingleStateConditionsCoverEveryPowerState()); c.Require(true, "电源状态、能力与配置依赖矩阵通过"); }]);
    }
}
