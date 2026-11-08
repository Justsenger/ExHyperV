using System.Reflection;
using System.Text.RegularExpressions;
using ExHyperV.Core.Tests.TestPlanning;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class ApiCatalogTests
{
    [TestMethod]
    public void PlannedApisExistOnThePublicCoreContract()
    {
        var types = typeof(VmQueryService).Assembly.GetExportedTypes().ToDictionary(TypeName);
        var invalid = new List<string>();
        foreach (string api in TestPlanner.Catalog.Select(item => item.Api).Distinct())
        {
            var method = Regex.Match(api, @"^(?<type>[A-Za-z]\w*(?:<T>)?(?:\.[A-Za-z]\w*)?)\.(?<member>[A-Za-z]\w*(?:<T>)?|构造)\(");
            if (method.Success)
            {
                string typeName = method.Groups["type"].Value;
                string name = method.Groups["member"].Value.Replace("<T>", "");
                if (!types.TryGetValue(typeName, out var type)
                    || (name == "构造" ? type.GetConstructors().Length == 0
                        : !type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                            .Concat(type.IsInterface ? type.GetInterfaces().SelectMany(contract => contract.GetMethods()) : [])
                            .Any(member => member.Name == name)))
                    invalid.Add(api);
            }
            else
            {
                foreach (Match typeName in Regex.Matches(api, @"\b[A-Z][A-Za-z0-9]+(?:\.[A-Z][A-Za-z0-9]+)?"))
                {
                    if (typeName.Value is "Core" or "GPU" or "PCIe") continue;
                    if (!types.ContainsKey(typeName.Value)) invalid.Add(api + " → " + typeName.Value);
                }
            }
        }
        Assert.IsEmpty(invalid, "测试清单中的公开接口已变化：\n" + string.Join("\n", invalid));
    }

    private static string TypeName(Type type) =>
        (type.IsNested ? TypeName(type.DeclaringType!) + "." : "") + type.Name.Replace("`1", "<T>");
}
