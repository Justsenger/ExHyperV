using System.Management;

namespace ExHyperV.Core.Tests.TestExecution;

// Independent system observations. This helper never calls a Core mapper or mutation API.
internal static class SystemBaseline
{
    internal static List<Dictionary<string, object?>> Query(string wql, string scope = @"root\virtualization\v2")
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        using var rows = searcher.Get();
        var result = new List<Dictionary<string, object?>>();
        foreach (ManagementObject row in rows) using (row)
        {
            var record = row.Properties.Cast<PropertyData>().ToDictionary(p => p.Name,
                p => p.Value is Array array ? (object?)array.Clone() : p.Value, StringComparer.OrdinalIgnoreCase);
            record["__PATH"] = row.Path.Path; result.Add(record);
        }
        return result;
    }

    internal static Dictionary<string, object?> Vm(Guid id) => Query($"SELECT * FROM Msvm_ComputerSystem WHERE Name='{id:D}'").Single();
    internal static Dictionary<string, object?> Settings(Guid id) => Query($"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier='{id:D}' AND VirtualSystemType='Microsoft:Hyper-V:System:Realized'").Single();
    internal static Dictionary<string, object?> Security(Guid id) => Query($"SELECT * FROM Msvm_SecuritySettingData WHERE InstanceID LIKE 'Microsoft:{id:D}%'").Single();

    internal static async Task WaitStateAsync(Guid id, ushort expected, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        ushort actual;
        do
        {
            actual = Convert.ToUInt16(Vm(id)["EnabledState"]);
            if (actual == expected) return;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        throw new ApiCheckFailureException($"系统状态等待超时：预期 {expected}，实际 {actual}。");
    }
}
