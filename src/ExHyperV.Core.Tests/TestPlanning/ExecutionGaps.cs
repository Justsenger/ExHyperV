namespace ExHyperV.Core.Tests.TestPlanning;

internal static class ExecutionGaps
{
    // New catalog entries must receive an explicit executor before becoming selectable.
    internal static string Describe(CatalogTest test) => $"检查点 {test.Id} 尚未登记执行器；请同步测试计划与执行注册表。";
}