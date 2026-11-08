using System.Text.Json;
using ExHyperV.Tools;

namespace ExHyperV.Core.Tests.TestExecution;

internal static class TemporaryCpuGroups
{
    internal static void Record(ApiCaseContext c, Guid id)
    {
        if (id == Guid.Empty) return;
        string path = Path.Combine(c.DirectoryPath, "cpu-groups.json");
        var ids = File.Exists(path) ? JsonSerializer.Deserialize<List<Guid>>(File.ReadAllText(path))! : [];
        if (!ids.Contains(id)) ids.Add(id);
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(ids)); File.Move(path + ".new", path, true);
    }
    internal static async Task CleanupAsync(string directory)
    {
        string path = Path.Combine(directory, "cpu-groups.json"); if (!File.Exists(path)) return;
        foreach (Guid id in JsonSerializer.Deserialize<List<Guid>>(File.ReadAllText(path))!)
        {
            var all = await HcsApi.GetAllCpuGroupsAsync(); if (!all.HasData) throw new IOException(all.Error);
            if (!all.Data!.Any(g => g.GroupId == id)) continue;
            var members = SystemBaseline.Query("SELECT CpuGroupId FROM Msvm_ProcessorSettingData");
            if (members.Any(v => Guid.TryParse(Convert.ToString(v.GetValueOrDefault("CpuGroupId")), out var g) && g == id)) throw new IOException("测试 CPU 组仍有成员，保留组：" + id);
            var removed = await HcsApi.DeleteCpuGroupAsync(id); if (!removed.Success) throw new IOException(removed.Error);
            var remaining = await HcsApi.GetAllCpuGroupsAsync(); if (!remaining.HasData || remaining.Data!.Any(g => g.GroupId == id)) throw new IOException("CPU 组清理未确认：" + id);
        }
        File.Delete(path);
    }
}
