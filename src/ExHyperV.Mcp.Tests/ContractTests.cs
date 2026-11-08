using System.Text.Json;
using ExHyperV.Mcp.Api;
using ExHyperV.Mcp.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExHyperV.Mcp.Tests;

[TestClass]
public sealed class ContractTests
{
    [TestMethod]
    public void CorePublicContractHasBeenReviewed()
    {
        var catalog = new ApiCatalog();
        Assert.IsTrue(catalog.ContractReviewed, "Core 契约已变化。检查映射和只读声明后更新 Api/Contracts.sha256。");
        var coverage = JsonSerializer.SerializeToElement(catalog.Coverage());
        Assert.AreEqual(0, coverage.GetProperty("transportGaps").GetArrayLength());
    }

    [TestMethod]
    public void EveryToolHasAnOutputContractAndStableOrdering()
    {
        var tools = ToolRegistry.Create(McpLaunchOptions.Parse([]));
        foreach (var tool in tools)
        {
            Assert.IsNotNull(tool.ProtocolTool.OutputSchema, tool.ProtocolTool.Name);
            Assert.AreEqual("object", tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
        }
        CollectionAssert.AreEqual(tools.Select(tool => tool.ProtocolTool.Name).Order(StringComparer.Ordinal).ToArray(), tools.Select(tool => tool.ProtocolTool.Name).ToArray());
    }

    [TestMethod]
    public void ReadOnlyAndToolsetFiltersRemoveWriteAndUnrelatedTools()
    {
        var tools = ToolRegistry.Create(McpLaunchOptions.Parse(["--read-only", "--toolsets", "host"]));
        Assert.IsTrue(tools.Any(tool => tool.ProtocolTool.Name == "host_hyperv_status"));
        Assert.IsFalse(tools.Any(tool => tool.ProtocolTool.Name is "vm_power" or "gpu_deployment_start" or "core_api_call" or "core_api_set"));
        Assert.IsTrue(tools.All(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true || ToolRegistry.IsCleanup(tool.ProtocolTool.Name)));
        foreach (string name in new[] { "core_operation_release", "core_object_release", "core_operation_cancel", "server_task_release" })
            Assert.IsTrue(tools.Any(tool => tool.ProtocolTool.Name == name), name);
    }

    [TestMethod]
    public void GenericApiCannotBypassDomainOrReadonlyPolicy()
    {
        var catalog = new ApiCatalog();
        var policy = new ApiPolicy(catalog, McpLaunchOptions.Parse(["--read-only", "--toolsets", "host"]));
        var start = catalog.Find("M:ExHyperV.Services.ArpSnoopService.Start");
        Assert.IsFalse(catalog.IsReadOnly(start));
        Assert.Throws<UnauthorizedAccessException>(() => policy.Demand(start));
        var vm = catalog.Members.Values.First(member => member.Name == "ExecuteControlActionAsync");
        Assert.Throws<UnauthorizedAccessException>(() => policy.Demand(vm));
    }

    [TestMethod]
    public void ParameterSchemaCarriesNamesTypesAndRequiredFields()
    {
        var catalog = new ApiCatalog();
        var member = catalog.Find("M:ExHyperV.Services.VmMemoryService.GetVmMemorySettingsAsync(System.Guid)");
        var description = JsonSerializer.SerializeToElement(catalog.Describe(member));
        var input = description.GetProperty("inputSchema");
        Assert.AreEqual("uuid", input.GetProperty("properties").GetProperty("vmId").GetProperty("format").GetString());
        Assert.AreEqual("vmId", input.GetProperty("required")[0].GetString());
        Assert.AreEqual(JsonValueKind.Object, description.GetProperty("valueSchema").ValueKind);
    }

    [TestMethod]
    public void ValuesCanStillBeExplicitlyRetainedForInstanceCalls()
    {
        var objects = new ApiObjects();
        var codec = new ApiCodec(new ApiCatalog(), objects);
        var model = new ExHyperV.Models.VmMemorySettings { Startup = 2048 };
        codec.Pack(model);
        Assert.AreEqual(0, objects.Count);
        var value = JsonSerializer.SerializeToElement(codec.Pack(model, retain: true));
        Assert.AreSame(model, objects.Get(value.GetProperty("$handle").GetGuid()));
    }

    [TestMethod]
    public void ScalarContractsAcceptFlagsCharactersAndExplicitNull()
    {
        var codec = new ApiCodec(new ApiCatalog(), new ApiObjects());
        Assert.AreEqual(FileAttributes.ReadOnly | FileAttributes.Hidden,
            codec.Decode(JsonSerializer.SerializeToElement("ReadOnly, Hidden"), typeof(FileAttributes)));
        Assert.AreEqual('中', codec.Decode(JsonSerializer.SerializeToElement("中"), typeof(char)));
        Assert.IsNull(codec.Decode(JsonSerializer.SerializeToElement<string?>(null), typeof(string)));
        var flags = JsonSerializer.SerializeToElement(ApiSchema.Type(typeof(FileAttributes), input: true));
        Assert.IsFalse(flags.TryGetProperty("enum", out _));
        Assert.AreEqual("string", JsonSerializer.SerializeToElement(ApiSchema.Type(typeof(char), input: true)).GetProperty("type").GetString());
        var method = typeof(ContractTests).GetMethod(nameof(NullableInput))!;
        var input = JsonSerializer.SerializeToElement(ApiSchema.Arguments(method, _ => ""));
        Assert.AreEqual("null", input.GetProperty("properties").GetProperty("value").GetProperty("anyOf")[1].GetProperty("type").GetString());
    }

    public static void NullableInput(string? value) { }
}
